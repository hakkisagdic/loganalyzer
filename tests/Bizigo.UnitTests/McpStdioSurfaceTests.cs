using Bizigo.Cli;
using Bizigo.Contracts.Security;
using Bizigo.Mcp;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>stdio yüzeylerinin servis grafiği KURULABİLİR mi</b> (M12).
///
/// <h3>Neden bu bekçi bugüne kadar yoktu ve bedeli neydi</h3>
///
/// <para>
/// stdio tarafının tek testi <c>McpComplianceTests.Stdio_beyansiz_ya_da_kurum_disi_kosmuyor</c>
/// ve o yalnızca <b>ret</b> yollarını ölçüyor — kendi belgesinde de bunu kapsam
/// olarak beyan ediyor: <i>"reddedilen her yol <c>McpStdioHost.RunAsync</c>'e
/// ulaşmadan dönüyor. Kabul edilen bir yolu buradan çağırmak süreç kapanana
/// kadar bloke olurdu."</i> Beyan dürüsttü, ama boşluğu kapatmıyordu.
/// </para>
///
/// <para>
/// <b>Boşluğun ölçülen bedeli:</b> M02 <c>ToolAssembliesFor(Product)</c>'a
/// <c>Bizigo.Mcp.Product</c>'ı ekledi (HTTP ile parite iddiası için) ve stdio'nun
/// servis grafiği tamamlanmadı. Sonuç — <c>bizigo mcp serve --surface bizigo</c>
/// <b>hiç ayağa kalkmıyordu</b>:
/// </para>
///
/// <code>
/// Unhandled exception: MCP aracı `AlertRulesTool` kurulamadı:
/// Unable to resolve service for type 'Bizigo.Alerting.AlertRuleService'
/// EXIT=1
/// </code>
///
/// <para>
/// HTTP yüzeyi yeşil, stdio ürün yüzeyi ölü, ve arada bir kapı yok. Bu, bu
/// deponun §7'de adını koyduğu sınıfın bir yüzey büyüklüğündeki hâli.
/// </para>
///
/// <h3>Bu bekçi neden OTURUM AÇMIYOR</h3>
///
/// <para>
/// Ölçülmesi gereken şey <i>"grafik kurulabiliyor mu"</i> ve o soru
/// <c>BizigoMcpServer.Tools(...)</c> ile cevaplanıyor: keşif her aracı
/// <c>ActivatorUtilities</c> ile <b>gerçekten kuruyor</b> ve kurulamayan aracı
/// <b>atlamıyor, patlıyor</b>. Yani protokol oturumu açmadan tam olarak arızanın
/// olduğu yere bakılıyor — ve süreci bloke etmeden.
/// </para>
///
/// <para>
/// <b>Konteyner GEREKMİYOR</b> (§2). Bağlantı dizgeleri yalnızca <i>kurulum</i>
/// için gerekiyor: <c>ClickHouseContext</c>, <c>ControlPlaneDbContext</c> ve
/// <c>AlertRuleService</c> yapıcılarında bağlanmıyor. Bu bekçi bir sorgu
/// koşturmuyor; koştursa entegrasyon testi olurdu.
/// </para>
/// </summary>
public sealed class McpStdioSurfaceTests
{
    /// <summary>
    /// Bekçinin verdiği ayarlar — <b>ulaşılamayan</b> adresler, ve bilerek.
    ///
    /// <para>
    /// <c>localhost</c> yazmak, makinede bir compose yığını ayaktaysa testin
    /// ona bağlanmasına izin verirdi ve o an bekçi sessizce başka bir şey
    /// ölçmeye başlardı: <i>"grafik kurulabiliyor mu"</i> yerine <i>"yığın
    /// ayakta mı"</i>. Ulaşılamayan bir adres, bu bekçinin ölçtüğü şeyi
    /// makinenin durumundan <b>ayırıyor</b>.
    /// </para>
    /// </summary>
    private const string UnreachableClickHouse =
        "Host=olmayan.bizigo.gecersiz;Port=8123;Database=bizigo;Username=bizigo;Password=bizigo";

    private const string UnreachableControlPlane =
        "Host=olmayan.bizigo.gecersiz;Port=5432;Database=bizigo;Username=bizigo;Password=bizigo";

    private static McpBoundaryDeclaration Boundary =>
        McpBoundaryDeclaration.Declare(DataBoundary.Internal, "stdio yüzey bekçisi: birim testi");

    /// <summary>
    /// <b>Her ilan edilen araç kurulabiliyor — iki yüzeyde de.</b>
    ///
    /// <para>
    /// <c>Theory</c> değil tek bir test, ve sebebi şu: iki yüzeyin ikisi de aynı
    /// grafikten besleniyor ve <b>ürün yüzeyinin bozulup simülatörün ayakta
    /// kalması</b> tam olarak bugünkü hâl. Tek testte yan yana durmaları, birinin
    /// yeşilliğinin diğerini gizleyememesi demek — kırmızı mesajı hangi yüzeyin
    /// düştüğünü söylüyor.
    /// </para>
    ///
    /// <para>
    /// Küme <c>McpExpectedTools</c>'a karşı da sınanıyor: grafik kurulabilir olup
    /// yüzeyin <b>eksik</b> bir küme ilan etmesi ayrı bir arıza, ve sıfır araçlı
    /// bir yüzey bu testin ilk iddiasını sessizce geçerdi.
    /// </para>
    /// </summary>
    [Fact]
    public void Her_yuzeyin_servis_grafigi_kurulabiliyor()
    {
        var basarisiz = new List<string>();

        foreach (var surface in new[] { McpSurface.Product, McpSurface.Simulator })
        {
            using var services = McpCommandHandlers.BuildServices(
                surface, UnreachableClickHouse, UnreachableControlPlane);

            try
            {
                var tools = BizigoMcpServer.Tools(
                    surface, McpCommandHandlers.ToolAssembliesFor(surface), services);

                Assert.Equal(
                    McpExpectedTools.For(surface),
                    tools.Select(static tool => tool.ToolName).Order(StringComparer.Ordinal).ToArray());
            }
            catch (Exception error)
            {
                basarisiz.Add($"{McpSurfaces.WireName(surface)}: {error.Message}");
            }
        }

        Assert.True(
            basarisiz.Count == 0,
            "stdio yüzeyinin servis grafiği KURULAMIYOR:\n  " + string.Join("\n  ", basarisiz)
            + "\n\nBir yüzeyin araç derlemesini `ToolAssembliesFor`'da ilan etmek, o araçların "
            + "bağımlılıklarını `BuildServices`'te kaydetmeyi de gerektiriyor: keşif bulduğu her "
            + "aracı KURUYOR ve kurulamayan aracı ATLAMIYOR, patlıyor. İlan ile grafik ayrıştığı "
            + "gün o yüzey hiç ayağa kalkmıyor — ve bu bir kez sessizce oldu (M02→M12).");
    }

    /// <summary>
    /// <b>Eksik ayar bir DI hatası olarak patlamıyor — adıyla söylenip
    /// reddediliyor.</b>
    ///
    /// <para>
    /// Bugünkü arızanın en pahalı yanı mesajıydı:
    /// <c>Unable to resolve service for type 'AlertRuleService'</c>. Okuyan kişi
    /// <b>DI kaydına</b> bakıyor, oysa eksik olan şey bir <b>ayar</b>. Bu, hata
    /// mesajının yanlış yüzeyi işaret etmesi — S04'ün düzelttiği ve FS-b'nin iki
    /// teşhis testiyle koruduğu sınıf.
    /// </para>
    ///
    /// <para>
    /// Ölçüt <i>"reddetti mi"</i> DEĞİL, <b>"ne yazması gerektiğini söyledi
    /// mi"</b>: ortam değişkeninin adı mesajın içinde geçmeli. Yalnızca
    /// <c>false</c> döndüğünü sınayan bir test, mesajı <c>"hata"</c>ya
    /// çevirdiğimizde de yeşil kalırdı.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null, UnreachableControlPlane, "BIZIGO_CLICKHOUSE")]
    [InlineData(UnreachableClickHouse, null, "BIZIGO_CONTROLPLANE")]
    [InlineData(null, null, "BIZIGO_CLICKHOUSE")]
    public void Eksik_ayar_adiyla_reddediliyor(string? clickHouse, string? controlPlane, string beklenenAd)
    {
        var reddedildi = McpCommandHandlers.MissingProductSetting(clickHouse, controlPlane);

        Assert.NotNull(reddedildi);
        Assert.Contains(beklenenAd, reddedildi, StringComparison.Ordinal);

        // MESAJ DI'DAN ŞİKÂYET ETMİYOR. Bugünkü arızanın mesajı tam olarak bunu
        // yapıyordu ve okuyanı yanlış yere gönderiyordu.
        Assert.DoesNotContain("Unable to resolve", reddedildi, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Karşı-kanıt: ayarlar verildiğinde ret YOK.</b>
    ///
    /// <para>
    /// Olmadan yukarıdaki test her zaman reddeden bir uygulamayla da geçerdi —
    /// ve o uygulama ürün yüzeyini hiç açılamaz yapardı. §6'nın kontrol satırı.
    /// </para>
    /// </summary>
    [Fact]
    public void Ayarlar_verildiginde_ret_yok() =>
        Assert.Null(McpCommandHandlers.MissingProductSetting(
            UnreachableClickHouse, UnreachableControlPlane));

    /// <summary>
    /// <b>Simülatör yüzeyi ürün ayarlarını İSTEMİYOR.</b>
    ///
    /// <para>
    /// Ayrı bir test, çünkü ayrı bir gerileme: ürün yüzeyini ayağa kaldırmak
    /// için konan bir ret, bugün <b>çalışan</b> simülatör yüzeyini (ölçüldü:
    /// <c>exit 0</c>) kırabilirdi. Simülatör ClickHouse'a ve kontrol düzlemine
    /// dokunmuyor; ondan bağlantı dizgesi istemek, ilgisiz bir ayarı zorunlu
    /// kılmak olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public void Simulator_yuzeyi_urun_ayarlarini_istemiyor()
    {
        using var services = McpCommandHandlers.BuildServices(
            McpSurface.Simulator, clickHouse: null, controlPlane: null);

        var tools = BizigoMcpServer.Tools(
            McpSurface.Simulator,
            McpCommandHandlers.ToolAssembliesFor(McpSurface.Simulator),
            services);

        Assert.Equal(
            McpExpectedTools.For(McpSurface.Simulator),
            tools.Select(static tool => tool.ToolName).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// <b>Kapsam çözücüsü ürün yüzeyinde kayıtlı.</b>
    ///
    /// <para>
    /// <c>BizigoMcpServer.Apply</c> kimlik isteyen bir araç varsa çözücüyü
    /// <b>kurulumda</b> arıyor ve yoksa patlıyor. Yukarıdaki grafik testi bunu
    /// <c>Tools(...)</c> üzerinden geçiyor — o metot çözücü kapısını
    /// <b>çalıştırmıyor</b>, yalnızca <c>Apply</c> çalıştırıyor. Yani grafik
    /// testi yeşilken sunucu kurulumu hâlâ düşebilirdi.
    /// </para>
    ///
    /// <para>
    /// Bu test <c>CreateOptions</c> üzerinden gidiyor, yani ürün yüzeyinin
    /// <b>gerçekten kurulabildiğini</b> ölçüyor — protokol oturumu hâlâ
    /// açılmıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Urun_yuzeyi_sunucu_secenekleriyle_kurulabiliyor()
    {
        using var services = McpCommandHandlers.BuildServices(
            McpSurface.Product, UnreachableClickHouse, UnreachableControlPlane);

        var options = BizigoMcpServer.CreateOptions(
            McpSurface.Product,
            Boundary,
            McpCommandHandlers.ToolAssembliesFor(McpSurface.Product),
            services);

        Assert.NotNull(options.ToolCollection);
        Assert.NotEmpty(options.ToolCollection!);
    }
}
