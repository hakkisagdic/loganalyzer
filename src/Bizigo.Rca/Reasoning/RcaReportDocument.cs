using System.Globalization;
using System.Text;

namespace Bizigo.Rca.Reasoning;

/// <param name="Hypothesis">Rapora giren metin — <b>yalnızca bağlanan cümleler</b>.</param>
/// <param name="EvidenceIds">Destekleyen kanıt; adımın gördüğü kümeden.</param>
/// <param name="ContradictingEvidenceIds">
/// Hipotezi <b>zayıflatan</b> kanıt. Ayrı tutulması RCA §4.2'nin bilinçli
/// kararı: modelden hipotezini zayıflatan kanıtı da göstermesini istemek, tek
/// yönlü hikâye anlatmasını ciddi biçimde azaltıyor.
/// </param>
public sealed record RcaReportFinding(
    string Hypothesis,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> ContradictingEvidenceIds);

/// <param name="Text">Aksiyon metni — bağlanan cümleler.</param>
/// <param name="EvidenceIds">
/// Aksiyonun <b>kendi</b> kanıt atfı. T43'ün ikinci açık ucu buydu ve karar
/// <see cref="RcaReportDocument"/> belgesinde gerekçesiyle duruyor.
/// </param>
public sealed record RcaReportAction(string Text, IReadOnlyList<string> EvidenceIds);

/// <param name="Provider">Sağlayıcı adı.</param>
/// <param name="Model">Model adı.</param>
/// <param name="PromptTokens">
/// <b>Bildirilen</b> giriş belirteci toplamı. Hiçbir deneme bildirmediyse
/// <see langword="null"/> — <b>0 değil</b> (T42'nin 8 numaralı ölçümü). Sıfır
/// yazılsaydı bütçe hiç tükenmez, kapı hiç kapanmaz, sebep hiç görünmezdi.
/// </param>
/// <param name="UnreportedAttempts">
/// Belirteç bildirmeyen deneme sayısı. <b>Kısmi bir toplam bir alt sınırdır</b>
/// ve bunu söylemeyen bir sayı, tam sanılır. Bütçeyi uygulayan taraf (T46)
/// bu alan sıfır değilse toplamı <i>"bilinmiyor"</i> saymalı, <i>"küçük"</i>
/// değil.
/// </param>
public sealed record RcaReportModelInfo(
    string Provider,
    string Model,
    int? PromptTokens,
    int? CompletionTokens,
    int UnreportedAttempts)
{
    /// <summary>Toplam bir alt sınır mı, yoksa tam mı.</summary>
    public bool TokensComplete => UnreportedAttempts == 0;
}

/// <summary>
/// <b>Üretilen belgenin sahibi</b> — ve statü taşımıyor.
///
/// <para>
/// Sınır RCA §4.2'nin 2026-08-26 düzeltmesinde çivilendi: <c>rca_runs</c>
/// koşumun başına gelen her şeyin sahibi (kabul, ret, yürütme, sonuç),
/// <c>rca_report</c> üretilen belgenin sahibi (metin, cümle atfı, <b>atılan
/// cümle sayısı</b>). İkiye bölünürlerse <i>"kota mı, boş mu"</i> sorusu bir
/// <c>join</c>'e döner ve join'in iki sessiz hâli var: satır ikisinde birden ya
/// da hiçbirinde. İkisi de belirti üretmez.
/// </para>
///
/// <para>
/// Bu tipe bir <c>Status</c> / <c>State</c> özelliği eklenirse
/// <c>RcaReportStatusGuardTests</c> <b>kırmızı yanıyor</b>. Bekçiyi T46 yazdı;
/// T44 yalnızca kapsamına girdi — ve girmesi gerekiyordu, çünkü bekçi
/// yazıldığında ortada <c>RcaReport</c> ile başlayan <b>tek bir tip yoktu</b>
/// ve boş küme üzerinde yeşil yanıyordu.
/// </para>
///
/// <para>
/// <b>Sayının paydası da taşınıyor.</b> Karar 1'in cümlesi <i>"Model 12 cümle
/// üretti, 3'ü kanıta bağlanamadı ve çıkarıldı"</i> — payda olmadan pay bir
/// oran değil bir sayı, ve iki farklı raporun kalitesi karşılaştırılamaz.
/// </para>
/// </summary>
public sealed record RcaReportDocument
{
    public required Guid BundleId { get; init; }

    public required string ScenarioId { get; init; }

    public required string ScenarioVersion { get; init; }

    public required IReadOnlyList<RcaReportFinding> Findings { get; init; }

    public required IReadOnlyList<RcaReportAction> Actions { get; init; }

    /// <summary>Karar 1'in payı — <b>rapora girmeyen</b> cümle sayısı.</summary>
    public required int DroppedSentenceCount { get; init; }

    /// <summary>Karar 1'in paydası — modelin ürettiği toplam cümle.</summary>
    public required int ProducedSentenceCount { get; init; }

    /// <summary>
    /// Çözülmeyen atıf taşıyan cümle sayısı — <i>"hiç atıf yapmadı"</i> ile
    /// <i>"atıf uydurdu"</i> ayrımı. İkisi de atılıyor; ayrımı T47 okuyacak.
    /// </summary>
    public required int FabricatedCitationSentenceCount { get; init; }

    public required RcaReportModelInfo ModelInfo { get; init; }

    /// <summary>
    /// <b>Cümle bağlamanın koşmadığı</b> adımlar ve sebebi. Boş liste "her
    /// adımda koştu" demek; sessizce atlanan bir kapı, kapının kendisinden
    /// tehlikeli (§7).
    /// </summary>
    public IReadOnlyList<string> SentenceGateSkipped { get; init; } = [];

    /// <summary>Payda sıfırsa oran da sıfır — <b>"ölçülmedi" değil</b>, "cümle üretilmedi".</summary>
    public double DroppedSentenceRatio =>
        ProducedSentenceCount > 0 ? (double)DroppedSentenceCount / ProducedSentenceCount : 0d;

    /// <summary>
    /// Sayının <b>gösterildiği</b> yer. Karar 1'in ikinci yarısı bir depolama
    /// kararı değil bir görünürlük kararı: <i>yalnızca atmak</i> kaliteyi
    /// ölçülemez yapardı.
    /// </summary>
    public string DescribeDroppedSentences() => ProducedSentenceCount == 0
        ? "Model hiç cümle üretmedi."
        : string.Format(
            CultureInfo.GetCultureInfo("tr-TR"),
            "Model {0} cümle üretti, {1}'i kanıta bağlanamadı ve çıkarıldı ({2:P1}).",
            ProducedSentenceCount,
            DroppedSentenceCount,
            DroppedSentenceRatio);

    /// <summary>
    /// Belgenin insan okunur hâli. <c>DeterministicReport.ToMarkdown</c> ile
    /// aynı sebeple Markdown: ekranda, ticket'ta ve terminalde okunuyor.
    /// </summary>
    public string ToMarkdown()
    {
        var text = new StringBuilder();

        text.AppendLine("# RCA raporu");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Kanıt paketi: `{BundleId}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Senaryo: `{ScenarioId}` v{ScenarioVersion}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Model: {ModelInfo.Provider} / {ModelInfo.Model}");
        text.AppendLine();

        // En ÜSTTE, DeterministicReport'un dürüstlük satırlarıyla aynı sebeple:
        // raporun sonunda duran bir kısıt okunmuyor.
        text.AppendLine("> " + DescribeDroppedSentences());

        if (!ModelInfo.TokensComplete)
        {
            text.AppendLine("> Belirteç sayısı **eksik bildirildi**: " +
                $"{ModelInfo.UnreportedAttempts} deneme sayı vermedi, toplam bir alt sınır.");
        }

        foreach (var skipped in SentenceGateSkipped)
        {
            text.AppendLine("> Cümle bağlama koşmadı — " + skipped);
        }

        text.AppendLine();
        text.AppendLine("## Bulgular");
        text.AppendLine();

        if (Findings.Count == 0)
        {
            text.AppendLine("_Kanıta bağlanan bulgu kalmadı._");
        }

        foreach (var (finding, rank) in Findings.Select((f, i) => (f, i + 1)))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{rank}. {finding.Hypothesis}");
            text.AppendLine(CultureInfo.InvariantCulture,
                $"   - Destekleyen: {Join(finding.EvidenceIds)}");
            text.AppendLine(CultureInfo.InvariantCulture,
                $"   - Çelişen: {Join(finding.ContradictingEvidenceIds)}");
        }

        text.AppendLine();
        text.AppendLine("## Önerilen aksiyonlar");
        text.AppendLine();

        if (Actions.Count == 0)
        {
            text.AppendLine("_Kanıta bağlanan aksiyon kalmadı._");
        }

        foreach (var action in Actions)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- {action.Text} (kanıt: {Join(action.EvidenceIds)})");
        }

        return text.ToString();
    }

    private static string Join(IReadOnlyList<string> ids) =>
        ids.Count == 0 ? "yok" : string.Join(", ", ids);
}
