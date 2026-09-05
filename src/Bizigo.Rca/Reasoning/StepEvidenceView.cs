using Bizigo.Evidence;
using Bizigo.ScenarioPlugin;

namespace Bizigo.Rca.Reasoning;

/// <summary>
/// Bir adımın <b>gerçekten gördüğü</b> tek kanıt satırı.
/// </summary>
/// <param name="Id">Kanıt kimliği — atıfların çözüldüğü şey.</param>
/// <param name="ProviderId">Hangi sağlayıcıdan geldiği; prompt'ta görünüyor.</param>
/// <param name="Summary">Satırın insan-okunur özeti.</param>
/// <param name="Timestamp">Zaman — sıralama prompt'ta anlam taşıyor.</param>
public sealed record VisibleEvidence(
    string Id,
    string ProviderId,
    string Summary,
    DateTimeOffset Timestamp);

/// <summary>
/// <b>Doğrulama adımın GÖRDÜĞÜ kanıta karşı yapılıyor</b>, paketin tamamına
/// karşı değil (F4 plugin formatı §6).
///
/// <para>
/// Fark bu ticket'ın taşıyıcı kararı. Bütün pakete karşı doğrulansaydı bir
/// adım, <b>hiç görmediği</b> bir kanıta atıf yapıp geçebilirdi: o atıf teknik
/// olarak var olan bir kimliğe işaret eder ama modelin onu görmesinin hiçbir
/// yolu yoktur — yani <b>kimlik doğru, gerekçe uydurma</b>. Kapının kapatmak
/// istediği şey tam olarak bu, ve paket düzeyinde doğrulayan bir kapı onu
/// hiç görmez.
/// </para>
///
/// <para>
/// Görüş alanı <c>input</c>'tan türüyor ve <b>ileri doğru</b> büyüyor:
/// <c>evidence.items</c> paketin kimliklerini açıyor, <c>steps.&lt;id&gt;</c> o
/// adımın <b>bağladığı</b> kimlikleri devrediyor, <c>evidence.summary</c> ise
/// hiç kimlik açmıyor. Sonuncusu bir eksiklik değil formatın kendi cümlesi:
/// özet bir kanıt <i>öğesi</i> değil, kimliği yok
/// (F4 plugin formatı §3.2).
/// </para>
/// </summary>
public sealed class StepEvidenceView
{
    /// <summary>
    /// Kimlik açan <b>tek</b> kanıt yolu.
    ///
    /// <para>
    /// Tek olması bilinçli: çekirdeğin yorumlayabildiği yol bu, ve
    /// yorumlayamadığı bir yola kimlik vermek — <c>evidence.rows</c> gibi —
    /// kapıyı sessizce açmak olurdu. Tanınmayan yollar
    /// <see cref="UnknownInputs"/>'ta <b>adıyla</b> duruyor: "kimlik vermedim"
    /// ile "böyle bir yol yok" ayrı cümleler.
    /// </para>
    /// </summary>
    public const string ItemsInput = "evidence.items";

    /// <summary>Kimlik açmayan, yalnızca sağlayıcı özetlerini veren yol.</summary>
    public const string SummaryInput = "evidence.summary";

    private StepEvidenceView(
        string stepId,
        IReadOnlyList<VisibleEvidence> items,
        IReadOnlyList<string> summaries,
        IReadOnlyList<string> upstream,
        IReadOnlyList<string> unknownInputs)
    {
        StepId = stepId;
        Items = items;
        Summaries = summaries;
        Upstream = upstream;
        UnknownInputs = unknownInputs;
        VisibleIds = items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
    }

    public string StepId { get; }

    /// <summary>Kimlikli kanıt satırları — prompt'a bunlar giriyor.</summary>
    public IReadOnlyList<VisibleEvidence> Items { get; }

    /// <summary>Sağlayıcı özetleri; <b>kimlik taşımıyor</b>.</summary>
    public IReadOnlyList<string> Summaries { get; }

    /// <summary>Önceki adımların bu adıma devrettiği metin.</summary>
    public IReadOnlyList<string> Upstream { get; }

    /// <summary>
    /// Çekirdeğin yorumlayamadığı <c>evidence.*</c> yolları.
    /// <b>Boş değilse bu adım o yoldan hiç kimlik almadı</b> — ve bunu bilerek
    /// söylüyor, sessizce boş küme döndürmüyor.
    /// </summary>
    public IReadOnlyList<string> UnknownInputs { get; }

    /// <summary>Kısıt doğrulamasının karşılaştırdığı küme.</summary>
    public IReadOnlySet<string> VisibleIds { get; }

    /// <summary>
    /// <b>"Bakılmadı" ile "bakıldı, boş" ayrı cümleler.</b> Kimliği olmayan bir
    /// görüş alanı, kimliği olan ama boş çıkan bir görüş alanından farklı; ikisi
    /// tek satıra indirilirse bir adımın neden reddedildiği okunamaz hâle
    /// geliyor.
    /// </summary>
    public string Describe()
    {
        var lines = new List<string>
        {
            $"{StepId}: {Items.Count} kimlikli kanıt, {Summaries.Count} özet, {Upstream.Count} önceki adım çıktısı",
        };

        lines.AddRange(UnknownInputs.Select(input =>
            $"{StepId}: `{input}` çekirdek tarafından yorumlanmıyor — bu yoldan kimlik ALINMADI"));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Adımın görüş alanını <c>input</c>'undan kurar.
    /// </summary>
    /// <param name="step">Görüş alanı kurulacak adım.</param>
    /// <param name="bundle">Kanıt paketi — kimlik kaynağı.</param>
    /// <param name="completed">
    /// Bu adımdan <b>önce</b> kabul edilmiş adımların çıktıları. Yükleyici ileri
    /// atfı zaten reddediyor, dolayısıyla burada eksik bir anahtar bir kusur
    /// değil bir imkânsızlık; yine de sessizce boş geçmiyoruz.
    /// </param>
    public static StepEvidenceView For(
        ScenarioStep step,
        EvidenceBundle bundle,
        IReadOnlyDictionary<string, ScenarioOutputDocument> completed)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(completed);

        var items = new List<VisibleEvidence>();
        var summaries = new List<string>();
        var upstream = new List<string>();
        var unknown = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var input in step.Input)
        {
            if (string.Equals(input, ItemsInput, StringComparison.Ordinal))
            {
                foreach (var item in bundle.Items.Where(i => seen.Add(i.Id)))
                {
                    items.Add(new VisibleEvidence(item.Id, item.ProviderId, item.Summary, item.Timestamp));
                }
            }
            else if (string.Equals(input, SummaryInput, StringComparison.Ordinal))
            {
                // Kimlik YOK ve bu bilinçli: bir sağlayıcı özeti bir kanıt öğesi
                // değil. Kimlik verilseydi uydurma olurdu ve "kimlik var" ile
                // "kimlik anlamlı" arasındaki fark hiçbir yerde görünmezdi.
                summaries.AddRange(bundle.Slices.Select(DescribeSlice));
            }
            else if (input.StartsWith("steps.", StringComparison.Ordinal))
            {
                var referenced = input["steps.".Length..];

                if (!completed.TryGetValue(referenced, out var document))
                {
                    unknown.Add(input);
                    continue;
                }

                // Devredilen şey o adımın BAĞLADIĞI kimlikler — paketin tamamı
                // değil. Zincir bu yüzden daralarak ilerliyor: `write-actions`
                // yalnızca `bind-evidence`'ın bağladığına atıf yapabiliyor.
                foreach (var id in document.CitedIds)
                {
                    if (!seen.Add(id))
                    {
                        continue;
                    }

                    var source = bundle.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

                    items.Add(source is null
                        ? new VisibleEvidence(id, "<önceki adım>", string.Empty, default)
                        : new VisibleEvidence(id, source.ProviderId, source.Summary, source.Timestamp));
                }

                upstream.AddRange(document.Prose.Select(p => p.Text));
            }
            else
            {
                unknown.Add(input);
            }
        }

        return new StepEvidenceView(step.Id, items, summaries, upstream, unknown);
    }

    private static string DescribeSlice(EvidenceSlice slice) =>
        $"{slice.ProviderId} ({slice.Kind}): {slice.Status.ToString().ToLowerInvariant()}" +
        $", {slice.Items.Count} satır" +
        (slice.Truncated ? ", KIRPILDI" : string.Empty) +
        (string.IsNullOrWhiteSpace(slice.Detail) ? string.Empty : $" — {slice.Detail}");
}
