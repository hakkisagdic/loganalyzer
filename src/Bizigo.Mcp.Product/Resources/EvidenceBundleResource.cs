using Bizigo.Contracts.Security;
using Bizigo.Evidence;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp.Product.Resources;

/// <summary>
/// <c>bizigo://evidence-bundle/{id}</c> — kanıt paketi belgesi.
///
/// <para>
/// Uç karşılığı <c>GET /v1/rca/{id}/export</c>, ve belgeyi üreten şey aynı:
/// <see cref="DeterministicReport"/>. <b>İkinci bir gösterim yazılmadı</b> —
/// yazılsaydı ekranda/export'ta görünen belge ile modelin okuduğu belge bir gün
/// ayrışır, ve ayrışmayı gösterecek hiçbir şey olmazdı (§9).
/// </para>
///
/// <h3>Kapsam okuma yolunda — ve kapının yeri BU KAYNAKTA en kritik</h3>
///
/// <para>
/// Adres <c>Guid</c> taşıyor, yani <b>tahmin edilebilir değil</b> ama tahmin
/// edilemezlik bir kapı değil: bir paket kimliği bir ekran bağlantısında,
/// bir alarm bildiriminde ya da bir sohbet geçmişinde geçebiliyor. Kapı
/// <see cref="BundleScope.IsReadableBy"/> ve o kontrol REST tarafında yazılı
/// gerekçesiyle duruyor: <i>"kimlikle okunan bir kayıt, kapsam kapısının
/// atlandığı yerdir: A grubunun kapsamıyla toplanmış bir paketi B grubundan biri
/// kimliğiyle isteyebilir ve içindeki her kanıt satırını görürdü."</i>
/// </para>
///
/// <para>
/// <b>Okuyamayan için cevap "bulunamadı"</b>, "yetkiniz yok" değil — REST'in 404
/// kararının aynısı ve aynı gerekçeyle: 403 paketin var olduğunu doğrular, yani
/// bir pencerede RCA koşulduğu bilgisini sızdırır.
/// </para>
///
/// <h3>Gövde log içeriği taşıyor</h3>
///
/// <para>
/// Kanıt dilimleri gerçek log satırları içeriyor. Gövdenin
/// <see cref="RedactedPrompt"/> olmadan kurulamaması bu kaynağın tamamının
/// gerekçesi: <see cref="McpResourceBody"/> serbest <c>string</c> kabul etmiyor,
/// yani kapıyı atlayan bir gövde <b>derlenmiyor</b>.
/// </para>
/// </summary>
public sealed class EvidenceBundleResource(IServiceScopeFactory scopes) : ProductResource
{
    /// <summary>Adresin tür segmenti.</summary>
    public const string ResourceKind = "evidence-bundle";

    /// <inheritdoc/>
    public override string Kind => ResourceKind;

    /// <inheritdoc/>
    public override string ResourceTitle => "Kanıt paketi";

    /// <inheritdoc/>
    public override string ResourceDescription =>
        "Bir RCA koşumunun topladığı kanıt: pencere, taban, güven ölçüsü ve "
        + "sıralanmış kanıt dilimleri. Adres `bizigo://evidence-bundle/{id}`. "
        + "Kapsam dışı bir paket 'bulunamadı' döner.";

    /// <inheritdoc/>
    public override string BodyMimeType => McpResourceMimeTypes.Markdown;

    /// <inheritdoc/>
    public override ValueTask<McpResourceBody> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Örnek GERÇEK şekillendirme yolundan geçiyor: `DeterministicReport.From`
        // + `ToMarkdown` + redaksiyon kapısı, yani üretimdeki gövdenin aynısı.
        // Atlanan tek şey veritabanı — kanıtlanması gereken şey gövdenin kapıdan
        // geçtiği, deponun cevabı entegrasyon testinin işi (§2).
        return ValueTask.FromResult(Body(SampleBundle));
    }

    /// <inheritdoc/>
    protected internal override async ValueTask<McpResourceBody?> ReadScopedAsync(
        McpResourceRead read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (!Guid.TryParse(read.Uri.Id, out var id))
        {
            // Bozuk kimlik BULUNAMADI ile aynı cevaba iniyor: "bu bir Guid
            // değil" demek, adres uzayının şeklini söyleyen bir yankı olurdu.
            return null;
        }

        // `EvidenceBundleStore` SCOPED; kaynak ise sunucu ömrü boyunca tek
        // örnek. Yapıcıda istemek esir bağımlılık olurdu — gerekçe
        // `ProductReadTool` belgesinde ölçülmüş hâliyle duruyor.
        await using var scope = scopes.CreateAsyncScope();

        var store = scope.ServiceProvider.GetRequiredService<EvidenceBundleStore>();
        var bundle = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);

        // İKİ RET, TEK CEVAP: paket yok ya da bu kimlik onu görmüyor.
        return bundle is null || !bundle.Scope.IsReadableBy(read.Scope) ? null : Body(bundle);
    }

    private static McpResourceBody Body(EvidenceBundle bundle) =>
        McpResourceBody.Of(
            RedactedPrompt.Redact(DeterministicReport.From(bundle).ToMarkdown()),
            McpResourceMimeTypes.Markdown);

    /// <summary>
    /// Uyum kapısının örnek girdisi. <b>Alanları zorunlu olduğu için elle
    /// yazılıyor</b> ve bu iyi: bir alan eklendiğinde burası derlenmiyor, yani
    /// örnek bayatlayamıyor.
    /// </summary>
    private static EvidenceBundle SampleBundle { get; } = new()
    {
        Id = Guid.Empty,
        GatheredAt = DateTimeOffset.UnixEpoch,
        Window = new RcaWindow
        {
            From = DateTimeOffset.UnixEpoch,
            To = DateTimeOffset.UnixEpoch.AddMinutes(45),
            BaselineFrom = DateTimeOffset.UnixEpoch.AddDays(-7),
            BaselineTo = DateTimeOffset.UnixEpoch,
        },
        Scope = new BundleScope(["ornek/grup"], IsSystem: false),
        Slices = [],
        Trust = new WindowTrust(TotalEvents: 0, UnreliableTimeEvents: 0),
    };
}
