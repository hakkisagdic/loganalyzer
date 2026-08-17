using System.Text;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Storage.Raw;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <c>event_id</c> → ham kayıt aramasının <b>zaman hassasiyeti</b> (K29).
///
/// <para>
/// Buradaki tek gerçek konu şu: manifest zaman damgasını Postgres'te
/// <b>mikrosaniyeyle</b> tutuyor, ClickHouse'daki <c>ts</c> ise
/// <c>DateTime64(3)</c> — yani <b>milisaniyeye kırpılmış</b>. Aralık payı olmadan
/// karşılaştırıldığında tek olaylı bir nesne <b>hiçbir zaman</b> bulunamıyor:
/// <c>ts_from</c> ile <c>ts_to</c> aynı ve kırpılmış <c>ts</c> ikisinden de küçük
/// kalıyor (14:48:02.904 &lt; 14:48:02.904923).
/// </para>
///
/// <para>
/// Uçtan uca doğrulamada bu, <c>/v1/events/{id}/raw</c>'ın nesne arşivde
/// dururken 404 dönmesi olarak göründü ve hata mesajı "henüz yüklenmemiş
/// olabilir" diyordu — yani yanlış yere bakmaya davet ediyordu. Büyük
/// nesnelerde yalnızca sınırdaki olaylarda görüneceği için teşhisi çok daha zor
/// olurdu.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class RawEventLocatorTests(DevStackFixture stack) : IAsyncLifetime
{
    /// <summary>Mikrosaniye bileşeni <b>kasıtlı</b>: kırpılma buradan doğuyor.</summary>
    private static readonly DateTimeOffset Observed =
        new DateTimeOffset(2026, 8, 17, 14, 48, 2, TimeSpan.Zero) + TimeSpan.FromTicks(9_049_234);

    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;
    private string _bucket = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _factory = new ControlPlaneFactory(stack.PostgresConnectionString);

        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await db.RawManifest.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await db.Sources.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        _bucket = "bizigo-locator-" + Guid.NewGuid().ToString("N")[..8];
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private RawStoreOptions Options() => new()
    {
        ServiceUrl = stack.S3ServiceUrl,
        Bucket = _bucket,
        AccessKey = "bizigoadmin",
        SecretKey = "bizigoadmin",
        ForcePathStyle = true,
        SegmentRetention = TimeSpan.FromHours(48),
    };

    private async Task<(RawRecord Record, string ObjectKey, IRawObjectStore Store)> ArchiveOneAsync()
    {
        var record = new RawRecord
        {
            EventId = Guid.CreateVersion7(Observed),
            ReceivedAt = Observed.AddSeconds(1),
            ObservedAt = Observed,
            SourceKey = "10.9.9.9",
            Body = Encoding.UTF8.GetBytes("kullanıcı oturum açma başarısız"),
        };

        var options = Options();
        var store = new S3RawObjectStore(Microsoft.Extensions.Options.Options.Create(options));
        await store.EnsureBucketAsync(TestContext.Current.CancellationToken);

        var segments = new FakeSegmentSource();
        segments.Add("segment-locator", [(ReadOnlyMemory<byte>)RawRecordCodec.ToLine(record)]);

        var uploader = new RawArchiveUploader(
            segments,
            store,
            _factory,
            new SourceDirectory(_factory),
            new NullRawRefSink(),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<RawArchiveUploader>.Instance);

        var report = await uploader.RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, report.ObjectsWritten);

        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var key = await db.RawManifest.Select(m => m.ObjectKey)
            .SingleAsync(TestContext.Current.CancellationToken);

        return (record, key, store);
    }

    private static LogEvent EventFor(RawRecord record, string objectKey, DateTimeOffset timestamp) => new()
    {
        EventId = record.EventId,
        Timestamp = timestamp,
        OwnerGroup = OwnerGroups.Unassigned,
        SourceId = record.SourceKey,
        RawRef = objectKey[..(objectKey.LastIndexOf('/') + 1)],
    };

    /// <summary>
    /// <b>Asıl bekçi.</b> Olayın zamanı ClickHouse'un yazacağı gibi milisaniyeye
    /// kırpılmış; manifest ise mikrosaniye tutuyor. Pay olmadan bu arama boş
    /// döner.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Milisaniyeye_kirpilmis_zamanla_bulunuyor()
    {
        var (record, key, store) = await ArchiveOneAsync();

        // ClickHouse `DateTime64(3)`: mikrosaniye bileşeni kayboluyor.
        var truncated = new DateTimeOffset(
            Observed.Ticks - (Observed.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);

        Assert.True(truncated < Observed, "Test kurgusu: kırpılmış zaman gerçekten daha küçük olmalı.");

        var locator = new RawEventLocator(_factory, store);
        var found = await locator.FindAsync(
            EventFor(record, key, truncated),
            AccessScope.System("test"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal(record.EventId, found.Record.EventId);
        Assert.Equal(record.Body.ToArray(), found.Record.Body.ToArray());
    }

    /// <summary>Kırpılmamış zamanla da bulunmalı — pay eskisini bozmuyor.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Tam_hassasiyetli_zamanla_da_bulunuyor()
    {
        var (record, key, store) = await ArchiveOneAsync();

        var locator = new RawEventLocator(_factory, store);
        var found = await locator.FindAsync(
            EventFor(record, key, Observed),
            AccessScope.System("test"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal(record.EventId, found.Record.EventId);
    }

    /// <summary>
    /// Pay <b>dar</b> kalmalı. Bir saniye uzaktaki olay bu nesnede değil ve
    /// aramanın onu getirmemesi gerekiyor; aksi halde pay, aralık daraltmasını
    /// tamamen anlamsız kılardı.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Aralik_disindaki_zaman_getirilmiyor()
    {
        var (record, key, store) = await ArchiveOneAsync();

        var locator = new RawEventLocator(_factory, store);
        var found = await locator.FindAsync(
            EventFor(record, key, Observed.AddSeconds(1)),
            AccessScope.System("test"),
            TestContext.Current.CancellationToken);

        Assert.Null(found);
    }

    /// <summary>
    /// Kapsam kontrolü <b>indirmeden önce</b>: başka gruptaki bir olay için
    /// nesne hiç açılmıyor.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Kapsam_disi_olay_nesneyi_hic_actirmiyor()
    {
        var (record, key, store) = await ArchiveOneAsync();

        var locator = new RawEventLocator(_factory, store);

        await Assert.ThrowsAsync<RawAccessDeniedException>(() => locator.FindAsync(
            EventFor(record, key, Observed),
            AccessScope.ForGroups("test", ["baska-grup"]),
            TestContext.Current.CancellationToken));
    }
}
