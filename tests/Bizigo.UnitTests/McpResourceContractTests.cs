using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Contracts.Security;
using Bizigo.Evidence;
using Bizigo.Mcp;
using Bizigo.Mcp.Product.Resources;
using Bizigo.Rca.Reasoning;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Kaynak kanalının uyum kapısı</b> — araç tarafındaki
/// <c>McpComplianceTests</c>'in kaynak karşılığı.
///
/// <para>
/// Araç kapısı <i>"ilan edilen her aracın şeması geçerli mi, örnek çağrısı
/// çıktı şemasına uyuyor mu"</i> diye soruyor. Kaynak kanalında ilan edilmiş bir
/// şema <b>yok</b> (gövde <c>text</c> + <c>mimeType</c>), dolayısıyla sorular
/// başka: adres kapsam taşıyor mu, gövde kapıdan geçiyor mu, yüzey ayrımı
/// yapısal mı, ve <b>kapsam dışı bir adres reddediliyor mu</b>.
/// </para>
/// </summary>
public sealed class McpResourceContractTests
{
    private static readonly McpBoundaryDeclaration Boundary =
        McpBoundaryDeclaration.Declare(DataBoundary.Internal, "kaynak kapısı: yerel bellek içi taşıma");

    /// <summary>
    /// <b>Dört belge türü de adreslenebiliyor</b> (kabul kriteri 1).
    ///
    /// <para>
    /// Küme <b>elle</b> yazılı ve bu bilinçli — araç tarafındaki
    /// <c>McpExpectedTools</c> ile aynı gerekçe: bu liste kapının <b>beyanı</b>,
    /// gözü değil. Keşiften türetilseydi test kendini kendisiyle karşılaştırır ve
    /// <b>hiçbir zaman kırmızı yanamazdı</b>.
    /// </para>
    ///
    /// <para>
    /// <b>Adlar HARFİ HARFİNE yazılı, sabitler üzerinden DEĞİL — ve bu ölçülerek
    /// düzeltildi.</b> İlk hâlde beklenen küme
    /// <c>ParserDefinitionResource.ResourceKind</c> gibi sabitleri okuyordu.
    /// Kırmızı ölçümü şunu gösterdi: sabiti <c>"parser-v2"</c> yapmak testi
    /// <b>düşürmüyor</b>, çünkü beklenen değer kusurla birlikte değişiyor. Yani
    /// "elle liste" görünen şey aslında türetilmiş bir listeydi ve tel adının
    /// değişmesini <b>hiç göremezdi</b> — istemci tarafında kırılan bir adres,
    /// bu depoda yeşil bir test.
    /// </para>
    /// </summary>
    [Fact]
    public void Dort_belge_turu_de_ilan_ediliyor()
    {
        var kinds = Urun().Select(static resource => resource.Kind).Order(StringComparer.Ordinal);

        // `rca-runs` M07'nin ikinci turunda eklendi ve BU LİSTE onu görerek
        // kırmızı yandı — elle yazılı bir beyanın istendiği davranış tam bu:
        // yeni bir kaynak sessizce girmiyor, bilinçli bir hareket gerektiriyor.
        Assert.Equal(["evidence-bundle", "parser", "rca-report", "rca-runs"], kinds);
    }

    /// <summary>
    /// <b>Koşum listesinin gövdesi gerçekten belgeyi taşıyor</b> — ve kapıdan
    /// geçmiş.
    ///
    /// <para>
    /// <c>rca-runs</c> gövdesini <c>rca.runs</c> aracının kendi yükünden
    /// üretiyor; ölçüt yükün <b>şeklinin</b> gövdede görünmesi. Bu olmadan
    /// redaksiyon kapısından geçmiş <b>boş</b> bir gövde de testleri geçerdi:
    /// <c>mimeType</c> doğru, kapı geçilmiş, içerik <b>yok</b> — ve bu depoda
    /// adı konmuş hâl tam olarak bu (<i>ölçülmüş görünen bir hiçlik</i>).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kosum_listesi_govdesi_kapidan_geciyor()
    {
        using var services = McpTestServices.ForDiscoveredTools();

        var resource = Urun(services).OfType<RcaRunsResource>().Single();

        var body = await resource.ReadScopedAsync(
            new McpResourceRead(
                new McpResourceUri(RcaRunsResource.ResourceKind, string.Empty),
                AccessScope.ForGroups("okuyucu", ["network/core"])),
            TestContext.Current.CancellationToken);

        Assert.NotNull(body);
        Assert.Equal(McpResourceMimeTypes.Json, body.MimeType);

        // Gövde AYRIŞTIRILABİLİR ve aracın yükünün şeklini taşıyor.
        using var parsed = JsonDocument.Parse(body.Text);

        Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
        Assert.True(
            parsed.RootElement.EnumerateObject().Any(),
            "Koşum listesi gövdesi boş bir nesne. Redaksiyon kapısından geçmiş boş bir "
            + "gövde, 'kapı çalışıyor' diyen ama hiçbir şey taşımayan bir kaynak olurdu.");
    }

    /// <summary>
    /// <b>Adres kapsam taşımıyor</b> (§6.1).
    ///
    /// <para>
    /// Şablona <c>owner_group</c> koymak, kapsam kontrolünü <b>adresin
    /// doğruluğuna</b> bağlamak olur — ve adresi istemci kuruyor. Ölçüt şablonun
    /// <b>tek</b> değişkeni olması: ikinci bir değişken eklendiği gün burası
    /// kırmızı yanıyor ve o değişkenin ne olduğu bilinçli bir karara dönüşüyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Adres_kapsam_tasimiyor()
    {
        foreach (var resource in Urun())
        {
            var template = resource.ProtocolResourceTemplate.UriTemplate;

            Assert.StartsWith($"{McpResourceUri.Scheme}://", template, StringComparison.Ordinal);

            var degiskenler = template.Count(static c => c == '{');

            Assert.True(
                degiskenler <= 1,
                $"`{template}` birden fazla değişken taşıyor. Adres yalnızca HANGİ BELGE "
                + "sorusunu cevaplamalı; kapsamı adrese koymak, kapsamı istemcinin yazdığı "
                + "bir dizgeye bağlamak olur (§6.1).");

            // Kapsamı ima eden bir segment de yok: `group`, `owner`, `tenant`.
            foreach (var yasak in new[] { "group", "owner", "tenant", "scope" })
            {
                Assert.DoesNotContain(yasak, template, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// <b>Yüzey ayrımı yapısal</b> (brief §3).
    ///
    /// <para>
    /// <c>bizigo-sim</c> ürün verisi sunmuyor, dolayısıyla ürün <b>kaynağı</b> da
    /// sunmuyor. Ve bu iki kapıdan geçiyor: <c>ProductResource.Surface</c>
    /// <c>sealed</c>, <b>ve</b> simülatör yüzeyinin derleme listesinde
    /// <c>Bizigo.Mcp.Product</c> hiç yok — keşif o türleri taramıyor bile.
    /// </para>
    /// </summary>
    [Fact]
    public void Simulator_yuzeyi_hic_kaynak_sunmuyor()
    {
        using var services = McpTestServices.ForDiscoveredTools();

        var simulator = BizigoMcpServer.Resources(
            McpSurface.Simulator,
            McpComplianceTests.DeclaredAssemblies(McpSurface.Simulator),
            services);

        Assert.Empty(simulator);
    }

    /// <summary>
    /// <b>Kaynak sunmayan yüzey yeteneği de ilan etmiyor</b> — ve bu ölçüm
    /// <c>Apply</c>'nin içindeki bir kusuru yakaladı.
    ///
    /// <para>
    /// İlk hâlde koleksiyon koşulsuz kuruluyordu (<c>??= []</c>) ve yalnızca
    /// <c>Capabilities.Resources</c> koşullu atanıyordu. Ölçüm: <b>SDK boş bir
    /// koleksiyonun varlığından yeteneği kendisi ilan ediyor</b>. Yani niyet
    /// doğru, mekanizma eksikti — ve fark ancak tel üzerinden görünüyordu.
    /// </para>
    /// </summary>
    [Fact]
    public void Kaynaksiz_yuzeyde_koleksiyon_hic_kurulmuyor()
    {
        using var services = McpTestServices.ForDiscoveredTools();

        var options = BizigoMcpServer.CreateOptions(
            McpSurface.Simulator,
            Boundary,
            McpComplianceTests.DeclaredAssemblies(McpSurface.Simulator),
            services);

        Assert.Null(options.ResourceCollection);
        Assert.Null(options.Capabilities?.Resources);
    }

    /// <summary>
    /// <b>Örnek gövde ilan edilen türü taşıyor ve kapıdan geçmiş.</b>
    ///
    /// <para>
    /// İlan edilen <c>mimeType</c> ile gövdenin taşıdığı tür ayrışırsa istemci
    /// belgeyi yanlış ayrıştırır — ve ayrışma sessiz olur: JSON diye ilan edilen
    /// markdown bir gövde çöp değil, <i>ayrıştırılamayan</i> bir şey.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Ornek_govde_ilan_edilen_turu_tasiyor()
    {
        foreach (var resource in Urun())
        {
            var body = await resource.SampleAsync(TestContext.Current.CancellationToken);

            Assert.Equal(resource.BodyMimeType, body.MimeType);
            Assert.NotNull(body.Text);
        }
    }

    /// <summary>
    /// <b>Her kaynak iptal edilmiş belirteci gözetiyor.</b>
    ///
    /// <para>
    /// Araç tarafındaki <c>Her_arac_iptal_edilmis_belirteci_gozetiyor</c>'un
    /// kaynak karşılığı ve aynı sebeple var: bir kaynak okuması da ClickHouse'a
    /// ya da Postgres'e inebiliyor, ve iptal edilmeyen bir okuma kaynak
    /// sızdırırken istemci <i>"iptal ettim"</i> sanıyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Her_kaynak_iptal_edilmis_belirteci_gozetiyor()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        foreach (var resource in Urun())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await resource.SampleAsync(cancelled.Token));
        }
    }

    /// <summary>
    /// <b>KAPSAM DIŞI BİR PAKETİN ADRESİ DOĞRUDAN İSTENDİĞİNDE REDDEDİLİYOR</b>
    /// (kabul kriteri 3 ve 5).
    ///
    /// <para>
    /// Ölçümün şekli önemli: paket <b>gerçekten var</b> ve adresi <b>doğru</b>.
    /// Reddin sebebi yalnızca okuyanın kapsamı. Adres tahmin edilebilir olsa da
    /// (bir bağlantıda, bir alarm bildiriminde geçebiliyor) belgeye
    /// ulaşılamıyor — yani kapı adreste değil <b>okuma yolunda</b>.
    /// </para>
    ///
    /// <para>
    /// Ve cevap <b>"bulunamadı"</b>: kapsam dışı bir paket ile hiç var olmayan
    /// bir paket ayırt edilemiyor. Ayrı bir cevap paketin varlığını doğrulardı,
    /// yani bir pencerede RCA koşulduğu bilgisini sızdırırdı — REST'in 404
    /// kararının aynısı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kapsam_disi_paketin_adresi_reddediliyor()
    {
        using var services = McpTestServices.ForDiscoveredTools();

        var bundle = await KaydetAsync(services, ownerGroup: "network/edge");

        var resource = Urun(services).OfType<EvidenceBundleResource>().Single();

        // 1 · PAKETİN SAHİBİ okuyabiliyor — testin kendi iddiasını doğrulamaması
        // için önce olumlu hâl ölçülüyor. Bu satır olmasa "her zaman null
        // dönen" bir kaynak da testi geçerdi.
        var izinli = await resource.ReadScopedAsync(
            new McpResourceRead(
                new McpResourceUri(EvidenceBundleResource.ResourceKind, bundle.Id.ToString()),
                AccessScope.ForGroups("sahip", ["network/edge"])),
            TestContext.Current.CancellationToken);

        Assert.NotNull(izinli);

        // 2 · BAŞKA GRUP aynı adresi istiyor → yok.
        var yabanci = await resource.ReadScopedAsync(
            new McpResourceRead(
                new McpResourceUri(EvidenceBundleResource.ResourceKind, bundle.Id.ToString()),
                AccessScope.ForGroups("yabanci", ["network/core"])),
            TestContext.Current.CancellationToken);

        Assert.Null(yabanci);

        // 3 · HİÇBİR grubu olmayan kimlik de göremiyor.
        var bos = await resource.ReadScopedAsync(
            new McpResourceRead(
                new McpResourceUri(EvidenceBundleResource.ResourceKind, bundle.Id.ToString()),
                AccessScope.ForGroups("bos", [])),
            TestContext.Current.CancellationToken);

        Assert.Null(bos);
    }

    /// <summary>
    /// <b>Rapor kapsamını paketten devralıyor.</b>
    ///
    /// <para>
    /// Raporun kendi <c>owner_group</c>'u yok. Kapsam kapısını rapor tablosuna
    /// taşımak — paket kimliğiyle doğrudan rapora gitmek — kapıyı <b>atlamak</b>
    /// olurdu, ve o hâl sessiz olurdu: rapor iner, kimse fark etmez.
    /// </para>
    ///
    /// <para>
    /// <b>Rapor GERÇEKTEN kaydediliyor — ve bu ölçülerek eklendi.</b> İlk hâlde
    /// yalnızca yabancı kapsamın <see langword="null"/> aldığı sınanıyordu ve
    /// kırmızı ölçümü şunu gösterdi: kapsam kontrolünü <b>tamamen kaldırmak</b>
    /// testi düşürmüyor, çünkü rapor kaydedilmemişken cevap yine
    /// <see langword="null"/>. Yani test <i>"kapsam reddetti"</i> ile <i>"rapor
    /// yok"</i> arasını ayırt edemiyordu — §7'nin sınıfı, bu kez bir testin
    /// içinde.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rapor_kapsamini_paketten_devraliyor()
    {
        using var services = McpTestServices.ForDiscoveredTools();

        var bundle = await KaydetAsync(services, ownerGroup: "network/edge");

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<RcaReportStore>()
                .SaveAsync(Rapor(bundle.Id), TestContext.Current.CancellationToken);
        }

        var resource = Urun(services).OfType<RcaReportResource>().Single();

        // OLUMLU YARI ŞART: rapor gerçekten okunabiliyor. Bu satır olmasa
        // aşağıdaki iddia "rapor yok" hâliyle de geçerdi.
        var sahip = await resource.ReadScopedAsync(
            new McpResourceRead(
                new McpResourceUri(RcaReportResource.ResourceKind, bundle.Id.ToString()),
                AccessScope.ForGroups("sahip", ["network/edge"])),
            TestContext.Current.CancellationToken);

        Assert.NotNull(sahip);

        var yabanci = await resource.ReadScopedAsync(
            new McpResourceRead(
                new McpResourceUri(RcaReportResource.ResourceKind, bundle.Id.ToString()),
                AccessScope.ForGroups("yabanci", ["network/core"])),
            TestContext.Current.CancellationToken);

        Assert.Null(yabanci);
    }

    private static RcaReportDocument Rapor(Guid bundleId) => new()
    {
        BundleId = bundleId,
        ScenarioId = "olcum",
        ScenarioVersion = "1",
        Findings = [],
        Actions = [],
        DroppedSentenceCount = 0,
        ProducedSentenceCount = 1,
        FabricatedCitationSentenceCount = 0,
        ModelInfo = new RcaReportModelInfo("olcum", "olcum", null, null, 0),
    };

    /// <summary>
    /// <b>Bozuk adres de "bulunamadı"</b> — sebep söylenmiyor.
    ///
    /// <para>
    /// <i>"Bu bir Guid değil"</i> demek, adres uzayının şeklini söyleyen bir
    /// yankı olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bozuk_kimlik_bulunamadi_donuyor()
    {
        using var services = McpTestServices.ForDiscoveredTools();

        var resource = Urun(services).OfType<EvidenceBundleResource>().Single();

        var sonuc = await resource.ReadScopedAsync(
            new McpResourceRead(
                new McpResourceUri(EvidenceBundleResource.ResourceKind, "bu-bir-guid-degil"),
                AccessScope.ForGroups("sahip", ["network/edge"])),
            TestContext.Current.CancellationToken);

        Assert.Null(sonuc);
    }

    /// <summary>Adres ayrıştırıcısı ilan edilen şablonla aynı kurala bakıyor.</summary>
    [Theory]
    [InlineData("bizigo://rca-report/abc", "rca-report", "abc")]
    [InlineData("bizigo://parser/fortinet/fortigate", "parser", "fortinet/fortigate")]
    public void Adres_ayristirilabiliyor(string uri, string kind, string id)
    {
        Assert.True(McpResourceUri.TryParse(uri, out var parsed));
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(id, parsed.Id);
        Assert.Equal(uri, parsed.ToString());
    }

    /// <summary>Tanınmayan biçim <b>fırlatmıyor</b>: bozuk URI istemci hatası.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://ornek/rca-report/1")]
    [InlineData("bizigo://rca-report")]
    [InlineData("bizigo://rca-report/")]
    public void Taninmayan_adres_reddediliyor(string? uri) =>
        Assert.False(McpResourceUri.TryParse(uri, out _));

    private static IReadOnlyList<ProductResource> Urun()
    {
        // Kaynakları kuran sağlayıcı, testin veri yazdığı sağlayıcıdan AYRI
        // olamıyor — ve bu ölçülerek görüldü: ayrı olduğunda her `ForDiscoveredTools()`
        // çağrısı YENİ bir bellek içi veritabanı kuruyor, yani kaydedilen paket
        // okuyanın göremediği bir yerde duruyor ve test "kapsam reddetti" diye
        // okunacak bir sonuç üretiyordu. Kapsam ölçen testler `Urun(services)`
        // kullanıyor; bu aşırı yükleme yalnızca veriye dokunmayan ölçümler için.
        var services = McpTestServices.ForDiscoveredTools();

        return Urun(services);
    }

    private static IReadOnlyList<ProductResource> Urun(IServiceProvider services)
    {
        return [.. BizigoMcpServer
            .Resources(
                McpSurface.Product,
                McpComplianceTests.DeclaredAssemblies(McpSurface.Product),
                services)
            .OfType<ProductResource>()];
    }

    /// <summary>
    /// Belirli bir gruba ait bir kanıt paketi kaydeder. <b>Gerçek depo</b>
    /// (bellek içi EF) — elle kurulmuş bir sahte, kapsam kontrolünün deponun
    /// döndürdüğü nesne üzerinde çalıştığını ölçmezdi.
    /// </summary>
    private static async Task<EvidenceBundle> KaydetAsync(IServiceProvider services, string ownerGroup)
    {
        await using var scope = services.CreateAsyncScope();

        var store = scope.ServiceProvider.GetRequiredService<EvidenceBundleStore>();

        return await store.SaveAsync(
            new EvidenceBundle
            {
                Id = Guid.NewGuid(),
                GatheredAt = DateTimeOffset.UnixEpoch,
                Window = new RcaWindow
                {
                    From = DateTimeOffset.UnixEpoch,
                    To = DateTimeOffset.UnixEpoch.AddMinutes(45),
                    BaselineFrom = DateTimeOffset.UnixEpoch.AddDays(-7),
                    BaselineTo = DateTimeOffset.UnixEpoch,
                },
                Scope = new BundleScope([ownerGroup], IsSystem: false),
                Slices = [],
                Trust = new WindowTrust(TotalEvents: 0, UnreliableTimeEvents: 0),
            },
            TestContext.Current.CancellationToken);
    }
}
