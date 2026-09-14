using System.Net;
using System.Text.RegularExpressions;
using Bizigo.Api;
using Bizigo.Contracts.Security;
using Bizigo.Mcp;
using Bizigo.Rca.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>M11 · K6'nın topoloji kapısı</b> — <i>log verisi kurum dışına çıkmaz.</i>
///
/// <para>
/// M06 kapıyı yazdı ve kör noktasını da yazdı: beyanın <b>dürüstlüğü</b>
/// doğrulanıyor (<c>Unspecified</c> reddi, <c>external</c> reddi),
/// <b>topolojisi</b> doğrulanmıyor. Yani <c>Mcp:DataBoundary=Internal</c>
/// kabul ediliyor ve sunucu <c>0.0.0.0</c> üzerinde koşuyor olabiliyor.
/// Aşağıdaki testler o boşluğun kapandığını değil — <b>görünür olduğunu</b>
/// tutuyor: kapının cevabı üç ayrı hâl (<c>Verified</c>, <c>Exempt</c>,
/// <c>Rejected</c>) ve ikisi arasındaki fark yazılı bir gerekçe.
/// </para>
///
/// <h3>Konteyner yok — ve iki test GERÇEK Kestrel bağlıyor (§2)</h3>
///
/// <para>
/// §2'nin ekseni <i>"hangi paket"</i> değil <b>"konteyner gerekiyor mu"</b>.
/// <see cref="Gercek_kestrel_dinleyici_adresini_kalkista_bildiriyor"/> ve
/// <see cref="Gercek_kestrel_gerekcesiz_joker_baglamada_kalkmiyor"/> döngüsel
/// arayüzde <b>0 numaralı porta</b> bağlanıyor: konteyner yok, dış ikili yok,
/// sabit port yok. Buna karşılık kapının kararının merkezindeki iddiayı —
/// <i>"adres <c>StartedAsync</c> anında biliniyor ve orada fırlatmak kalkışı
/// durduruyor"</i> — yalnızca bu iki test ölçebiliyor. Sahte bir sunucuyla
/// ölçmek, SDK'nın sırasını ölçmek yerine kendi varsayımımızı ölçmek olurdu.
/// </para>
///
/// <para>
/// Adres <b>çözümü</b> ise sahte (<see cref="SahteCozucu"/>): gerçek DNS bir
/// bekçiyi tekrarlanamaz yapardı ve bu kalıp T42'den geliyor.
/// </para>
/// </summary>
public sealed class McpListenerBoundaryTests
{
    /// <summary>
    /// Bugün sevk edilen muafiyet sayısı. <b>Sabitle çivili</b> (T42/T48
    /// kalıbı): muafiyet eklemek iki ayrı bilinçli hareket gerektiriyor —
    /// yapılandırmaya gerekçeyi yazmak, ve bu sayıyı artırmak.
    /// </summary>
    private const int ExpectedExemptCount = 1;

    private static readonly McpBoundaryDeclaration Internal =
        McpBoundaryDeclaration.Declare(DataBoundary.Internal, "test");

    private sealed class SahteCozucu(params string[] addresses) : IEndpointAddressResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<IPAddress>>(
                [.. addresses.Select(IPAddress.Parse)]);
    }

    private static McpListenerBoundaryGate Kapi(params string[] resolvesTo) =>
        new(new SahteCozucu(resolvesTo.Length > 0 ? resolvesTo : ["127.0.0.1"]));

    private static ValueTask<McpListenerBoundaryVerdict> Sina(
        McpListenerBoundaryGate gate,
        string? reason,
        params string[] addresses) =>
        gate.InspectAsync(addresses, Internal, reason, TestContext.Current.CancellationToken);

    // --------------------------------------------------------------- kural 1

    /// <summary>
    /// <b>Kanıtlanmış yönlendirilemezlik geçiyor</b> — ve dört ayrı uzay ayrı
    /// ayrı ölçülüyor.
    ///
    /// <para>
    /// Ölçüt <see cref="ModelBoundaryGate.YonlendirilemezMi"/>, yani T42'nin
    /// yüklemi. Buradaki testin işi o yüklemi ikinci kez sınamak değil
    /// (<c>ModelBoundaryTests</c> onu sınırlarıyla ölçüyor); <b>dinleyici
    /// kapısının onu çağırdığını</b> ve adres biçiminin (URL, köşeli parantezli
    /// IPv6, port) yolda kaybolmadığını ölçmek.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://[::1]:5080")]
    [InlineData("http://10.20.30.40:8080")]
    [InlineData("http://172.16.0.9:8080")]
    [InlineData("http://192.168.1.5:8080")]
    [InlineData("http://[fc00::1]:8080")]
    public async Task Yonlendirilemez_dinleyici_dogrulaniyor(string address)
    {
        var verdict = await Sina(Kapi(), reason: null, address);

        Assert.Equal(McpListenerBoundaryOutcome.Verified, verdict.Outcome);
        Assert.True(verdict.Allowed);
    }

    /// <summary>
    /// <b>Ana makine adı çözülüyor</b> — <c>localhost</c> bir adres değil bir
    /// ad, ve kapı onu sınıflandırmak için çözmek zorunda.
    /// </summary>
    [Fact]
    public async Task Ad_cozulup_siniflandiriliyor()
    {
        var ici = await Sina(Kapi("127.0.0.1"), reason: null, "http://localhost:5058");

        Assert.Equal(McpListenerBoundaryOutcome.Verified, ici.Outcome);

        var disi = await Sina(Kapi("203.0.113.5"), reason: null, "http://api.kurum.example:8080");

        Assert.Equal(McpListenerBoundaryOutcome.Rejected, disi.Outcome);
        Assert.Contains("203.0.113.5", disi.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Çözülemeyen ad iç ağ sayılmıyor.</b> T42'nin aynı kararı: <i>"çözemedim,
    /// iç ağ olmalı"</i> diye bir çıkarım yok.
    /// </summary>
    [Fact]
    public async Task Cozulemeyen_ad_ic_ag_sayilmiyor()
    {
        // Doğrudan kurulum: `Kapi()` yardımcısı boş listeyi loopback'e
        // çeviriyor ve o, tam olarak ölçülmek istenen hâli gizlerdi.
        var gate = new McpListenerBoundaryGate(new SahteCozucu());

        var verdict = await gate.InspectAsync(
            ["http://bilinmeyen.local:8080"], Internal, null, TestContext.Current.CancellationToken);

        Assert.Equal(McpListenerBoundaryOutcome.Rejected, verdict.Outcome);
        Assert.Contains("hiçbir adrese çözülmedi", verdict.Detail, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------- kural 2

    /// <summary>
    /// <b>Joker bağlama KANIT DEĞİL</b> — ve bu ticket'ın var olma sebebi.
    ///
    /// <para>
    /// Dördü de <c>Rejected</c>: <c>0.0.0.0</c> ve <c>::</c> ayrıştırılabilir
    /// birer <see cref="IPAddress"/>, <c>+</c> ve <c>*</c> değil. Aynı hâlde
    /// buluşmaları bilinçli — dördü de <i>"her arayüz"</i> demek.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("http://0.0.0.0:8080")]
    [InlineData("http://[::]:8080")]
    [InlineData("http://+:8080")]
    [InlineData("http://*:8080")]
    public async Task Joker_baglama_gerekcesiz_reddediliyor(string address)
    {
        var verdict = await Sina(Kapi(), reason: null, address);

        Assert.Equal(McpListenerBoundaryOutcome.Rejected, verdict.Outcome);
        Assert.False(verdict.Allowed);

        // Mesaj kapının TEK çıktısı (M06'nın aynı ölçütü): operatörün üç yolu
        // da görmesi gerekiyor, yoksa kapı "bir şey yanlış" deyip bırakıyor.
        Assert.Contains("K6", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains(McpListenerBoundaryGate.OverrideReasonKey, verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_URLS", verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Joker ile genel adres aynı hâle düşüyor ama AYNI ŞEYİ söylemiyor.</b>
    ///
    /// <para>
    /// İkisi de reddediliyor, ve ayrımın kaybolmaması gerekiyor: joker bağlama
    /// <b>bilinmeyen</b>, genel adres <b>bilinen bir çelişki</b>. Operatörün
    /// yapacağı iş farklı — birincide gerekçe yazmak makul, ikincide beyanı ya
    /// da bağlamayı düzeltmek gerekiyor. Kapı ikisini tek metinle anlatsaydı,
    /// joker bağlamayı olduğu gibi bırakıp gerekçe yazmak <i>"genel adres"</i>
    /// hâlinde de doğru görünürdü.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Joker_ile_genel_adres_ayri_anlatiliyor()
    {
        var joker = await Sina(Kapi(), reason: null, "http://0.0.0.0:8080");
        var genel = await Sina(Kapi(), reason: null, "http://203.0.113.5:8080");

        Assert.Equal(McpListenerBoundaryOutcome.Rejected, joker.Outcome);
        Assert.Equal(McpListenerBoundaryOutcome.Rejected, genel.Outcome);

        Assert.Contains("JOKER", joker.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("JOKER", genel.Detail, StringComparison.Ordinal);
        Assert.Contains("GENEL bir adres", genel.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Bir adresin kanıtlanamaması yetiyor.</b> Kalıp T42'nin aynısı:
    /// <i>"çözülen HER adres"</i>. Loopback'in yanına konmuş bir joker bağlama,
    /// dinleyicinin dışa açık olmadığını göstermiyor.
    /// </summary>
    [Fact]
    public async Task Tek_kanitlanamayan_adres_kumeyi_dusuruyor()
    {
        var verdict = await Sina(
            Kapi(), reason: null, "http://127.0.0.1:5080", "http://0.0.0.0:8080");

        Assert.Equal(McpListenerBoundaryOutcome.Rejected, verdict.Outcome);

        // Ve sınıflandırma adres başına duruyor: geçen adresi de suçlayan bir
        // kapı operatörü yanlış yere gönderirdi. Ölçüm `Detail` metninde DEĞİL
        // `Addresses` üzerinde, çünkü ret metni operatöre örnek olarak bir
        // loopback adresi de öneriyor — metin araması o örneği bulur ve testi
        // sessizce anlamsız yapardı.
        Assert.Collection(
            verdict.Addresses,
            first => Assert.False(first.Routable),
            second => Assert.True(second.Routable));

        Assert.Contains("0.0.0.0", verdict.Detail, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------- kural 3

    /// <summary>
    /// <b>Gerekçe muafiyet, sessizlik değil.</b>
    ///
    /// <para>
    /// Aynı joker bağlama, tek fark yazılı gerekçe — ve gerekçe kapının
    /// cevabında <b>görünüyor</b>, yani koşum kaydına giriyor. T42'nin
    /// <c>BoundaryOverrideReason</c>'ının aynı kalıbı: sessiz bir muafiyet,
    /// muafiyetin olmamasından tehlikeli.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Gerekce_yazildiginda_joker_baglama_geciyor()
    {
        var verdict = await Sina(
            Kapi(), "Konteyner ağı; publish kuralı compose'da.", "http://0.0.0.0:8080");

        Assert.Equal(McpListenerBoundaryOutcome.Exempt, verdict.Outcome);
        Assert.True(verdict.Allowed);
        Assert.Contains("Konteyner ağı", verdict.Detail, StringComparison.Ordinal);

        // Muafiyet DOĞRULAMA DEĞİL ve iki hâl ayrı adlarda duruyor: aynı ada
        // düşseler, "kaç kurulum gerçekten doğrulanmış" sorusu sorulamaz hâle
        // gelirdi (§8'in "bir gün kapanacak ile hiç kapanmayacak" ayrımı).
        Assert.NotEqual(McpListenerBoundaryOutcome.Verified, verdict.Outcome);
    }

    /// <summary>
    /// <b>Boş gerekçe gerekçe değil.</b> Beyaz boşluk yazmak muafiyet açmıyor —
    /// aksi hâlde muafiyet, bir anahtarı boş bırakarak <b>kazara</b>
    /// açılabilirdi.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Bos_gerekce_muafiyet_acmiyor(string? reason)
    {
        var verdict = await Sina(Kapi(), reason, "http://0.0.0.0:8080");

        Assert.Equal(McpListenerBoundaryOutcome.Rejected, verdict.Outcome);
    }

    // ------------------------------------------------------------ kapsam

    /// <summary>
    /// <b>Adressiz kalkış bir doğrulama DEĞİL</b> — bu deponun beş kez ödediği
    /// sınıf: boş küme üzerinde dönen bekçi yeşil rapor ediyor.
    ///
    /// <para>
    /// <c>TestServer</c> ile koşan bir host gerçek bir adres bağlamıyor; o hâl
    /// <see cref="McpListenerBoundaryOutcome.NoListener"/> ve
    /// <see cref="McpListenerBoundaryOutcome.Verified"/>'dan <b>ayrı bir ad</b>
    /// taşıyor. Ayrı olmasının bedeli kayıt seviyesinde de görünüyor: bu hâl
    /// bilgi değil <b>uyarı</b> basıyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Adressiz_kalkis_dogrulama_sayilmiyor()
    {
        var verdict = await Kapi().InspectAsync(
            [], Internal, null, TestContext.Current.CancellationToken);

        Assert.Equal(McpListenerBoundaryOutcome.NoListener, verdict.Outcome);
        Assert.NotEqual(McpListenerBoundaryOutcome.Verified, verdict.Outcome);

        // Geçiyor — ama geçmesinin sebebi doğrulama değil, doğrulanacak bir şey
        // olmaması. Metin bunu söylemek zorunda.
        Assert.True(verdict.Allowed);
        Assert.Contains("DEĞİL", verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Topoloji şartı yalnızca <c>internal</c> iddiasının bedeli.</b>
    ///
    /// <para>
    /// <b>Kapsam beyanı:</b> bu dal bugün ürün yolunda <b>ulaşılamaz</b> —
    /// <c>external</c> beyan edilmiş bir ürün yüzeyi <see cref="McpBoundaryGate"/>'ten
    /// geçemiyor, yani host hiç kurulmuyor. Yazılı olmasının sebebi
    /// <c>bizigo-sim</c>'in HTTP yüzeyi (M03) açıldığı gün bu yolun gerçek
    /// olması.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Internal_olmayan_beyan_topoloji_sartina_girmiyor()
    {
        var verdict = await Kapi().InspectAsync(
            ["http://0.0.0.0:8080"],
            McpBoundaryDeclaration.Declare(DataBoundary.External, "test"),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(McpListenerBoundaryOutcome.NotApplicable, verdict.Outcome);
        Assert.True(verdict.Allowed);
    }

    /// <summary>
    /// Adres ayrıştırma — <see cref="Uri"/> ile yapılmamasının gerekçesi
    /// gatede yazılı ve buradaki tablo onu ölçüyor.
    /// </summary>
    [Theory]
    [InlineData("http://0.0.0.0:8080", "0.0.0.0")]
    [InlineData("http://[::1]:5080", "::1")]
    [InlineData("http://[::]:8080", "::")]
    [InlineData("http://+:8080", "+")]
    [InlineData("http://*:8080", "*")]
    [InlineData("https://api.kurum.local:443/mcp", "api.kurum.local")]
    [InlineData("http://localhost:5058", "localhost")]
    [InlineData("http://127.0.0.1", "127.0.0.1")]
    public void Adresin_ana_makine_kismi_okunuyor(string address, string expected) =>
        Assert.Equal(expected, McpListenerBoundaryGate.ExtractHost(address));

    // ------------------------------------------------------------ bağlanmışlık

    /// <summary>
    /// <b>Kapı üretimin kendi kaydında bağlı</b> — T50'nin ayrımı: <i>var
    /// olmak ile bağlı olmak</i>.
    ///
    /// <para>
    /// Yukarıdaki testlerin tamamı kapıyı doğrudan çağırıyor ve tek başına
    /// yanıltıcı olurdu: kapı kusursuz cevap verirken hiç kaydedilmemiş
    /// olabilir. Ölçülen şey <c>AddBizigoMcp</c>'nin — yani MCP'nin HTTP
    /// taşımasını kuran <b>tek yerin</b> — kapıyı da kurduğu.
    /// </para>
    /// </summary>
    [Fact]
    public void Kapi_uretimin_mcp_kaydinda_bagli()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddBizigoMcp(Yapilandirma());

        var hosted = services
            .Where(static d => d.ServiceType == typeof(IHostedService))
            .Select(static d => d.ImplementationType?.Name)
            .ToArray();

        Assert.Contains("McpListenerBoundaryCheck", hosted);

        // Ve kapının kendisi çözülebiliyor: çözücüsü kayıtlı olmasa kanca ilk
        // kalkışta patlardı ve bu, kapının kırmızı yanmasıyla AYNI görünürdü.
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<McpListenerBoundaryGate>());
        Assert.NotNull(provider.GetService<IEndpointAddressResolver>());
    }

    // ------------------------------------------------------------ gerçek Kestrel

    /// <summary>
    /// <b>Dinleyici adresi <c>StartedAsync</c> anında biliniyor</b> — kararın
    /// merkezindeki iddia, ve ölçülmüş hâli.
    ///
    /// <para>
    /// <see cref="IServerAddressesFeature"/> Kestrel bağlandıktan sonra
    /// doluyor; <c>StartedAsync</c> bütün <c>StartAsync</c>'ler bittikten sonra
    /// koşuyor. İkinci cümle SDK'nın sırası hakkında bir <b>varsayım</b>
    /// olurdu — bu test onu ölçüyor: sonda kanca kendi <c>StartedAsync</c>'inde
    /// adresi <b>görüyor</b> ve gördüğü şey 0 numaralı portun bağlandığı
    /// gerçek port.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Gercek_kestrel_dinleyici_adresini_kalkista_bildiriyor()
    {
        var probe = new AddressProbe();

        await using var app = Host("http://127.0.0.1:0", reason: null, probe);

        await app.StartAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(probe.Addresses);
        Assert.NotEmpty(probe.Addresses!);

        // Port 0 gerçek bir porta çözülmüş olmalı: aksi hâlde kanca
        // YAPILANDIRILMIŞ adresi görüyor demektir ve o, Kestrel'in gerçekten
        // bağlandığı adres olmak zorunda değil.
        Assert.DoesNotContain(":0", probe.Addresses![0], StringComparison.Ordinal);

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// <b>Reddedilen topoloji kalkışı DURDURUYOR</b> — ve bu, ikinci kapının
    /// tek başına anlamlı olmasının şartı.
    ///
    /// <para>
    /// Kanca <c>StartedAsync</c>'te fırlatıyor ve istisna
    /// <c>host.StartAsync()</c>'ten çıkıyor. Yani reddedilen bir kurulum
    /// <b>ayakta kalmıyor</b>. Bedeli de burada görünüyor ve gizlenmiyor:
    /// soket bu noktada zaten bağlanmış oluyor — kapı onu kapatıyor, hiç
    /// açılmamasını sağlamıyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Gercek_kestrel_gerekcesiz_joker_baglamada_kalkmiyor()
    {
        await using var app = Host("http://0.0.0.0:0", reason: null, probe: null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("K6", error.Message, StringComparison.Ordinal);
        Assert.Contains("JOKER", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Aynı joker bağlama, tek fark yazılı gerekçe — ve host <b>kalkıyor</b>.
    /// Yukarıdaki testle çifti bir <b>aralık</b> ölçüyor: biri tek başına
    /// ölçülseydi her şeyi düşüren ya da hiçbir şeyi düşürmeyen bir kapı da
    /// geçerdi.
    /// </summary>
    [Fact]
    public async Task Gercek_kestrel_gerekceli_joker_baglamada_kalkiyor()
    {
        await using var app = Host(
            "http://0.0.0.0:0",
            "Ölçüm: konteyner ağı gerekçesinin sonda hâli.",
            probe: null);

        await app.StartAsync(TestContext.Current.CancellationToken);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// <b>`TestServer` ile koşan host topolojiyi DOĞRULAMIYOR</b> — ve bu
    /// cümle bir çıkarım değil, ölçüm.
    ///
    /// <para>
    /// Bu paketteki mevcut MCP host'ları (<c>McpHttpTransportTests</c>,
    /// <c>McpIdentityTests</c>) <c>UseTestServer()</c> ile koşuyor ve kapı
    /// onlarda da çalışıyor. Kapı orada ne diyor sorusunun cevabı
    /// <b>kapsamın kendisi</b>: bir gün <see cref="McpListenerBoundaryOutcome.NoListener"/>
    /// hâli <see cref="McpListenerBoundaryOutcome.Verified"/>'a katlanırsa,
    /// süreç içinde koşan onlarca host sessizce <i>"doğrulandı"</i> sayılır.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TestServer_ile_kosan_host_topolojiyi_dogrulamiyor()
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BizigoMcpSetup.DataBoundaryKey] = nameof(DataBoundary.Internal),
        });
        builder.Services.AddBizigoMcp(builder.Configuration);

        await using var app = builder.Build();

        await app.StartAsync(TestContext.Current.CancellationToken);

        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;

        var verdict = await Kapi().InspectAsync(
            addresses is null ? [] : [.. addresses],
            Internal,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(McpListenerBoundaryOutcome.NoListener, verdict.Outcome);

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Üretimin kendi MCP kaydını taşıyan asgari bir host. <c>MapBizigoMcp</c>
    /// çağrılmıyor: ölçülen şey <b>kalkış</b>, uç davranışı değil.
    /// </summary>
    private static WebApplication Host(string url, string? reason, AddressProbe? probe)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);

        var settings = new Dictionary<string, string?>
        {
            [BizigoMcpSetup.DataBoundaryKey] = nameof(DataBoundary.Internal),
        };

        if (reason is not null)
        {
            settings[McpListenerBoundaryGate.OverrideReasonKey] = reason;
        }

        builder.Configuration.AddInMemoryCollection(settings);

        builder.Services.AddBizigoMcp(builder.Configuration);

        if (probe is not null)
        {
            // `IServer` sondaya YAPICIDAN verilemiyor (örnek DI'dan önce
            // kuruluyor), o yüzden kayıt anında bağlanıyor. Kayıt sırası önemli
            // değil: ölçülen şey `StartedAsync` AŞAMASI, kancalar arası sıra
            // değil.
            builder.Services.AddSingleton<IHostedService>(services =>
            {
                probe.Bind(services.GetRequiredService<IServer>());

                return probe;
            });
        }

        return builder.Build();
    }

    /// <summary>
    /// Kancanın gördüğü şeyi <b>aynı yaşam döngüsü aşamasında</b> kaydeden
    /// sonda. Kapının kendi içinden ölçmek yerine ayrı bir sonda kullanılıyor
    /// çünkü ölçülen iddia kapıya değil <b>aşamaya</b> ait.
    /// </summary>
    private sealed class AddressProbe : IHostedLifecycleService
    {
        private IServer? server;

        public IReadOnlyList<string>? Addresses { get; private set; }

        /// <summary>
        /// <see cref="IServer"/> yapıcıdan alınamıyor (sonda örneği DI'dan
        /// önce kuruluyor), o yüzden <see cref="StartAsync"/>'te değil
        /// <see cref="Bind"/> ile veriliyor.
        /// </summary>
        public void Bind(IServer instance) => server = instance;

        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken)
        {
            Addresses = [.. server?.Features.Get<IServerAddressesFeature>()?.Addresses ?? []];

            return Task.CompletedTask;
        }

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // ------------------------------------------------------------ sevk edilen hâl

    /// <summary>
    /// <b>Sevk edilen muafiyet sayısı çivili — ve muafiyet, onu gerektiren
    /// bağlamaya BAĞLI.</b>
    ///
    /// <para>
    /// Bekçi iki yönlü ve asıl değeri ikinci yönde: <c>Dockerfile</c> joker
    /// bağlıyorsa gerekçe <b>zorunlu</b>, joker bağlamıyorsa gerekçe
    /// <b>yasak</b>. Tek yönlü olsaydı, bir gün bağlama loopback'e çekildiğinde
    /// gerekçe dosyada kalır ve <i>"bu kurulum muaf"</i> diye okunmaya devam
    /// ederdi — bu depoda muafiyetin kendi gerekçesinden uzun yaşaması ölçülmüş
    /// bir olay (T53'ün <c>KnownDivergence</c> girişleri).
    /// </para>
    /// </summary>
    [Fact]
    public void Sevk_edilen_muafiyet_baglamasina_bagli_ve_sayisi_civili()
    {
        var dockerfile = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "src", "Bizigo.Api", "Dockerfile"));

        var urls = Regex.Match(
            dockerfile,
            @"^ENV\s+ASPNETCORE_URLS=(?<url>\S+)",
            RegexOptions.Multiline,
            TimeSpan.FromSeconds(2));

        Assert.True(urls.Success, "Dockerfile `ENV ASPNETCORE_URLS=` satırı taşımıyor.");

        var host = McpListenerBoundaryGate.ExtractHost(urls.Groups["url"].Value);
        var wildcard = McpListenerBoundaryGate.IsWildcard(host);

        var reason = Regex.Match(
            dockerfile,
            @"^ENV\s+Mcp__ListenerBoundaryOverrideReason=""(?<reason>[^""]+)""",
            RegexOptions.Multiline,
            TimeSpan.FromSeconds(2));

        Assert.Equal(wildcard, reason.Success);

        if (wildcard)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(reason.Groups["reason"].Value),
                "Joker bağlamanın gerekçesi boş olamaz.");
        }

        // İkinci yüzey: yapılandırma dosyaları. `//` ile başlayan belge
        // satırları AYRI bir anahtara düşüyor, yani anahtarı GERÇEKTEN ayarlayan
        // dosya sayılıyor — metin araması yapan bir bekçi belgeyi muafiyet
        // sanardı.
        var configured = Directory
            .EnumerateFiles(
                Path.Combine(RepositoryLayout.Root, "src", "Bizigo.Api"), "appsettings*.json")
            .Count(path => !string.IsNullOrWhiteSpace(
                new ConfigurationBuilder().AddJsonFile(path).Build()
                    [McpListenerBoundaryGate.OverrideReasonKey]));

        var composed = File
            .ReadAllLines(RepositoryLayout.ComposeFile)
            .Count(line => line.Contains("Mcp__ListenerBoundaryOverrideReason", StringComparison.Ordinal));

        Assert.Equal(
            ExpectedExemptCount,
            (reason.Success ? 1 : 0) + configured + composed);
    }

    /// <summary>
    /// <b>Bekçinin kapsam beyanı:</b> compose bugün <c>ASPNETCORE_URLS</c>'i
    /// ezmiyor, yani konteynerin bağlaması imajın kararı ve yukarıdaki bekçi
    /// onu görüyor. Compose bir gün adresi ezmeye başlarsa bu test kırmızı
    /// yanıyor — ve doğru davranış bekçiyi <b>genişletmek</b>, çünkü o hâlde
    /// muafiyetin gerekçesi imajda, bağlama compose'da durur ve ikisi ayrı ayrı
    /// değişebilir.
    /// </summary>
    [Fact]
    public void Compose_dinleyici_adresini_ezmiyor()
    {
        var compose = File.ReadAllText(RepositoryLayout.ComposeFile);

        Assert.DoesNotContain("ASPNETCORE_URLS", compose, StringComparison.Ordinal);
    }

    private static IConfiguration Yapilandirma() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BizigoMcpSetup.DataBoundaryKey] = nameof(DataBoundary.Internal),
            })
            .Build();
}
