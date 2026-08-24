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
            [Sample("net-core", "fg-core"), Sample("net-edge", "fg-edge"), .. CorrelationSeed()],
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

    // ---- Korelasyon verisi (T35) -------------------------------------------
    //
    // Mevcut testlerin penceresinin (Now ± 1 saat) DIŞINDA duruyor ve bu bilerek:
    // `Kapsam_disi_sayimi_icerik_sizdirmiyor` kapsam dışı olay sayısının tam 1
    // olduğunu sınıyor. Buraya eklenen her net-edge satırı o sayıyı sessizce
    // değiştirirdi ve testin düştüğü gün sebebi bu dosyada aranmazdı.

    private static readonly DateTimeOffset CorrelationFrom = Now.AddDays(-3);
    private static readonly DateTimeOffset CorrelationTo = Now.AddDays(-3).AddHours(1);
    private static readonly DateTimeOffset BaselineFrom = Now.AddDays(-10);
    private static readonly DateTimeOffset BaselineTo = Now.AddDays(-4);

    private const ulong CoreSignature = 0x00C0_0000_0000_0001;
    private const ulong EdgeSignature = 0x00ED_0000_0000_0002;

    /// <summary>
    /// Her iki grup için de <b>aynı şekli</b> taşıyan veri: tabanda görülmüş bir
    /// imza, pencerede beliren yeni bir imza, ve bozulmuş olaylar.
    ///
    /// <para>
    /// Simetri kasıtlı — kapsam filtresi çalışmazsa her korelasyon <b>iki</b>
    /// sonuç döndürür ve testler tam olarak o ikinciyi arıyor. Yalnızca bir
    /// grubun verisi olsaydı "kapsam çalıştı" ile "veri zaten yoktu" ayırt
    /// edilemezdi.
    /// </para>
    /// </summary>
    private static LogEvent[] CorrelationSeed() =>
    [
        // Tabanda görülmüş imzalar — "ilk kez görüldü" sayılmamalılar.
        Correlated("net-core", "fg-core", CoreSignature, BaselineFrom.AddHours(1), "core-allow"),
        Correlated("net-edge", "fg-edge", EdgeSignature, BaselineFrom.AddHours(1), "edge-deny"),

        // Pencerede: aynı imzalar tekrar (hacim), artı her grup için tabanda
        // hiç olmayan yeni bir imza (ilk-görülen).
        Correlated("net-core", "fg-core", CoreSignature, CorrelationFrom.AddMinutes(5), "core-allow"),
        Correlated("net-edge", "fg-edge", EdgeSignature, CorrelationFrom.AddMinutes(5), "edge-deny"),
        Correlated("net-core", "fg-core", CoreSignature + 100, CorrelationFrom.AddMinutes(10), "core-allow"),
        Correlated("net-edge", "fg-edge", EdgeSignature + 100, CorrelationFrom.AddMinutes(10), "edge-deny"),

        // Bozulmuş olaylar — yayılma (propagation) bunları sıralıyor.
        Degraded("net-core", "fg-core", CorrelationFrom.AddMinutes(20)),
        Degraded("net-edge", "fg-edge", CorrelationFrom.AddMinutes(15)),
    ];

    private static LogEvent Correlated(
        string ownerGroup,
        string sourceId,
        ulong signature,
        DateTimeOffset at,
        string action) => new()
    {
        EventId = Guid.CreateVersion7(at),
        Timestamp = at,
        OwnerGroup = ownerGroup,
        SourceId = sourceId,
        Host = sourceId,
        Body = $"{ownerGroup} korelasyon satırı {signature:X}",
        RawRef = $"raw/{ownerGroup}/2026/08/14/10/default/",
        SrcIp = IPAddress.IPv6Any,
        DstIp = IPAddress.IPv6Any,
        SignatureHash = signature,
        ParseStatus = ParseStatus.Ok,
        TimeSource = TimeSources.Parsed,
        // `action`, `user_name` DEĞİL: lift sorgusu alan adını bir İZİN
        // LİSTESİNDEN alıyor (`source_id, host, vendor, product, parser_id,
        // proto, action, outcome`) ve liste dışı bir ad `ArgumentException`
        // fırlatıyor. `user_name` sıcak kolonlarda var ama listede yok; onu
        // listeye eklemek bir ÜRÜN kararı ve gerekçesi "testim istiyor"
        // olamaz — o yüzden tohum listeye uyduruldu.
        Action = action,
        SeverityNum = 9,
    };

    /// <summary>Ayrıştırması düşmüş olay: yayılma sorgusunun "bozulma" tanımı.</summary>
    private static LogEvent Degraded(string ownerGroup, string sourceId, DateTimeOffset at) => new()
    {
        EventId = Guid.CreateVersion7(at),
        Timestamp = at,
        OwnerGroup = ownerGroup,
        SourceId = sourceId,
        Host = sourceId,
        Body = $"{ownerGroup} bozulmus satir",
        RawRef = $"raw/{ownerGroup}/2026/08/14/10/default/",
        SrcIp = IPAddress.IPv6Any,
        DstIp = IPAddress.IPv6Any,
        ParseStatus = ParseStatus.Failed,
        TimeSource = TimeSources.Parsed,
        SeverityNum = 3,
    };

    private static CorrelationWindow CorrelationSpan() => new()
    {
        From = CorrelationFrom,
        To = CorrelationTo,
        BaselineFrom = BaselineFrom,
        BaselineTo = BaselineTo,
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
    // ---- T35 korelasyonları -------------------------------------------------
    //
    // Beşi de T35 ile geldi, T36'nın kanıt paketi onları tüketiyor, ve
    // hiçbirinin kapsam negatif testi YOKTU. Bedeli bu deponun en pahalı
    // sınıfından: kapsam dışı bir imza "ilk kez görüldü" diye rapora düşse
    // hata da sayaç da belirti de olmaz — kullanıcı **bir sinyalin yokluğunu
    // bulgu sanar**.

    /// <summary>
    /// Koşturulduğunda kanıtladığı şey: başka grubun tabanda hiç görülmemiş
    /// imzası, kapsam altındaki "ilk kez görüldü" listesine <b>girmiyor</b>.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: GetFirstSeenSignaturesAsync
    public async Task Ilk_gorulen_imza_baska_grubunkini_getirmiyor()
    {
        var mine = await _query.GetFirstSeenSignaturesAsync(
            CorrelationSpan(), CoreOnly(), 50, TestContext.Current.CancellationToken);

        // Kendi yeni imzam görünüyor: "kapsam çalıştı" ile "sorgu hiçbir şey
        // bulamadı" ayırt edilebilsin diye pozitif taraf da sınanıyor.
        Assert.Contains(mine, s => s.SignatureHash == CoreSignature + 100);

        Assert.DoesNotContain(mine, s => s.SignatureHash == EdgeSignature + 100);
        Assert.DoesNotContain(mine, s => s.SignatureHash == EdgeSignature);
    }

    /// <summary>
    /// Koşturulduğunda kanıtladığı şey: hacim sayımları başka grubun
    /// satırlarını <b>toplamıyor</b>. Sızıntı burada iki yönlü zarar verir —
    /// hem sahte bir imza görünür, hem kendi imzamın sayısı şişer.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: GetSignatureVolumeAsync
    public async Task Imza_hacmi_baska_grubun_satirlarini_saymiyor()
    {
        var mine = await _query.GetSignatureVolumeAsync(
            CorrelationSpan(), CoreOnly(), 50, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(mine, v => v.SignatureHash == EdgeSignature);

        var core = Assert.Single(mine, v => v.SignatureHash == CoreSignature);

        // Pencerede bir, tabanda bir — net-edge'in aynı şekildeki satırları
        // sayıya karışsaydı ikisi de iki olurdu.
        Assert.Equal(1, core.WindowCount);
        Assert.Equal(1, core.BaselineCount);
    }

    /// <summary>
    /// Koşturulduğunda kanıtladığı şey: alan değeri sayımları kapsam dışı
    /// değerleri <b>listelemiyor</b>. Sızıntının belirtisi burada özellikle
    /// sinsi: başka ekibin kullanıcı adı, benim raporumda "öne çıkan değer"
    /// olarak görünürdü.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: GetAttributeLiftAsync
    public async Task Alan_degeri_sayimi_baska_grubun_degerini_gostermiyor()
    {
        var mine = await _query.GetAttributeLiftAsync(
            CorrelationSpan(), CoreOnly(), ["action"], 50, TestContext.Current.CancellationToken);

        Assert.Contains(mine, v => v.Value == "core-allow");
        Assert.DoesNotContain(mine, v => v.Value == "edge-deny");
    }

    /// <summary>
    /// Koşturulduğunda kanıtladığı şey: yayılma sıralaması başka grubun
    /// kaynağını <b>içermiyor</b>.
    ///
    /// <para>
    /// Bu, beşi arasında sızıntısı en çok yanıltan olanı: <c>fg-edge</c>
    /// tohumda <b>daha erken</b> bozuluyor, yani sızsaydı "önce edge bozuldu,
    /// sonra core" diye okunan bir <b>nedensellik</b> üretirdi.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: GetPropagationAsync
    public async Task Yayilma_baska_grubun_kaynagini_siralamiyor()
    {
        var mine = await _query.GetPropagationAsync(
            CorrelationSpan(), CoreOnly(), severityAtOrBelow: 5, 50, TestContext.Current.CancellationToken);

        Assert.All(mine, onset => Assert.Equal("net-core", onset.OwnerGroup));
        Assert.DoesNotContain(mine, onset => onset.SourceId == "fg-edge");
        Assert.Contains(mine, onset => onset.SourceId == "fg-core");
    }

    /// <summary>
    /// Koşturulduğunda kanıtladığı şey: histogram kovaları kapsam dışı olayları
    /// <b>saymıyor</b>. Alarm önizlemesinin tek sorgusu bu; şişmiş bir kova
    /// kullanıcıya var olmayan bir yük gösterir ve eşiği ona göre seçtirir.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: GetEventHistogramAsync
    public async Task Histogram_kapsam_disi_olaylari_saymiyor()
    {
        var query = new EventHistogramQuery
        {
            From = CorrelationFrom,
            To = CorrelationTo,
            BucketSeconds = 3600,
        };

        var mine = await _query.GetEventHistogramAsync(
            query, CoreOnly(), TestContext.Current.CancellationToken);

        var all = await _query.GetEventHistogramAsync(
            query, AccessScope.System("admin"), TestContext.Current.CancellationToken);

        // Tohum simetrik: her grup pencerede aynı sayıda satır taşıyor.
        Assert.Equal(all.Sum(b => b.Count) / 2, mine.Sum(b => b.Count));
    }

    /// <summary>
    /// Koşturulduğunda kanıtladığı şey: kapsam dışı <b>değişiklik</b> sayımı da
    /// sayı veriyor, içerik değil — ve saydığı şey gerçekten kapsam dışı olan.
    ///
    /// <para>
    /// Olay tarafının eşdeğeri (<c>CountOutOfScopeEventsAsync</c>) sınanıyordu,
    /// bu sınanmıyordu. İki uç aynı vaadi veriyor ve yalnızca birinin
    /// doğrulanması, ikisinin de doğrulandığı izlenimi bırakıyordu.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: CountOutOfScopeChangesAsync
    public async Task Kapsam_disi_degisiklik_sayimi_icerik_sizdirmiyor()
    {
        await _query.WriteChangeAsync(
            NewChange("net-edge"), AccessScope.System("admin"), TestContext.Current.CancellationToken);
        await _query.WriteChangeAsync(
            NewChange("net-core"), CoreOnly(), TestContext.Current.CancellationToken);

        var query = new ChangeQuery { From = Now.AddHours(-1), To = Now.AddHours(1) };

        var outside = await _query.CountOutOfScopeChangesAsync(
            query, CoreOnly(), TestContext.Current.CancellationToken);

        Assert.Equal(1, outside);

        // Ve içerik gerçekten dönmüyor.
        var visible = await _query.SearchChangesAsync(
            query, CoreOnly(), TestContext.Current.CancellationToken);

        Assert.All(visible, c => Assert.Equal("net-core", c.OwnerGroup));
    }

    private static ChangeEvent NewChange(string ownerGroup) => new()
    {
        ChangeId = Guid.CreateVersion7(),
        Timestamp = Now,
        OwnerGroup = ownerGroup,
        TargetKind = ChangeTargetKind.Device,
        TargetId = ownerGroup == "net-core" ? "fg-core" : "fg-edge",
        ChangeKind = "config",
    };

    /// <summary>
    /// Koşturulduğunda kanıtladığı şey: ham nesne okuma kapısı grubu
    /// <b>anahtardan</b> okuyor ve kapsam dışı anahtarı reddediyor.
    ///
    /// <para>
    /// Grubun nesne anahtarının içinde durmasının tek sebebi bu kontrol (F1
    /// §7.1): karar nesne <b>indirilmeden önce</b> verilebiliyor. Kapı yanlış
    /// çalışsaydı sızan şey bir satır değil, ham arşivin tamamı olurdu.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: CanReadRawObjectAsync
    public async Task Ham_nesne_kapisi_baska_grubun_anahtarini_reddediyor()
    {
        const string mine = "raw/net-core/2026/08/17/10/default/01J0.ndjson.zst";
        const string theirs = "raw/net-edge/2026/08/17/10/default/01J1.ndjson.zst";

        Assert.True(await _query.CanReadRawObjectAsync(
            mine, CoreOnly(), TestContext.Current.CancellationToken));

        Assert.False(await _query.CanReadRawObjectAsync(
            theirs, CoreOnly(), TestContext.Current.CancellationToken));

        // Boş kapsam kendi grubunu da göremiyor: kapalı başlıyor.
        Assert.False(await _query.CanReadRawObjectAsync(
            mine, AccessScope.Denied, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Koşturulduğunda kanıtladığı şey: <b>grubu çözülemeyen</b> anahtar da
    /// reddediliyor.
    ///
    /// <para>
    /// Kapsam kapısının en sessiz kaçış deliği bu — anahtarın <i>biçimini
    /// bozarak</i> geçmek. Kod bugün doğru davranıyor (<c>ExtractOwnerGroup</c>
    /// <c>null</c> dönüyor, kapı <c>false</c> veriyor) ama davranışı hiçbir şey
    /// tutmuyordu: "belli değil" ile "serbest"in ayrıldığı yer yalnızca bir
    /// <c>null</c> kontrolüydü ve o kontrol bir gün gevşetilse hiçbir yerde
    /// kırmızı yanmazdı.
    /// </para>
    ///
    /// <para>
    /// Varsayılanın <b>ret</b> olması, <c>AccessScope</c>'un "kapalı başlar"
    /// kuralının bu kapıdaki karşılığı: tanınmayan bir anahtar, kapsamı
    /// bilinmeyen bir nesne demek ve kapsamı bilinmeyen nesne okunmaz.
    /// </para>
    /// </summary>
    [Theory]
    [Trait("Category", "Integration")]
    // kapsam: CanReadRawObjectAsync
    [InlineData("baska/net-core/2026/08/17/10/default/01J0.ndjson.zst")]  // önek yanlış
    [InlineData("raw")]                                                   // grup segmenti yok
    [InlineData("raw/")]                                                  // grup boş
    [InlineData("net-core/2026/08/17/10/default/01J0.ndjson.zst")]        // önek hiç yok
    public async Task Cozulemeyen_anahtar_reddediliyor(string objectKey)
    {
        Assert.False(await _query.CanReadRawObjectAsync(
            objectKey, CoreOnly(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Koşturulduğunda kanıtladığı şey: kapsam dışı sayım, kullanıcının
    /// <b>kendi daraltmasına</b> bakmıyor.
    ///
    /// <para>
    /// Karar <c>ScopedQuery</c>'de yalnızca yorumda duruyordu: soru <i>"senin
    /// kapsamının dışında ne var"</i>, <i>"daraltmanın dışında ne var"</i>
    /// değil. Daraltma uygulansaydı sayı, kullanıcı listeyi <b>kendi</b>
    /// daralttığı için büyürdü ve rapor bunu "başka grubun verisi" diye
    /// gösterirdi — cümle aynı, anlamı farklı, ve hiçbir şey kırmızı yanmaz.
    /// </para>
    ///
    /// <para>
    /// İki uç da (olay ve değişiklik) aynı vaadi veriyor, o yüzden ikisi de
    /// burada.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    // kapsam: CountOutOfScopeEventsAsync
    // kapsam: CountOutOfScopeChangesAsync
    public async Task Kapsam_disi_sayimi_kullanicinin_kendi_daraltmasina_bakmiyor()
    {
        // Her iki gruba da erişimi olan kullanıcı: kapsamının dışında hiçbir
        // şey YOK.
        var both = AccessScope.ForGroups("u-both", ["net-core", "net-edge"]);

        await _query.WriteChangeAsync(
            NewChange("net-edge"), AccessScope.System("admin"), TestContext.Current.CancellationToken);

        // Sorguda kendini net-core'a daraltıyor.
        var narrowedEvents = AllEvents() with { OwnerGroups = ["net-core"] };
        var narrowedChanges = new ChangeQuery
        {
            From = Now.AddHours(-1),
            To = Now.AddHours(1),
            OwnerGroups = ["net-core"],
        };

        // Daraltma uygulansaydı ikisi de 1 dönerdi: "kapsam dışında bir şey var".
        Assert.Equal(0, await _query.CountOutOfScopeEventsAsync(
            narrowedEvents, both, TestContext.Current.CancellationToken));

        Assert.Equal(0, await _query.CountOutOfScopeChangesAsync(
            narrowedChanges, both, TestContext.Current.CancellationToken));
    }
}

/// <summary>Denetim kaydı bu testlerin konusu değil; ayrı testleri var.</summary>
internal sealed class NoOpAuditSink : IAuditSink
{
    public Task RecordAsync(AuditRecord record, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
