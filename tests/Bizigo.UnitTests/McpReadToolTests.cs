using System.Text.Json;
using Bizigo.Alerting;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Mcp;
using Bizigo.Mcp.Product;
using Bizigo.Mcp.Product.Tools;
using Bizigo.Parsing.Dispatch;
using Bizigo.Query;
using Json.Schema;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// M04 okuma araçlarının <b>gövdeleri</b>.
///
/// <para>
/// <b>Neden uyum kapısı yetmiyor.</b> Kapı şemayı, anlaşmayı ve iptali ölçüyor
/// — hepsi protokol soruları. Bu sınıfın soruları ürün soruları: kapsam
/// geçiriliyor mu, log içeriği sızıyor mu, kesilme görünür mü, "göremiyorsun"
/// ile "yok" ayrışıyor mu. Kapı yeşilken bu dördü de yanlış olabilir.
/// </para>
///
/// <para>
/// Gövdeler <c>ExecuteScopedAsync</c> üzerinden <b>doğrudan</b> çağrılıyor.
/// Kapsam bağı (M08) bu yolun üstünde duruyor; buradan çağırmak onu atlamak
/// değil, <b>ayrı ölçmek</b>: kapsamın nereden geldiği M08'in sorusu, gelen
/// kapsamla ne yapıldığı bu sınıfın.
/// </para>
/// </summary>
public sealed class McpReadToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AccessScope Core => AccessScope.ForGroups("analyst.core", ["network/core"]);

    private static AccessScope Nothing => AccessScope.ForGroups("yeni.kullanici", []);

    // ---------------------------------------------------------------------
    // 1 · Kapsam — verilen kapsam GEÇİRİLİYOR
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Araç kendi kapsamını kurmuyor, verileni geçiriyor.</b>
    ///
    /// <para>
    /// MemberRef kapısı (<c>McpProductScopeGateTests</c>) aracın
    /// <c>AccessScope.System</c>'i <i>tanımadığını</i> ölçüyor — ama tanımayan
    /// bir araç yine de <c>AccessScope.ForGroups("x", [])</c> kurup kendi
    /// kapsamını uydurabilir. Bu test geçirilen nesnenin <b>aynı örnek</b>
    /// olduğunu ölçüyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Logs_search_verilen_kapsami_aynen_geciriyor()
    {
        var query = new FakeScopedQuery();
        var scope = Core;

        await Logs(query).ExecuteScopedAsync(McpReadToolFixtures.Invocation(), scope, Ct);

        Assert.Same(scope, query.LastEventScope);
    }

    /// <summary>
    /// <b>Boş kapsam <c>not_found</c> alıyor — boş liste DEĞİL.</b>
    ///
    /// <para>
    /// Ayrım §7'nin sınıfı: <c>logs.search</c>'ün sıfır satırı model tarafından
    /// <i>"eşleşme yok"</i> diye okunur. Söylenmesi gereken şey <i>"bu kimliğin
    /// göreceği hiçbir grup yok"</i>.
    /// </para>
    ///
    /// <para>
    /// Ve sorgu <b>hiç koşmuyor</b>: koşup boş dönen bir araç aynı yükü
    /// üretirdi ama ClickHouse'a gitmiş olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public void Bos_kapsam_not_found_aliyor()
    {
        var rejection = ProductReadTool.ScopeRejection(Nothing, readsScopedData: true);

        Assert.NotNull(rejection);
        Assert.Equal(McpToolError.NotFound, rejection!.Code);
        Assert.Equal(Nothing.Subject, rejection.Details!["subject"]);

        // Karşı-kanıt: dolu kapsam reddedilmiyor. Olmadan yukarıdaki iddia her
        // şeyi reddeden bir fonksiyonla da geçerdi.
        Assert.Null(ProductReadTool.ScopeRejection(Core, readsScopedData: true));
    }

    /// <summary>
    /// <b>Katalog aracı boş kapsamla da cevap veriyor.</b>
    ///
    /// <para>
    /// Muafiyet gerekçeli ve REST'in kararıyla aynı: katalog <b>yapılandırma</b>,
    /// veri değil. Bu test muafiyetin gerçekten <i>çalıştığını</i> ölçüyor —
    /// <c>McpProductScopeGateTests</c> yalnızca <b>sayıldığını</b> ölçüyor, ve
    /// sayılan bir muafiyetin işe yaraması ayrı bir soru.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Katalog_araci_kapsam_muafiyetini_gercekten_kullaniyor()
    {
        var tool = new CatalogParsersTool(new ParserCatalog());

        // Muafiyet reddi kapatıyor…
        Assert.Null(ProductReadTool.ScopeRejection(Nothing, tool.ReadsScopedData));

        // …ve araç gerçekten cevap veriyor. İkincisi olmadan birincisi yalnızca
        // "reddedilmiyor" derdi, "çalışıyor" demezdi.
        var result = await tool.ExecuteScopedAsync(McpReadToolFixtures.Invocation(), Nothing, Ct);

        Assert.False(result.IsError);
        Assert.Equal(0, result.Payload.GetProperty("count").GetInt32());
    }

    /// <summary>
    /// Envanter aracı kapsamı <c>IScopedQuery</c>'ye geçiriyor ve sahte kapsamı
    /// <b>gerçekten uyguluyor</b>: başka grubun kaynağı dönmüyor.
    /// </summary>
    [Fact]
    public async Task Inventory_list_kapsam_disi_kaynagi_dondurmuyor()
    {
        var query = new FakeScopedQuery();

        query.Sources.Add(Source("fw-edge-01", "network/core"));
        query.Sources.Add(Source("db-01", "database/prod"));

        var result = await new InventoryListTool(McpReadToolFixtures.Scopes(query))
            .ExecuteScopedAsync(McpReadToolFixtures.Invocation(), Core, Ct);

        var sources = result.Payload.GetProperty("sources").EnumerateArray()
            .Select(static s => s.GetProperty("source_id").GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal(["fw-edge-01"], sources);
        Assert.Equal(1, result.Payload.GetProperty("total").GetInt32());
    }

    // ---------------------------------------------------------------------
    // 2 · Log içeriği sızmıyor — K6
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b><c>logs.search</c> yükünde log satırının kendisi YOK.</b>
    ///
    /// <para>
    /// Olayın <c>Body</c>'si bilerek <b>tanınabilir</b> bir metin: yükte
    /// aranıyor ve bulunmaması ölçülüyor. Alan adlarını saymak yetmezdi —
    /// bir gün gövde başka bir adla taşınırsa ad listesi yeşil kalır ama içerik
    /// sızmış olur.
    /// </para>
    ///
    /// <para>
    /// Aynı ölçüm <c>attrs</c>, <c>user_name</c> ve <c>src_ip</c> için de:
    /// üçü de logdan türüyor ve üçü de REST yükünde <b>var</b>. Buradaki
    /// yokluğu bir tercih, ve tercihler ölçülmezse kaybolur.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Logs_search_log_icerigini_dondurmuyor()
    {
        const string body = "%ASA-4-106023: Deny tcp src outside:203.0.113.9/44321 dst inside:10.1.2.3/443";
        const string user = "hsagdic";
        const string attribute = "gizli-etiket-degeri";

        var query = new FakeScopedQuery
        {
            EventPageFactory = (_, _) => new EventPage(
                [
                    new LogEvent
                    {
                        EventId = Guid.NewGuid(),
                        Timestamp = DateTimeOffset.UnixEpoch,
                        OwnerGroup = "network/core",
                        SourceId = "fw-edge-01",
                        Body = body,
                        UserName = user,
                        Host = "fw-edge-01.internal",
                        RawRef = "s3://bizigo/raw/2026/09/05/abc#128:256",
                        Attrs = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["etiket"] = attribute,
                        },
                    },
                ],
                Next: null,
                HasMore: false),
        };

        var result = await Logs(query).ExecuteScopedAsync(McpReadToolFixtures.Invocation(), Core, Ct);

        var payload = result.Payload.GetRawText();

        // Satır gerçekten döndü — yoksa aşağıdaki yoklamalar boş bir yükte
        // yeşil yanardı ve hiçbir şey ifade etmezdi.
        Assert.Equal(1, result.Payload.GetProperty("count").GetInt32());

        Assert.DoesNotContain(body, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(user, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(attribute, payload, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.9", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("fw-edge-01.internal", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("s3://", payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Hiçbir okuma aracı log metni taşımıyor.</b>
    ///
    /// <para>
    /// M06 <c>McpToolResult.WithLogText</c>'i redaksiyon kapısının çıktısına
    /// bağladı. M04 araçları o kapıyı <b>hiç kullanmıyor</b> ve bu bir eksik
    /// değil bir beyan: kapının ilk müşterisi <c>logs.get</c> olacak. Bu test
    /// o beyanı ölçüyor — bir araç bir gün log metni eklerse burası kırmızı
    /// yanıyor ve karar bilinçli olmak zorunda kalıyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Hicbir_okuma_araci_log_metni_tasimiyor()
    {
        foreach (var tool in Tools())
        {
            var sample = await tool.SampleAsync(Ct);

            Assert.Empty(sample.LogText);
        }
    }

    // ---------------------------------------------------------------------
    // 3 · Örnek ↔ şema — kendi kapım
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Her aracın örneği kendi <c>OutputSchema</c>'sına uyuyor.</b>
    ///
    /// <para>
    /// Uyum kapısı bunu <c>tools/call</c> ile ölçüyor, yani <b>kimlik</b>
    /// gerektiren bir yoldan. Bu test <c>SampleAsync</c>'ten ölçüyor: aynı
    /// şekillendirme, kimlik gerektirmeyen yol. İkisi ayrı soru soruyor ve
    /// ikincisinin değeri şu — şema sürüklendiğinde kırmızı yanan şey
    /// <b>protokol kurulumundan bağımsız</b> oluyor.
    /// </para>
    ///
    /// <para>
    /// Örnekler bilerek <b>null dallarını da</b> dolduruyor
    /// (<c>parser_id</c>, <c>gated_reason</c>, <c>closed_at</c>, <c>source_id</c>):
    /// şemada <c>["string","null"]</c> yazmak o dalı sınamakla aynı şey değil.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Her_aracin_ornegi_cikti_semasina_uyuyor()
    {
        foreach (var tool in Tools())
        {
            var schema = JsonSchema.FromText(tool.OutputSchema.GetRawText());
            var sample = await tool.SampleAsync(Ct);

            var evaluation = schema.Evaluate(
                sample.Payload,
                new EvaluationOptions { OutputFormat = OutputFormat.List });

            Assert.True(
                evaluation.IsValid,
                $"`{tool.ToolName}` örneği kendi `outputSchema`'sına UYMUYOR.\n"
                + $"Çıktı: {sample.Payload.GetRawText()}\n"
                + $"Hatalar: {Errors(evaluation)}");
        }
    }

    /// <summary>
    /// <b>Her araç iptal edilmiş belirteci gözetiyor.</b>
    ///
    /// <para>
    /// Uyum kapısı bunu bütün araçlar için zaten yapıyor; burada tekrar
    /// edilmesinin sebebi <b>kapsam</b>: o kapı keşfin bulduğu kümeye bakıyor ve
    /// keşif bir gün bu derlemeyi göremezse (budama körlüğü — ölçüldü, bkz.
    /// <c>BizigoReadToolsSetup</c>) sessizce sıfır araç denetler. Bu test
    /// derlemeyi <b>doğrudan</b> gezdiği için o körlüğe bağışık.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Her_arac_iptal_edilmis_belirteci_gozetiyor()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        foreach (var tool in Tools())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await tool.SampleAsync(cancelled.Token));
        }
    }

    // ---------------------------------------------------------------------
    // 4 · Kesilme görünür · "göremiyorsun" ≠ "yok"
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Kesilme yükte GÖRÜNÜYOR.</b>
    ///
    /// <para>
    /// <c>total</c> kesilmeden önceki sayı, <c>truncated</c> bayrağı açık.
    /// Sessiz kesme modele <i>"envanter bu kadar"</i> diye okunurdu — sayamadığını
    /// sıfır sanmanın kardeşi: <b>gösterdiğini hepsi sanmak</b>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Envanter_kesilmesi_yukte_gorunuyor()
    {
        var query = new FakeScopedQuery();

        for (var index = 0; index < 3; index++)
        {
            query.Sources.Add(Source($"fw-{index}", "network/core"));
        }

        var result = await new InventoryListTool(McpReadToolFixtures.Scopes(query))
            .ExecuteScopedAsync(McpReadToolFixtures.InvocationOf(("limit", 2)), Core, Ct);

        Assert.Equal(3, result.Payload.GetProperty("total").GetInt32());
        Assert.True(result.Payload.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, result.Payload.GetProperty("sources").GetArrayLength());
    }

    /// <summary>
    /// <b>Kapsam dışı kural <c>not_found</c> alıyor — boş tetiklenme listesi DEĞİL.</b>
    ///
    /// <para>
    /// Sıfır tetiklenme <i>"alarm yok"</i> diye okunur; söylenmesi gereken şey
    /// <i>"o kuralı göremiyorsun"</i>. Aynı ayrımı REST ucu <c>404</c> ile
    /// yapıyor ve ikisi <b>aynı</b> metottan besleniyor
    /// (<c>AlertRuleService.ResolveTriggerScopeAsync</c>).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kapsam_disi_kural_not_found_aliyor()
    {
        using var factory = new InMemoryControlPlaneFactory();
        var rules = new AlertRuleService(factory, new AlertingOptions());

        var result = await new AlertTriggersTool(rules).ExecuteScopedAsync(
            McpReadToolFixtures.Invocation(("rule_id", Guid.NewGuid().ToString())),
            Core,
            Ct);

        Assert.True(result.IsError);
        Assert.Equal(McpToolError.NotFound, result.Error!.Code);
    }

    /// <summary>
    /// Görünür kuralı olmayan kimlik <b>boş liste</b> alıyor — ve bu doğru cevap.
    ///
    /// <para>
    /// Yukarıdaki testle birlikte ayrımın <b>iki tarafı</b> ölçülüyor: belirli
    /// bir kural istenip görünmüyorsa <c>not_found</c>, hiç kural yoksa boş
    /// liste. Yalnızca birini ölçmek, ikisini tek cevaba indiren bir kusuru
    /// göremezdi.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Gorunur_kural_yoksa_bos_liste()
    {
        using var factory = new InMemoryControlPlaneFactory();
        var rules = new AlertRuleService(factory, new AlertingOptions());

        var result = await new AlertTriggersTool(rules)
            .ExecuteScopedAsync(McpReadToolFixtures.Invocation(), Core, Ct);

        Assert.False(result.IsError);
        Assert.Equal(0, result.Payload.GetProperty("count").GetInt32());
    }

    // ---------------------------------------------------------------------
    // 5 · Argüman doğrulaması iş hatası, protokol hatası değil
    // ---------------------------------------------------------------------

    /// <summary>
    /// Tavanın üstünde bir <c>limit</c> <see cref="McpToolArgumentException"/>
    /// fırlatıyor — M01'in mühürlü <c>InvokeAsync</c>'i onu
    /// <c>invalid_argument</c> iş hatasına çeviriyor.
    ///
    /// <para>
    /// Kendi <c>Failure</c>'ımızı kurmak ikinci bir dönüşüm noktası olurdu (§9);
    /// tek dönüşüm M01'de.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task Sinir_disi_limit_arguman_hatasi(int limit)
    {
        var query = new FakeScopedQuery();

        var error = await Assert.ThrowsAsync<McpToolArgumentException>(async () =>
            await Logs(query).ExecuteScopedAsync(
                McpReadToolFixtures.InvocationOf(("limit", limit)), Core, Ct));

        Assert.Equal("limit", error.ArgumentName);
        Assert.Null(query.LastEventQuery);
    }

    /// <summary>
    /// <c>from &gt;= to</c> reddediliyor — REST'in aynı kuralı.
    /// </summary>
    [Fact]
    public async Task Ters_zaman_araligi_reddediliyor()
    {
        var query = new FakeScopedQuery();

        await Assert.ThrowsAsync<McpToolArgumentException>(async () =>
            await Logs(query).ExecuteScopedAsync(
                McpReadToolFixtures.Invocation(
                    ("from", "2026-09-05T12:00:00Z"),
                    ("to", "2026-09-05T11:00:00Z")),
                Core,
                Ct));

        Assert.Null(query.LastEventQuery);
    }

    /// <summary>
    /// <b>Varsayılan pencere duvar saatinden bağımsız ölçülüyor.</b>
    ///
    /// <para>
    /// <c>TimeProvider</c> sabitlenmiş. Sabitlenmemiş olsaydı test <i>"pencere 24
    /// saat mi"</i> sorusunu <b>koşum anına</b> göre cevaplardı — ve §6 bu depoda
    /// iki kez ısıran şeklin adını koyuyor: bir testin geçme sebebinin duvar
    /// saatiyle ilgisi olmamalı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Varsayilan_pencere_yirmi_dort_saat()
    {
        var now = new DateTimeOffset(2026, 9, 5, 18, 30, 0, TimeSpan.Zero);
        var query = new FakeScopedQuery();

        var tool = new LogsSearchTool(
            McpReadToolFixtures.Scopes(query),
            new FakeTimeProvider(now));

        await tool.ExecuteScopedAsync(McpReadToolFixtures.Invocation(), Core, Ct);

        Assert.Equal(now, query.LastEventQuery!.To);
        Assert.Equal(now.AddHours(-24), query.LastEventQuery.From);
    }

    // ---------------------------------------------------------------------

    /// <summary>Şema değerlendirmesinin hata satırları — okunabilir hâlde.</summary>
    private static string Errors(EvaluationResults evaluation) =>
        string.Join(
            "\n  ",
            (evaluation.Details ?? [])
                .Where(static detail => detail.Errors is { Count: > 0 })
                .SelectMany(static detail => detail.Errors!
                    .Select(error => $"{detail.InstanceLocation}: {error.Value}")));

    private static LogsSearchTool Logs(FakeScopedQuery query) =>
        new(McpReadToolFixtures.Scopes(query));

    /// <summary>
    /// Keşfin bulduğu araçlar değil <b>derlemenin</b> araçları: budama körlüğüne
    /// bağışık (gerekçe <c>BizigoReadToolsSetup</c> belgesinde).
    /// </summary>
    private static IReadOnlyList<ProductReadTool> Tools() =>
        [.. McpToolDiscovery
            .Instantiate(
                McpToolDiscovery.ToolTypes([typeof(LogsSearchTool).Assembly]),
                McpSurface.Product,
                McpTestServices.ForDiscoveredTools())
            .OfType<ProductReadTool>()];

    private static SourceSummary Source(string sourceId, string ownerGroup) => new(
        sourceId,
        ownerGroup,
        PeerAddress: null,
        Hostname: null,
        Vendor: "cisco",
        Product: "asa",
        ParserId: "cisco-asa",
        Encoding: "auto",
        SourceClass: "firewall",
        Enabled: true,
        IsKnownToDispatcher: true,
        CreatedAt: DateTimeOffset.UnixEpoch);
}
