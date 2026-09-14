using System.Reflection;
using System.Text.Json;
using Bizigo.Alerting;
using Bizigo.Contracts;
using Bizigo.Contracts.Security;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Mcp;
using Bizigo.Mcp.Product;
using Bizigo.Mcp.Product.Tools;
using Bizigo.Parsing.Dispatch;
using Bizigo.Query;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Protocol;

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
    /// <b>Hiçbir okuma aracı <c>WithLogText</c> ile log metni taşımıyor.</b>
    ///
    /// <para>
    /// M04 bu testi yazarken beklentisi şuydu: <i>"kapının ilk müşterisi
    /// <c>logs.get</c> olacak"</i>, yani M10 gelince burası kırmızı yanacak.
    /// <b>Yanmadı, ve sebebi ölçüldü:</b> <c>logs.get</c> geldi, log metnini
    /// gerçekten döndürüyor, ama kapıyı <c>WithLogText</c> yerine
    /// <b>yapısal kanaldan</b> geçiriyor —
    /// <c>Payload.Body</c> alanının tipi <see cref="RedactedPrompt"/> ve
    /// <c>RedactedPromptJsonConverter</c> onu tele maskelenmiş dize olarak
    /// yazıyor. Üç gerekçesi <c>LogsGetTool</c> belgesinde; kısası
    /// <c>WithLogText</c>'in metni hiçbir <c>outputSchema</c>'nın tarif etmediği
    /// bir kanalda gidiyor ve aynı satırı <b>üçüncü</b> kez taşıyor.
    /// </para>
    ///
    /// <para>
    /// <b>Test yine de duruyor ve artık başka bir şey söylüyor:</b> ürün
    /// yüzeyinde şema dışı metin kanalı <b>kullanılmıyor</b>. Bir araç bir gün
    /// onu kullanmaya başlarsa burası kırmızı yanıyor ve o karar bilinçli olmak
    /// zorunda kalıyor — çünkü o metin ilan edilmemiş bir kanaldan modelin
    /// bağlamına girer.
    /// </para>
    ///
    /// <para>
    /// Yapısal kanaldaki kapının bekçileri ayrı:
    /// <see cref="Logs_get_sirri_maskelenmis_donduruyor"/> ve
    /// <see cref="Logs_get_govdesi_RedactedPrompt_olarak_yazili"/>.
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
    // 6 · M10 — redaksiyon kapısı YAPISAL kanalda, ve yazma yokluğu
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b><c>logs.get</c> sırrı maskelenmiş döndürüyor — iki kanalda birden.</b>
    ///
    /// <para>
    /// <b>Bu testin öznesi bir tip kaydı değil, davranış.</b>
    /// <see cref="RedactedPrompt"/> alanı doğru tipte olsa bile gövde
    /// <c>found.Body</c>'yi başka bir alandan geçirebilir; ve tersi, alan
    /// <c>string</c>'e çevrilse derleme yeşil kalır. O yüzden iddia
    /// <b>teldeki metinde</b>: sır orada geçmiyor, maske geçiyor.
    /// </para>
    ///
    /// <para>
    /// <b>Ve iki kanal ayrı ayrı ölçülüyor.</b>
    /// <see cref="BizigoMcpTool.ToProtocol"/> yükü hem
    /// <c>structuredContent</c> olarak hem <c>content</c> içinde bir metin
    /// bloğu olarak gönderiyor. Yalnızca yükü kontrol eden bir test, metin
    /// kopyasının maskesiz gittiği bir kusuru <b>göremezdi</b> — ve M06 o ikinci
    /// kanalı kendi belgesinde <i>"kapının ULAŞMADIĞI yer"</i> diye beyan
    /// etmişti.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Logs_get_sirri_maskelenmis_donduruyor()
    {
        const string secret = "Xk7Qm2Rv9Tz4Lw8Yb3Nc";

        var query = new FakeScopedQuery
        {
            EventFactory = _ => Event($"set psksecret {secret}"),
        };

        var result = await new LogsGetTool(McpReadToolFixtures.Scopes(query))
            .ExecuteScopedAsync(EventArgument(), Core, Ct);

        Assert.False(result.IsError);

        var wire = BizigoMcpTool.ToProtocol(result);

        // Olay GERÇEKTEN döndü — yoksa aşağıdaki yoklamalar boş bir yükte
        // yeşil yanardı ve hiçbir şey ifade etmezdi (§6).
        Assert.Equal(1, wire.StructuredContent!.Value.GetProperty("masked_values").GetInt32());

        var channels = wire.Content.OfType<TextContentBlock>().Select(static block => block.Text)
            .Append(wire.StructuredContent!.Value.GetRawText())
            .ToArray();

        Assert.NotEmpty(channels);

        foreach (var channel in channels)
        {
            Assert.DoesNotContain(secret, channel, StringComparison.Ordinal);
        }

        // Maske gerçekten YAZILDI: sırrın yokluğu tek başına gövdenin hiç
        // dönmediği hâlle de sağlanırdı.
        Assert.Contains(
            SecretRedactor.Mask,
            wire.StructuredContent!.Value.GetProperty("body").GetString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <b><c>attrs</c> değerleri de kapıdan geçiyor.</b>
    ///
    /// <para>
    /// Ayrı bir test, çünkü ayrı bir kayıp: gövdeyi maskeleyip alanları
    /// maskelemeyen bir uygulama yukarıdaki testi <b>geçer</b>. Bir parola
    /// <c>attrs["psksecret"]</c> içinde de durabiliyor ve orada maskelenmezse
    /// gövdenin maskelenmesi hiçbir şey ifade etmez.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Logs_get_attrs_degerleri_de_maskeleniyor()
    {
        const string secret = "Qw3Er4Ty5Ui6Op7As8Df";

        var query = new FakeScopedQuery
        {
            EventFactory = _ => Event("action=deny") with
            {
                Attrs = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ayar"] = $"psksecret {secret}",
                },
            },
        };

        var result = await new LogsGetTool(McpReadToolFixtures.Scopes(query))
            .ExecuteScopedAsync(EventArgument(), Core, Ct);

        var payload = result.Payload.GetRawText();

        // Alan gerçekten döndü.
        Assert.Single(result.Payload.GetProperty("attrs").EnumerateObject());

        Assert.DoesNotContain(secret, payload, StringComparison.Ordinal);
        Assert.Equal(1, result.Payload.GetProperty("masked_values").GetInt32());
    }

    /// <summary>
    /// <b>Gövde alanının TİPİ <see cref="RedactedPrompt"/>.</b>
    ///
    /// <para>
    /// M06 bu bekçiyi <b>bilerek yazmamıştı</b> ve gerekçesi
    /// <c>McpToolResult</c> belgesinde duruyor: <i>"bugün görülmeyen bir şeye
    /// bekçi yazmak tüketicisi olmayan bir tip yazmakla aynı hata (§8)"</i>.
    /// Bugün tüketici var — <c>logs.get</c> — dolayısıyla bekçi yazılabilir
    /// hâle geldi.
    /// </para>
    ///
    /// <para>
    /// <b>Neden davranış testi yetmiyor.</b> Yukarıdaki iki test yükü
    /// maskeliyor, ama <c>string</c> bir alan + elle çağrılan bir
    /// <c>RedactedPrompt.Redact(...).Text</c> de onları geçer — ve o hâlde kapı
    /// bir <b>çağrı alışkanlığına</b> inmiş olur, yani M06'nın tam olarak
    /// kaldırdığı şey geri gelir. Tipin kendisi ölçülüyor: <c>string</c>'e
    /// çevirmek burayı kırmızı yakıyor.
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>Kapsam beyanı — bu bekçi bir DERLEME şartı değil.</b> Yapısal
    /// kanalda kapıyı atlayan çağrı <b>derleniyor</b> (M06'nın beyanı) ve bu
    /// test onu ancak <i>bu</i> araç için tutuyor. Bütün araçlar için
    /// mekanik bir ölçüt yazılamıyor, çünkü bir yükte meşru <c>string</c>'ler
    /// var (kaynak adı, zaman damgası, kimlik) ve hangisinin log içeriği
    /// taşıdığına karar verebilen bir makine yok — o karar aracın yazarının.
    /// </para>
    /// </summary>
    [Fact]
    public void Logs_get_govdesi_RedactedPrompt_olarak_yazili()
    {
        var payloadType = typeof(LogsGetTool)
            .GetNestedTypes(IlCallReader.Everything)
            .Single(static type => string.Equals(type.Name, "Payload", StringComparison.Ordinal));

        var body = payloadType.GetProperty("Body", IlCallReader.Everything);

        Assert.NotNull(body);
        Assert.Equal(typeof(RedactedPrompt), body!.PropertyType);

        var attrs = payloadType.GetProperty("Attrs", IlCallReader.Everything);

        Assert.NotNull(attrs);

        // Sözlüğün DEĞER tipi de kapı: `IReadOnlyDictionary<string, string>`
        // yazmak derlenirdi ve `attrs` maskeleme kararını çağıranın hatırlamasına
        // bırakırdı.
        Assert.Equal(
            typeof(IReadOnlyDictionary<string, RedactedPrompt>),
            attrs!.PropertyType);
    }

    /// <summary>
    /// <b>Ham baytlar base64 olarak DÖNMÜYOR</b> — ne şemada ne yükte.
    ///
    /// <para>
    /// Kapının en kolay atlatma yolu bu ve bir bekçi hak ediyor: base64
    /// <b>kayıpsız</b> bir kodlama, redaksiyon onun içinde hiçbir şey göremiyor
    /// ve model tek adımda çözüyor. Yani <c>raw_b64</c> taşıyan bir araç
    /// maskeliyor <i>görünürken</i> sırrın tamamını gönderirdi — bu deponun
    /// sessiz-yanlış sınıfının en pahalı hâli.
    /// </para>
    ///
    /// <para>
    /// İki yönlü: şema böyle bir alanı <b>ilan etmiyor</b>
    /// (<c>additionalProperties: false</c> ile birlikte bu bir kapı) ve yük
    /// gövdenin base64'ünü <b>hiçbir alanda</b> taşımıyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Logs_get_ham_baytlari_base64_olarak_dondurmuyor()
    {
        const string body = "set psksecret Zx1Cv2Bn3Ml4Kj5Hg6Fd";

        var query = new FakeScopedQuery { EventFactory = _ => Event(body) };
        var tool = new LogsGetTool(McpReadToolFixtures.Scopes(query));

        var declared = tool.OutputSchema.GetProperty("properties").EnumerateObject()
            .Select(static field => field.Name)
            .ToArray();

        Assert.DoesNotContain("raw_b64", declared);
        Assert.DoesNotContain("raw_bytes", declared);
        Assert.DoesNotContain("raw_hex", declared);

        // Ve şema kapalı: ilan edilmemiş bir alan yüke sızamıyor.
        Assert.False(tool.OutputSchema.GetProperty("additionalProperties").GetBoolean());

        var result = await tool.ExecuteScopedAsync(EventArgument(), Core, Ct);
        var payload = result.Payload.GetRawText();

        Assert.DoesNotContain(
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(body)),
            payload,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Kapsam dışı (ya da olmayan) olay <c>not_found</c> alıyor.</b>
    ///
    /// <para>
    /// REST'in kendi gerekçesi: <i>"403 dönmek 'böyle bir olay var ama
    /// göremezsin' bilgisini sızdırırdı"</i>. MCP'de bedeli daha yüksek — o
    /// cümle modelin bağlamına giren bir <b>varlık iddiası</b> olurdu ve model
    /// onu bir kök neden gerekçesine çevirebilir.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Logs_get_kapsam_disi_olay_not_found_aliyor()
    {
        // `FakeScopedQuery.GetEventAsync` varsayılan olarak `null` dönüyor —
        // yani "kapsamda yok" hâli.
        var result = await new LogsGetTool(McpReadToolFixtures.Scopes(new FakeScopedQuery()))
            .ExecuteScopedAsync(EventArgument(), Core, Ct);

        Assert.True(result.IsError);
        Assert.Equal(McpToolError.NotFound, result.Error!.Code);
    }

    /// <summary>
    /// <b><c>attrs</c> kesilmesi yükte görünüyor.</b>
    ///
    /// <para>
    /// Sessiz kesme, modelin eksik bir alan kümesini <b>tam</b> sanması demek —
    /// ve bu araçta o yanlış doğrudan bir kök neden iddiasına dönüşür
    /// (<i>"alan yok, demek ki parser görmemiş"</i>).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Logs_get_attrs_kesilmesi_yukte_gorunuyor()
    {
        var attrs = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < 75; index++)
        {
            attrs[$"alan{index:D3}"] = $"deger{index}";
        }

        var query = new FakeScopedQuery
        {
            EventFactory = _ => Event("action=deny") with { Attrs = attrs },
        };

        var result = await new LogsGetTool(McpReadToolFixtures.Scopes(query))
            .ExecuteScopedAsync(EventArgument(), Core, Ct);

        Assert.True(result.Payload.GetProperty("attrs_truncated").GetBoolean());
        Assert.Equal(60, result.Payload.GetProperty("attrs").EnumerateObject().Count());
    }

    /// <summary>
    /// <b>Hiçbir ürün aracı yazmıyor — ve bu IL'den ölçülüyor.</b>
    ///
    /// <para>
    /// M10'un <c>alerts.maintenance</c> kararı şu: <i>bu ürün MCP üzerinden
    /// YAZMA yapmıyor</i>. Karar bir yorum olarak yazıldı; burada
    /// <b>mekanik</b> hâli. Bir gün biri bakım penceresi açan bir araç yazarsa
    /// (ya da mevcut bir aracın gövdesine bir <c>SaveChangesAsync</c> girerse)
    /// bu satır kırmızı yanıyor ve karar bilinçli olarak geri alınmak zorunda
    /// kalıyor.
    /// </para>
    ///
    /// <para>
    /// <b>Neden <see cref="ProductReadTool.IsReadOnly"/>'a bakmak yetmiyor:</b>
    /// o özellik <c>sealed</c> ve <c>true</c>, yani <b>her zaman</b> doğru
    /// cevabı veriyor — gövdesi yazan bir araç için de. Yani ona bakan bir test
    /// hiçbir şey ölçmez; ölçülmesi gereken şey <c>ReadOnlyHint</c>'in
    /// <b>doğru</b> olduğu.
    /// </para>
    ///
    /// <para>
    /// <b>Kapsam beyanı</b> (<c>IlCallReader</c>'ın kendi sınırları):
    /// yansımayla yapılan çağrı görünmüyor, ölü kod "var" sayılıyor, ve
    /// çözülemeyen tokenlar sayılıp <b>bildiriliyor</b> — çözülemeyen her token
    /// görülemeyen bir çağrı. Tarama araç gövdelerinin <b>bir</b> seviyesine
    /// bakıyor: bir aracın çağırdığı yardımcı metodun içindeki yazma bu
    /// bekçiye görünmez.
    /// </para>
    /// </summary>
    [Fact]
    public void Hicbir_urun_araci_yazma_cagirmiyor()
    {
        string[] forbidden = ["SaveChanges", "SaveChangesAsync", "ExecuteDelete", "ExecuteUpdate"];

        var unresolved = 0;
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var type in McpToolDiscovery.ToolTypes([typeof(LogsSearchTool).Assembly]))
        {
            foreach (var method in WithStateMachines(type))
            {
                scanned++;

                foreach (var callee in IlCallReader.Callees(method, ref unresolved))
                {
                    if (forbidden.Contains(callee.Name, StringComparer.Ordinal))
                    {
                        offenders.Add(
                            $"{method.DeclaringType?.Name}.{method.Name} → "
                            + $"{callee.DeclaringType?.Name}.{callee.Name}");
                    }
                }
            }
        }

        // ÖLÇÜM ARACININ KENDİSİ: sıfır metot gezilirse yukarıdaki döngü hiçbir
        // şey ölçmez ve test her zaman yeşil kalır (§6).
        Assert.True(scanned > 0, "Hiçbir araç metodu gezilmedi — bekçi kör.");

        Assert.True(
            offenders.Count == 0,
            "MCP ürün yüzeyi YAZMA çağırıyor:\n  " + string.Join("\n  ", offenders)
            + "\n\nM10'un kararı: bu ürün MCP üzerinden yazma yapmıyor. Gerekçesi "
            + "`AlertsMaintenanceTool` belgesinde ve dördüncü maddesi asıl olan — bakım "
            + "penceresinin hatası SESSİZ. Kararı geri almak bir tartışma gerektiriyor: "
            + "aktör kaydı, `ReadOnlyHint` ve ikinci bir kapsam kapısı.");
    }

    /// <summary>
    /// Bir tipin metotları <b>artı derleyicinin ürettiği durum makineleri</b>.
    ///
    /// <para>
    /// <b>Bu metot bir kırmızı ölçümünden doğdu.</b> İlk hâl yalnızca
    /// <c>type.GetMethods(...)</c> geziyordu ve §6 ölçümünde
    /// <c>AlertsMaintenanceTool</c>'a bir <c>SaveChangesAsync</c> konduğunda
    /// bekçi <b>YEŞİL KALDI</b>. Sebep: <c>async</c> bir metodun gövdesi
    /// derlenmiş IL'de o metotta <b>durmuyor</b> — derleyici onu iç içe bir
    /// durum makinesi tipine (<c>&lt;ExecuteScopedAsync&gt;d__N.MoveNext</c>)
    /// taşıyor ve metodun kendisi yalnızca makineyi başlatan bir sap oluyor.
    /// Yani bekçi, ölçmek istediği <b>bütün gövdeleri</b> kaçırıyordu ve
    /// ürünün her aracı <c>async</c>.
    /// </para>
    ///
    /// <para>
    /// Bu, <c>IlCallReader</c>'ın kendi belgesinde <b>yazılı olmayan</b> bir
    /// sınırdı; orada üç kör nokta sayılıyor (yansıma, ölü kod, kaynak
    /// üreteçleri) ve <c>async</c> dönüşümü yok. Sınırın adı konuldu.
    /// </para>
    /// </summary>
    private static IEnumerable<MethodBase> WithStateMachines(Type type)
    {
        foreach (var method in type.GetMethods(IlCallReader.Everything))
        {
            yield return method;
        }

        // Durum makineleri `private sealed` iç içe tipler; `Everything` onları
        // görüyor. Yük kayıtları (`Payload`, `Row`) da buraya giriyor ve
        // girmesi zararsız: onlarda yazma çağrısı olmaması da bir iddia.
        foreach (var nested in type.GetNestedTypes(IlCallReader.Everything))
        {
            foreach (var method in nested.GetMethods(IlCallReader.Everything))
            {
                yield return method;
            }
        }
    }

    /// <summary>
    /// <b>Bakım penceresinin yürürlük kararı bastırma motorunun kararıyla AYNI.</b>
    ///
    /// <para>
    /// <c>alerts.maintenance</c> <c>state</c> alanını
    /// <see cref="AlertSuppression.IsOpen"/>'dan okuyor. Bu test o bağın
    /// gerçekten kurulduğunu <b>sınırda</b> ölçüyor: <c>now == EndsAt</c>
    /// anında aralık yarı-açık olduğu için pencere <b>kapalı</b>. Bir kopya
    /// yazılıp <c>&lt;=</c> kullanılsaydı araç <i>"open"</i> derken motor
    /// bastırmıyor olurdu — ve o ayrışmanın hiçbir belirtisi olmazdı.
    /// </para>
    ///
    /// <para>
    /// <b>İki yönlü:</b> sınırın bir tık öncesi <c>open</c>, sınırın kendisi
    /// <c>ended</c>. Tek yön ölçmek, her zaman <c>ended</c> diyen bir
    /// uygulamayı geçirirdi.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Alerts_maintenance_yururluk_karari_bastirma_motoruyla_ayni()
    {
        var ends = new DateTimeOffset(2026, 9, 5, 16, 0, 0, TimeSpan.Zero);

        using var factory = new InMemoryControlPlaneFactory();

        await using (var db = factory.CreateDbContext())
        {
            db.MaintenanceWindows.Add(new MaintenanceWindowEntity
            {
                OwnerGroup = "network/core",
                StartsAt = ends.AddHours(-1),
                EndsAt = ends,
                Reason = "bakım",
                CreatedBy = "analyst.core",
            });

            await db.SaveChangesAsync(Ct);
        }

        // (a) Sınırın bir tık öncesi: açık.
        Assert.Equal("open", await State(factory, ends.AddTicks(-1)));

        // (b) Sınırın kendisi: kapalı — yarı-açık aralığın tanımı.
        Assert.Equal("ended", await State(factory, ends));

        // Ve motorun kendisi de aynı şeyi söylüyor. Bu satır olmasa yukarıdaki
        // iki iddia yalnızca ARACIN tutarlı olduğunu gösterirdi, motorla
        // AYNI olduğunu göstermezdi.
        var window = new MaintenanceWindowEntity
        {
            OwnerGroup = "network/core",
            StartsAt = ends.AddHours(-1),
            EndsAt = ends,
        };

        Assert.True(AlertSuppression.IsOpen(window, ends.AddTicks(-1)));
        Assert.False(AlertSuppression.IsOpen(window, ends));

        async Task<string> State(InMemoryControlPlaneFactory contexts, DateTimeOffset now)
        {
            var result = await new AlertsMaintenanceTool(contexts, new FakeTimeProvider(now))
                .ExecuteScopedAsync(McpReadToolFixtures.Invocation(), Core, Ct);

            return result.Payload.GetProperty("windows")[0].GetProperty("state").GetString()!;
        }
    }

    /// <summary>
    /// <b>Kapsam dışı bakım penceresi dönmüyor.</b>
    ///
    /// <para>
    /// Filtre bellekte (<c>scope.Allows</c>) çünkü tablo tek bir
    /// <c>owner_group</c> kolonu taşıyor ve kapsam bir küme — REST ucunun
    /// birebir aynı şekli. Bellekte olması onu <b>ölçülmesi daha gerekli</b>
    /// yapıyor: bir <c>Where</c> satırının düşmesi derlemeyi kırmıyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Alerts_maintenance_kapsam_disi_pencereyi_dondurmuyor()
    {
        var now = new DateTimeOffset(2026, 9, 5, 15, 0, 0, TimeSpan.Zero);

        using var factory = new InMemoryControlPlaneFactory();

        await using (var db = factory.CreateDbContext())
        {
            foreach (var group in new[] { "network/core", "database/prod" })
            {
                db.MaintenanceWindows.Add(new MaintenanceWindowEntity
                {
                    OwnerGroup = group,
                    StartsAt = now.AddMinutes(-10),
                    EndsAt = now.AddHours(1),
                    Reason = "bakım",
                    CreatedBy = "analyst.core",
                });
            }

            await db.SaveChangesAsync(Ct);
        }

        var result = await new AlertsMaintenanceTool(factory, new FakeTimeProvider(now))
            .ExecuteScopedAsync(McpReadToolFixtures.Invocation(), Core, Ct);

        var groups = result.Payload.GetProperty("windows").EnumerateArray()
            .Select(static window => window.GetProperty("owner_group").GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal(["network/core"], groups);
        Assert.Equal(1, result.Payload.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// <b><c>rca.quality</c> boş kümede oranları <see langword="null"/>
    /// döndürüyor — sıfır DEĞİL.</b>
    ///
    /// <para>
    /// Kaynağın kendi gerekçesi (<c>GoldenSetQuality.Accuracy</c>): <i>"%0 doğru"
    /// ile "henüz karar verilmiş inceleme yok" aynı sayıyla gösterilirse ekran,
    /// ölçülmemiş bir şeyi kötü ölçülmüş gibi gösterir.</i> Modelde bedeli
    /// daha yüksek: ekran yanlış bir rozet gösterir, model yanlış bir
    /// <b>cümle</b> kurar.
    /// </para>
    ///
    /// <para>
    /// Ve boş kümede <b>bir yük dönüyor</b>, hata değil: <c>not_found</c>
    /// dönmek <i>"gösterge yok"</i> derdi, oysa doğru cümle <i>"gösterge var,
    /// ölçülecek kayıt yok"</i>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rca_quality_bos_kumede_oranlari_null_donduruyor()
    {
        using var factory = new InMemoryControlPlaneFactory();

        var result = await Quality(factory).ExecuteScopedAsync(
            McpReadToolFixtures.Invocation(), Core, Ct);

        Assert.False(result.IsError);

        Assert.Equal(0, result.Payload.GetProperty("total").GetInt64());

        // Dört oran, dört ayrı payda — dördü de `null`. Yalnızca birini
        // ölçmek, bir oranın sessizce sıfıra düştüğü hâli göremezdi.
        foreach (var ratio in new[]
                 {
                     "accuracy", "accuracy_at_one",
                     "contradicting_trivial_ratio", "dropped_sentence_ratio",
                 })
        {
            Assert.Equal(JsonValueKind.Null, result.Payload.GetProperty(ratio).ValueKind);
        }
    }

    /// <summary>
    /// <b><c>rca.quality</c> başka grubun incelemesini saymıyor.</b>
    ///
    /// <para>
    /// Gösterge bir sayı ve kapsam kaçağının burada belirtisi <b>yok</b>: yanlış
    /// bir toplam da geçerli bir toplam gibi görünür. Kapı
    /// <c>GoldenReviewStore.Visible</c>'da ve tek; bu test onun gerçekten
    /// <b>bu yolda</b> koştuğunu ölçüyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rca_quality_baska_grubun_incelemesini_saymiyor()
    {
        using var factory = new InMemoryControlPlaneFactory();

        await using (var db = factory.CreateDbContext())
        {
            db.GoldenReviews.Add(Review("network/core", ReviewVerdict.Correct));
            db.GoldenReviews.Add(Review("database/prod", ReviewVerdict.Wrong));

            await db.SaveChangesAsync(Ct);
        }

        var result = await Quality(factory).ExecuteScopedAsync(
            McpReadToolFixtures.Invocation(), Core, Ct);

        // Bir inceleme sayıldı, iki değil — ve sayılan doğru olan. Kararların
        // BİLEREK farklı olması ikinci iddiayı da taşıyor: filtre sızsaydı
        // yalnızca sayı değil oran da değişirdi (1/1 yerine 1/2).
        Assert.Equal(1, result.Payload.GetProperty("total").GetInt64());
        Assert.Equal(1, result.Payload.GetProperty("correct").GetInt64());
        Assert.Equal(1d, result.Payload.GetProperty("accuracy").GetDouble());
    }

    // ---------------------------------------------------------------------

    /// <summary>M10 testlerinin ortak olay şekli.</summary>
    private static LogEvent Event(string body) => new()
    {
        EventId = SampleEventId,
        Timestamp = DateTimeOffset.UnixEpoch,
        OwnerGroup = "network/core",
        SourceId = "fw-edge-01",
        ParserId = "fortinet-fortigate-kv",
        ParseStatus = ParseStatus.Ok,
        EncodingDetected = "utf-8",
        Body = body,
    };

    private static readonly Guid SampleEventId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static McpToolInvocation EventArgument() =>
        McpReadToolFixtures.Invocation(("event_id", SampleEventId.ToString()));

    /// <summary>
    /// <c>rca.quality</c>'yi <b>gerçek</b> <c>GoldenReviewStore</c> ile kurar.
    ///
    /// <para>
    /// Sahte bir depo yazılmadı ve yazılmamalı: ölçülmek istenen şey kapsam
    /// filtresinin <b>sorguya</b> uygulanması, ve sahte bir depo o filtreyi
    /// taklit ederdi — yani testin kendi iddiasını doğrulaması (§6).
    /// </para>
    /// </summary>
    private static RcaQualityTool Quality(InMemoryControlPlaneFactory factory) =>
        new(new ServiceCollection()
            .AddSingleton<IDbContextFactory<ControlPlaneDbContext>>(factory)
            .AddScoped<GoldenReviewStore>()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>());

    private static GoldenReviewEntity Review(string ownerGroup, ReviewVerdict verdict) => new()
    {
        BundleId = Guid.NewGuid(),
        OwnerGroup = ownerGroup,
        Verdict = verdict,
        ReviewerSubject = "analyst.core",
        ReviewedAt = DateTimeOffset.UnixEpoch,
    };

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
        CreatedAt: DateTimeOffset.UnixEpoch,

        // F5 · S1 — üç alan boş: bu yardımcının sorusu kapsam ve şekil, topoloji
        // değil. Boş `Upstream` bu tipte "bilinmiyor" demek, "yok" demiyor.
        Upstream: string.Empty,
        Vlan: string.Empty,
        Firmware: string.Empty);
}
