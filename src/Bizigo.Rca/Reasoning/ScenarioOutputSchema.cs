using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Bizigo.Rca.Reasoning;

/// <summary>Bir çıktı kaydının serbest metin alanı — <b>cümle bağlamanın</b> hedefi.</summary>
/// <param name="Field">Alan adı; hangi alandan geldiği raporda görünüyor.</param>
/// <param name="Index">Kaçıncı kayıt — atıf sırası korunuyor.</param>
/// <param name="Text">Metnin kendisi.</param>
public sealed record ScenarioProse(string Field, int Index, string Text);

/// <summary>
/// Modelin ürettiği <b>ayrıştırılmış</b> belge.
///
/// <para>
/// İki kapının iki ayrı hedefi burada ayrı duruyor ve karıştırılmamalı:
/// <see cref="CitedIds"/> belgenin <b>yapısı</b> (kısıt doğrulamanın baktığı
/// yer), <see cref="Prose"/> belgenin <b>düzyazısı</b> (cümle bağlamanın
/// baktığı yer). İkisi tek kapıda birleştirilseydi bir kötü cümle yüzünden
/// bütün adım atılırdı — yani iki iyi hipotez de kaybolurdu.
/// </para>
/// </summary>
/// <param name="SchemaName">Hangi şemayla ayrıştırıldı.</param>
/// <param name="CitedIds">Yapısal kanıt atıfları — sıralı ve tekilleştirilmiş.</param>
/// <param name="ContradictingIds">
/// Çelişen kanıt atıfları. <see cref="CitedIds"/>'e de giriyorlar, ama ayrıca
/// burada duruyorlar çünkü RCA riski #5 (çelişen kanıt tiyatrosu) onları ayrı
/// ölçmek zorunda.
/// </param>
/// <param name="Prose">Serbest metin alanları.</param>
/// <param name="ProseIsReportContent">
/// Bu adımın düzyazısı rapora <b>giriyor mu</b>. Girmiyorsa cümle bağlama
/// koşmuyor — ve koşmadığı <see cref="ScenarioStepOutcome.SentenceGate"/>'te
/// <b>yazılı</b>: sessizce atlayan bir kapı, kapının kendisinden tehlikeli.
/// </param>
/// <param name="ItemCount">Kaç kayıt üretildi — <c>max_items</c> bunun üstünde.</param>
public sealed record ScenarioOutputDocument(
    string SchemaName,
    IReadOnlyList<string> CitedIds,
    IReadOnlyList<string> ContradictingIds,
    IReadOnlyList<ScenarioProse> Prose,
    bool ProseIsReportContent,
    int ItemCount);

/// <summary>Ayrıştırma sonucu; <b>hata sebebiyle</b> geliyor çünkü yeniden deneme onu adlandırıyor.</summary>
public sealed record ScenarioSchemaParse(ScenarioOutputDocument? Document, string? Error)
{
    [MemberNotNullWhen(true, nameof(Document))]
    public bool Ok => Document is not null && Error is null;
}

/// <summary>
/// Bir adımın çıktı sözleşmesi — <c>output.schema</c>'nın <b>karşılığı</b>.
///
/// <para>
/// <b>T43'ün üçüncü açık ucu buydu:</b> format şema adlarını yalnızca isimle
/// anıyordu, tanımlarının nerede duracağı yazılmamıştı. Karar: tanım burada,
/// motorda — çünkü şemayı <b>ayrıştıran</b> ve iki kapıya <i>neye bakacağını</i>
/// söyleyen taraf motor. Adı bir dizgi olarak bırakmak, motorun ayrıştıramadığı
/// bir çıktıyı "sözleşmeye uydu" sayması demekti.
/// </para>
///
/// <para>
/// Küme <b>koşum anında kapalı</b>, yükleme anında açık — F4 plugin formatı
/// §6.1'in bölmesinin aynısı. Yükleyici bir şema adını tanımak zorunda değil
/// (o bir uzantı noktası); motor <b>zorunda</b>, ve tanımadığı bir adla
/// koşmayı reddediyor. Üçüncü hâl — <i>"tanımadım ama geçtim"</i> — bu deponun
/// <c>Produces&lt;T&gt;</c> ile ödediği hâlin kendisi olurdu.
/// </para>
/// </summary>
public interface IScenarioOutputSchema
{
    /// <summary><c>output.schema</c>'da yazılan ad.</summary>
    string Name { get; }

    /// <summary>Bu şemanın düzyazısı rapora giriyor mu.</summary>
    bool ProseIsReportContent { get; }

    /// <summary>Prompt'a giren şekil tarifi — model neyi üretmesi gerektiğini buradan öğreniyor.</summary>
    string Shape { get; }

    ScenarioSchemaParse Parse(string modelText);
}

/// <summary>
/// JSON kök nesnesinin altında <b>tek bir liste</b> taşıyan şema ailesi.
///
/// <para>
/// Üç yerleşik şemanın üçü de bu şekilde ve tek bir uygulama yeterli oldu —
/// üç ayrı sınıf yazmak, üç kez aynı ayrıştırma hatasını yapabilmek demekti.
/// Aile dar tutuldu: kök bir nesne, içinde bir dizi, dizinin her öğesi düz bir
/// nesne. Daha derin bir şekil gerektiğinde yeni bir uygulama gelir; bugün
/// tüketicisi yok ve <b>tüketicisi olmayan bir tip tahmindir</b>.
/// </para>
/// </summary>
public sealed class JsonListSchema : IScenarioOutputSchema
{
    private readonly string _root;
    private readonly IReadOnlyList<string> _proseFields;
    private readonly IReadOnlyList<string> _idFields;
    private readonly string? _contradictingField;

    public JsonListSchema(
        string name,
        string rootProperty,
        IReadOnlyList<string> proseFields,
        IReadOnlyList<string> idFields,
        bool proseIsReportContent,
        string? contradictingField = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootProperty);
        ArgumentNullException.ThrowIfNull(proseFields);
        ArgumentNullException.ThrowIfNull(idFields);

        Name = name;
        _root = rootProperty;
        _proseFields = proseFields;
        _idFields = idFields;
        _contradictingField = contradictingField;
        ProseIsReportContent = proseIsReportContent;
    }

    public string Name { get; }

    public bool ProseIsReportContent { get; }

    public string Shape
    {
        get
        {
            var fields = _proseFields.Select(f => $"\"{f}\": \"<metin>\"")
                .Concat(_idFields.Select(f => $"\"{f}\": [\"<kanıt kimliği>\"]"));

            return $"{{\"{_root}\": [{{{string.Join(", ", fields)}}}]}}";
        }
    }

    public ScenarioSchemaParse Parse(string modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText))
        {
            return new ScenarioSchemaParse(null, $"`{Name}`: model boş çıktı üretti.");
        }

        JsonDocument parsed;

        try
        {
            parsed = JsonDocument.Parse(ExtractJson(modelText));
        }
        catch (JsonException ex)
        {
            return new ScenarioSchemaParse(null, $"`{Name}`: çıktı JSON olarak ayrışmadı — {ex.Message}");
        }

        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ScenarioSchemaParse(null, $"`{Name}`: kök bir JSON nesnesi olmalı.");
            }

            if (!parsed.RootElement.TryGetProperty(_root, out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return new ScenarioSchemaParse(null, $"`{Name}`: kökte `{_root}` dizisi yok.");
            }

            var prose = new List<ScenarioProse>();
            var cited = new List<string>();
            var contradicting = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;

            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    return new ScenarioSchemaParse(null, $"`{Name}`: `{_root}[{index}]` bir nesne olmalı.");
                }

                foreach (var field in _proseFields)
                {
                    if (!entry.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
                    {
                        return new ScenarioSchemaParse(
                            null, $"`{Name}`: `{_root}[{index}].{field}` bir metin olmalı.");
                    }

                    prose.Add(new ScenarioProse(field, index, value.GetString() ?? string.Empty));
                }

                foreach (var field in _idFields)
                {
                    if (!entry.TryGetProperty(field, out var value))
                    {
                        // Alanın hiç yazılmaması meşru: çelişen kanıt her zaman
                        // yok. Yazılıp yanlış tipte olması meşru DEĞİL.
                        continue;
                    }

                    if (value.ValueKind != JsonValueKind.Array)
                    {
                        return new ScenarioSchemaParse(
                            null, $"`{Name}`: `{_root}[{index}].{field}` bir dizi olmalı.");
                    }

                    foreach (var id in value.EnumerateArray())
                    {
                        if (id.ValueKind != JsonValueKind.String)
                        {
                            return new ScenarioSchemaParse(
                                null, $"`{Name}`: `{_root}[{index}].{field}` yalnızca metin kimlik taşıyabilir.");
                        }

                        var text = id.GetString() ?? string.Empty;

                        if (seen.Add(text))
                        {
                            cited.Add(text);
                        }

                        if (string.Equals(field, _contradictingField, StringComparison.Ordinal)
                            && !contradicting.Contains(text, StringComparer.Ordinal))
                        {
                            contradicting.Add(text);
                        }
                    }
                }

                index++;
            }

            return new ScenarioSchemaParse(
                new ScenarioOutputDocument(Name, cited, contradicting, prose, ProseIsReportContent, index),
                null);
        }
    }

    /// <summary>
    /// Modeller JSON'u sık sık ``` çitiyle sarıyor. Çiti sökmek bir hoşgörü
    /// değil bir <b>ölçüm kararı</b>: sökülmezse her koşum bir yeniden denemeyi
    /// çite harcar ve o belirteçler kotadan düşer — yani hoşgörüsüzlüğün bedeli
    /// ölçülen bir sayıda görünür, kalitede değil.
    /// </summary>
    private static string ExtractJson(string text)
    {
        var trimmed = text.Trim();

        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n', StringComparison.Ordinal);

        if (firstNewline < 0)
        {
            return trimmed;
        }

        var body = trimmed[(firstNewline + 1)..];
        var fence = body.LastIndexOf("```", StringComparison.Ordinal);

        return (fence < 0 ? body : body[..fence]).Trim();
    }
}

/// <summary>
/// Motorun tanıdığı şema adları — <b>koşum anında kapalı</b> küme.
/// </summary>
public sealed class ScenarioOutputSchemas
{
    private readonly Dictionary<string, IScenarioOutputSchema> _schemas;

    public ScenarioOutputSchemas(IEnumerable<IScenarioOutputSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        _schemas = schemas.ToDictionary(s => s.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// RCA senaryosunun üç adımının üç şeması.
    ///
    /// <para>
    /// <c>hypothesis_list</c> tek başına <b>rapor içeriği değil</b>: çıktısı
    /// hipotez metni ve hiçbir kanıt kimliği taşımıyor, çünkü girdisi
    /// <c>evidence.summary</c> — özetlerin kimliği yok. Cümle bağlama orada
    /// koşsaydı <b>her cümle atılırdı</b> ve rapor boş çıkardı; bağlama bir
    /// sonraki adımda, kimliklerin göründüğü yerde koşuyor.
    /// </para>
    /// </summary>
    public static ScenarioOutputSchemas BuiltIn { get; } = new(
    [
        new JsonListSchema(
            "hypothesis_list",
            rootProperty: "hypotheses",
            proseFields: ["text"],
            idFields: [],
            proseIsReportContent: false),

        new JsonListSchema(
            "evidence_binding",
            rootProperty: "findings",
            proseFields: ["hypothesis"],
            idFields: ["evidence_ids", "contradicting_evidence_ids"],
            proseIsReportContent: true,
            contradictingField: "contradicting_evidence_ids"),

        new JsonListSchema(
            "action_list",
            rootProperty: "actions",
            proseFields: ["text"],
            idFields: ["evidence_ids"],
            proseIsReportContent: true),
    ]);

    public IReadOnlyCollection<string> Names => _schemas.Keys;

    public bool TryGet(string name, [NotNullWhen(true)] out IScenarioOutputSchema? schema) =>
        _schemas.TryGetValue(name, out schema);
}
