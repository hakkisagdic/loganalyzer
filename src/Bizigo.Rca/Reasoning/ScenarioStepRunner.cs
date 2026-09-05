using Bizigo.Evidence;
using Bizigo.Rca.Models;
using Bizigo.ScenarioPlugin;

namespace Bizigo.Rca.Reasoning;

/// <param name="Number">Kaçıncı deneme — 1 ya da 2.</param>
/// <param name="PromptTokens">Bildirilmediyse <see langword="null"/>, <b>0 değil</b>.</param>
/// <param name="Failure">Bu denemenin neden yetmediği; başarılıysa <see langword="null"/>.</param>
public sealed record ScenarioStepAttempt(
    int Number,
    int? PromptTokens,
    int? CompletionTokens,
    TimeSpan Duration,
    string? Failure);

/// <summary>
/// İkinci kapının bir adımdaki durumu — <b>kapalı küme</b>.
///
/// <para>
/// Bir <c>enum</c>, çünkü bu değeri okuyacak ilk kod onu <b>gruplayacak</b>.
/// Dizge olsaydı ayrıştırılırdı, ve bu depoda ayrıştırılan dizgenin adı konmuş
/// bir hata sınıfı var: <i>"SQL doğru, kolon doğru, dizge yanlış"</i>.
/// </para>
/// </summary>
public enum SentenceGateOutcome
{
    /// <summary>Kapı koştu; cümleler bağlandı ya da atıldı.</summary>
    Ran = 0,

    /// <summary>
    /// Kapı <b>koşmadı ve koşmaması doğru</b>: bu adımın düzyazısı rapora
    /// girmiyor. Koşsaydı her cümle atılırdı ve rapor boş çıkardı.
    /// </summary>
    NotApplicable = 1,

    /// <summary>Adım reddedildi; kapıya hiç sıra gelmedi.</summary>
    Skipped = 2,
}

/// <summary>
/// <b>"Koştu, temiz" ile "koşmadı" ayrı cümleler.</b> Bu depoda aynı ayrım
/// <c>EvidenceStatus</c> ve <c>ScenarioEvidenceGate.Describe</c> ile iki kez
/// ödendi; sessizce atlayan bir kapı, kapının kendisinden tehlikeli.
/// </summary>
/// <param name="StepId">Hangi adım — T47 bunlara göre gruplayacak.</param>
/// <param name="Outcome">Kapalı kümeden durum.</param>
/// <param name="Detail">İnsan okunur gerekçe; <b>ayrıştırılmak için değil</b>.</param>
public sealed record SentenceGateStatus(string StepId, SentenceGateOutcome Outcome, string Detail);

/// <summary>Bir adımın sonucu — <b>iki kapının izi ayrı ayrı</b> duruyor.</summary>
/// <param name="StepId">Adım.</param>
/// <param name="Accepted">Adım kabul edildi mi.</param>
/// <param name="Document">Ayrıştırılmış çıktı; reddedildiyse <see langword="null"/>.</param>
/// <param name="Binding">
/// Cümle bağlamanın sonucu — alan başına. Kapı koşmadıysa <b>boş</b>, ve
/// koşmadığı <paramref name="SentenceGate"/>'te yazılı.
/// </param>
/// <param name="SentenceGate">İkinci kapının durumu.</param>
/// <param name="Rejection">Reddedildiyse sebebi; adı konmuş.</param>
public sealed record ScenarioStepOutcome(
    string StepId,
    bool Accepted,
    ScenarioOutputDocument? Document,
    IReadOnlyList<SentenceBinding> Binding,
    SentenceGateStatus SentenceGate,
    IReadOnlyList<ScenarioStepAttempt> Attempts,
    string? Rejection)
{
    public int DroppedSentences => Binding.Sum(b => b.Dropped);

    public int ProducedSentences => Binding.Sum(b => b.Produced);
}

/// <summary>
/// Senaryo koşumunun tamamı.
/// </summary>
/// <param name="Steps">Koşan adımlar, sırasıyla.</param>
/// <param name="Report">Üretilen belge; koşum durduysa <see langword="null"/>.</param>
/// <param name="Stopped">Senaryo bir adımda durdu mu.</param>
/// <param name="StopDetail">Durduysa sebebi.</param>
public sealed record ScenarioRunOutcome(
    IReadOnlyList<ScenarioStepOutcome> Steps,
    RcaReportDocument? Report,
    bool Stopped,
    string? StopDetail);

/// <summary>
/// <b>LLM adımları ve iki kapı</b> (T44).
///
/// <para>
/// Adım makinesi F4 plugin formatı §6'daki akışın karşılığı, ve iki kapı
/// <b>ayrı</b>:
/// </para>
///
/// <list type="number">
/// <item><b>Şema ayrıştırma</b> — ayrışmadıysa bir kez yeniden dene.</item>
/// <item><b>Kısıt doğrulama</b> — ihlalde <b>adım</b> reddedilir, bir kez
/// yeniden denenir, ikinci düşüşte <b>senaryo durur</b>. Kısmi rapor
/// üretilmiyor.</item>
/// <item><b>Cümle bağlama</b> — bağlanamayan <b>cümle atılır</b>, adım kabul
/// edilir, <b>sayaç artar</b>.</item>
/// </list>
///
/// <para>
/// <b>İkisi tek kapıda birleştirilseydi</b> bir kötü cümle yüzünden bütün adım
/// atılırdı — iki iyi hipotez de kaybolurdu. Tersi de kötü: kısıt ihlalini
/// cümle atarak geçiştirmek, var olmayan bir <c>evidence_id</c>'yi rapordan
/// silip raporu <b>geçerli göstermek</b> olurdu.
/// </para>
///
/// <para>
/// <b>Bir adım en fazla iki kez koşuyor</b> ve bu sayı kotanın muhasebesine
/// giriyor: yeniden deneme belirteç harcıyor. Kısıtı burada uygulamıyoruz —
/// bütçe T46'nın alanı; buradaki taahhüt yalnızca ölçülen sayıyı
/// <b>bildirmek</b> ve bildirilmeyeni <see langword="null"/> bırakmak.
/// </para>
/// </summary>
public sealed class ScenarioStepRunner
{
    /// <summary>
    /// Bir adımın koşabileceği en fazla kez sayısı.
    ///
    /// <para>
    /// Sabit, çünkü kota tarafı (T46) bunu <b>bilerek</b> plan yapmak zorunda:
    /// tavan senaryodan okunsaydı bir plugin kendi belirteç bütçesini
    /// yazabilirdi ve <i>"kota kuyrukta uygulanır, senaryonun insafına
    /// bırakılmaz"</i> (RCA §5) cümlesi delinirdi.
    /// </para>
    /// </summary>
    public const int MaxAttemptsPerStep = 2;

    private readonly IModelProvider _provider;
    private readonly ModelEndpoint _endpoint;
    private readonly PromptContentLevel _level;
    private readonly ScenarioConstraintRegistry _constraints;
    private readonly ScenarioOutputSchemas _schemas;

    public ScenarioStepRunner(
        IModelProvider provider,
        ModelEndpoint endpoint,
        PromptContentLevel level,
        ScenarioConstraintRegistry? constraints = null,
        ScenarioOutputSchemas? schemas = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(endpoint);

        _provider = provider;
        _endpoint = endpoint;
        _level = level;
        _constraints = constraints ?? ScenarioConstraintRegistry.BuiltIn;
        _schemas = schemas ?? ScenarioOutputSchemas.BuiltIn;
    }

    public async ValueTask<ScenarioRunOutcome> RunAsync(
        ScenarioDefinition scenario,
        EvidenceBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(bundle);

        // ÖN UÇUŞ: tanımadığı bir kısıt ya da şema adıyla koşmak, ilk iki adımın
        // belirteçlerini ödeyip üçüncüde durmak olurdu.
        var preflight = ScenarioConstraintGate.Check(scenario, _constraints, _schemas);

        if (preflight.Count > 0)
        {
            return new ScenarioRunOutcome([], null, Stopped: true, string.Join(Environment.NewLine, preflight));
        }

        var outcomes = new List<ScenarioStepOutcome>();
        var completed = new Dictionary<string, ScenarioOutputDocument>(StringComparer.Ordinal);

        foreach (var step in scenario.Steps)
        {
            var view = StepEvidenceView.For(step, bundle, completed);

            // Ön uçuş geçtiği için şema kesin var; yine de sessiz bir `null`
            // bırakmıyoruz — ön uçuşun atlanabildiği bir çağrı yolu doğarsa
            // burası sessizce geçmesin.
            if (!_schemas.TryGet(step.Output.Schema, out var schema))
            {
                outcomes.Add(Rejected(step, [], $"`{step.Output.Schema}` şemasını motor tanımıyor."));
                return Stop(outcomes, $"{step.Id}: şema tanınmıyor.");
            }

            var outcome = await RunStepAsync(step, schema, view, cancellationToken).ConfigureAwait(false);
            outcomes.Add(outcome);

            if (!outcome.Accepted)
            {
                // Senaryo DURUYOR — kısmi rapor üretilmiyor.
                return Stop(outcomes, $"{step.Id}: {outcome.Rejection}");
            }

            completed[step.Id] = outcome.Document!;
        }

        return new ScenarioRunOutcome(
            outcomes,
            Assemble(scenario, bundle, _endpoint, outcomes),
            Stopped: false,
            null);
    }

    private async ValueTask<ScenarioStepOutcome> RunStepAsync(
        ScenarioStep step,
        IScenarioOutputSchema schema,
        StepEvidenceView view,
        CancellationToken cancellationToken)
    {
        var attempts = new List<ScenarioStepAttempt>(MaxAttemptsPerStep);
        IReadOnlyList<string> violations = [];

        for (var attempt = 1; attempt <= MaxAttemptsPerStep; attempt++)
        {
            var prompt = attempt == 1
                ? ScenarioStepPrompt.Build(step, schema, view, _level)
                : ScenarioStepPrompt.Retry(step, schema, view, _level, violations);

            var request = ModelRequest.Create(_endpoint, _level, prompt.System, prompt.User);

            if (!request.Allowed)
            {
                // Ret bir arıza değil bir karar ve yeniden denemeye konu değil:
                // aynı uç ve aynı düzeyle ikinci deneme de aynı cevabı alır.
                attempts.Add(new ScenarioStepAttempt(attempt, null, null, TimeSpan.Zero, request.Rejection));
                return Rejected(step, attempts, request.Rejection);
            }

            var completion = await _provider
                .CompleteAsync(request.Request!, cancellationToken)
                .ConfigureAwait(false);

            if (!completion.Ok)
            {
                attempts.Add(new ScenarioStepAttempt(
                    attempt, completion.PromptTokens, completion.CompletionTokens,
                    completion.Duration, completion.Failure));

                violations = [$"Model çağrısı başarısız: {completion.Failure}"];
                continue;
            }

            // ---------------------------------------------- 1 · şema ayrıştırma
            var parse = schema.Parse(completion.Text);

            if (!parse.Ok)
            {
                attempts.Add(new ScenarioStepAttempt(
                    attempt, completion.PromptTokens, completion.CompletionTokens,
                    completion.Duration, parse.Error));

                violations = [parse.Error!];
                continue;
            }

            var document = parse.Document;

            // ---------------------------------------------- 1b · max_items
            // T43 bu sayıyı taşıdı ama yorumlamadı ve zorlamasını T44'e bıraktı.
            // Kısıt doğrulamayla aynı sonucu veriyor çünkü aynı soruyu soruyor:
            // belgenin YAPISI sözleşmeye uyuyor mu.
            var structural = new List<string>();

            if (step.Output.MaxItems is { } max && document.ItemCount > max)
            {
                structural.Add(
                    $"`max_items`: {max} kayıt isteniyordu, {document.ItemCount} geldi.");
            }

            // ---------------------------------------------- 2 · kısıt doğrulama
            foreach (var name in step.Output.Constraints)
            {
                if (!_constraints.TryGet(name, out var constraint))
                {
                    structural.Add($"`{name}` kısıdını motor zorlayamıyor.");
                    continue;
                }

                structural.AddRange(constraint.Check(document, view));
            }

            if (structural.Count > 0)
            {
                attempts.Add(new ScenarioStepAttempt(
                    attempt, completion.PromptTokens, completion.CompletionTokens,
                    completion.Duration, string.Join(" · ", structural)));

                violations = structural;
                continue;
            }

            attempts.Add(new ScenarioStepAttempt(
                attempt, completion.PromptTokens, completion.CompletionTokens, completion.Duration, null));

            // ---------------------------------------------- 3 · cümle bağlama
            // Adım BURADA KABUL EDİLİYOR. Bağlanamayan cümle atılıyor ve
            // sayılıyor; adım reddedilmiyor.
            var (binding, gate) = BindSentences(step, document, view);

            return new ScenarioStepOutcome(step.Id, Accepted: true, document, binding, gate, attempts, null);
        }

        return Rejected(
            step,
            attempts,
            $"{MaxAttemptsPerStep} denemede de geçemedi: {string.Join(" · ", violations)}");
    }

    private static (IReadOnlyList<SentenceBinding> Binding, SentenceGateStatus Gate) BindSentences(
        ScenarioStep step,
        ScenarioOutputDocument document,
        StepEvidenceView view)
    {
        if (!document.ProseIsReportContent)
        {
            // Bu adımın düzyazısı rapora GİRMİYOR — ara çıktı. Kapıyı burada
            // koşturmak her cümleyi attırırdı: `evidence.summary` kimlik
            // açmıyor, dolayısıyla hiçbir cümle bağlanamazdı ve rapor boş
            // çıkardı. Atlandığı YAZILI; sessiz bir atlama, kapının kendisinden
            // tehlikeli.
            return ([], new SentenceGateStatus(
                step.Id,
                SentenceGateOutcome.NotApplicable,
                $"`{document.SchemaName}` düzyazısı rapora girmiyor (ara adım)."));
        }

        var binding = document.Prose
            .Select(prose => SentenceBinder.Bind(prose.Text, view.VisibleIds))
            .ToArray();

        return (binding, new SentenceGateStatus(step.Id, SentenceGateOutcome.Ran, "Cümleler bağlandı."));
    }

    private static ScenarioStepOutcome Rejected(
        ScenarioStep step,
        IReadOnlyList<ScenarioStepAttempt> attempts,
        string? rejection) =>
        new(step.Id, Accepted: false, null, [],
            new SentenceGateStatus(step.Id, SentenceGateOutcome.Skipped, "Adım reddedildi."),
            attempts, rejection);

    private static ScenarioRunOutcome Stop(IReadOnlyList<ScenarioStepOutcome> outcomes, string detail) =>
        new(outcomes, null, Stopped: true, detail);

    /// <summary>
    /// Kabul edilmiş adımlardan belgeyi kurar.
    ///
    /// <para>
    /// Bulgular ve aksiyonlar <b>şemadan</b> tanınıyor, adım kimliğinden değil:
    /// adım adı senaryonun, şema adı motorun sözleşmesi. Adım adına bağlansaydı
    /// aynı şemayı başka bir adım adıyla kullanan ikinci bir senaryo sessizce
    /// boş rapor üretirdi.
    /// </para>
    /// </summary>
    private static RcaReportDocument Assemble(
        ScenarioDefinition scenario,
        EvidenceBundle bundle,
        ModelEndpoint endpoint,
        IReadOnlyList<ScenarioStepOutcome> outcomes)
    {
        var findings = new List<RcaReportFinding>();
        var actions = new List<RcaReportAction>();

        foreach (var outcome in outcomes.Where(o => o is { Accepted: true, Document.ProseIsReportContent: true }))
        {
            var document = outcome.Document!;

            for (var i = 0; i < outcome.Binding.Count; i++)
            {
                var text = outcome.Binding[i].Text;

                if (text.Length == 0)
                {
                    // Bütün cümleleri atılan bir kayıt rapora GİRMİYOR. Boş
                    // gövdeli bir bulgu, kanıt kimlikleri yüzünden dolu
                    // görünürdü — ve okuyan onu bir bulgu sanardı.
                    continue;
                }

                if (string.Equals(document.SchemaName, "action_list", StringComparison.Ordinal))
                {
                    actions.Add(new RcaReportAction(text, IdsOf(outcome.Binding[i])));
                }
                else
                {
                    findings.Add(new RcaReportFinding(
                        text,
                        IdsOf(outcome.Binding[i]),
                        [.. document.ContradictingIds.Where(id => IdsOf(outcome.Binding[i]).Contains(id, StringComparer.Ordinal))]));
                }
            }
        }

        var reported = outcomes
            .SelectMany(o => o.Attempts)
            .ToArray();

        return new RcaReportDocument
        {
            BundleId = bundle.Id,
            ScenarioId = scenario.Metadata.Id,
            ScenarioVersion = scenario.Metadata.Version,
            Findings = findings,
            Actions = actions,
            DroppedSentenceCount = outcomes.Sum(o => o.DroppedSentences),
            ProducedSentenceCount = outcomes.Sum(o => o.ProducedSentences),
            FabricatedCitationSentenceCount = outcomes
                .SelectMany(o => o.Binding)
                .SelectMany(b => b.Sentences)
                .Count(s => !s.Bound && s.UnresolvedCitations.Count > 0),
            SentenceGateSkipped =
            [
                .. outcomes
                    .Select(o => o.SentenceGate)
                    .Where(g => g.Outcome != SentenceGateOutcome.Ran),
            ],
            // Uçtan OKUNUYOR, uydurulmuyor. Önceki hâli yer tutucuydu ve
            // `Model` alanına SENARYO KİMLİĞİ yazıyordu — rapor "hangi modelle
            // üretildi" sorusuna senaryo adıyla cevap veriyordu, yani F4'ün
            // "aynı kanıt, farklı model" karşılaştırması iki koşumu ayırt
            // edemezdi.
            ModelInfo = new RcaReportModelInfo(
                Provider: endpoint.Name,
                Model: endpoint.Model,

                // Hiçbir deneme bildirmediyse `null` — 0 DEĞİL. Sıfır yazılsaydı
                // bütçe hiç tükenmez, kapı hiç kapanmaz, sebep hiç görünmezdi.
                PromptTokens: Total(reported.Select(a => a.PromptTokens)),
                CompletionTokens: Total(reported.Select(a => a.CompletionTokens)),
                UnreportedAttempts: reported.Count(a => a.PromptTokens is null),

                // T54: muafiyetin bağlanacağı nokta burası. `ModelEndpoint`
                // bunu T42'den beri taşıyordu ve üretimde tüketicisi yoktu —
                // yani doğru duran ama hiçbir yere ulaşmayan bir kayıt.
                BoundaryOverridden: endpoint.BoundaryOverridden,
                BoundaryOverrideReason: endpoint.BoundaryOverrideReason),
        };
    }

    private static IReadOnlyList<string> IdsOf(SentenceBinding binding) =>
        [.. binding.Sentences.Where(s => s.Bound).SelectMany(s => s.CitedIds).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Bildirilen sayıların toplamı; <b>hiçbiri bildirilmediyse
    /// <see langword="null"/></b>.
    ///
    /// <para>
    /// Kısmi bir toplam bir <b>alt sınır</b> ve tek başına yanıltıcı; bu yüzden
    /// <see cref="RcaReportModelInfo.UnreportedAttempts"/> onun yanında duruyor
    /// ve <see cref="RcaReportModelInfo.TokensComplete"/> soruyu tek satırda
    /// cevaplıyor.
    /// </para>
    /// </summary>
    private static int? Total(IEnumerable<int?> values)
    {
        var reported = values.Where(v => v is not null).Select(v => v!.Value).ToArray();

        return reported.Length == 0 ? null : reported.Sum();
    }
}
