using System.Net;
using System.Reflection;

using Bizigo.Contracts.Security;
using Bizigo.Evidence;
using Bizigo.Rca.Models;
using Bizigo.Rca.Reasoning;
using Bizigo.ScenarioPlugin;

namespace Bizigo.UnitTests;

/// <summary>
/// T44'ün ortak koşum takımı — <b>tek yerde</b>.
///
/// <para>
/// Ayrı bir sınıf olmasının sebebi §6: bir testin geçme sebebi kurulumun
/// ayrıntısına kaymamalı. Üç test sınıfı aynı paketi, aynı ucu ve aynı sahte
/// modeli kullanıyor; üç kopya olsaydı biri düzeltilip diğerleri eski kalabilir
/// ve testler farklı şeyleri ölçerken aynı şeyi ölçüyor görünürdü.
/// </para>
/// </summary>
internal static class ReasoningHarness
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 20, 14, 0, 0, TimeSpan.Zero);

    internal static readonly IScenarioProviderRegistry Registry = new ScenarioProviderRegistry(
    [
        "logs.window", "logs.first-seen", "logs.volume", "logs.silence",
        "logs.attribute-lift", "logs.propagation", "change.feed",
    ]);

    /// <summary>Üç kimlikli kanıt satırı. Üç, çünkü "gördüğü" ile "pakette var" ayrımı en az iki satır istiyor.</summary>
    internal static EvidenceBundle Bundle(string summaryOfFirst = "core-sw-02 arkasında BGP_PEER_DOWN") => new()
    {
        Id = Guid.Parse("01920000-0000-7000-8000-0000000000aa"),
        GatheredAt = Now,
        Window = new RcaWindow
        {
            From = Now.AddMinutes(-30),
            To = Now,
            BaselineFrom = Now.AddDays(-7),
            BaselineTo = Now.AddMinutes(-30),
        },
        Scope = new BundleScope(["network/core"], IsSystem: false),
        Trust = new WindowTrust(1_204, 0),
        Slices =
        [
            new EvidenceSlice
            {
                ProviderId = "logs.volume",
                Kind = EvidenceKind.Log,
                Status = EvidenceStatus.Gathered,
                Items =
                [
                    new EvidenceItem("EV-01", "logs.volume", EvidenceKind.Log, Now, 1.0,
                        summaryOfFirst, new Dictionary<string, string>(StringComparer.Ordinal)),
                    new EvidenceItem("EV-02", "logs.volume", EvidenceKind.Log, Now.AddSeconds(1), 0.9,
                        "edge-rtr-07 sustu", new Dictionary<string, string>(StringComparer.Ordinal)),
                    new EvidenceItem("EV-03", "logs.volume", EvidenceKind.Log, Now.AddSeconds(2), 0.8,
                        "ACL push", new Dictionary<string, string>(StringComparer.Ordinal)),
                ],
            },
        ],
    };

    /// <summary>
    /// İki adımlı iskelet: birincisi paketin kimliklerini görüyor, ikincisi
    /// <b>yalnızca birincinin bağladıklarını</b>. Görüş alanının daralması bu
    /// ticket'ın taşıyıcı kararı ve iskelet onu doğrudan kuruyor.
    /// </summary>
    internal const string TwoStepYaml = """
        apiVersion: bizigo.dev/v1
        kind: Scenario
        metadata:
          id: test.reasoning
          version: 1.0.0
          owner: platform-team
        spec:
          trigger:
            on: [manual]
          evidence:
            providers: [logs.volume]
          steps:
            - id: bind-evidence
              task: "Hipotezleri kanıta bağla."
              input: evidence.items
              output:
                schema: evidence_binding
                constraints: [evidence_ids_must_exist]
            - id: write-actions
              task: "Aksiyon öner."
              input: steps.bind-evidence
              output:
                schema: action_list
                constraints: [evidence_ids_must_exist]
          publish:
            requires_review: true
        """;

    internal static ScenarioDefinition Scenario(string yaml = TwoStepYaml)
    {
        var result = ScenarioYamlLoader.Load(yaml, Registry, "test.yaml");

        // Yüklenmemiş bir senaryoyla koşmak, ölçmek istediğimiz şeyin yerine
        // yükleyici hatasını ölçmek olurdu — ve yeşil/kırmızı ayırt edilemezdi.
        Assert.True(result.Ok, result.Describe());
        return result.Value;
    }

    internal static async ValueTask<ModelEndpoint> EndpointAsync(CancellationToken ct)
    {
        var verdict = await new ModelBoundaryGate(new LoopbackResolver()).VerifyAsync(
            new ModelEndpointOptions
            {
                Name = "test-gpu",
                BaseUrl = "http://gpu.kurum.local:8000/v1",
                Model = "qwen3-32b",
                DataBoundary = ModelDataBoundary.Internal,
            },
            ct);

        Assert.True(verdict.Allowed, verdict.Rejection);
        return verdict.Endpoint!;
    }

    internal static async ValueTask<ScenarioRunOutcome> RunAsync(
        IModelProvider model,
        CancellationToken ct,
        string yaml = TwoStepYaml,
        EvidenceBundle? bundle = null)
    {
        var runner = new ScenarioStepRunner(model, await EndpointAsync(ct), PromptContentLevel.Summary);

        return await runner.RunAsync(Scenario(yaml), bundle ?? Bundle(), ct);
    }

    /// <summary>`evidence_binding` çıktısı — tek bulgu, verilen kimliklerle.</summary>
    internal static string Binding(string prose, params string[] ids) =>
        $$"""
        {"findings": [{"hypothesis": {{Quote(prose)}}, "evidence_ids": [{{Ids(ids)}}]}]}
        """;

    internal static string Actions(string prose, params string[] ids) =>
        $$"""
        {"actions": [{"text": {{Quote(prose)}}, "evidence_ids": [{{Ids(ids)}}]}]}
        """;

    private static string Ids(IEnumerable<string> ids) => string.Join(", ", ids.Select(Quote));

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private sealed class LoopbackResolver : IEndpointAddressResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("10.20.30.40")]);
    }
}

/// <summary>
/// Sahte model — <b>senaryolanmış</b> cevaplar, sırayla.
///
/// <para>
/// Gerçek bir uç yok ve olmamalı: bu testlerin ölçtüğü şey modelin kalitesi
/// değil <b>motorun kapıları</b>. Gerçek modelle koşan bir test, kapının
/// çalıştığını değil modelin o gün ne ürettiğini ölçerdi — ve duvar saatiyle
/// birlikte kararsız olurdu (§6).
/// </para>
/// </summary>
internal sealed class FakeModel(params string[] responses) : IModelProvider
{
    private int _index;

    public string Name => "sahte";

    /// <summary>Kaç kez çağrıldı — yeniden denemenin ölçüldüğü yer.</summary>
    public int Calls { get; private set; }

    /// <summary>Gönderilen prompt'lar; retry'ın ihlali adlandırdığı burada okunuyor.</summary>
    public List<string> Prompts { get; } = [];

    /// <summary>Uç belirteç bildirsin mi. <see langword="false"/> ise <see langword="null"/> — <b>0 değil</b>.</summary>
    public bool ReportTokens { get; init; } = true;

    public ValueTask<ModelCompletion> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Calls++;
        Prompts.Add(request.User.Text);

        var text = _index < responses.Length ? responses[_index] : responses[^1];
        _index++;

        return ValueTask.FromResult(new ModelCompletion(
            text,
            ReportTokens ? 100 : null,
            ReportTokens ? 20 : null,

            // Sabit süre: bu testlerin hiçbiri duvar saatini ölçmüyor ve
            // ölçmemeli. Gerçek bir süre, testi makinenin yüküne bağlardı.
            TimeSpan.FromMilliseconds(5),
            Failure: null));
    }
}

/// <summary>
/// <b>İkinci kapı: cümle bağlama</b> — F4'ün Karar 1'i.
///
/// <para>
/// <i>"Referanssız cümle rapora hiç girmiyor — ama atıldığı sayılıyor ve
/// gösteriliyor."</i> <b>Atmak ile saymak ayrı iddialar ve ayrı testleri
/// var.</b> Tek testte ölçülselerdi, sayacı silen bir değişiklik atma testini
/// hâlâ geçerdi ve tersi de doğru olurdu — yani iki kusurdan biri her zaman
/// görünmez kalırdı.
/// </para>
/// </summary>
public sealed class SentenceBindingGateTests
{
    private const string Bagli = "ACL değişikliği BGP oturumlarını düşürdü [EV-01].";
    private const string Bagsiz = "Yukarı akış sağlayıcıda bakım penceresi olabilir.";

    private static FakeModel Model() => new(
        ReasoningHarness.Binding($"{Bagli} {Bagsiz}", "EV-01"),
        ReasoningHarness.Actions("ACL değişikliğini geri al [EV-01].", "EV-01"));

    /// <summary>
    /// <b>Birinci iddia: içerik atılıyor.</b> Bağlanamayan cümle raporun
    /// metninde <b>hiç yok</b> — rozetle işaretlenmiş değil, yok.
    /// </summary>
    [Fact]
    public async Task Referanssiz_cumle_rapora_girmiyor()
    {
        var outcome = await ReasoningHarness.RunAsync(Model(), TestContext.Current.CancellationToken);

        Assert.False(outcome.Stopped, outcome.StopDetail);
        Assert.NotNull(outcome.Report);

        var finding = Assert.Single(outcome.Report.Findings);

        Assert.Contains("ACL değişikliği BGP oturumlarını düşürdü", finding.Hypothesis, StringComparison.Ordinal);
        Assert.DoesNotContain("bakım penceresi", finding.Hypothesis, StringComparison.Ordinal);

        // Markdown da aynı şeyi söylüyor: iki çıktının ayrışması, ekranda
        // görünen bir kısıtın export'ta kaybolmasının kendisi olurdu.
        Assert.DoesNotContain("bakım penceresi", outcome.Report.ToMarkdown(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>İkinci iddia: sayı kalıyor.</b> Ve payda da — Karar 1'in cümlesi
    /// <i>"12 cümle üretti, 3'ü bağlanamadı"</i>; payda olmadan pay bir oran
    /// değil, iki rapor karşılaştırılamaz.
    /// </summary>
    [Fact]
    public async Task Atilan_cumle_sayiliyor()
    {
        var outcome = await ReasoningHarness.RunAsync(Model(), TestContext.Current.CancellationToken);

        Assert.NotNull(outcome.Report);

        // İki adım: `bind-evidence` iki cümle üretti (biri atıldı),
        // `write-actions` bir cümle üretti (bağlandı).
        Assert.Equal(1, outcome.Report.DroppedSentenceCount);
        Assert.Equal(3, outcome.Report.ProducedSentenceCount);
    }

    /// <summary>
    /// <b>Sayı GÖSTERİLİYOR.</b> Karar 1'in ikinci yarısı bir depolama kararı
    /// değil: yalnızca atmak kaliteyi ölçülemez yapardı — <i>"ölçemedim"</i> ile
    /// <i>"sorun yok"</i>un aynı çıktıya inmesi.
    /// </summary>
    [Fact]
    public async Task Atilan_cumle_sayisi_raporda_gorunuyor()
    {
        var outcome = await ReasoningHarness.RunAsync(Model(), TestContext.Current.CancellationToken);

        Assert.NotNull(outcome.Report);

        var markdown = outcome.Report.ToMarkdown();

        Assert.Contains("3 cümle üretti", markdown, StringComparison.Ordinal);
        Assert.Contains("1'i kanıta bağlanamadı", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>"Hiç atıf yapmadı" ile "atıf uydurdu" ayrı sayılıyor.</b> İkisi de
    /// atılıyor — ama biri prompt'un, diğeri modelin sorunu, ve T47 ikisine
    /// farklı karar verecek.
    /// </summary>
    [Fact]
    public async Task Uydurulan_atif_ayrica_sayiliyor()
    {
        var model = new FakeModel(
            ReasoningHarness.Binding($"{Bagli} Yukarı akışta bakım vardı [EV-99].", "EV-01"),
            ReasoningHarness.Actions("ACL değişikliğini geri al [EV-01].", "EV-01"));

        var outcome = await ReasoningHarness.RunAsync(model, TestContext.Current.CancellationToken);

        Assert.NotNull(outcome.Report);
        Assert.Equal(1, outcome.Report.DroppedSentenceCount);
        Assert.Equal(1, outcome.Report.FabricatedCitationSentenceCount);
    }

    /// <summary>
    /// <b>Bütün cümleleri atılan bir kayıt rapora hiç girmiyor.</b> Girseydi
    /// gövdesi boş ama kanıt kimlikleri dolu bir bulgu doğardı — ve okuyan onu
    /// bir bulgu sanardı. Sayı yine de duruyor.
    /// </summary>
    [Fact]
    public async Task Butun_cumleleri_atilan_kayit_rapora_girmiyor()
    {
        var model = new FakeModel(
            ReasoningHarness.Binding("Bir şey olmuş olabilir.", "EV-01"),
            ReasoningHarness.Actions("Bir şeyler yapın.", "EV-01"));

        var outcome = await ReasoningHarness.RunAsync(model, TestContext.Current.CancellationToken);

        Assert.NotNull(outcome.Report);
        Assert.Empty(outcome.Report.Findings);
        Assert.Empty(outcome.Report.Actions);
        Assert.Equal(2, outcome.Report.DroppedSentenceCount);
    }

    /// <summary>
    /// <b>Cümle bağlamanın KOŞMADIĞI yer yazılı.</b> Sessizce atlayan bir kapı,
    /// kapının kendisinden tehlikeli: <c>Produces&lt;T&gt;</c> üç uç dosyasını
    /// hiç görmedi ve üç test de yeşildi.
    /// </summary>
    [Fact]
    public async Task Ara_adimda_kapinin_kosmadigi_yaziyor()
    {
        const string yaml = """
            apiVersion: bizigo.dev/v1
            kind: Scenario
            metadata:
              id: test.intermediate
              version: 1.0.0
              owner: platform-team
            spec:
              trigger:
                on: [manual]
              evidence:
                providers: [logs.volume]
              steps:
                - id: rank-hypotheses
                  task: "Hipotez sırala."
                  input: evidence.summary
                  output:
                    schema: hypothesis_list
                    constraints: []
                    constraints_waived: "Özetlerin kimliği yok; görüş alanı boş."
              publish:
                requires_review: false
            """;

        var model = new FakeModel("""{"hypotheses": [{"text": "ACL değişikliği şüpheli."}]}""");

        var outcome = await ReasoningHarness.RunAsync(model, TestContext.Current.CancellationToken, yaml);

        Assert.False(outcome.Stopped, outcome.StopDetail);
        Assert.NotNull(outcome.Report);

        // Kapı koşmadı — ve koşmadığı raporda YAZILI. Sayaç da bu adımdan
        // beslenmeyecek, çünkü bu adımın düzyazısı rapora girmiyor.
        var skipped = Assert.Single(outcome.Report.SentenceGateSkipped);
        Assert.Contains("not-applicable", skipped, StringComparison.Ordinal);
        Assert.Equal(0, outcome.Report.ProducedSentenceCount);
        Assert.Contains("Cümle bağlama koşmadı", outcome.Report.ToMarkdown(), StringComparison.Ordinal);
    }
}

/// <summary>
/// <b>Birinci kapı: kısıt doğrulama</b> — ve doğrulamanın <b>adımın gördüğü</b>
/// kanıta karşı yapıldığı.
///
/// <para>
/// Paketin tamamına karşı doğrulansaydı bir adım hiç görmediği bir kanıta atıf
/// yapıp geçerdi: kimlik doğru, gerekçe uydurma. Kapının kapatmak istediği şey
/// tam olarak bu, ve <b>paket düzeyinde doğrulayan bir kapı onu hiç görmez</b> —
/// yani kapı yeşil yanarken hiçbir şey söylemez.
/// </para>
/// </summary>
public sealed class ConstraintGateTests
{
    /// <summary>
    /// <b>Ölçümün kırmızı yanabildiği taraf.</b> <c>EV-02</c> pakette
    /// <b>var</b> — ama <c>write-actions</c> onu görmüyor, çünkü
    /// <c>bind-evidence</c> yalnızca <c>EV-01</c>'i bağladı.
    /// </summary>
    [Fact]
    public async Task Adimin_gormedigi_kanita_atif_reddediliyor()
    {
        var model = new FakeModel(
            ReasoningHarness.Binding("ACL değişikliği [EV-01].", "EV-01"),
            ReasoningHarness.Actions("Şunu yap [EV-02].", "EV-02"));

        var outcome = await ReasoningHarness.RunAsync(model, TestContext.Current.CancellationToken);

        Assert.True(outcome.Stopped);
        Assert.Null(outcome.Report);

        var rejected = outcome.Steps[^1];

        Assert.False(rejected.Accepted);
        Assert.Equal("write-actions", rejected.StepId);
        Assert.Contains("EV-02", rejected.Rejection!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Aynı kurulum, tek fark atfın görüş alanında olması.</b> Kırmızının
    /// karşıtı yazılmazsa yukarıdaki testin ne yüzünden düştüğü bilinmez —
    /// "atıf reddedildi" ile "her atıf reddediliyor" aynı çıktıyı verir.
    /// </summary>
    [Fact]
    public async Task Adimin_gordugu_kanita_atif_geciyor()
    {
        var model = new FakeModel(
            ReasoningHarness.Binding("ACL değişikliği [EV-01].", "EV-01"),
            ReasoningHarness.Actions("Şunu yap [EV-01].", "EV-01"));

        var outcome = await ReasoningHarness.RunAsync(model, TestContext.Current.CancellationToken);

        Assert.False(outcome.Stopped, outcome.StopDetail);
        Assert.NotNull(outcome.Report);
        Assert.Single(outcome.Report.Actions);
    }

    /// <summary>
    /// <b>Bir adım en fazla iki kez koşuyor</b> ve ikinci koşum ihlali
    /// <b>adlandırarak</b> soruyor. Aynı metinle tekrar sormak bir yeniden
    /// deneme değil bir kumar olurdu.
    /// </summary>
    [Fact]
    public async Task Kisit_ihlali_bir_kez_yeniden_deneniyor()
    {
        var model = new FakeModel(
            ReasoningHarness.Binding("ACL değişikliği [EV-01].", "EV-01"),
            ReasoningHarness.Actions("Şunu yap [EV-02].", "EV-02"),   // 1. deneme: görmediği kimlik
            ReasoningHarness.Actions("Şunu yap [EV-01].", "EV-01"));  // 2. deneme: düzeltilmiş

        var outcome = await ReasoningHarness.RunAsync(model, TestContext.Current.CancellationToken);

        Assert.False(outcome.Stopped, outcome.StopDetail);

        var step = outcome.Steps[^1];

        Assert.True(step.Accepted);
        Assert.Equal(2, step.Attempts.Count);
        Assert.Equal(ScenarioStepRunner.MaxAttemptsPerStep, step.Attempts.Count);

        // Yeniden deneme prompt'u ihlali TAŞIYOR.
        Assert.Contains("ÖNCEKİ DENEMEN REDDEDİLDİ", model.Prompts[^1], StringComparison.Ordinal);
        Assert.Contains("EV-02", model.Prompts[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>İkinci düşüşte senaryo duruyor — kısmi rapor üretilmiyor.</b>
    ///
    /// <para>
    /// Kısmi rapor üretilseydi, kapının kapattığı adım rapordan <b>sessizce</b>
    /// düşerdi ve okuyan eksik bir raporu tam sanardı: hata yok, sayaç yok,
    /// belirti yok.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Ikinci_dususte_senaryo_duruyor()
    {
        var model = new FakeModel(
            ReasoningHarness.Binding("ACL değişikliği [EV-01].", "EV-01"),
            ReasoningHarness.Actions("Şunu yap [EV-02].", "EV-02"));

        var outcome = await ReasoningHarness.RunAsync(model, TestContext.Current.CancellationToken);

        Assert.True(outcome.Stopped);
        Assert.Null(outcome.Report);
        Assert.Equal(2, outcome.Steps[^1].Attempts.Count);

        // Üçüncü çağrı YOK: bir bulgu adımı + iki aksiyon denemesi.
        Assert.Equal(3, model.Calls);
    }

    /// <summary>
    /// Ayrışmayan çıktı da bir kez yeniden deneniyor — ve ihlal adlandırılıyor.
    /// </summary>
    [Fact]
    public async Task Ayrismayan_cikti_bir_kez_yeniden_deneniyor()
    {
        var model = new FakeModel(
            "burası JSON değil",
            ReasoningHarness.Binding("ACL değişikliği [EV-01].", "EV-01"),
            ReasoningHarness.Actions("Şunu yap [EV-01].", "EV-01"));

        var outcome = await ReasoningHarness.RunAsync(model, TestContext.Current.CancellationToken);

        Assert.False(outcome.Stopped, outcome.StopDetail);
        Assert.Equal(2, outcome.Steps[0].Attempts.Count);
        Assert.Contains("JSON", model.Prompts[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>max_items</c>'ı T43 taşıdı ama yorumlamadı; zorlaması T44'ün.
    /// Yapısal bir ihlal olduğu için <b>adım reddi</b> tarafında, cümle atma
    /// tarafında değil.
    /// </summary>
    [Fact]
    public async Task Max_items_asilirsa_adim_reddediliyor()
    {
        const string yaml = """
            apiVersion: bizigo.dev/v1
            kind: Scenario
            metadata:
              id: test.maxitems
              version: 1.0.0
              owner: platform-team
            spec:
              trigger:
                on: [manual]
              evidence:
                providers: [logs.volume]
              steps:
                - id: bind-evidence
                  task: "Bağla."
                  input: evidence.items
                  output:
                    schema: evidence_binding
                    max_items: 1
                    constraints: [evidence_ids_must_exist]
              publish:
                requires_review: false
            """;

        var model = new FakeModel("""
            {"findings": [
              {"hypothesis": "Bir [EV-01].", "evidence_ids": ["EV-01"]},
              {"hypothesis": "İki [EV-02].", "evidence_ids": ["EV-02"]}
            ]}
            """);

        var outcome = await ReasoningHarness.RunAsync(model, TestContext.Current.CancellationToken, yaml);

        Assert.True(outcome.Stopped);
        Assert.Contains("max_items", outcome.StopDetail!, StringComparison.Ordinal);
    }
}

/// <summary>
/// <b>Ön uçuş:</b> motorun tanımadığı bir kısıt ya da şema adıyla koşum
/// başlamıyor.
///
/// <para>
/// T43 kısıt adlarını ve şema adlarını <b>açık</b> bıraktı ve gerekçesi
/// duruyor: küme kapatılırsa her yeni senaryo çekirdeği değiştirir. T44 o
/// açıklığı korurken üçüncü hâli kapatıyor — <i>"tanımadım ama geçtim"</i>.
/// Yükleme anında açık, <b>koşum anında kapalı</b>: yeni bir ad yazmak
/// senaryoyu yüklenebilir yapıyor ama koşulabilir yapmıyor, ve ayrışma
/// <b>ilk koşumda</b> görünüyor, raporun içinde değil.
/// </para>
/// </summary>
public sealed class ReasoningPreflightTests
{
    private static string YamlWith(string schema, string constraint) => $$"""
        apiVersion: bizigo.dev/v1
        kind: Scenario
        metadata:
          id: test.preflight
          version: 1.0.0
          owner: platform-team
        spec:
          trigger:
            on: [manual]
          evidence:
            providers: [logs.volume]
          steps:
            - id: only-step
              task: "Tek iş."
              input: evidence.items
              output:
                schema: {{schema}}
                constraints: [{{constraint}}]
          publish:
            requires_review: false
        """;

    /// <summary>
    /// Bilinmeyen bir kısıt adı <b>yükleniyor</b> — format kararı bunu böyle
    /// istiyor — ama <b>koşmuyor</b>.
    /// </summary>
    [Fact]
    public async Task Bilinmeyen_kisit_adi_sessizce_gecmiyor()
    {
        var yaml = YamlWith("evidence_binding", "pattern_must_compile");

        // Önce: yükleyici bunu KABUL ediyor. Kabul etmeseydi aşağıdaki iddia
        // motoru değil yükleyiciyi ölçerdi.
        Assert.True(ScenarioYamlLoader.Load(yaml, ReasoningHarness.Registry, "t.yaml").Ok);

        var outcome = await ReasoningHarness.RunAsync(
            new FakeModel("{}"), TestContext.Current.CancellationToken, yaml);

        Assert.True(outcome.Stopped);
        Assert.Contains("pattern_must_compile", outcome.StopDetail!, StringComparison.Ordinal);

        // Ve koşum HİÇ başlamadı: ilk adımın belirteçleri bile ödenmedi.
        Assert.Empty(outcome.Steps);
    }

    [Fact]
    public async Task Bilinmeyen_sema_adi_sessizce_gecmiyor()
    {
        var yaml = YamlWith("threshold_projection", "evidence_ids_must_exist");

        Assert.True(ScenarioYamlLoader.Load(yaml, ReasoningHarness.Registry, "t.yaml").Ok);

        var outcome = await ReasoningHarness.RunAsync(
            new FakeModel("{}"), TestContext.Current.CancellationToken, yaml);

        Assert.True(outcome.Stopped);
        Assert.Contains("threshold_projection", outcome.StopDetail!, StringComparison.Ordinal);
        Assert.Empty(outcome.Steps);
    }

    /// <summary>
    /// <b>Sevk edilen senaryo bu motorda koşabiliyor.</b>
    ///
    /// <para>
    /// Yukarıdaki iki test uydurma adları reddettiğini gösteriyor; bu test
    /// reddin <b>her şeyi</b> reddetmediğini gösteriyor. İkisi olmadan "kapı
    /// çalışıyor" ile "kapı hep kapalı" ayırt edilemez.
    /// </para>
    /// </summary>
    [Fact]
    public void Sevk_edilen_senaryo_motorda_kosabiliyor()
    {
        var report = ScenarioCatalog.Load(
            Path.Combine(RepositoryLayout.Root, "catalog", "scenarios"),
            ReasoningHarness.Registry);

        Assert.True(report.Ok, report.Describe());
        Assert.NotEmpty(report.Scenarios);

        foreach (var scenario in report.Scenarios)
        {
            var violations = ScenarioConstraintGate.Check(
                scenario, ScenarioConstraintRegistry.BuiltIn, ScenarioOutputSchemas.BuiltIn);

            Assert.True(
                violations.Count == 0,
                $"{scenario.Metadata.Id} bu motorda koşamaz:{Environment.NewLine}" +
                string.Join(Environment.NewLine, violations));
        }
    }

    /// <summary>
    /// <b>T43'ün ikinci açık ucunun kaydı.</b> <c>write-actions</c> artık kendi
    /// kanıt atfını taşıyor; muafiyet düştü ve düşüşü <b>iki dosyada birden</b>
    /// görünüyor (senaryo dosyası + <c>ExpectedWaivedCount</c>).
    /// </summary>
    [Fact]
    public void Aksiyon_adimi_kendi_kanit_atfini_tasiyor()
    {
        var report = ScenarioCatalog.Load(
            Path.Combine(RepositoryLayout.Root, "catalog", "scenarios"),
            ReasoningHarness.Registry);

        var rca = Assert.Single(report.Scenarios, s => s.Metadata.Id == "builtin.rca.network");
        var actions = Assert.Single(rca.Steps, s => s.Id == "write-actions");

        Assert.False(actions.Output.IsWaived);
        Assert.Contains(
            EvidenceIdsMustExistConstraint.ConstraintName,
            actions.Output.Constraints,
            StringComparer.Ordinal);
    }
}

/// <summary>
/// <b>Prompt kurucusu <see cref="RedactedPrompt"/> alıyor, <c>string</c>
/// değil</b> — T41'in şartının üçüncü halkası.
///
/// <para>
/// T41 kapıyı bir tipe bağladı, T42 <see cref="ModelRequest"/>'i o tipten girdi
/// almaya zorladı, T44 prompt'u <b>kuran</b> tarafı aynı çizgiye çekti. Zincir
/// tamamlanmasaydı garanti yarım kalırdı: kapıdan geçmiş bir tip isteyen bir
/// istek, kapıyı atlayan bir kurucuyla beslenebilirdi.
/// </para>
/// </summary>
public sealed class PromptGateTests
{
    /// <summary>
    /// İddia bir çalışma zamanı kontrolü değil bir <b>tip</b>. Test bunu
    /// yansımayla tutuyor — emsali <c>ModelBoundaryTests</c> ve aynı gerekçe.
    /// </summary>
    [Fact]
    public void Prompt_tipini_yalnizca_kurucu_uretebiliyor()
    {
        var ctors = typeof(ScenarioStepPrompt)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotEmpty(ctors);
        Assert.All(ctors, c => Assert.True(c.IsPrivate, $"Yapıcı private değil: {c}"));
        Assert.Empty(typeof(ScenarioStepPrompt).GetConstructors());

        // Ve çıktı redakte tipinde: `string` döndüren bir kurucu, kapıyı bir
        // çağrı alışkanlığına çevirirdi.
        Assert.Equal(typeof(RedactedPrompt), typeof(ScenarioStepPrompt).GetProperty("System")!.PropertyType);
        Assert.Equal(typeof(RedactedPrompt), typeof(ScenarioStepPrompt).GetProperty("User")!.PropertyType);
    }

    /// <summary>
    /// Prompt metnini kuran gövdeler dışarıya <c>string</c> <b>vermiyor</b>:
    /// <see cref="ScenarioPromptBuilder"/>'ın açık yüzeyinde <c>string</c>
    /// döndüren bir üye yok.
    /// </summary>
    [Fact]
    public void Kurucu_disariya_ham_metin_vermiyor()
    {
        var leaking = typeof(ScenarioPromptBuilder)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(string))
            .Select(m => m.Name)
            .ToArray();

        Assert.True(
            leaking.Length == 0,
            "Prompt kurucusu dışarıya ham metin veriyor: " + string.Join(", ", leaking) +
            ". Kapıdan geçmemiş bir dizgi elde edilebiliyorsa kapı bir tip değil bir alışkanlıktır.");
    }

    /// <summary>
    /// <b>Ve davranış olarak da tutuyor.</b> Yansıma testi yüzeyi ölçüyor; bu
    /// test tabanın gerçekten koştuğunu ölçüyor. İkisi ayrı sorular: bir yüzey
    /// doğru tipte olup içeride kapıyı hiç çağırmayabilir.
    /// </summary>
    [Fact]
    public void Kanit_metnindeki_sir_prompta_girmiyor()
    {
        var bundle = ReasoningHarness.Bundle("cihaz yanıtı: password=Sup3rGizliParola!");
        var scenario = ReasoningHarness.Scenario();
        var view = StepEvidenceView.For(scenario.Steps[0], bundle, new Dictionary<string, ScenarioOutputDocument>());

        var prompt = ScenarioStepPrompt.Build(
            scenario.Steps[0],
            Assert.IsType<JsonListSchema>(Schema("evidence_binding")),
            view,
            PromptContentLevel.Summary);

        Assert.DoesNotContain("Sup3rGizliParola", prompt.User.Text, StringComparison.Ordinal);
        Assert.True(prompt.User.MaskedValues > 0, "Redaksiyon kapısı hiç maskeleme yapmamış.");
    }

    private static IScenarioOutputSchema Schema(string name)
    {
        Assert.True(ScenarioOutputSchemas.BuiltIn.TryGet(name, out var schema));
        return schema;
    }
}

/// <summary>
/// <b>Bildirilmemiş belirteç sayısı <see langword="null"/>, sıfır değil</b> —
/// T42'nin 8 numaralı ölçümü, T44'ün toplamında.
///
/// <para>
/// Sıfır yazılsaydı bütçe hiç tükenmez, kapı hiç kapanmaz, sebep hiç
/// görünmezdi. Toplamda ikinci bir tuzak var ve o T44'e ait: <b>kısmi bir
/// toplam bir alt sınırdır</b>, ve bunu söylemeyen bir sayı tam sanılır.
/// </para>
/// </summary>
public sealed class ReasoningTokenAccountingTests
{
    [Fact]
    public async Task Bildirilmemis_belirtec_sifir_degil_null()
    {
        var model = new FakeModel(
            ReasoningHarness.Binding("ACL [EV-01].", "EV-01"),
            ReasoningHarness.Actions("Yap [EV-01].", "EV-01"))
        {
            ReportTokens = false,
        };

        var outcome = await ReasoningHarness.RunAsync(model, TestContext.Current.CancellationToken);

        Assert.NotNull(outcome.Report);
        Assert.Null(outcome.Report.ModelInfo.PromptTokens);
        Assert.NotEqual(0, outcome.Report.ModelInfo.PromptTokens ?? -1);
        Assert.False(outcome.Report.ModelInfo.TokensComplete);
    }

    [Fact]
    public async Task Bildirilen_belirtecler_toplaniyor()
    {
        var model = new FakeModel(
            ReasoningHarness.Binding("ACL [EV-01].", "EV-01"),
            ReasoningHarness.Actions("Yap [EV-01].", "EV-01"));

        var outcome = await ReasoningHarness.RunAsync(model, TestContext.Current.CancellationToken);

        Assert.NotNull(outcome.Report);
        Assert.Equal(200, outcome.Report.ModelInfo.PromptTokens);
        Assert.True(outcome.Report.ModelInfo.TokensComplete);
    }

    /// <summary>
    /// <b>Kısmi toplam bir alt sınır olduğunu SÖYLÜYOR.</b> Söylemeseydi, iki
    /// adımın biri bildirmediğinde toplam tam sanılır ve bütçe sessizce
    /// yanılırdı — sıfır yazmanın daha sinsi hâli.
    /// </summary>
    [Fact]
    public async Task Kismi_toplam_eksik_oldugunu_soyluyor()
    {
        var model = new PartialTokenModel();

        var outcome = await ReasoningHarness.RunAsync(model, TestContext.Current.CancellationToken);

        Assert.NotNull(outcome.Report);
        Assert.Equal(100, outcome.Report.ModelInfo.PromptTokens);
        Assert.Equal(1, outcome.Report.ModelInfo.UnreportedAttempts);
        Assert.False(outcome.Report.ModelInfo.TokensComplete);
        Assert.Contains("alt sınır", outcome.Report.ToMarkdown(), StringComparison.Ordinal);
    }

    /// <summary>İlk çağrı sayı bildiriyor, ikincisi bildirmiyor.</summary>
    private sealed class PartialTokenModel : IModelProvider
    {
        private int _calls;

        public string Name => "kismi";

        public ValueTask<ModelCompletion> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            var first = _calls++ == 0;

            return ValueTask.FromResult(new ModelCompletion(
                first
                    ? ReasoningHarness.Binding("ACL [EV-01].", "EV-01")
                    : ReasoningHarness.Actions("Yap [EV-01].", "EV-01"),
                first ? 100 : null,
                first ? 20 : null,
                TimeSpan.FromMilliseconds(5),
                Failure: null));
        }
    }
}
