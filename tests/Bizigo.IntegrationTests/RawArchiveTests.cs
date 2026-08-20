using System.Text;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Storage.Raw;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.IntegrationTests;

/// <summary>
/// T04 kabul kriterleri. Gerçek RustFS ve gerçek Postgres'e karşı koşar.
///
/// <para>
/// Buradaki en önemli test <see cref="Dogrulanmamis_segment_silinmiyor"/>:
/// RustFS 1.0-rc olduğu için "yazdım" ile "yazıldı" arasındaki farkı yerel
/// kopyayla kapatıyoruz. O kontrol düşerse geriye hiçbir koruma kalmaz.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class RawArchiveTests(DevStackFixture stack) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 30, 0, TimeSpan.Zero);

    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;
    private RawStoreOptions _options = null!;

    public async ValueTask InitializeAsync()
    {
        // Göç, temizlik ve kova adı ortak kurulum yüzeyinde (`DevStackSetup`):
        // aynı hazırlık üç test sınıfında tekrarlanıyordu.
        _factory = await DevStackSetup.ControlPlaneAsync(stack, TestContext.Current.CancellationToken);
        _options = DevStackSetup.RawOptions(stack);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private RawStoreOptions Options() => _options;

    private static RawRecord Record(string body, string sourceKey) => new()
    {
        EventId = Guid.CreateVersion7(Now),
        ReceivedAt = Now,
        SourceKey = sourceKey,
        Body = Encoding.UTF8.GetBytes(body),
    };

    private async Task<(RawArchiveUploader Uploader, FakeSegmentSource Segments, IRawObjectStore Store)>
        BuildAsync(RawStoreOptions options, TimeProvider time, params RawRecord[] records)
    {
        var store = new S3RawObjectStore(Microsoft.Extensions.Options.Options.Create(options));
        await store.EnsureBucketAsync(TestContext.Current.CancellationToken);

        var segments = new FakeSegmentSource();
        segments.Add("segment-1", records.Select(r => (ReadOnlyMemory<byte>)RawRecordCodec.ToLine(r)));

        var directory = new SourceDirectory(_factory);

        var uploader = new RawArchiveUploader(
            segments,
            store,
            _factory,
            directory,
            new NullRawRefSink(),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<RawArchiveUploader>.Instance,
            time);

        return (uploader, segments, store);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Nesne_yaziliyor_geri_okunuyor_ve_manifest_dogrulaniyor()
    {
        await using (var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            db.Sources.Add(new SourceEntity
            {
                SourceId = "fg-ankara-01",
                PeerAddress = "10.1.2.3",
                OwnerGroup = "network/core",
                SourceClass = "firewall",
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (uploader, _, store) = await BuildAsync(
            Options(),
            TimeProvider.System,
            Record("bağlantı düştü", "10.1.2.3"),
            Record("arayüz kapandı", "10.1.2.3"));

        var report = await uploader.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report.ObjectsWritten);

        await using var check = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var entry = await check.RawManifest.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal("network/core", entry.OwnerGroup);
        Assert.Equal(2, entry.EventCount);
        Assert.Equal(RawObjectState.Verified, entry.State);
        Assert.NotNull(entry.VerifiedAt);

        // owner_group anahtarın içinde olmalı — ham okuma kapsam kontrolü buna dayanıyor.
        Assert.Equal("network/core", RawObjectKey.ReadOwnerGroup(entry.ObjectKey));

        // Geri okunan içerik gerçekten aynı mı?
        var content = await store.GetAsync(entry.ObjectKey, TestContext.Current.CancellationToken);
        Assert.NotNull(content);

        var records = await new RawReader(store).ReadObjectAsync(
            entry.ObjectKey,
            AccessScope.System("test"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, records.Count);
        Assert.Equal("bağlantı düştü", Encoding.UTF8.GetString(records[0].Body.Span));

        // Çözülen kimlikler arşiv satırına yazılmış olmalı: satır kendi başına anlamlı.
        Assert.Equal("network/core", records[0].OwnerGroup);
        Assert.Equal("fg-ankara-01", records[0].SourceId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Envanterde_olmayan_kaynak_unassigned_grubuna_dusuyor()
    {
        var (uploader, _, _) = await BuildAsync(
            Options(),
            TimeProvider.System,
            Record("bilinmeyen cihaz", "10.9.9.9"));

        await uploader.RunOnceAsync(TestContext.Current.CancellationToken);

        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var entry = await db.RawManifest.SingleAsync(TestContext.Current.CancellationToken);

        // Reddetmek veri kaybı olurdu; eksik envanter düzeltilebilir bir sorun.
        Assert.Equal(OwnerGroups.Unassigned, entry.OwnerGroup);
        Assert.Equal(1, entry.EventCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Dogrulanmamis_segment_silinmiyor()
    {
        var options = Options();
        var time = new FakeTime(Now);

        var (uploader, segments, _) = await BuildAsync(options, time, Record("veri", "10.1.2.3"));

        await uploader.RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("segment-1", segments.Deleted);

        // Doğrulanmış olmasına rağmen saklama süresi dolmadan da silinmemeli.
        time.Advance(TimeSpan.FromHours(47));
        await uploader.RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("segment-1", segments.Deleted);

        // 48 saat dolunca silinir (koruma #3).
        time.Advance(TimeSpan.FromHours(2));
        var report = await uploader.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report.SegmentsDeleted);
        Assert.Contains("segment-1", segments.Deleted);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ayni_segment_iki_kez_yuklenmiyor()
    {
        var (uploader, _, _) = await BuildAsync(Options(), TimeProvider.System, Record("veri", "10.1.2.3"));

        await uploader.RunOnceAsync(TestContext.Current.CancellationToken);
        var second = await uploader.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, second.ObjectsWritten);

        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, await db.RawManifest.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Scrub_sha256_uyusmazligini_yakaliyor()
    {
        var options = Options();
        var store = new FakeObjectStoreOverS3(new S3RawObjectStore(
            Microsoft.Extensions.Options.Options.Create(options)));

        await store.EnsureBucketAsync(TestContext.Current.CancellationToken);

        var segments = new FakeSegmentSource();
        segments.Add("segment-scrub", [(ReadOnlyMemory<byte>)RawRecordCodec.ToLine(Record("veri", "10.1.2.3"))]);

        var uploader = new RawArchiveUploader(
            segments,
            store,
            _factory,
            new SourceDirectory(_factory),
            new NullRawRefSink(),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<RawArchiveUploader>.Instance);

        await uploader.RunOnceAsync(TestContext.Current.CancellationToken);

        await using (var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            var key = await db.RawManifest.Select(m => m.ObjectKey)
                .SingleAsync(TestContext.Current.CancellationToken);
            store.Corrupt(key);
        }

        var scrubber = new RawArchiveScrubber(
            store,
            _factory,
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<RawArchiveScrubber>.Instance);

        var report = await scrubber.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Mismatched);

        await using var check = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var entry = await check.RawManifest.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RawObjectState.ChecksumMismatch, entry.State);
        Assert.NotNull(entry.LastScrubbedAt);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Scrub_kayip_nesneyi_yakaliyor()
    {
        var options = Options();
        var store = new FakeObjectStoreOverS3(new S3RawObjectStore(
            Microsoft.Extensions.Options.Options.Create(options)));

        await store.EnsureBucketAsync(TestContext.Current.CancellationToken);

        var segments = new FakeSegmentSource();
        segments.Add("segment-missing", [(ReadOnlyMemory<byte>)RawRecordCodec.ToLine(Record("veri", "10.1.2.3"))]);

        var uploader = new RawArchiveUploader(
            segments,
            store,
            _factory,
            new SourceDirectory(_factory),
            new NullRawRefSink(),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<RawArchiveUploader>.Instance);

        await uploader.RunOnceAsync(TestContext.Current.CancellationToken);
        store.Hide();

        var scrubber = new RawArchiveScrubber(
            store,
            _factory,
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<RawArchiveScrubber>.Instance);

        var report = await scrubber.RunOnceAsync(TestContext.Current.CancellationToken);

        // Manifest olmasa bu kayıp fark edilmezdi — tablonun varlık sebebi bu.
        Assert.Equal(1, report.MissingObjects);
    }
}
