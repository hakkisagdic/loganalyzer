using System.Net;
using System.Net.Sockets;
using Bizigo.Contracts.Security;
using Bizigo.Mcp;
using Bizigo.Rca.Models;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Bizigo.Api;

/// <summary>
/// Dinleyici kapısının verebileceği beş cevap. <b>Beşi de bir sonuç</b> — ve
/// üçünün ayrı ayrı adlandırılmasının sebebi ölçülmüş bir tuzak: <i>"geçti"</i>
/// ile <i>"bakacak bir şey bulamadım"</i> aynı isimde durursa kapı, boş küme
/// üzerinde döndüğü gün de yeşil rapor eder.
/// </summary>
public enum McpListenerBoundaryOutcome
{
    /// <summary>
    /// Beyan <see cref="DataBoundary.Internal"/> değil, dolayısıyla
    /// doğrulanacak bir topoloji iddiası yok.
    ///
    /// <para>
    /// <b>Bugün ürün yolunda ulaşılamaz:</b> <c>external</c> beyan edilmiş bir
    /// ürün yüzeyi <see cref="McpBoundaryGate"/>'ten geçemiyor, yani bu hâl
    /// buraya gelmiyor. Yazılı olması gerekiyor çünkü kapının <b>hangi
    /// beyanı</b> doğruladığı kapının kapsamı: topoloji şartı yalnızca
    /// <c>internal</c> iddiasının bedeli.
    /// </para>
    /// </summary>
    NotApplicable,

    /// <summary>
    /// Hiç dinleyici adresi görülmedi. <b>Yeşil değil</b>: kapı bu hâlde bir
    /// şey doğrulamadı, yalnızca doğrulayacak bir şey bulamadı.
    /// </summary>
    NoListener,

    /// <summary>
    /// Çözülen <b>her</b> dinleyici adresi yönlendirilemez. Beyan artık
    /// topolojiyle destekli.
    /// </summary>
    Verified,

    /// <summary>
    /// En az bir adres yönlendirilemez olduğu <b>kanıtlanamadı</b>, ama yazılı
    /// bir gerekçe var. Geçiyor, ve gerekçe koşum kaydına giriyor.
    /// </summary>
    Exempt,

    /// <summary>
    /// En az bir adres yönlendirilemez olduğu kanıtlanamadı ve gerekçe yok.
    /// Beyan ile gerçek arasındaki ilişki <b>bilinmiyor</b> ve bilinmemesi
    /// kabul edilmiyor.
    /// </summary>
    Rejected,
}

/// <summary>Tek bir dinleyici adresinin sınıflandırılmış hâli.</summary>
/// <param name="Address">Kestrel'in bildirdiği adres, olduğu gibi.</param>
/// <param name="Host">Adresten çıkarılan ana makine kısmı.</param>
/// <param name="Verdict">
/// Neden geçtiği ya da neden kanıtlanamadığı — operatörün gördüğü tek metin.
/// </param>
/// <param name="Routable">
/// <see langword="false"/> yalnızca <b>kanıtlanmış</b> yönlendirilemezlik için;
/// joker ve çözülemeyen adresler <see langword="true"/> tarafında duruyor,
/// çünkü kapının bilmediği şey kapının lehine sayılamaz.
/// </param>
public sealed record McpListenerAddress(string Address, string Host, string Verdict, bool Routable);

/// <summary>Kapının cevabı ve o cevabın nasıl kurulduğu.</summary>
/// <param name="Outcome">Beş hâlden biri.</param>
/// <param name="Addresses">Sınıflandırılmış dinleyici adresleri.</param>
/// <param name="Detail">Kayda ve hata metnine giren açıklama.</param>
public sealed record McpListenerBoundaryVerdict(
    McpListenerBoundaryOutcome Outcome,
    IReadOnlyList<McpListenerAddress> Addresses,
    string Detail)
{
    /// <summary>Sunucunun ayakta kalıp kalmadığı.</summary>
    public bool Allowed => Outcome is not McpListenerBoundaryOutcome.Rejected;
}

/// <summary>
/// <b>K6'nın topoloji kapısı</b> (M11) — <i>log verisi kurum dışına çıkmaz.</i>
///
/// <para>
/// M06 <see cref="McpBoundaryGate"/>'i yazdı ve <b>kendi kör noktasını
/// yazdı</b>: <i>"HTTP taşımasında dinlenen adresi doğrulamıyor. Kestrel'in
/// nereye bağlandığı <c>Bizigo.Api</c>'nin yapılandırması ve bu kapının
/// göremediği yer."</i> Sonuç ölçülebilir bir hâl: <c>Mcp:DataBoundary=Internal</c>
/// beyanı kabul ediliyor, sunucu <c>0.0.0.0</c> üzerinde koşuyor olabiliyor, ve
/// <b>hiçbir yerde kırmızı yanmıyor</b>. Bu sınıf o boşluğu kapatmıyor —
/// <b>yazılı hâle getiriyor</b>, ve aşağıdaki ayrım tam olarak bu yüzden var.
/// </para>
///
/// <h3>Kapının tek mekanik ölçütü ikinci kez yazılmadı</h3>
///
/// <para>
/// Adres sınıfı ölçütü T42'nin <see cref="ModelBoundaryGate.YonlendirilemezMi"/>'si
/// ve buradan <b>çağrılıyor</b> (§9: ikinci kopya yazma). İki kopya, birinin
/// <c>172.16/12</c> sınırını düzelttiği gün diğerinin yanlış kalması demekti.
/// </para>
///
/// <para>
/// <b>Bilinen borç:</b> yüklem <c>Bizigo.Rca.Models</c> içinde yaşıyor ve artık
/// iki farklı alanın kapısı onu çağırıyor. Doğru evi
/// <c>Bizigo.Contracts.Security</c> — <see cref="DataBoundary"/>'nin yanı;
/// taşımak T42'nin dosyasını değiştirmeyi gerektirdiği için bu ticket'ta
/// yapılmadı, gizlenmedi.
/// </para>
///
/// <h3>Üç kural</h3>
/// <list type="number">
/// <item><b>Kanıtlanmış yönlendirilemezlik geçiyor.</b> Loopback, RFC1918,
/// bağlantı-yerel, IPv6 ULA — ve çözülen <b>her</b> adres için, bir tanesi
/// bile değil.</item>
/// <item><b>Joker bağlama (<c>0.0.0.0</c>, <c>[::]</c>, <c>+</c>, <c>*</c>)
/// KANIT DEĞİL.</b> Süreç içinden bakıldığında joker bir bağlamanın dışa açık
/// olup olmadığı <b>bilinemiyor</b>: cevabı container ağı, publish kuralları ve
/// ana makinenin güvenlik duvarı veriyor, üçü de bu sürecin göremediği yerde.
/// Mekanik çıkarım (<i>"joker ⇒ herkese açık"</i>) yanlış pozitif, tersi
/// (<i>"container'da normaldir ⇒ iç ağ"</i>) sessiz yanlış olurdu. Kapı
/// üçüncüyü seçiyor: <b>bilinmiyor, o yüzden yazılı bir gerekçe istiyor.</b>
/// Kalıp M06'nın stdio kararının aynısı — mekanik çıkarımı beyanla
/// değiştirmek.</item>
/// <item><b>Gerekçe muafiyet, sessizlik değil.</b>
/// <see cref="OverrideReasonKey"/> yazılıysa geçiyor ve gerekçe koşum kaydına
/// giriyor; yazılı değilse sunucu <b>ayağa kalkmıyor</b>. Kalıp T42'nin
/// <c>BoundaryOverrideReason</c>'ı.</item>
/// </list>
///
/// <h3>Bu kapının TUTAMADIKLARI — yazılı olması şart</h3>
///
/// <list type="bullet">
/// <item>
/// <b>Ters vekil arkasındaki <c>127.0.0.1</c>'i göremiyor</b>, ve bu kapının en
/// önemli kör noktası. Yalnızca loopback dinleyen bir sunucu, önünde dışa açık
/// bir nginx varsa <b>herkese açıktır</b> ve kapı ona <c>Verified</c> diyor.
/// Kapatılamıyor: vekilin varlığı ve dinlediği arayüz bu sürecin bilgi
/// alanında değil. Kapatmaya çalışmak <i>"X-Forwarded-For görüyorsam vekil
/// vardır"</i> gibi bir sezgi yazmak olurdu — kanıt değil, ve tam olarak
/// susturulmayı öğrenen bekçiyi üretir.
/// </item>
/// <item>
/// <b>Ağın kendisini görmüyor, adresin sınıfını görüyor.</b> RFC1918 bir adres
/// kurumun ağı olmak zorunda değil; VPN, tünel ya da paylaşılan bir bulut
/// segmenti de o uzayda durur. T42'nin aynı beyanı.
/// </item>
/// <item>
/// <b>Yalan söyleyen bir yöneticiyi tutmuyor.</b> Muafiyet gerekçesini yazan
/// kişi kurumun kendisi (K10, K16). Kapının işi kararı imkânsız kılmak değil,
/// <b>kazara</b> olmasını imkânsız kılmak.
/// </item>
/// <item>
/// <b>Yalnızca MCP'nin beyanı yüzünden koşuyor, ama ölçtüğü şey SÜRECİN
/// TAMAMI.</b> Tek Kestrel var: <c>/mcp</c> ile diğer 45 uç aynı adresleri
/// dinliyor ve kapı ikisini ayırt edemiyor. Sonucu bilinçli olarak kabul
/// edildi — MCP <c>internal</c> beyan ettiği an ürünün dinleyicisi de o beyanın
/// kapsamına giriyor. Tersi, beyanı yalnızca kendi yolu için doğrulayan ve o
/// yolun aynı sokette durduğunu görmezden gelen bir kapı olurdu.
/// </item>
/// <item>
/// <b>Kalkıştan sonra değişen bir bağlamayı görmüyor.</b> Ölçüm bir kez, host
/// başladığında yapılıyor; süreç ömrü boyunca yeniden bakılmıyor. Kestrel
/// çalışırken yeni bir uç eklemenin yolu bu üründe yok, yani bugün kurbanı
/// olmayan bir sınır — ama <c>UseUrls</c>'in dinamik bir kaynağa bağlandığı gün
/// gerçek olur.
/// </item>
/// </list>
/// </summary>
/// <param name="resolver">
/// Ana makine adı çözücüsü — T42'nin <see cref="IEndpointAddressResolver"/>'ı.
/// Ayrı olmasının sebebi orada yazılı: kapı DNS'e bağlı olsa
/// <b>ölçülemezdi</b>.
/// </param>
public sealed class McpListenerBoundaryGate(IEndpointAddressResolver resolver)
{
    /// <summary>
    /// Muafiyetin okunduğu yapılandırma anahtarı.
    ///
    /// <para>
    /// <c>Mcp:</c> öneki altında, <see cref="BizigoMcpSetup.DataBoundaryKey"/>
    /// ile aynı bölümde: beyan ile beyanın muafiyeti yan yana duruyor, yani
    /// birini okuyan diğerini de görüyor.
    /// </para>
    /// </summary>
    public const string OverrideReasonKey = "Mcp:ListenerBoundaryOverrideReason";

    private readonly IEndpointAddressResolver resolver =
        resolver ?? throw new ArgumentNullException(nameof(resolver));

    /// <summary>
    /// Dinleyici adreslerini beyana karşı sınar. <b>Fırlatmıyor</b> — kararı
    /// çağıran veriyor, çünkü bu tip aynı zamanda ölçümün öznesi.
    /// </summary>
    /// <param name="addresses">
    /// <see cref="IServerAddressesFeature.Addresses"/> içeriği, olduğu gibi.
    /// </param>
    /// <param name="declaration">Yüzeyin K6 beyanı (M06).</param>
    /// <param name="overrideReason">
    /// <see cref="OverrideReasonKey"/> değeri; boş ise muafiyet yok.
    /// </param>
    /// <param name="cancellationToken">İptal.</param>
    public async ValueTask<McpListenerBoundaryVerdict> InspectAsync(
        IReadOnlyList<string> addresses,
        McpBoundaryDeclaration declaration,
        string? overrideReason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(declaration);

        if (declaration.Boundary != DataBoundary.Internal)
        {
            return new McpListenerBoundaryVerdict(
                McpListenerBoundaryOutcome.NotApplicable,
                [],
                $"Beyan `{declaration.Boundary}` ({declaration.Basis}); topoloji şartı "
                + "yalnızca `internal` iddiasının bedeli.");
        }

        if (addresses.Count == 0)
        {
            return new McpListenerBoundaryVerdict(
                McpListenerBoundaryOutcome.NoListener,
                [],
                "Hiç dinleyici adresi bildirilmedi. Bu bir doğrulama DEĞİL: kapı "
                + "bakacak bir şey bulamadı. Bugün bilinen tek sebebi `TestServer` "
                + "(ölçüldü — `TestServer_ile_kosan_host_topolojiyi_dogrulamiyor`); "
                + "gerçek bir Kestrel bağlaması adresini bildiriyor.");
        }

        var classified = new List<McpListenerAddress>(addresses.Count);

        foreach (var address in addresses)
        {
            classified.Add(await ClassifyAsync(address, cancellationToken).ConfigureAwait(false));
        }

        var unproven = classified.Where(static a => a.Routable).ToArray();

        if (unproven.Length == 0)
        {
            return new McpListenerBoundaryVerdict(
                McpListenerBoundaryOutcome.Verified,
                classified,
                "Dinleyici adreslerinin tamamı yönlendirilemez: "
                + string.Join(" · ", classified.Select(static a => a.Verdict)));
        }

        if (!string.IsNullOrWhiteSpace(overrideReason))
        {
            return new McpListenerBoundaryVerdict(
                McpListenerBoundaryOutcome.Exempt,
                classified,
                "Dinleyici adresi yönlendirilemez olduğu KANITLANMADI ve yazılı bir "
                + $"gerekçe var. Adresler: {string.Join(" · ", unproven.Select(static a => a.Verdict))}. "
                + $"Gerekçe (`{OverrideReasonKey}`): {overrideReason!.Trim()}");
        }

        return new McpListenerBoundaryVerdict(
            McpListenerBoundaryOutcome.Rejected,
            classified,
            $"MCP yüzeyi `internal` beyan edilmiş ({declaration.Basis}) ama dinleyici "
            + "adresinin kurum içinde kaldığı KANITLANAMIYOR: "
            + string.Join(" · ", unproven.Select(static a => a.Verdict))
            + ". K6 gereği log verisi kurum dışına çıkmıyor ve beyan tek başına bunu "
            + "göstermiyor — bugüne kadar göstermiş SAYILIYORDU, kapının var olma sebebi "
            + "bu. Üç yol var: (1) dinleyiciyi yönlendirilemez bir adrese bağlayın "
            + "(`ASPNETCORE_URLS=http://127.0.0.1:5080`), (2) joker bağlama bir dış "
            + "sınırla (container ağı, publish kuralı, güvenlik duvarı) korunuyorsa "
            + $"gerekçesini `{OverrideReasonKey}` ile YAZIN — süreç o sınırı göremiyor, "
            + "(3) yüzey gerçekten kurum dışına açıksa beyanı düzeltin; `external` bir "
            + "ürün yüzeyi zaten reddediliyor.");
    }

    private async ValueTask<McpListenerAddress> ClassifyAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var host = ExtractHost(address);

        if (host.Length == 0)
        {
            return new McpListenerAddress(
                address,
                host,
                $"`{address}` ayrıştırılamadı — ana makine kısmı okunamayan bir adres "
                + "kanıt sayılmıyor",
                Routable: true);
        }

        if (IsWildcard(host))
        {
            return new McpListenerAddress(
                address,
                host,
                $"`{address}` JOKER bağlama ({host}) — her arayüzü dinliyor ve hangi "
                + "arayüzlerin dışa açık olduğu süreç içinden bilinemiyor",
                Routable: true);
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            return Classify(address, host, literal);
        }

        IReadOnlyList<IPAddress> resolved;

        try
        {
            resolved = await this.resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException error)
        {
            return new McpListenerAddress(
                address,
                host,
                $"`{address}` çözümlenemedi ({error.Message}) — çözülemeyen bir ad iç ağ "
                + "sayılmıyor",
                Routable: true);
        }

        if (resolved.Count == 0)
        {
            return new McpListenerAddress(
                address,
                host,
                $"`{address}` hiçbir adrese çözülmedi — bilinmeyen bir adres bilinen bir "
                + "iç adres değildir",
                Routable: true);
        }

        var routable = resolved.Where(static a => !ModelBoundaryGate.YonlendirilemezMi(a)).ToArray();

        return routable.Length == 0
            ? new McpListenerAddress(
                address,
                host,
                $"`{address}` yönlendirilemez adreslere çözülüyor ("
                + string.Join(", ", resolved.Select(static a => a.ToString())) + ")",
                Routable: false)
            : new McpListenerAddress(
                address,
                host,
                $"`{address}` genel bir adrese çözülüyor ("
                + string.Join(", ", routable.Select(static a => a.ToString())) + ")",
                Routable: true);
    }

    private static McpListenerAddress Classify(string address, string host, IPAddress literal) =>
        ModelBoundaryGate.YonlendirilemezMi(literal)
            ? new McpListenerAddress(
                address,
                host,
                $"`{address}` yönlendirilemez ({literal})",
                Routable: false)
            : new McpListenerAddress(
                address,
                host,
                $"`{address}` GENEL bir adres ({literal}) — beyan `internal` diyor",
                Routable: true);

    /// <summary>
    /// Kestrel'in bildirdiği adresten ana makine kısmını çıkarır.
    ///
    /// <para>
    /// <see cref="Uri"/> ile yapılmıyor ve sebebi ölçülebilir: Kestrel
    /// <c>http://+:8080</c> ve <c>http://*:8080</c> biçimlerini de bildiriyor,
    /// ikisi de geçerli bir <see cref="Uri"/> ana makinesi değil ve
    /// <see cref="Uri.TryCreate(string, UriKind, out Uri?)"/> bunlarda ya
    /// düşüyor ya ana makineyi boş bırakıyor. Joker bağlamayı ayrıştırma
    /// hatasına çevirmek, kapının en önemli dalını erişilemez yapardı.
    /// </para>
    /// </summary>
    /// <param name="address">Dinleyici adresi.</param>
    public static string ExtractHost(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        var span = address.AsSpan().Trim();

        var scheme = span.IndexOf("://", StringComparison.Ordinal);

        if (scheme >= 0)
        {
            span = span[(scheme + 3)..];
        }

        // IPv6 köşeli parantez içinde: `http://[::1]:5080`. Parantezi burada
        // düşürmek zorunlu — `IPAddress.TryParse("[::1]")` false döner ve adres
        // sessizce "ayrıştırılamadı" tarafına düşerdi.
        if (span.Length > 0 && span[0] == '[')
        {
            var close = span.IndexOf(']');

            return close > 1 ? span[1..close].ToString() : string.Empty;
        }

        var stop = span.IndexOfAny(':', '/', '?');

        if (stop >= 0)
        {
            span = span[..stop];
        }

        return span.ToString();    }

    /// <summary>
    /// Joker bağlama mı. <c>0.0.0.0</c> ve <c>::</c> de burada: ikisi
    /// <see cref="IPAddress"/> olarak ayrıştırılabiliyor ama <b>bir arayüzü
    /// değil hepsini</b> gösteriyor, yani sınıflandırılacak bir adres değil.
    /// </summary>
    /// <param name="host">Ana makine kısmı.</param>
    public static bool IsWildcard(string host) =>
        host is "+" or "*" or "0.0.0.0" or "::" or "[::]" or "::0" or "0";
}

/// <summary>
/// Kapıyı <b>host başladıktan sonra</b> koşturan kanca (M11).
///
/// <h3>Neden ikinci bir kapı, neden birincisini geciktirmedik</h3>
///
/// <para>
/// Dinleyici adresi <see cref="IServerAddressesFeature"/> içinde ve o özellik
/// <b>Kestrel bağlandıktan sonra</b> doluyor. M06'nın kapısı ise kayıt anında
/// koşuyor (<c>AddBizigoMcpCore</c>) — yani soru bir <b>sıra</b> sorusu ve iki
/// cevabı var. Bedelleri:
/// </para>
///
/// <list type="table">
/// <item>
/// <term>Kapıyı geciktirmek</term>
/// <description>
/// Tek kapı, tek yer. Ama <b>üç şeyi birden bozuyordu.</b> (1) M06 beyan
/// reddini bilerek <b>kurulum anına</b> çekti — <i>"yanlış yapılandırılmış bir
/// sunucu hiç başlamıyor, yarım başlamıyor"</i>; kapıyı kalkışa taşımak o
/// kararı geri alır. (2) Kapı taşımadan bağımsız ve <c>Bizigo.Cli</c> de onu
/// çağırıyor; stdio'da <see cref="IServerAddressesFeature"/> diye bir şey yok,
/// yani ortak kapı ASP.NET'e bağlanmış olurdu — <c>Bizigo.Mcp</c>'nin web
/// çatısından uzak tutulması ölçülmüş bir karar (beş CS0433). (3) Çağrı yeri
/// <c>BizigoMcpServer.Apply</c> ve o dosya M07'nin elinde.
/// </description>
/// </item>
/// <item>
/// <term>İkinci kapı (seçilen)</term>
/// <description>
/// Beyanın <b>dürüstlüğü</b> kurulum anında, <b>topolojisi</b> kalkışta
/// sınanıyor; ikisi ayrı soru ve ayrı bilgiye ihtiyaç duyuyor. Bedeli iki
/// yerde bakmak — ve bir kapının ne zaman koştuğunu bilmeyen okuyucunun
/// ikisini tek sanması. Bu belge o bedeli ödemek için var.
/// </description>
/// </item>
/// </list>
///
/// <para>
/// <b>Seçimin ölçülen bedeli:</b> <see cref="StartedAsync"/> bütün
/// <c>StartAsync</c>'ler bittikten sonra koşuyor, yani <b>Kestrel çoktan
/// bağlanmış oluyor</b>. Reddedilen bir topoloji sokete hiç açılmamış olmuyor;
/// açılıp milisaniyeler içinde kapanıyor (fırlatılan istisna
/// <c>app.RunAsync()</c>'ten çıkıyor ve süreç sıfırdan farklı bir kodla
/// ölüyor). O pencerede gelen bir isteğin <c>/mcp</c>'ye ulaşması için ayrıca
/// kimlik doğrulamasından geçmesi gerekiyor. Pencereyi sıfırlamanın yolu
/// bağlamadan önce adresi bilmek, o da yapılandırmayı <b>tahmin etmek</b>
/// demekti (<c>ASPNETCORE_URLS</c>, <c>--urls</c>, <c>Kestrel:Endpoints</c>,
/// <c>UseUrls</c>, <c>launchSettings</c>) — tam olması gereken bir liste, ve bu
/// depo eksik listelerin bedelini beş kez ödedi.
/// </para>
/// </summary>
internal sealed class McpListenerBoundaryCheck(
    IServer server,
    IConfiguration configuration,
    McpListenerBoundaryGate gate,
    ILogger<McpListenerBoundaryCheck> logger) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Kapının koştuğu yer. <b>Fırlatmak bir tercih:</b> reddedilen bir
    /// topolojiyle sunucunun ayakta kalması, kapının hiç olmamasıyla aynı
    /// sonucu verirdi (M06'nın aynı cümlesi).
    /// </summary>
    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;

        var verdict = await gate.InspectAsync(
            addresses is null ? [] : [.. addresses],
            BizigoMcpSetup.ReadBoundary(configuration),
            configuration[McpListenerBoundaryGate.OverrideReasonKey],
            cancellationToken).ConfigureAwait(false);

        if (!verdict.Allowed)
        {
            throw new InvalidOperationException(verdict.Detail);
        }

        // Kayıt seviyesi sonuca göre: `Verified` bir bilgi, `Exempt` ve
        // `NoListener` bir UYARI. İkisini aynı seviyede basmak, muafiyeti
        // doğrulamadan ayırt edilemez yapardı — ve bu depoda sessiz muafiyet,
        // muafiyetin olmamasından tehlikeli.
        if (verdict.Outcome is McpListenerBoundaryOutcome.Verified)
        {
            logger.LogInformation("MCP dinleyici sınırı doğrulandı. {Detail}", verdict.Detail);
        }
        else
        {
            logger.LogWarning(
                "MCP dinleyici sınırı DOĞRULANMADI ({Outcome}). {Detail}",
                verdict.Outcome,
                verdict.Detail);
        }
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
