using System.Diagnostics.CodeAnalysis;

using Bizigo.ScenarioPlugin;

namespace Bizigo.Rca.Reasoning;

/// <summary>
/// Motorun <b>zorladığı</b> bir kısıt — birinci kapı.
///
/// <para>
/// İhlalde <b>adım</b> reddediliyor ve bir kez yeniden deneniyor. Cümle
/// atılmıyor: kısıt ihlalini cümle atarak geçiştirmek, var olmayan bir
/// <c>evidence_id</c>'yi rapordan silip raporu <b>geçerli göstermek</b> olurdu
/// (F4 plugin formatı §6).
/// </para>
/// </summary>
public interface IScenarioConstraint
{
    /// <summary><c>output.constraints</c>'ta yazılan ad.</summary>
    string Name { get; }

    /// <summary>İhlal listesi; boş liste "geçti" demek. Hepsi tek seferde dönüyor
    /// çünkü yeniden deneme ihlali <b>adlandırarak</b> soruyor.</summary>
    IReadOnlyList<string> Check(ScenarioOutputDocument document, StepEvidenceView view);
}

/// <summary>
/// <b>Halüsinasyon kapısı</b> — ve kapattığı şey "kimlik yok" değil,
/// <b>"kimlik doğru, gerekçe uydurma"</b>.
///
/// <para>
/// Karşılaştırma kümesi <see cref="StepEvidenceView.VisibleIds"/>: adımın
/// gerçekten gördüğü kimlikler. Paketin tamamına karşı doğrulansaydı bir adım
/// hiç görmediği bir kanıta atıf yapıp geçerdi — o atıf var olan bir kimliğe
/// işaret eder ama modelin onu görmesinin hiçbir yolu yoktur.
/// </para>
/// </summary>
public sealed class EvidenceIdsMustExistConstraint : IScenarioConstraint
{
    public const string ConstraintName = "evidence_ids_must_exist";

    public string Name => ConstraintName;

    public IReadOnlyList<string> Check(ScenarioOutputDocument document, StepEvidenceView view)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(view);

        var unseen = document.CitedIds
            .Where(id => !view.VisibleIds.Contains(id))
            .ToArray();

        if (unseen.Length == 0)
        {
            return [];
        }

        // Mesaj hangi kimliklerin görülmediğini SAYIYOR ve LİSTELİYOR: yeniden
        // deneme aynı adımı ihlali adlandırarak soruyor, "bir şey yanlış" diyen
        // bir tekrar modele hiçbir şey öğretmez.
        return
        [
            $"`{ConstraintName}`: {unseen.Length} atıf bu adımın gördüğü kanıtta yok — " +
            string.Join(", ", unseen) +
            $". Bu adımın görebildiği kimlikler: {DescribeVisible(view)}",
        ];
    }

    private static string DescribeVisible(StepEvidenceView view) =>
        view.VisibleIds.Count == 0
            ? "hiçbiri (bu adım kimlikli kanıt görmüyor)"
            : string.Join(", ", view.VisibleIds.Order(StringComparer.Ordinal));
}

/// <summary>
/// Motorun tanıdığı kısıt adları.
///
/// <para>
/// <b>T43'ün birinci açık ucu buydu:</b> çekirdek kümesi tutmuyordu, bilinmeyen
/// bir ad yüklemede geçiyordu ve koşumda ne olacağı yazılmamıştı. Karar aşağıda
/// ve iki parçalı — parçaları ayırmak §6.1'in bölmesinin aynısı:
/// </para>
///
/// <list type="number">
/// <item><b>Yükleme anında küme açık kalıyor.</b> Format kararı (§5.2) kısıt
/// adlarını uzantı noktası bıraktı ve gerekçesi duruyor: kaç tane olacağını
/// kimse bilmiyor, küme kapatılırsa her yeni senaryo <b>çekirdeği</b>
/// değiştirir.</item>
/// <item><b>Koşum anında küme kapalı.</b> Motor zorlayamadığı bir adla adımı
/// koşturmuyor. Zorlasaydı üçüncü hâl doğardı — <i>"kapı yazılı ama hiçbir şey
/// yapmıyor"</i> — ve bu deponun <c>Produces&lt;T&gt;</c> ile ödediği hâlin
/// kendisi o: kapı üç uç dosyasını hiç görmedi ve <b>üç test de yeşildi</b>.
/// Yeşilliği hiçbir şey ifade etmiyordu.</item>
/// </list>
///
/// <para>
/// İki parça birlikte şunu veriyor: yeni bir kısıt adı yazmak senaryoyu
/// <b>yüklenebilir</b> yapıyor ama <b>koşulabilir</b> yapmıyor, ve koşulabilir
/// yapmak için motora bir uygulama eklemek gerekiyor. Yani ad ile uygulamanın
/// ayrışması <b>ilk koşumda</b> görünüyor, raporun içinde değil.
/// </para>
///
/// <para>
/// <c>pattern_must_compile</c> <b>uygulanmadı</b>: format kararının parser
/// kalite taslağında doğdu, bugün onu yazan bir senaryo yok, ve tüketicisi
/// olmayan bir tip tahmindir (§8). Bugün onu yazan bir senaryo
/// <see cref="ScenarioConstraintGate"/> tarafından <b>adıyla</b> reddediliyor.
/// </para>
/// </summary>
public sealed class ScenarioConstraintRegistry
{
    private readonly Dictionary<string, IScenarioConstraint> _constraints;

    public ScenarioConstraintRegistry(IEnumerable<IScenarioConstraint> constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        _constraints = constraints.ToDictionary(c => c.Name, StringComparer.Ordinal);
    }

    public static ScenarioConstraintRegistry BuiltIn { get; } = new([new EvidenceIdsMustExistConstraint()]);

    public IReadOnlyCollection<string> Names => _constraints.Keys;

    public bool TryGet(string name, [NotNullWhen(true)] out IScenarioConstraint? constraint) =>
        _constraints.TryGetValue(name, out constraint);
}

/// <summary>
/// <b>Ön uçuş kontrolü:</b> bu senaryo bu motorda koşabilir mi.
///
/// <para>
/// Emsali <see cref="ScenarioEvidenceGate"/> ve aynı sebeple ayrı duruyor:
/// yükleyici zarfı doğruladı, geriye <i>"bu adları ben zorlayabiliyor muyum"</i>
/// sorusu kaldı ve onu yalnızca motor cevaplayabiliyor.
/// </para>
///
/// <para>
/// <b>Neden koşum başlamadan sorulmalı:</b> üçüncü adımda çıkan bir "bu kısıdı
/// tanımıyorum", ilk iki adımın belirteçleri ödendikten sonra çıkar — kota
/// harcanır ve rapor üretilmez. Kapıyı öne almak, ödenmeyecek bir maliyeti
/// ödememek.
/// </para>
/// </summary>
public static class ScenarioConstraintGate
{
    /// <param name="scenario">Zarfı doğrulanmış senaryo.</param>
    /// <param name="constraints">Motorun tanıdığı kısıtlar.</param>
    /// <param name="schemas">Motorun tanıdığı şemalar.</param>
    /// <returns>İhlal listesi; boş liste "koşabilir" demek.</returns>
    public static IReadOnlyList<string> Check(
        ScenarioDefinition scenario,
        ScenarioConstraintRegistry constraints,
        ScenarioOutputSchemas schemas)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(constraints);
        ArgumentNullException.ThrowIfNull(schemas);

        var violations = new List<string>();

        foreach (var step in scenario.Steps)
        {
            if (!schemas.TryGet(step.Output.Schema, out _))
            {
                violations.Add(
                    $"{scenario.Metadata.Id}/{step.Id}: `{step.Output.Schema}` şemasını motor tanımıyor. " +
                    $"Tanınan şemalar: {string.Join(", ", schemas.Names.Order(StringComparer.Ordinal))}. " +
                    "Tanımadığı bir şemayla koşmak, ayrıştıramadığı bir çıktıyı sözleşmeye uydu saymak olurdu.");
            }

            foreach (var name in step.Output.Constraints.Where(n => !constraints.TryGet(n, out _)))
            {
                violations.Add(
                    $"{scenario.Metadata.Id}/{step.Id}: `{name}` kısıdını motor zorlayamıyor. " +
                    $"Zorlanabilen kısıtlar: {string.Join(", ", constraints.Names.Order(StringComparer.Ordinal))}. " +
                    "Yükleme anında ad kümesi açık (uzantı noktası); koşum anında kapalı — " +
                    "zorlanmayan bir kısıt, yazılı ama hiçbir şey yapmayan bir kapı olurdu.");
            }
        }

        return violations;
    }

    /// <summary>
    /// Hangi adımda hangi kapı koşacak — <b>"kapı yok" ile "kapı geçti" ayrı
    /// cümleler</b>.
    /// </summary>
    public static IReadOnlyList<string> Describe(ScenarioDefinition scenario, ScenarioOutputSchemas schemas)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(schemas);

        return
        [
            .. scenario.Steps.Select(step =>
            {
                var gate1 = step.Output.IsWaived
                    ? $"kısıt YOK — gerekçeli muafiyet: {step.Output.ConstraintsWaived}"
                    : "kısıt: " + string.Join(", ", step.Output.Constraints);

                var gate2 = schemas.TryGet(step.Output.Schema, out var schema)
                    ? schema.ProseIsReportContent
                        ? "cümle bağlama KOŞUYOR"
                        : "cümle bağlama koşmuyor — bu adımın düzyazısı rapora girmiyor"
                    : "şema TANINMIYOR — bu adım koşamaz";

                return $"{scenario.Metadata.Id}/{step.Id}: {gate1}; {gate2}";
            }),
        ];
    }
}
