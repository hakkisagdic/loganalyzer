using System.Collections.Concurrent;
using System.Text;
using Bizigo.Contracts;
using Bizigo.Storage.Raw;

namespace Bizigo.UnitTests;

/// <summary>
/// Bellek içi nesne deposu — S3 API sözleşmesinin test karşılığı.
/// Yalnızca arayüzü uyguluyor; RustFS'e özel hiçbir davranış taklit edilmiyor.
/// </summary>
public sealed class FakeObjectStore : IRawObjectStore
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, byte[]> Objects => _objects;

    public Task EnsureBucketAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        _objects[key] = content.ToArray();
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_objects.TryGetValue(key, out var value) ? value : null);

    public Task<RawObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_objects.TryGetValue(key, out var value)
            ? new RawObjectInfo(key, value.LongLength)
            : null);

    /// <summary>Sessiz bozulmayı taklit eder — scrub'ın yakalaması gereken durum.</summary>
    public void Corrupt(string key) => _objects[key] = "bozuk"u8.ToArray();

    public void Remove(string key) => _objects.TryRemove(key, out _);
}

public sealed class RawReaderTests
{
    private static readonly DateTimeOffset Hour = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static (FakeObjectStore Store, string Key, RawRefEntry Ref) Seed(string ownerGroup)
    {
        var builder = new RawObjectBuilder();
        var record = new RawRecord
        {
            EventId = Guid.CreateVersion7(),
            ReceivedAt = Hour,
            SourceKey = "fg-01",
            OwnerGroup = ownerGroup,
            Body = Encoding.UTF8.GetBytes("bağlantı düştü"),
        };

        builder.Add(record.EventId, Hour, RawRecordCodec.ToLine(record));
        var built = builder.Build(3);

        var key = new RawObjectKey(ownerGroup, Hour, "firewall", "abc").Value;
        var store = new FakeObjectStore();
        store.PutAsync(key, built.Compressed).GetAwaiter().GetResult();

        return (store, key, built.Refs[0]);
    }

    [Fact]
    public async Task Kapsam_icindeki_nesne_okunabiliyor()
    {
        var (store, key, entry) = Seed("network/core");
        var reader = new RawReader(store);

        var record = await reader.ReadAsync(
            entry.ToRawRef(key),
            AccessScope.ForGroups("u1", ["network/core"]),
            TestContext.Current.CancellationToken);

        Assert.NotNull(record);
        Assert.Equal("bağlantı düştü", Encoding.UTF8.GetString(record.Body.Span));
    }

    [Fact]
    public async Task Kapsam_disindaki_nesne_INDIRILMEDEN_reddediliyor()
    {
        var (store, key, entry) = Seed("network/core");
        var reader = new RawReader(store);

        await Assert.ThrowsAsync<RawAccessDeniedException>(
            () => reader.ReadAsync(
                entry.ToRawRef(key),
                AccessScope.ForGroups("u1", ["baska/grup"]),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Bos_kapsam_hicbir_seyi_okuyamiyor()
    {
        var (store, key, entry) = Seed("network/core");
        var reader = new RawReader(store);

        // Boş kapsam "her şey" değil "hiçbir şey" — K17'nin en pahalı hata sınıfı.
        await Assert.ThrowsAsync<RawAccessDeniedException>(
            () => reader.ReadAsync(
                entry.ToRawRef(key),
                AccessScope.Denied,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Bicimi_bozuk_anahtar_reddediliyor()
    {
        var store = new FakeObjectStore();
        var reader = new RawReader(store);

        // Anahtardan grup okunamıyorsa "bilinmiyor" geçirilmez; aksi halde tek bir
        // bozuk anahtar kapsam kontrolünü atlardı.
        await Assert.ThrowsAsync<RawAccessDeniedException>(
            () => reader.ReadAsync(
                "bozuk/anahtar#0:10",
                AccessScope.ForGroups("u1", ["network/core"]),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sinirsiz_kapsam_her_grubu_okuyabiliyor()
    {
        var (store, key, entry) = Seed("network/core");
        var reader = new RawReader(store);

        var record = await reader.ReadAsync(
            entry.ToRawRef(key),
            AccessScope.System("replay"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(record);
    }

    [Theory]
    [InlineData("")]
    [InlineData("anahtar-yok")]
    [InlineData("raw/g/2026/08/16/12/c/x.ndjson.zst")]
    [InlineData("raw/g/x.ndjson.zst#abc:10")]
    [InlineData("raw/g/x.ndjson.zst#10")]
    public void Bozuk_raw_ref_ayristirilmiyor(string rawRef)
    {
        Assert.False(RawReader.TryParseRawRef(rawRef, out _, out _, out _));
    }

    [Fact]
    public void Raw_ref_tur_gidis()
    {
        var key = new RawObjectKey("network/core", Hour, "firewall", "abc").Value;
        var entry = new RawRefEntry(Guid.CreateVersion7(), 4096, 312);

        Assert.True(RawReader.TryParseRawRef(entry.ToRawRef(key), out var parsedKey, out var offset, out var length));
        Assert.Equal(key, parsedKey);
        Assert.Equal(4096, offset);
        Assert.Equal(312, length);
    }

    [Fact]
    public async Task Nesnenin_tamami_okunabiliyor()
    {
        var builder = new RawObjectBuilder();
        for (var i = 0; i < 5; i++)
        {
            var record = new RawRecord
            {
                EventId = Guid.CreateVersion7(),
                ReceivedAt = Hour,
                SourceKey = "fg-01",
                Body = Encoding.UTF8.GetBytes($"satır-{i}"),
            };
            builder.Add(record.EventId, Hour, RawRecordCodec.ToLine(record));
        }

        var built = builder.Build(3);
        var key = new RawObjectKey("network/core", Hour, "firewall", "abc").Value;
        var store = new FakeObjectStore();
        await store.PutAsync(key, built.Compressed, TestContext.Current.CancellationToken);

        var records = await new RawReader(store).ReadObjectAsync(
            key,
            AccessScope.System("replay"),
            TestContext.Current.CancellationToken);

        Assert.Equal(5, records.Count);
        Assert.Equal("satır-4", Encoding.UTF8.GetString(records[4].Body.Span));
    }
}
