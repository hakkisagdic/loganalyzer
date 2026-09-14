using System.Net;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Contracts.Security;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Mcp;
using Bizigo.Mcp.Product.Tools;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>M10'un üç aracı GERÇEK depolamanın üstünde</b> — birim paketinin
/// kanıtlayamadığı üç şey.
///
/// <para>
/// Birim testleri (<c>McpReadToolTests</c>) gövdeleri sahte bir
/// <c>IScopedQuery</c> ve <b>bellek içi</b> bir EF sağlayıcısıyla ölçüyor ve
/// oradaki iddialar geçerli: kapsam geçiriliyor mu, redaksiyon koşuyor mu,
/// kesilme görünüyor mu. Üç şeyi <b>ölçemiyorlar</b>, ve üçü de bu depoda bir
/// kez ısırmış sınıflardan:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Kapsam filtresinin SQL'e çevrilmesi.</b> Bellek içinde
/// <c>groups.Contains(w.OwnerGroup)</c> LINQ olarak <i>değerlendiriliyor</i>;
/// Postgres'te <c>= ANY(@groups)</c>'a <b>çevrilmek</b> zorunda. Çeviri
/// kaybolsa bellek içi paket <b>yeşil kalır</b> — T38'de tam olarak bu boşluk
/// ölçüldü ve <c>ScopeNegativeTests</c> onu kapatmak için genişletildi.
/// </item>
/// <item>
/// <b><c>rca.quality</c>'nin ikinci sorgusunun çevrilmesi.</b>
/// <c>ReasoningQualityAsync</c> içindeki <i>"daha yenisi yok"</i> ifadesi
/// (<c>!db.RcaReports.Any(other =&gt; …)</c>) ilişkisel bir alt sorguya
/// çevriliyor. Bellek içi sağlayıcı onu da yürütür, ve <b>yürüttüğü için</b>
/// çevrilemez bir hâle düşse fark edilmezdi.
/// </item>
/// <item>
/// <b><c>logs.get</c>'in zinciri.</b> ClickHouse → <c>EventReader</c> →
/// <c>ScopedQuery</c> → araç → <see cref="RedactedPrompt"/> → tel. Sahte sorgu
/// bu zincirin ilk üç halkasını atlıyor, ve <c>Body</c> kolonunun gerçekten
/// okunduğu yer orası.
/// </item>
/// </list>
///
/// <para>
/// ⚠️ <b>§2 — BU TESTLER BU TURDA KOŞTURULMADI.</b> Docker açık ve §2 ajanın
/// entegrasyon testi koşturmasına yalnızca Docker kapalıyken izin veriyor;
/// koşum koordinatörde. <b>Yeşil gösterilmiyor</b>: aşağıdaki iddiaların hiçbiri
/// bugün ölçülmüş değil, yazılmış.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class McpProductToolChainTests(DevStackFixture stack) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 15, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Olayın gövdesindeki sır — <b>tanınabilir</b> ve redaksiyon kapısının C
    /// katmanının gördüğü biçimde (<c>anahtar değer</c>).
    /// </summary>
    private const string Secret = "Xk7Qm2Rv9Tz4Lw8Yb3Nc";

    private static readonly Guid CoreEventId = Guid.CreateVersion7(Now);
    private static readonly Guid EdgeEventId = Guid.CreateVersion7(Now.AddSeconds(1));

    private ClickHouseContext _context = null!;
    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;
    private IServiceScopeFactory _scopes = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static AccessScope Core => AccessScope.ForGroups("u-core", ["net-core"]);

    public async ValueTask InitializeAsync()
    {
        _context = await stack.CreateIsolatedClickHouseContextAsync(Token);
        await new ClickHouseMigrator(_context).MigrateAsync(RepoPath("db/clickhouse"), Token);

        _factory = new ControlPlaneFactory(stack.PostgresConnectionString);

        await using var db = await _factory.CreateDbContextAsync(Token);
        await db.Database.MigrateAsync(Token);

        // Bu sınıfın iddiaları TOPLAM sayılara bakıyor (`rca.quality` bir
        // gösterge), dolayısıyla başka bir sınıftan kalan satır "kapsam
        // sızdırdı"ya benzeyen bir sayı üretir ve sebebi bu dosyada aranmaz.
        // `ScopeNegativeTests` aynı gerekçeyi aynı tablo için yazmış.
        await db.GoldenReviews.ExecuteDeleteAsync(Token);
        await db.MaintenanceWindows.ExecuteDeleteAsync(Token);

        var writer = new EventWriter(_context);
        await writer.WriteEventsAsync([Event("net-core", CoreEventId), Event("net-edge", EdgeEventId)], Token);

        var query = new ScopedQuery(
            new EventReader(_context),
            new ChangeEventReader(_context),
            new CorrelationReader(_context),
            writer,
            await _factory.CreateDbContextAsync(Token),
            new NoOpAuditSink());

        // Araçlar kapsamı ÇAĞRI BAŞINA açıyor, yani gerçek bir
        // `IServiceScopeFactory` gerekiyor — üretimdeki ömürle aynı (scoped).
        _scopes = new ServiceCollection()
            .AddScoped(_ => query)
            .AddSingleton(_factory)
            .AddScoped<GoldenReviewStore>()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
    }

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------------
    // 1 · logs.get — zincirin tamamı, ve kapı zincirin sonunda
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Gerçek ClickHouse'tan okunan gövde tele maskelenmiş iniyor.</b>
    ///
    /// <para>
    /// Birim testi aynı iddiayı sahte bir sorguyla ölçüyor; oradaki gövde
    /// <b>testin kendi yazdığı</b> dize. Burada gövde ClickHouse'a yazılıp geri
    /// okunuyor, yani kolonun kodlaması ve <c>EventReader</c>'ın dönüşümü de
    /// zincirin içinde. Sırrın depolamadan geçip <b>yine</b> maskelenmesi,
    /// kapının araç katmanında durduğunun kanıtı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Logs_get_clickhouse_govdesini_maskelenmis_donduruyor()
    {
        var result = await new LogsGetTool(_scopes).ExecuteScopedAsync(
            Invocation(("event_id", CoreEventId.ToString())), Core, Token);

        Assert.False(result.IsError);

        var payload = result.Payload;

        // Satır GERÇEKTEN döndü — yoksa aşağıdaki yoklamalar boş bir yükte
        // yeşil yanardı (§6).
        Assert.Equal(CoreEventId, payload.GetProperty("event_id").GetGuid());
        Assert.Equal(1, payload.GetProperty("masked_values").GetInt32());
        Assert.Contains(SecretRedactor.Mask, payload.GetProperty("body").GetString()!, StringComparison.Ordinal);

        Assert.DoesNotContain(Secret, payload.GetRawText(), StringComparison.Ordinal);

        // ⚠️ KAPSAM BEYANI — burada YALNIZCA yapısal yük ölçülüyor.
        // `BizigoMcpTool.ToProtocol` `internal` ve bu derlemeye açık değil, yani
        // `content` metin kopyasının da maskeli olduğu buradan görülemiyor. O
        // ikinci kanal `McpReadToolTests.Logs_get_sirri_maskelenmis_donduruyor`
        // içinde ölçülüyor; bu testin eklediği şey ClickHouse zinciri, tel
        // dönüşümü değil. `InternalsVisibleTo` genişletmek daha küçük bir satır
        // olurdu ve yanlış olurdu: M01 o metodu araçların erişemeyeceği tek
        // dönüşüm noktası olarak mühürledi.
    }

    /// <summary>
    /// <b>Başka grubun olayı <c>not_found</c> alıyor — ve kapsam SQL'de.</b>
    ///
    /// <para>
    /// <c>ScopedQuery.GetEventAsync</c> filtreyi ClickHouse sorgusuna koyuyor.
    /// Bellek içi bir sahte bunu taklit edemez: burada olay gerçekten <b>var</b>
    /// (<c>net-edge</c> satırı yazıldı) ve dönmemesi filtrenin koştuğunu
    /// söylüyor. Sahte sorguyla aynı iddia yalnızca <i>"sahte null döndürdü"</i>
    /// derdi.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Logs_get_baska_grubun_olayini_dondurmuyor()
    {
        var result = await new LogsGetTool(_scopes).ExecuteScopedAsync(
            Invocation(("event_id", EdgeEventId.ToString())), Core, Token);

        Assert.True(result.IsError);
        Assert.Equal(McpToolError.NotFound, result.Error!.Code);

        // Karşı-kanıt: aynı araç aynı kapsamla KENDİ grubunun olayını
        // döndürüyor. Olmadan yukarıdaki iddia her şeye `not_found` diyen bir
        // uygulamayla da geçerdi.
        var own = await new LogsGetTool(_scopes).ExecuteScopedAsync(
            Invocation(("event_id", CoreEventId.ToString())), Core, Token);

        Assert.False(own.IsError);
    }

    // ---------------------------------------------------------------------
    // 2 · rca.quality — kapsam filtresi SQL'e çevriliyor
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Başka grubun incelemesi Postgres tarafında eleniyor.</b>
    ///
    /// <para>
    /// Bu, T38'de ölçülmüş boşluğun M10 yüzeyindeki hâli: bellek içinde
    /// <c>groups.Contains(...)</c> LINQ olarak değerlendiriliyor, Postgres'te
    /// <c>= ANY(@groups)</c>'a <b>çevrilmek</b> zorunda ve çeviri kaybolsa
    /// birim paketi yeşil kalır.
    /// </para>
    ///
    /// <para>
    /// Kararlar bilerek <b>farklı</b> (<c>Correct</c> ↔ <c>Wrong</c>): filtre
    /// sızsaydı yalnızca sayı değil <b>oran</b> da değişirdi (1/1 yerine 1/2),
    /// yani iki bağımsız iddia birden düşerdi.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rca_quality_baska_grubun_incelemesini_saymiyor()
    {
        await SeedReviewsAsync();

        var result = await new RcaQualityTool(_scopes).ExecuteScopedAsync(
            Invocation(), Core, Token);

        Assert.False(result.IsError);

        Assert.Equal(1, result.Payload.GetProperty("total").GetInt64());
        Assert.Equal(1, result.Payload.GetProperty("correct").GetInt64());
        Assert.Equal(1d, result.Payload.GetProperty("accuracy").GetDouble());

        // İkinci sorgu da koştu ve ÇEVRİLDİ: `ReasoningQualityAsync`'in
        // "daha yenisi yok" alt sorgusu ilişkisel SQL'e iniyor. Rapor yok, yani
        // ölçülen paket de yok — ve `null` olması doğru cevap.
        Assert.Equal(1, result.Payload.GetProperty("reviewed_bundles").GetInt64());
        Assert.Equal(1, result.Payload.GetProperty("reasoning_absent").GetInt64());
        Assert.Equal(
            JsonValueKind.Null,
            result.Payload.GetProperty("dropped_sentence_ratio").ValueKind);
    }

    // ---------------------------------------------------------------------
    // 3 · alerts.maintenance — kapsam + yürürlük, gerçek satırlar üstünde
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Bakım penceresi kapsamla eleniyor ve yürürlük kararı sınırda doğru.</b>
    ///
    /// <para>
    /// İki iddia tek testte, çünkü tek bir tohumlama ikisini de besliyor ve
    /// ayırmak aynı satırları iki kez yazmak olurdu. İkisi ayrı şey söylüyor:
    /// birincisi <c>scope.Allows</c>'un koştuğunu, ikincisi
    /// <c>AlertSuppression.IsOpen</c>'ın yarı-açık aralığının <b>Postgres'ten
    /// okunmuş</b> zaman damgalarında da tuttuğunu.
    /// </para>
    ///
    /// <para>
    /// ⚠️ İkincisi burada ölçülmeye <b>değer</b>, çünkü Postgres
    /// <c>timestamptz</c>'i mikrosaniyeye yuvarlıyor ve dönen değer yazılanla
    /// bit-bit aynı olmayabiliyor — <c>RawEventLocator</c>'ın hassasiyet payı
    /// bu depoda tam olarak bu sınıftan doğdu. Sınır anı seçildi ki yuvarlama
    /// varsa görünsün.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Alerts_maintenance_kapsami_ve_yururlugu_gercek_satirlarda()
    {
        var ends = Now.AddHours(1);

        await using (var db = await _factory.CreateDbContextAsync(Token))
        {
            foreach (var group in new[] { "net-core", "net-edge" })
            {
                db.MaintenanceWindows.Add(new MaintenanceWindowEntity
                {
                    OwnerGroup = group,
                    StartsAt = Now.AddHours(-1),
                    EndsAt = ends,
                    Reason = "çekirdek anahtar firmware yükseltmesi",
                    CreatedBy = "u-core",
                });
            }

            await db.SaveChangesAsync(Token);
        }

        // (a) Pencerenin içinde: tek satır, ve o satır `net-core`.
        var open = await new AlertsMaintenanceTool(_factory, new FakeTimeProvider(Now))
            .ExecuteScopedAsync(Invocation(), Core, Token);

        Assert.Equal(1, open.Payload.GetProperty("total").GetInt32());

        var window = open.Payload.GetProperty("windows")[0];

        Assert.Equal("net-core", window.GetProperty("owner_group").GetString());
        Assert.Equal("open", window.GetProperty("state").GetString());

        // `rule_id` yok: pencere grubun TAMAMINI susturuyor, ve `null` olması
        // teldeki ayrımın kendisi.
        Assert.Equal(JsonValueKind.Null, window.GetProperty("rule_id").ValueKind);

        // (b) Bitiş ANINDA: yarı-açık aralık kapalı. Postgres'ten okunmuş
        // damgada da tutuyor mu — sorunun tamamı bu.
        var ended = await new AlertsMaintenanceTool(_factory, new FakeTimeProvider(ends))
            .ExecuteScopedAsync(Invocation(), Core, Token);

        Assert.Equal(
            "ended",
            ended.Payload.GetProperty("windows")[0].GetProperty("state").GetString());

        // (c) `open_only` gerçekten süzüyor: bitmiş pencere listeden düşüyor.
        var filtered = await new AlertsMaintenanceTool(_factory, new FakeTimeProvider(ends))
            .ExecuteScopedAsync(Invocation(("open_only", "true")), Core, Token);

        Assert.Equal(0, filtered.Payload.GetProperty("total").GetInt32());
    }

    // ---------------------------------------------------------------------

    private async Task SeedReviewsAsync()
    {
        var bundleId = Guid.CreateVersion7(Now);

        await using (var db = await _factory.CreateDbContextAsync(Token))
        {
            db.EvidenceBundles.Add(new EvidenceBundleEntity
            {
                Id = bundleId,

                // Paketin KENDİ `owner_group` kolonu YOK — kapsamı yalnızca
                // gövdesindeki `BundleScope`'ta duruyor ve orası sorgulanamıyor
                // (`GoldenReviewStore` belgesi). Ayrımı yapan şey incelemenin
                // kendi kolonu, ve tek paket + iki grup kurulumu tam olarak
                // bunu görünür kılıyor.
                GatheredAt = Now,
                SchemaVersion = 1,
                ContentHash = "m10-tool-chain",
                WindowFrom = Now.AddHours(-1),
                WindowTo = Now,
                BaselineFrom = Now.AddDays(-7),
                BaselineTo = Now.AddDays(-1),

                // jsonb boş dizeyi kabul etmiyor; paketin içeriği bu testin
                // konusu değil, kimliğinin var olması yeterli.
                Payload = "{}",
            });

            await db.SaveChangesAsync(Token);
        }

        var store = new GoldenReviewStore(_factory);
        var system = AccessScope.System("seed");

        await store.AddAsync(
            new ReviewInput(
                bundleId, null, ReviewVerdict.Correct,
                ContradictingEvidenceVerdict.Sound, "core incelemesi"),
            Core,
            Token);

        await store.AddAsync(
            new ReviewInput(
                bundleId, null, ReviewVerdict.Wrong,
                ContradictingEvidenceVerdict.Trivial, "edge incelemesi"),
            AccessScope.ForGroups("u-edge", ["net-edge"]),
            Token);

        // İKİSİNİN DE YAZILDIĞI görülüyor — yoksa yukarıdaki `1`'ler kapsamı
        // değil boşluğu ölçerdi (§6: yeşil bir sonuç, ölçümün yapılmadığı
        // anlamına da gelebiliyor).
        var all = await store.QualityAsync(system, Token);

        Assert.Equal(2, all.Total);
    }

    private static McpToolInvocation Invocation(params (string Name, string Value)[] arguments)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var (name, value) in arguments)
        {
            map[name] = JsonSerializer.SerializeToElement(value);
        }

        // Çağrı nesnesinin kapsamı bilerek `Denied`: gövde kapsamı PARAMETRE
        // olarak alıyor ve `invocation.Scope`'u okuyan bir gövde bu değerle
        // hiçbir iddiayı sağlayamıyor. Gerekçenin tamamı
        // `McpReadToolFixtures` içinde.
        return new McpToolInvocation(map, AccessScope.Denied);
    }

    private static LogEvent Event(string ownerGroup, Guid eventId) => new()
    {
        EventId = eventId,
        Timestamp = Now,
        OwnerGroup = ownerGroup,
        SourceId = $"fg-{ownerGroup}",
        Host = $"fg-{ownerGroup}",
        ParserId = "fortinet-fortigate-kv",
        ParseStatus = ParseStatus.Ok,
        EncodingDetected = "utf-8",
        Body = $"devname=FG100 action=deny set psksecret {Secret}",
        RawRef = $"raw/{ownerGroup}/2026/09/05/15/default/",
        SrcIp = IPAddress.IPv6Any,
        DstIp = IPAddress.IPv6Any,
    };

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bizigo.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, relative);
    }
}
