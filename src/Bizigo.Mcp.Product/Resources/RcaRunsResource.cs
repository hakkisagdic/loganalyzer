using Bizigo.ControlPlane;
using Bizigo.Contracts.Security;
using Bizigo.Mcp.Product.Tools;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Mcp.Product.Resources;

/// <summary>
/// <c>bizigo://rca-runs</c> — RCA koşumlarının <b>durumu</b>, ve M07'nin
/// aboneliğinin tek hedefi.
///
/// <para>
/// <b>Şablonsuz</b> (<see cref="BizigoMcpResource.IsAddressedById"/> false):
/// adres tek, değişen şey içeriği. Aboneliğin şekli bunu gerektiriyor — abone
/// olunacak şey <i>bir</i> belge, ve o belgenin içeriği koşumlar ilerledikçe
/// değişiyor. Kimlikle adreslenen bir kaynağa (örn. <c>rca-runs/{id}</c>) abone
/// olmak, her koşum için ayrı bir abonelik açmak demek olurdu ve koşumun kimliği
/// <b>tetiklenmeden önce</b> bilinmiyor.
/// </para>
///
/// <h3>Sorgu M14'ün aracından geliyor — ikinci bir gösterim YOK</h3>
///
/// <para>
/// Gövde <see cref="RcaRunsTool"/>'un kendi <c>ExecuteScopedAsync</c>'inden
/// üretiliyor: aynı sorgu, aynı kapsam filtresi, aynı şekillendirme. İkinci bir
/// sorgu yazmak bu depoda adı konmuş hatadır (§9) ve burada bedeli özellikle
/// yüksek olurdu: <c>rca.runs</c> aracının kapsam filtresi <c>Take</c>'ten
/// <b>önce</b> uygulanıyor ve gerekçesi ölçülmüş (<i>"bellekte yapılsaydı en
/// yeni elli satırın hepsi kapsam dışı olduğunda cevap boş dönerdi ve boş
/// listenin garantisi sessizce yalan olurdu"</i>). O kararı ikinci kez doğru
/// yazmak zorunda kalmak, bir gün yanlış yazmak demektir.
/// </para>
///
/// <para>
/// Yani bu kaynak <c>rca.runs</c>'ın <b>belge hâli</b>: araç çağrılıyor ve
/// argüman alıyor, kaynak adreslenip okunuyor. Aynı veri, iki erişim biçimi —
/// M07'nin ticket'ının ilk cümlesi.
/// </para>
///
/// <h3>Abonelik: neden YALNIZCA bu kaynak</h3>
///
/// <para>
/// <see cref="SupportsSubscription"/> yalnızca burada <see langword="true"/>.
/// Kanıt paketi ve RCA raporu <b>değişmiyor</b> — bir kez yazılıp okunuyorlar;
/// parser tanımı değişiyor ama sıcak yeniden yükleme bir <i>operatör</i> olayı,
/// modelin beklediği bir şey değil. Üçüne de abonelik açmak, ilan edilip
/// bildirimi hiç gönderilmeyen bir yetenek bırakırdı.
/// </para>
/// </summary>
public sealed class RcaRunsResource(IDbContextFactory<ControlPlaneDbContext> factory) : ProductResource
{
    /// <summary>Adresin tür segmenti. Adres bunun kendisi — kimlik almıyor.</summary>
    public const string ResourceKind = "rca-runs";

    /// <summary>Aboneliğin hedefi olan tam adres.</summary>
    public static string Uri { get; } = McpResourceUri.Fixed(ResourceKind);

    private readonly RcaRunsTool _runs = new(factory);

    /// <inheritdoc/>
    public override string Kind => ResourceKind;

    /// <inheritdoc/>
    public override string ResourceTitle => "RCA koşumları";

    /// <inheritdoc/>
    public override string ResourceDescription =>
        "Son RCA koşumlarının durumu: kuyrukta, koşuyor, bitti, reddedildi. "
        + "`rca.runs` aracının belge hâli. Koşum durumu değiştiğinde abonelere "
        + "bildirim gidiyor.";

    /// <inheritdoc/>
    public override string BodyMimeType => McpResourceMimeTypes.Json;

    /// <summary>Tek adres, kimlik yok — gerekçe sınıf belgesinde.</summary>
    public override bool IsAddressedById => false;

    /// <summary>
    /// <b>Aboneliğin desteklendiği tek kaynak.</b> Yetenek ilanı buradan
    /// türüyor (<c>BizigoMcpServer.Apply</c>), yani bu bayrağı açmak sunucunun
    /// <c>resources.subscribe</c> ilan etmesi demek — ve o ilan, bildirimin
    /// gerçekten gönderilmesini gerektiriyor.
    /// </summary>
    public override bool SupportsSubscription => true;

    /// <inheritdoc/>
    public override async ValueTask<McpResourceBody> SampleAsync(CancellationToken cancellationToken)
    {
        // Örnek GERÇEK okuma yolundan geçiyor — aracın kendi gövdesi, kapsamı
        // BOŞ bir okuyucuyla. Boş kapsam ürün verisi döndürmüyor ama şekli ve
        // redaksiyon kapısını ölçüyor.
        var tool = await _runs
            .ExecuteScopedAsync(
                new McpToolInvocation(new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal), Contracts.AccessScope.Denied),
                Contracts.AccessScope.Denied,
                cancellationToken)
            .ConfigureAwait(false);

        return Body(tool);
    }

    /// <inheritdoc/>
    protected internal override async ValueTask<McpResourceBody?> ReadScopedAsync(
        McpResourceRead read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        var tool = await _runs
            .ExecuteScopedAsync(
                new McpToolInvocation(
                    new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal),
                    read.Scope),
                read.Scope,
                cancellationToken)
            .ConfigureAwait(false);

        return Body(tool);
    }

    /// <summary>
    /// Aracın yükünü kaynak gövdesine çevirir — <b>redaksiyon kapısından</b>.
    ///
    /// <para>
    /// Koşum satırları <c>StateDetail</c> taşıyor ve o alan bir <b>istisna
    /// metni</b> olabiliyor (<c>RcaAdmission.StopAsync</c> onu 512 karaktere
    /// kısaltıp yazıyor). Bu deponun ölçtüğü şey tam burada geçerli: sızıntının
    /// en sık gerçekleştiği yer bağlantı hatasının mesajı, ve orada gizli bilgi
    /// çoğu zaman kimsenin yazmadığı bir yerden — kütüphanenin istisna
    /// metninden — geliyor.
    /// </para>
    /// </summary>
    private static McpResourceBody Body(McpToolResult result) =>
        McpResourceBody.Of(
            RedactedPrompt.Redact(result.Payload.GetRawText()),
            McpResourceMimeTypes.Json);
}
