using System.Net;
using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.IntegrationTests;

/// <summary>
/// T11'in ClickHouse tarafı: gölge tablo ve <c>REPLACE PARTITION</c>.
///
/// <para>
/// Buradaki en önemli test <see cref="Filtre_disi_satirlar_kopyalanmazsa_kayboluyor"/>:
/// filtreli replay'in en kolay gözden kaçan tuzağını <b>gösteriyor</b>.
/// <c>REPLACE PARTITION</c> bölümün tamamını değiştiriyor, dolayısıyla gölge
/// tabloya kopyalanmayan satır sessizce siliniyor. Testin işi bu davranışı
/// belgelemek — motorun kopyalama adımı da bu yüzden var.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class ReplayStoreTests(DevStackFixture stack) : IAsyncLifetime
{
    private static readonly DateTimeOffset Day = new(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);
    private const string Partition = "20260817";

    private ClickHouseContext _context = null!;
    private EventWriter _writer = null!;
    private ReplayStore _replay = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await stack.CreateIsolatedClickHouseContextAsync(TestContext.Current.CancellationToken);
        await new ClickHouseMigrator(_context).MigrateAsync(
            RepoPath("db/clickhouse"), TestContext.Current.CancellationToken);

        _writer = new EventWriter(_context);
        _replay = new ReplayStore(_context);
    }

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bizigo.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, relative);
    }

    private static LogEvent Event(string ownerGroup, string action, int offsetSeconds = 0) => new()
    {
        EventId = Guid.CreateVersion7(Day.AddSeconds(offsetSeconds)),
        Timestamp = Day.AddSeconds(offsetSeconds),
        OwnerGroup = ownerGroup,
        SourceId = "fg-01",
        Host = "fw-01",
        ParseStatus = ParseStatus.Ok,
        Action = action,
        SrcIp = IPAddress.IPv6Any,
        DstIp = IPAddress.IPv6Any,
        Body = "satır",
    };

    private Task<string> ScalarAsync(string sql) => stack.QueryScalarAsync(
        _context.Options.ConnectionString, sql, TestContext.Current.CancellationToken);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Bolumler_ve_satir_sayilari_listeleniyor()
    {
        await _writer.WriteEventsAsync(
            [Event("net-core", "accept"), Event("net-core", "deny", 1)],
            TestContext.Current.CancellationToken);

        var partitions = await _replay.ListPartitionsAsync(
            Day.AddDays(-1), Day.AddDays(1), TestContext.Current.CancellationToken);

        var partition = Assert.Single(partitions);
        Assert.Equal(Partition, partition.Partition);
        Assert.Equal(2, partition.Rows);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Golge_tablo_events_ile_ayni_yapida()
    {
        await _replay.CreateShadowAsync("events_replay_test", TestContext.Current.CancellationToken);

        // `CREATE TABLE … AS` kullanılıyor: şema elle tekrarlanırsa bir kolon
        // eklendiği gün replay sessizce eksik yazar.
        var columns = await ScalarAsync(
            "SELECT count() FROM system.columns WHERE table = 'events_replay_test'");
        var expected = await ScalarAsync(
            "SELECT count() FROM system.columns WHERE table = 'events'");

        Assert.Equal(expected, columns);

        await _replay.DropShadowAsync("events_replay_test", TestContext.Current.CancellationToken);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Bolum_atomik_olarak_degistiriliyor()
    {
        var original = Event("net-core", "accept");
        await _writer.WriteEventsAsync([original], TestContext.Current.CancellationToken);

        const string shadow = "events_replay_20260817";
        await _replay.CreateShadowAsync(shadow, TestContext.Current.CancellationToken);

        var replacement = original with { Action = "deny", ParseGeneration = 2 };
        await _writer.WriteEventsToAsync(shadow, [replacement], TestContext.Current.CancellationToken);

        await _replay.ReplacePartitionAsync(shadow, Partition, TestContext.Current.CancellationToken);
        await _replay.DropShadowAsync(shadow, TestContext.Current.CancellationToken);

        Assert.Equal("deny", await ScalarAsync("SELECT action FROM events LIMIT 1"));
        Assert.Equal("2", await ScalarAsync("SELECT parse_generation FROM events LIMIT 1"));
        Assert.Equal("1", await ScalarAsync("SELECT count() FROM events"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Filtre_disi_satirlar_kopyalanmazsa_kayboluyor()
    {
        // Bu testin işi doğru davranışı değil, TUZAĞI belgelemek.
        await _writer.WriteEventsAsync(
            [Event("net-core", "accept"), Event("net-edge", "accept", 1)],
            TestContext.Current.CancellationToken);

        const string shadow = "events_replay_kismi";
        await _replay.CreateShadowAsync(shadow, TestContext.Current.CancellationToken);

        // YALNIZCA net-core yazılıyor — net-edge kopyalanmıyor.
        await _writer.WriteEventsToAsync(
            shadow, [Event("net-core", "deny")], TestContext.Current.CancellationToken);

        await _replay.ReplacePartitionAsync(shadow, Partition, TestContext.Current.CancellationToken);
        await _replay.DropShadowAsync(shadow, TestContext.Current.CancellationToken);

        // net-edge satırı GİTTİ. Motorun kopyalama adımı tam da bunu önlüyor.
        Assert.Equal("1", await ScalarAsync("SELECT count() FROM events"));
        Assert.Equal("0", await ScalarAsync("SELECT count() FROM events WHERE owner_group = 'net-edge'"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Filtre_disi_satirlar_kopyalaninca_korunuyor()
    {
        var core = Event("net-core", "accept");
        var edge = Event("net-edge", "accept", 1);
        await _writer.WriteEventsAsync([core, edge], TestContext.Current.CancellationToken);

        const string shadow = "events_replay_tam";
        await _replay.CreateShadowAsync(shadow, TestContext.Current.CancellationToken);

        // Filtre dışı satır DEĞİŞTİRİLMEDEN kopyalanıyor — motorun yaptığı bu.
        await _writer.WriteEventsToAsync(
            shadow,
            [core with { Action = "deny" }, edge],
            TestContext.Current.CancellationToken);

        await _replay.ReplacePartitionAsync(shadow, Partition, TestContext.Current.CancellationToken);
        await _replay.DropShadowAsync(shadow, TestContext.Current.CancellationToken);

        Assert.Equal("2", await ScalarAsync("SELECT count() FROM events"));
        Assert.Equal("accept", await ScalarAsync(
            "SELECT action FROM events WHERE owner_group = 'net-edge' LIMIT 1"));
        Assert.Equal("deny", await ScalarAsync(
            "SELECT action FROM events WHERE owner_group = 'net-core' LIMIT 1"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ayni_replay_iki_kez_calistirilabiliyor()
    {
        var original = Event("net-core", "accept");
        await _writer.WriteEventsAsync([original], TestContext.Current.CancellationToken);

        const string shadow = "events_replay_idempotent";

        for (var run = 0; run < 2; run++)
        {
            // Eski gölge kalıntısı düşürülüyor — ikinci koşu birinciyi görmemeli.
            await _replay.DropShadowAsync(shadow, TestContext.Current.CancellationToken);
            await _replay.CreateShadowAsync(shadow, TestContext.Current.CancellationToken);
            await _writer.WriteEventsToAsync(
                shadow, [original with { Action = "deny" }], TestContext.Current.CancellationToken);
            await _replay.ReplacePartitionAsync(shadow, Partition, TestContext.Current.CancellationToken);
        }

        await _replay.DropShadowAsync(shadow, TestContext.Current.CancellationToken);

        // İki koşu, tek satır: replay çoğaltmıyor.
        Assert.Equal("1", await ScalarAsync("SELECT count() FROM events"));
        Assert.Equal("deny", await ScalarAsync("SELECT action FROM events LIMIT 1"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Bolum_okunabiliyor()
    {
        await _writer.WriteEventsAsync(
            [Event("net-core", "accept"), Event("net-edge", "deny", 1)],
            TestContext.Current.CancellationToken);

        var rows = await _replay.ReadPartitionAsync(Partition, TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.OwnerGroup == "net-edge" && r.Action == "deny");
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("events; DROP TABLE events")]
    [InlineData("events`")]
    [InlineData("events-1")]
    public async Task Gecersiz_tablo_adi_reddediliyor(string name)
    {
        // Tablo adları parametreleştirilemiyor; beyaz liste tek koruma.
        await Assert.ThrowsAsync<ArgumentException>(
            () => _replay.CreateShadowAsync(name, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Gecersiz_bolum_reddediliyor()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _replay.ReplacePartitionAsync("events_replay_x", "2026; DROP", TestContext.Current.CancellationToken));
    }
}
