using System.Net;
using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.IntegrationTests;

/// <summary>
/// T02 kabul kriterleri. Gerçek ClickHouse'a karşı koşar.
///
/// Buradaki en önemli test <see cref="Baska_grubun_verisi_hicbir_yoldan_donmuyor"/>:
/// kapsam ayrımı (K17) bu üründeki en pahalı hata sınıfı ve tek koruması bu.
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class StorageSchemaTests(DevStackFixture stack) : IAsyncLifetime
{
    private ClickHouseContext _context = null!;
    private EventWriter _writer = null!;
    private EventReader _reader = null!;
    private ChangeEventReader _changeReader = null!;

    public async ValueTask InitializeAsync()
    {
        _context = stack.CreateClickHouseContext();
        _writer = new EventWriter(_context);
        _reader = new EventReader(_context);
        _changeReader = new ChangeEventReader(_context);

        // Gerçek göç dosyaları — testin şemayı kendi kurması, üretimden ayrışmasına yol açardı.
        var migrator = new ClickHouseMigrator(_context);
        await migrator.MigrateAsync(RepoPath("db/clickhouse"), TestContext.Current.CancellationToken);
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

        return dir is null
            ? throw new InvalidOperationException("Depo kökü bulunamadı (Bizigo.sln).")
            : Path.Combine(dir.FullName, relative);
    }

    private static LogEvent Sample(string ownerGroup, string sourceId, string body, DateTimeOffset ts) => new()
    {
        EventId = Guid.NewGuid(),
        Timestamp = ts,
        OwnerGroup = ownerGroup,
        SourceId = sourceId,
        Host = sourceId,
        Vendor = "fortinet",
        Product = "fortigate",
        ParserId = "fortinet.fortigate.traffic",
        ParserVersion = "1.0.0",
        ParseStatus = ParseStatus.Ok,
        SeverityNum = 6,
        OcsfClassUid = 4001,
        SrcIp = IPAddress.Parse("10.0.0.5"),
        DstIp = IPAddress.Parse("8.8.8.8"),
        SrcPort = 51514,
        DstPort = 53,
        Proto = "udp",
        Action = "accept",
        Outcome = "allowed",
        UserName = "mehmet",
        Attrs = new Dictionary<string, string>(StringComparer.Ordinal) { ["policyid"] = "42" },
        Body = body,
        RawRef = "raw/x#0:100",
    };

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Sema_kuruluyor_ve_tekrar_uygulanabiliyor()
    {
        var migrator = new ClickHouseMigrator(_context);

        var result = await migrator.MigrateAsync(RepoPath("db/clickhouse"), TestContext.Current.CancellationToken);

        Assert.Empty(result.Applied);                                  // InitializeAsync zaten uyguladı
        Assert.Contains("0001_events", result.AlreadyApplied);
        Assert.Contains("0002_change_events", result.AlreadyApplied);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Olay_yazilip_geri_okunabiliyor()
    {
        var ts = DateTimeOffset.UtcNow.AddMinutes(-5);
        var sample = Sample("network-core", "fg-ankara-01", "traffic accepted", ts);

        var write = await _writer.WriteEventsAsync([sample], TestContext.Current.CancellationToken);
        Assert.Equal(1, write.RowsWritten);

        var scope = ScopePredicate.From(AccessScope.ForGroups("ali", ["network-core"]));
        var found = await _reader.GetByIdAsync(sample.EventId, scope, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal("fg-ankara-01", found.SourceId);
        Assert.Equal(ParseStatus.Ok, found.ParseStatus);
        Assert.Equal(53, found.DstPort);
        Assert.Equal("42", found.Attrs["policyid"]);
        // IPv4 → ::ffff:a.b.c.d olarak saklanıyor; geri okunduğunda eşdeğer olmalı.
        Assert.Equal(EventWriter.ToV6(IPAddress.Parse("10.0.0.5")), found.SrcIp);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Baska_grubun_verisi_hicbir_yoldan_donmuyor()
    {
        var ts = DateTimeOffset.UtcNow.AddMinutes(-3);
        var mine = Sample("network-core", "fg-1", "benim olayım", ts);
        var theirs = Sample("finans", "fg-2", "onların olayı", ts);

        await _writer.WriteEventsAsync([mine, theirs], TestContext.Current.CancellationToken);

        var scope = ScopePredicate.From(AccessScope.ForGroups("ali", ["network-core"]));
        var query = new EventQuery { From = ts.AddMinutes(-1), To = ts.AddMinutes(1) };

        // 1) Arama
        var page = await _reader.SearchAsync(query, scope, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(page.Events, e => string.Equals(e.OwnerGroup, "finans", StringComparison.Ordinal));

        // 2) Kimlikle doğrudan erişim
        Assert.Null(await _reader.GetByIdAsync(theirs.EventId, scope, TestContext.Current.CancellationToken));

        // 3) Sayım
        var count = await _reader.CountAsync(
            query with { OwnerGroups = ["finans"] }, scope, TestContext.Current.CancellationToken);
        Assert.Equal(0, count);

        // 4) Kapsam dışı SAYISI görünür ama içerik görünmez (RCA §3.2)
        var outOfScope = await _reader.CountOutOfScopeAsync(query, scope, TestContext.Current.CancellationToken);
        Assert.True(outOfScope >= 1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Bos_kapsam_hicbir_satir_dondurmuyor()
    {
        var ts = DateTimeOffset.UtcNow.AddMinutes(-2);
        await _writer.WriteEventsAsync([Sample("network-core", "fg-3", "x", ts)], TestContext.Current.CancellationToken);

        var denied = ScopePredicate.From(AccessScope.Denied);
        var page = await _reader.SearchAsync(
            new EventQuery { From = ts.AddMinutes(-1), To = ts.AddMinutes(1) },
            denied, TestContext.Current.CancellationToken);

        Assert.Empty(page.Events);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("Arayüz kapandı: GigabitEthernet0/1 bağlantısı düştü", "bağlantısı")]
    [InlineData("انقطع الاتصال بالواجهة", "الاتصال")]
    [InlineData("接口连接已断开，请检查线路", "连接")]
    [InlineData("INTERFACE DOWN on port 24", "INTERFACE")]
    public async Task Cok_dilli_govdede_alt_dizi_aramasi_calisiyor(string body, string needle)
    {
        var ts = DateTimeOffset.UtcNow.AddMinutes(-1);
        var sample = Sample("network-core", "sw-1", body, ts);
        await _writer.WriteEventsAsync([sample], TestContext.Current.CancellationToken);

        var scope = ScopePredicate.From(AccessScope.ForGroups("ali", ["network-core"]));
        var page = await _reader.SearchAsync(
            new EventQuery
            {
                From = ts.AddMinutes(-1),
                To = ts.AddMinutes(1),
                FullText = needle,
            },
            scope, TestContext.Current.CancellationToken);

        Assert.Contains(page.Events, e => e.EventId == sample.EventId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Izin_listesinde_olmayan_alan_filtresi_reddediliyor()
    {
        var scope = ScopePredicate.From(AccessScope.ForGroups("ali", ["network-core"]));
        var query = new EventQuery
        {
            From = DateTimeOffset.UtcNow.AddHours(-1),
            To = DateTimeOffset.UtcNow,
            Filters = [new FieldFilter("body; DROP TABLE events --", FilterOperator.Equals, ["x"])],
        };

        // Sessizce yok sayılmıyor: kullanıcı filtresinin uygulandığını sanmamalı.
        await Assert.ThrowsAsync<ArgumentException>(
            () => _reader.SearchAsync(query, scope, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Keyset_sayfalama_tekrar_veya_atlama_yapmiyor()
    {
        var ts = DateTimeOffset.UtcNow.AddMinutes(-30);
        var events = Enumerable.Range(0, 25)
            .Select(i => Sample("network-page", "fg-page", $"olay {i}", ts.AddSeconds(i)))
            .ToArray();
        await _writer.WriteEventsAsync(events, TestContext.Current.CancellationToken);

        var scope = ScopePredicate.From(AccessScope.ForGroups("ali", ["network-page"]));
        var query = new EventQuery
        {
            From = ts.AddMinutes(-1),
            To = ts.AddMinutes(2),
            Limit = 10,
        };

        var seen = new List<Guid>();
        EventCursor? cursor = null;
        for (var page = 0; page < 5; page++)
        {
            var result = await _reader.SearchAsync(
                query with { After = cursor }, scope, TestContext.Current.CancellationToken);
            seen.AddRange(result.Events.Select(e => e.EventId));
            cursor = result.Next;
            if (!result.HasMore)
            {
                break;
            }
        }

        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count());   // tekrar yok
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Degisiklik_olaylari_yazilip_okunabiliyor()
    {
        var ts = DateTimeOffset.UtcNow.AddMinutes(-10);
        var change = new ChangeEvent
        {
            ChangeId = Guid.NewGuid(),
            Timestamp = ts,
            OwnerGroup = "network-core",
            TargetKind = ChangeTargetKind.Device,
            TargetId = "core-sw-02",
            ChangeKind = "acl_change",
            Actor = "m.yilmaz",
            Summary = "ACL push",
            Details = new Dictionary<string, string>(StringComparer.Ordinal) { ["rules"] = "3" },
            Source = "ansible",
        };

        await _writer.WriteChangeEventsAsync([change], TestContext.Current.CancellationToken);

        var scope = ScopePredicate.From(AccessScope.ForGroups("ali", ["network-core"]));
        var found = await _changeReader.SearchAsync(
            new ChangeQuery { From = ts.AddMinutes(-1), To = ts.AddMinutes(1) },
            scope, TestContext.Current.CancellationToken);

        var match = Assert.Single(found, c => c.ChangeId == change.ChangeId);
        Assert.Equal("core-sw-02", match.TargetId);
        Assert.Equal("3", match.Details["rules"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Toplu_yazim_hizi_olculuyor()
    {
        // Kabul kriteri: 1M satır bulk insert ölçülüp CI çıktısında raporlanıyor.
        // CI'da tam 1M pahalı; ölçek burada 100k, oran raporlanıyor.
        const int rowCount = 100_000;
        var ts = DateTimeOffset.UtcNow.AddHours(-2);
        var events = Enumerable.Range(0, rowCount)
            .Select(i => Sample("network-bulk", $"fg-{i % 50}", $"bulk satır {i}", ts.AddMilliseconds(i)))
            .ToArray();

        var result = await _writer.WriteEventsAsync(events, TestContext.Current.CancellationToken);

        Assert.Equal(rowCount, result.RowsWritten);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"bulk insert: {rowCount} satır / {result.Duration.TotalSeconds:F2} sn " +
            $"= {rowCount / Math.Max(result.Duration.TotalSeconds, 0.001):F0} satır/sn");
    }
}
