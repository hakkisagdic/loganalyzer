using Bizigo.Contracts.Security;
using Bizigo.Evidence;
using Bizigo.Rca.Reasoning;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp.Product.Resources;

/// <summary>
/// <c>bizigo://rca-report/{paket kimliği}</c> — LLM'in ürettiği RCA raporu.
///
/// <para>
/// <b>Adres paketin kimliği, raporun değil</b> ve bu bilinçli. Raporun kendi
/// <c>owner_group</c>'u yok; kapsamını <b>paketten devralıyor</b>
/// (<c>RcaReportStore.LatestForAsync</c> imzası bir <see cref="EvidenceBundle"/>
/// istiyor, <c>Guid</c> değil — kapı imzada). Adresi rapor kimliğine bağlamak,
/// kapsamın kapıya nasıl geldiğini <b>ikinci kez</b> kurmak demek olurdu.
/// </para>
///
/// <para>
/// <b>En yeni rapor</b> dönüyor, hepsi değil: aynı paket üzerinde farklı
/// model/prompt koşturmak F4'ün karşılaştırma akışı ve o akış REST'te duruyor.
/// Bir ajanın sorduğu soru <i>"son söz ne"</i>; bütün sürümleri modelin bağlamına
/// basmak, bağlam bütçesini bir karşılaştırma özelliği için harcamak olurdu.
/// </para>
///
/// <para>
/// <b>Rapor hiç üretilmemişse "bulunamadı"</b> — boş bir gövde değil. Boş gövde,
/// <i>koşup her cümlesi atılmış</i> bir raporla <i>hiç koşmamış</i> bir paketi
/// aynı şeye indirirdi; REST tarafı da bu ikisini bilerek ayırıyor.
/// </para>
/// </summary>
public sealed class RcaReportResource(IServiceScopeFactory scopes, RcaReportStore reports) : ProductResource
{
    /// <summary>Adresin tür segmenti.</summary>
    public const string ResourceKind = "rca-report";

    /// <inheritdoc/>
    public override string Kind => ResourceKind;

    /// <inheritdoc/>
    public override string ResourceTitle => "RCA raporu";

    /// <inheritdoc/>
    public override string ResourceDescription =>
        "Bir kanıt paketi için üretilmiş en yeni kök neden raporu — bulgular, "
        + "atılan cümle sayacı, model bilgisi. Adres "
        + "`bizigo://rca-report/{kanıt paketi kimliği}`. Rapor yoksa 'bulunamadı'.";

    /// <inheritdoc/>
    public override string BodyMimeType => McpResourceMimeTypes.Markdown;

    /// <inheritdoc/>
    public override ValueTask<McpResourceBody> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(Body(SampleDocument));
    }

    /// <inheritdoc/>
    protected internal override async ValueTask<McpResourceBody?> ReadScopedAsync(
        McpResourceRead read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (!Guid.TryParse(read.Uri.Id, out var bundleId))
        {
            return null;
        }

        await using var scope = scopes.CreateAsyncScope();

        var store = scope.ServiceProvider.GetRequiredService<EvidenceBundleStore>();
        var bundle = await store.GetAsync(bundleId, cancellationToken).ConfigureAwait(false);

        // KAPSAM KAPISI PAKETTE. Raporu doğrudan okumak — `RcaReports` tablosuna
        // paket kimliğiyle gitmek — kapıyı ATLAMAK olurdu, ve o hâl sessiz
        // olurdu: rapor iner, kimse bir şey fark etmez.
        if (bundle is null || !bundle.Scope.IsReadableBy(read.Scope))
        {
            return null;
        }

        var stored = await reports.LatestForAsync(bundle, cancellationToken).ConfigureAwait(false);

        return stored is null ? null : Body(stored.Document);
    }

    /// <summary>
    /// Gövde — <b>redaksiyon kapısından</b>.
    ///
    /// <para>
    /// Bu belge bir modelin ürettiği metin ve kanıt satırlarından <b>alıntı</b>
    /// taşıyor: yani içindeki sır, ürünün hiç yazmadığı bir yerden geliyor. T41
    /// tam olarak bu kanal için kurulmuştu; gövdenin kapıdan geçmesi burada bir
    /// önlem değil kanalın <b>tanımı</b>.
    /// </para>
    /// </summary>
    private static McpResourceBody Body(RcaReportDocument document) =>
        McpResourceBody.Of(
            RedactedPrompt.Redact(document.ToMarkdown()),
            McpResourceMimeTypes.Markdown);

    /// <summary>
    /// Uyum kapısının örnek girdisi. Alanları <c>required</c> olduğu için elle
    /// yazılıyor ve bu <b>iyi</b>: belgeye bir alan eklendiğinde burası
    /// derlenmiyor, yani örnek bayatlayamıyor.
    /// </summary>
    private static RcaReportDocument SampleDocument { get; } = new()
    {
        BundleId = Guid.Empty,
        ScenarioId = "ornek-senaryo",
        ScenarioVersion = "1",
        Findings = [],
        Actions = [],
        DroppedSentenceCount = 0,
        ProducedSentenceCount = 0,
        FabricatedCitationSentenceCount = 0,
        ModelInfo = new RcaReportModelInfo(
            Provider: "ornek",
            Model: "ornek",
            PromptTokens: null,
            CompletionTokens: null,
            UnreportedAttempts: 0),
    };
}
