using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Bizigo.Rca.Models;

/// <summary>
/// Bir ana makinenin adreslerini çözen taraf — kapının tek dış bağımlılığı ve
/// testte değiştirilebilir olmasının tek sebebi.
///
/// <para>
/// Ayrı bir arayüz olmasa kapı DNS'e bağlı olurdu ve <b>ölçülemezdi</b>: bir
/// bekçinin kırmızı yanabildiğini göstermek için ona yönlendirilebilir bir
/// adres verebilmek gerekiyor.
/// </para>
/// </summary>
public interface IEndpointAddressResolver
{
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default);
}

/// <summary>Varsayılan çözücü — DNS.</summary>
public sealed class DnsEndpointAddressResolver : IEndpointAddressResolver
{
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Kapının verdiği cevap. <b>Reddi de bir sonuç</b> — sessizce <see langword="null"/>
/// dönmek "neden kullanılamadı" sorusunu cevapsız bırakırdı.
/// </summary>
public sealed record ModelBoundaryVerdict(ModelEndpoint? Endpoint, string? Rejection)
{
    public bool Allowed => Endpoint is not null;
}

/// <summary>
/// <b>K6'nın kapısı</b> — <i>log verisi kurum dışına çıkmaz</i> (T42).
///
/// <para>
/// Bugüne kadar K6 bir <b>cümleydi</b>: mimari karar tablosunda yazılıydı ve
/// onu tutan hiçbir mekanizma yoktu. Bu sınıf o cümleyi bir kapıya çeviriyor.
/// </para>
///
/// <h3>Kapı hangi eksende duruyor</h3>
///
/// <para>
/// <b>Yerel/uzak ekseninde değil.</b> K6'nın kendi metni uzak bir GPU kümesini
/// açıkça kapsıyor. Yasak olan uzaklık değil <b>kurum dışına çıkmak</b>, ve o
/// eksende yerel Ollama ile kurum içi GPU kümesi aynı yerde duruyor.
/// </para>
///
/// <para>
/// <b>Ve kapı içerik düzeyi ekseninde de değil.</b> Kurum dışı bir uçta hiçbir
/// düzey geçmiyor — <c>summary</c> dahil. Özet de müşteri verisi: host adı,
/// sahiplik grubu, topoloji taşıyor. Kapıyı düzey eksenine kurmak K6'ya
/// düzeylerden birini istisna yazmak olurdu ve K6 istisna kaldırmıyor.
/// Düzeyin kendi ekseni ayrı ve orada karar <b>kurumun</b>
/// (<see cref="PromptContentLevel"/>).
/// </para>
///
/// <h3>Üç kural</h3>
/// <list type="number">
/// <item><b>Beyan zorunlu.</b> <see cref="ModelDataBoundary.Unspecified"/>
/// reddediliyor. Beyansız bir ucu "iç ağ" saymak, kimsenin karar vermediği bir
/// yerde ürünün en büyük sözünü boşa çıkarırdı.</item>
/// <item><b>Kurum dışı beyan reddediliyor.</b> Uç kurulabiliyor ama RCA
/// kullanamıyor — ve reddin sebebi kayda geçiyor.</item>
/// <item><b>İç beyan doğrulanıyor.</b> Çözülen <b>her</b> adres
/// yönlendirilemeyen olmak zorunda. Bir tanesi bile genel adresse beyan ile
/// gerçek çelişiyor demektir ve çelişki reddediliyor.</item>
/// </list>
///
/// <h3>Bu kapının TUTAMADIKLARI — yazılı olması şart</h3>
///
/// <list type="bullet">
/// <item><b>Yalan söyleyen bir yöneticiyi tutmuyor</b>, ve tutamaz. Ürün tek
/// kurum / tek tenant (K10, K16); yapılandırmayı yazan kişi <b>kurumun
/// kendisi</b>. Kapının işi kararı imkânsız kılmak değil, <b>kazara</b>
/// olmasını imkânsız kılmak ve bilinçli olanı <b>görünür</b> kılmak.</item>
/// <item><b>Adresi doğruluyor, kurumu değil.</b> Özel adres uzayında duran bir
/// tünelin ucu dışarısı olabilir; kapı bunu göremez.</item>
/// <item><b>Kurumun kendi AS'indeki yönlendirilebilir adres yanlış yere
/// düşer.</b> Bilinen bir yanlış pozitif ve çözümü muafiyet — gerekçe
/// yazılmadan açılmıyor (<see cref="ModelEndpointOptions.BoundaryOverrideReason"/>).</item>
/// <item><b>Genel LLM sağlayıcılarının bir listesi YOK</b> ve bilinçli yok.
/// Tam olması gereken bir liste, tam olmadığı gün bekçiyi körleştirir — bu
/// depo o dersi beş kez ödedi. Adres sınıfı ölçütü mekanik ve kendi sınıfı
/// için eksiksiz.</item>
/// </list>
/// </summary>
public sealed class ModelBoundaryGate(IEndpointAddressResolver resolver)
{
    private readonly IEndpointAddressResolver _resolver =
        resolver ?? throw new ArgumentNullException(nameof(resolver));

    public async ValueTask<ModelBoundaryVerdict> VerifyAsync(
        ModelEndpointOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Zorunlu alanlar burada sınanıyor, seçenek bağlamada değil: kapı tek
        // yer ve testte DI olmadan koşuyor. İki yerde sınanmaları, birinin
        // gevşediği gün diğerinin fark edilmemesi demekti.
        if (string.IsNullOrWhiteSpace(options.Name) || string.IsNullOrWhiteSpace(options.Model))
        {
            return Reddet(options, "`Name` ve `Model` boş olamaz — hangi ucun ne koşturduğu kayda geçemez.");
        }

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri))
        {
            return Reddet(options, $"`BaseUrl` çözümlenemedi: '{options.BaseUrl}'.");
        }

        switch (options.DataBoundary)
        {
            case ModelDataBoundary.Unspecified:
                return Reddet(
                    options,
                    "`DataBoundary` beyan edilmemiş. K6 bir varsayılan kabul etmiyor: " +
                    "beyansız bir uç 'iç ağ' sayılsaydı, kurumun en büyük sözü kimse " +
                    "karar vermeden boşa çıkardı. `internal` ya da `external` yazılmalı.");

            case ModelDataBoundary.External:
                return Reddet(
                    options,
                    "Uç `external` beyan edilmiş. K6 gereği kurum dışına log verisi " +
                    "çıkmıyor ve bu HİÇBİR içerik düzeyi için esnemiyor — `summary` " +
                    "dahil, çünkü özet de o verinin türevi.");

            case ModelDataBoundary.Internal:
                break;

            default:
                return Reddet(options, $"Bilinmeyen `DataBoundary` değeri: {options.DataBoundary}.");
        }

        if (!string.IsNullOrWhiteSpace(options.BoundaryOverrideReason))
        {
            // Muafiyet: adres doğrulaması atlanıyor ama GEREKÇE koşum kaydına
            // giriyor. Sessiz bir muafiyet, muafiyetin olmamasından tehlikeli.
            return new ModelBoundaryVerdict(
                ModelEndpoint.Verified(options, uri, [], options.BoundaryOverrideReason!.Trim()),
                null);
        }

        IReadOnlyList<IPAddress> addresses;

        try
        {
            addresses = await _resolver.ResolveAsync(uri.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            return Reddet(options, $"'{uri.Host}' çözümlenemedi: {ex.Message}. Çözülemeyen bir ad iç ağ sayılmıyor.");
        }

        if (addresses.Count == 0)
        {
            return Reddet(
                options,
                $"'{uri.Host}' hiçbir adrese çözülmedi. **Çözemedim, iç ağ olmalı** diye bir çıkarım yok: " +
                "bilinmeyen bir adres bilinen bir iç adres değildir.");
        }

        var disari = addresses.Where(a => !YonlendirilemezMi(a)).ToArray();

        if (disari.Length > 0)
        {
            return Reddet(
                options,
                $"`internal` beyan edilmiş ama '{uri.Host}' genel bir adrese çözülüyor: " +
                string.Join(", ", disari.Select(a => a.ToString())) +
                ". Beyan ile gerçek çelişiyor. Kurumun kendi yönlendirilebilir adresiyse " +
                "`BoundaryOverrideReason` ile gerekçe yazılmalı.");
        }

        return new ModelBoundaryVerdict(ModelEndpoint.Verified(options, uri, addresses, null), null);
    }

    private static ModelBoundaryVerdict Reddet(ModelEndpointOptions options, string sebep) =>
        new(null, string.Create(CultureInfo.InvariantCulture, $"Model ucu '{options.Name}' kullanılamıyor. {sebep}"));

    /// <summary>
    /// Adres yönlendirilemez mi — loopback, RFC1918 özel, bağlantı-yerel,
    /// IPv6 benzersiz-yerel (<c>fc00::/7</c>) ve IPv4'e eşlenmiş hâlleri.
    ///
    /// <para>
    /// <c>public</c> çünkü kapının <b>tek mekanik ölçütü</b> bu ve sınırın
    /// doğru yerde olduğu ayrı ayrı sınanabilmeli: <c>172.16/12</c>'nin bir
    /// yanı iç, öbür yanı dış.
    /// </para>
    /// </summary>
    public static bool YonlendirilemezMi(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();

            return b[0] switch
            {
                10 => true,
                127 => true,
                172 => b[1] >= 16 && b[1] <= 31,
                192 => b[1] == 168,
                169 => b[1] == 254,   // bağlantı-yerel
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7 benzersiz-yerel, fe80::/10 bağlantı-yerel.
            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
        }

        return false;
    }
}
