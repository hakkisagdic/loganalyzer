using System.Net;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>
/// T10 kabul kriteri: <b>her uç için "başka grubun verisi" testi</b>.
///
/// <para>
/// Testler HTTP katmanının değil <see cref="IScopedQuery"/>'nin üstünde koşuyor,
/// çünkü zorlamanın gerçekten yaşadığı yer orası — uçlar yalnızca oraya
/// devrediyor ve mimari testler başka bir yolu zaten kapatıyor. Bir uç eklenip
/// kapıyı atlarsa <c>ApiSurfaceTests</c> yakalıyor; kapıdan geçip yanlış veri
/// dönerse buradaki testler yakalıyor.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class ScopeNegativeTests(DevStackFixture stack) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);

    private ClickHouseContext _context = null!;
    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;
    private IScopedQuery _query = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await stack.CreateIsolatedClickHouseContextAsync(TestContext.Current.CancellationToken);
        await new ClickHouseMigrator(_context).MigrateAsync(
            RepoPath("db/clickhouse"), TestContext.Current.CancellationToken);

        _factory = new ControlPlaneFactory(stack.PostgresConnectionString);
        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await db.Sources.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        db.Sources.AddRange(
            new SourceEntity { SourceId = "fg-core", OwnerGroup = "net-core", PeerAddress = "10.0.0.1" },
            new SourceEntity { SourceId = "fg-edge", OwnerGroup = "net-edge", PeerAddress = "10.0.0.2" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var writer = new EventWriter(_context);
        await writer.WriteEventsAsync(
            [Sample("net-core", "fg-core"), Sample("net-edge", "fg-edge")],
            TestContext.Current.CancellationToken);

        _query = new ScopedQuery(
            new EventReader(_context),
            new ChangeEventReader(_context),
            new CorrelationReader(_context),
            writer,
            await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken),
            new NoOpAuditSink());
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

    private static LogEvent Sample(string ownerGroup, string sourceId) => new()
    {
        EventId = Guid.CreateVersion7(Now),
        Timestamp = Now,
        OwnerGroup = ownerGroup,
        SourceId = sourceId,
        Host = sourceId,
        Body = $"{ownerGroup} grubuna ait satır",
        RawRef = $"raw/{ownerGroup}/2026/08/17/10/default/",
        SrcIp = IPAddress.IPv6Any,
        DstIp = IPAddress.IPv6Any,
    };

    private static EventQuery AllEvents() => new()
    {
        From = Now.AddHours(-1),
        To = Now.AddHours(1),
    };

    private static AccessScope CoreOnly() => AccessScope.ForGroups("u-core", ["net-core"]);

    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: SearchEventsAsync
    public async Task Arama_baska_grubun_olayini_dondurmuyor()
    {
        var page = await _query.SearchEventsAsync(AllEvents(), CoreOnly(), TestContext.Current.CancellationToken);

        Assert.NotEmpty(page.Events);
        Assert.All(page.Events, e => Assert.Equal("net-core", e.OwnerGroup));
    }

    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: SearchEventsAsync
    public async Task Kimlikli_ama_eslemesiz_kullanici_hicbir_sey_gormuyor()
    {
        // Boş kapsam "her şey" değil "hiçbir şey" — eşleme tablosu boşken
        // kullanıcı veri göremez.
        var page = await _query.SearchEventsAsync(
            AllEvents(),
            AccessScope.ForGroups("u-yok", []),
            TestContext.Current.CancellationToken);

        Assert.Empty(page.Events);
    }

    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: GetEventAsync
    public async Task Baska_grubun_olayi_kimlikle_de_okunamiyor()
    {
        var edge = (await _query.SearchEventsAsync(
            AllEvents(),
            AccessScope.System("admin"),
            TestContext.Current.CancellationToken))
            .Events.Single(e => e.OwnerGroup == "net-edge");

        var found = await _query.GetEventAsync(
            edge.EventId, CoreOnly(), TestContext.Current.CancellationToken);

        // 403 değil "yok": varlığını sızdırmak da bilgi sızdırmaktır.
        Assert.Null(found);
    }

    /// <summary>
    /// OCSF/OTel görünümleri de aynı kapıdan geçiyor (T16).
    ///
    /// <para>
    /// Görünümler <c>events</c> tablosunun <b>şeklini</b> değiştiriyor, yetkisini
    /// değil. Kapsam filtresi görünüme gömülmedi — gömülseydi kapsam iki yerde
    /// tanımlanmış olurdu. Bu test o kararın bedelini ödeyip ödemediğimizi
    /// ölçüyor: filtre gerçekten <see cref="IScopedQuery"/> tarafında uygulanıyor mu.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(EventViewKind.Ocsf)]
    [InlineData(EventViewKind.Otel)]
    [Trait("Category", "Integration")]
    // kapsam: GetEventViewAsync
    public async Task Baska_grubun_olayi_gorunumden_de_okunamiyor(EventViewKind view)
    {
        var edge = (await _query.SearchEventsAsync(
            AllEvents(),
            AccessScope.System("admin"),
            TestContext.Current.CancellationToken))
            .Events.Single(e => e.OwnerGroup == "net-edge");

        var fields = await _query.GetEventViewAsync(
            edge.EventId, view, CoreOnly(), TestContext.Current.CancellationToken);

        Assert.Empty(fields);
    }

    /// <summary>
    /// Kendi olayının görünümü okunabiliyor ve alan adları <b>görünümden</b>
    /// geliyor — API'de ikinci bir eşleme kopyası yok.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: GetEventViewAsync
    public async Task Kendi_olayinin_OCSF_ve_OTel_gorunumu_okunabiliyor()
    {
        var core = (await _query.SearchEventsAsync(
            AllEvents(), CoreOnly(), TestContext.Current.CancellationToken)).Events.Single();

        var ocsf = await _query.GetEventViewAsync(
            core.EventId, EventViewKind.Ocsf, CoreOnly(), TestContext.Current.CancellationToken);
        var otel = await _query.GetEventViewAsync(
            core.EventId, EventViewKind.Otel, CoreOnly(), TestContext.Current.CancellationToken);

        // Adlar `db/clickhouse/0003_ocsf_otel_views.sql` içindeki görünümden
        // doğuyor. Burada birkaçını sabitlemek, bir gün görünüm değişip ekranın
        // sessizce boşalmasını engelliyor.
        Assert.Contains(ocsf, f => f.Name == "class_uid");
        Assert.Contains(ocsf, f => f.Name == "connection_info_protocol_name");
        Assert.Contains(otel, f => f.Name == "host.name");
        Assert.Contains(otel, f => f.Name == "SeverityNumber");

        // `owner_group` iki görünümde de taşınıyor; kapsam filtresinin dayandığı
        // kolon bu.
        Assert.Contains(ocsf, f => f.Name == "owner_group" && f.Value == "net-core");
        Assert.Contains(otel, f => f.Name == "owner_group" && f.Value == "net-core");

        // Harita kolonu tip adı değil, okunabilir bir metin dönüyor.
        Assert.Contains(ocsf, f => f.Name == "unmapped");
    }

    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: SearchEventsAsync
    public async Task Kapsam_daraltmasi_kapsami_genisletemiyor()
    {
        // Kullanıcı sorguda başka bir grup isterse bu bir DARALTMA denemesidir;
        // genişletme olarak yorumlanamaz.
        var query = AllEvents() with { OwnerGroups = ["net-edge"] };

        var page = await _query.SearchEventsAsync(query, CoreOnly(), TestContext.Current.CancellationToken);

        Assert.Empty(page.Events);
    }

    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: SearchSourcesAsync
    public async Task Envanter_baska_grubun_kaynagini_gostermiyor()
    {
        var sources = await _query.SearchSourcesAsync(CoreOnly(), TestContext.Current.CancellationToken);

        Assert.Equal(["fg-core"], sources.Select(s => s.SourceId));
    }

    /// <summary>
    /// "Son görülme" de kapsamlı (T17).
    ///
    /// <para>
    /// Envanter listesi kapsamlı olsa bile etkinlik sorgusu olmasaydı, bir ekip
    /// başka bir ekibin cihazının <b>ne zaman veri gönderdiğini</b> öğrenirdi —
    /// yani cihazın varlığını ve çalışma düzenini. Envanterin gizlediği bilgiyi
    /// etkinlik ucunun sızdırması, kapsam ayrımını arka kapıdan delmek olurdu.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: GetSourceActivityAsync
    public async Task Son_gorulme_baska_grubun_kaynagini_sizdirmiyor()
    {
        var window = new SourceActivityWindow { From = Now.AddHours(-1), To = Now.AddHours(1) };

        var mine = await _query.GetSourceActivityAsync(
            window, CoreOnly(), TestContext.Current.CancellationToken);

        Assert.Equal(["fg-core"], mine.Select(r => r.SourceId));
        Assert.All(mine, r => Assert.Equal("net-core", r.OwnerGroup));

        // Yönetici ikisini de görüyor — testin diğer yarısı: yukarıdaki liste
        // filtre çalıştığı için tek satır, veri olmadığı için değil.
        var all = await _query.GetSourceActivityAsync(
            window, AccessScope.System("admin"), TestContext.Current.CancellationToken);

        Assert.Equal(2, all.Count);
    }

    /// <summary>
    /// Kapsam daraltması etkinlik sorgusunda da kapsamı <b>genişletemiyor</b>.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: GetSourceActivityAsync
    public async Task Son_gorulme_daraltmasi_kapsami_genisletemiyor()
    {
        var window = new SourceActivityWindow
        {
            From = Now.AddHours(-1),
            To = Now.AddHours(1),
            OwnerGroups = ["net-edge"],
        };

        var rows = await _query.GetSourceActivityAsync(
            window, CoreOnly(), TestContext.Current.CancellationToken);

        Assert.Empty(rows);
    }

    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: WriteChangeAsync
    public async Task Baska_gruba_degisiklik_olayi_yazilamiyor()
    {
        var change = new ChangeEvent
        {
            ChangeId = Guid.CreateVersion7(),
            Timestamp = Now,
            OwnerGroup = "net-edge",
            TargetKind = ChangeTargetKind.Device,
            TargetId = "fg-edge",
            ChangeKind = "config",
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _query.WriteChangeAsync(change, CoreOnly(), TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: SearchChangesAsync
    public async Task Kendi_grubuna_degisiklik_olayi_yazilabiliyor()
    {
        var change = new ChangeEvent
        {
            ChangeId = Guid.CreateVersion7(),
            Timestamp = Now,
            OwnerGroup = "net-core",
            TargetKind = ChangeTargetKind.Device,
            TargetId = "fg-core",
            ChangeKind = "config",
        };

        await _query.WriteChangeAsync(change, CoreOnly(), TestContext.Current.CancellationToken);

        var changes = await _query.SearchChangesAsync(
            new ChangeQuery { From = Now.AddHours(-1), To = Now.AddHours(1) },
            CoreOnly(),
            TestContext.Current.CancellationToken);

        Assert.Contains(changes, c => c.ChangeId == change.ChangeId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: CountOutOfScopeEventsAsync
    public async Task Kapsam_disi_sayimi_icerik_sizdirmiyor()
    {
        // "Kapsamınız dışında N ilişkili olay var" — sayı veriliyor, içerik değil.
        var count = await _query.CountOutOfScopeEventsAsync(
            AllEvents(), CoreOnly(), TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
    }
}

/// <summary>Denetim kaydı bu testlerin konusu değil; ayrı testleri var.</summary>
internal sealed class NoOpAuditSink : IAuditSink
{
    public Task RecordAsync(AuditRecord record, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
