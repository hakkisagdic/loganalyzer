using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bizigo.Cli.Fields;

/// <param name="Field">Kuralın vurduğu ad — OCSF takma adı olması beklenen.</param>
/// <param name="Operator">Sigma operatörü; boş dize eşitlik.</param>
/// <param name="Value">Aranan dizge.</param>
/// <param name="Verdict"><c>explain_misses.py</c>'nin metin ekseni kutusu.</param>
public sealed record RuleLiteral(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("operator")] string Operator,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("verdict")] string Verdict);

/// <param name="Name">Kural dosyası.</param>
/// <param name="Product"><c>logsource.product</c>.</param>
/// <param name="Verdict">Kuralın metin ekseni kutusu.</param>
/// <param name="Literals">Kuralın aradığı dizgeler.</param>
public sealed record RuleEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("literals")] IReadOnlyList<RuleLiteral> Literals);

public enum ReachVerdict
{
    /// <summary>Kural, veri ne olursa olsun eşleşemez. Şema söylüyor, örneklem değil.</summary>
    Unreachable,

    /// <summary>
    /// Vendor düzeyinde uzay açık ama <b>bazı parser'lar kolonu hiç
    /// doldurmuyor</b>. Kural o parser'ın ürettiği satırlara vuruyorsa
    /// eşleşemez; vurmuyorsa sorun yok. Birleşimde kaybolan, burada
    /// görünüyor.
    /// </summary>
    ParserGap,

    /// <summary>Şema açısından engel yok; eşleşip eşleşmediği veriye bakar.</summary>
    Reachable,

    /// <summary>Söylenemez — ve söylememek doğru.</summary>
    Unknown,
}

/// <param name="Rule">Kural dosyası.</param>
/// <param name="Vendor">Çözülen vendor; çözülemediyse boş.</param>
/// <param name="Literal">İncelenen dizge.</param>
/// <param name="Verdict">Sonuç.</param>
/// <param name="Reason">Gerekçe — rapora aynen giriyor.</param>
/// <param name="TextAxisWrong">
/// Metin ekseni "dizge örneklerde YOK" dedi ama değer, kolonun kapalı uzayında
/// <b>var</b>.
///
/// <para>
/// Ölçülmüş vaka: <c>fortigate_user_auth_fail</c> <c>status: 'failure'</c>
/// arıyor, FortiGate satırda <c>status="failed"</c> yazıyor —ama
/// <c>auth_outcome.yaml</c> <c>failed → failure</c> çeviriyor, yani kolonda
/// gerçekten <c>failure</c> duruyor. Metin ekseni tek başına bu kuralı
/// "kuralın kusuru" diye raporlardı; kural doğru, ürün zaten çeviriyor.
/// </para>
/// </param>
public sealed record LiteralReach(
    string Rule,
    string Vendor,
    RuleLiteral Literal,
    ReachVerdict Verdict,
    string Reason,
    bool TextAxisWrong = false);

/// <summary>
/// <b>Kuralın aradığı değer, gittiği kolonun taşıyabildiği değerler arasında mı</b>
/// (T39).
///
/// <para>
/// Kuralları <b>okumuyor</b>: <c>prototypes/t30-sigma/explain_misses.py</c>
/// zaten <c>alan|operatör = değer</c> üçlülerini çıkarıyor ve JSON basıyor. Bu
/// tip o JSON'u tüketiyor. İkinci bir Sigma ayrıştırıcısı yazmak, iki aracın
/// aynı kuralı farklı okuduğu ve farkın hiçbir yerde görünmediği günü
/// hazırlamak olurdu.
/// </para>
///
/// <para>
/// <b>İki eksen birleşiyor.</b> Metin ekseni "dizge örneklerde var mı" diyor;
/// bu tip "olsa bile o kolonda durabilir mi" diyor. İkincisi veriden bağımsız:
/// örneklem düzelse de değişmeyen bir cevap.
/// </para>
/// </summary>
public static class RuleReachability
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<RuleEntry> ReadRules(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var rules = JsonSerializer.Deserialize<List<RuleEntry>>(File.ReadAllText(path), Options);

        return rules is null || rules.Count == 0
            ? throw new InvalidOperationException(
                $"{path} hiç kural taşımıyor. Boş bir listeye '0 erişilemez kural' demek, " +
                "ölçümün yapıldığı izlenimi bırakırdı.")
            : rules;
    }

    /// <param name="rules"><c>explain_misses.py --json</c> çıktısı.</param>
    /// <param name="spaces">Vendor başına değer uzayları.</param>
    /// <param name="fieldMap">
    /// Sigma taxonomy adı → görünüm kolonu; <see cref="SigmaFieldMap"/> ile
    /// pipeline dosyasından okunuyor. Kural <c>srcip</c> yazıyor, görünümde
    /// <c>src_endpoint_ip</c> duruyor — çeviri olmadan her dizge "kolona bağlı
    /// değil" görünürdü.
    /// </param>
    public static IReadOnlyList<LiteralReach> Join(
        IReadOnlyList<RuleEntry> rules,
        IReadOnlyList<VendorValueSpace> spaces,
        IReadOnlyDictionary<string, string> fieldMap)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(spaces);
        ArgumentNullException.ThrowIfNull(fieldMap);

        var results = new List<LiteralReach>();

        foreach (var rule in rules)
        {
            // Vendor, kuralın `logsource.product`'ı ile parser'ın
            // `metadata.product`'ı eşleştirilerek çözülüyor — elle yazılmış bir
            // eşanlamlı tablosu YOK. Dördü de büyük/küçük harf duyarsız
            // tutuyor: fortigate↔FortiGate, asa↔ASA, routeros↔RouterOS,
            // nginx↔nginx.
            var space = spaces.FirstOrDefault(candidate =>
                candidate.Products.Contains(rule.Product, StringComparer.OrdinalIgnoreCase));

            foreach (var literal in rule.Literals)
            {
                results.Add(Examine(rule, literal, space, fieldMap));
            }
        }

        return results;
    }

    private static LiteralReach Examine(
        RuleEntry rule,
        RuleLiteral literal,
        VendorValueSpace? space,
        IReadOnlyDictionary<string, string> fieldMap)
    {
        if (space is null)
        {
            return new LiteralReach(
                rule.Name,
                string.Empty,
                literal,
                ReachVerdict.Unknown,
                $"`{rule.Product}` hiçbir parser'ın `metadata.product`'ıyla eşleşmiyor — " +
                "bu ürün için katalogda parser yok.");
        }

        // Kural Sigma taxonomy'siyle yazılmış; kolon adı pipeline'ın çevirdiği.
        var alias = fieldMap.GetValueOrDefault(literal.Field, literal.Field);

        if (!space.Columns.TryGetValue(alias, out var column))
        {
            return new LiteralReach(
                rule.Name,
                space.Vendor,
                literal,
                ReachVerdict.Unknown,
                $"`{literal.Field}` → `{alias}`: bu vendor'ın parser'larının doldurduğu bir kolona bağlı değil " +
                "(ör. `message` gibi ham metne çevrilen bir ad, ya da yalnızca `unmapped`'te duran bir alan).");
        }

        // Parser düzeyindeki boşluk vendor birleşiminde kayboluyor: kolonu
        // dolduran bir parser varsa uzay "açık" görünür ama kural başka bir
        // parser'ın satırlarına vuruyor olabilir.
        var gap = column.MissingIn.Count == 0
            ? string.Empty
            : $" (şu parser'lar doldurmuyor: {string.Join(", ", column.MissingIn)})";

        if (column.Kind == ValueSpaceKind.Open)
        {
            return column.MissingIn.Count > 0
                ? new LiteralReach(
                    rule.Name,
                    space.Vendor,
                    literal,
                    ReachVerdict.ParserGap,
                    $"`{alias}` bu vendor'da açık ama şu parser'lar onu HİÇ doldurmuyor: " +
                    $"{string.Join(", ", column.MissingIn)}. Kural o parser'ın satırlarına " +
                    "vuruyorsa eşleşemez.")
                : new LiteralReach(
                    rule.Name,
                    space.Vendor,
                    literal,
                    ReachVerdict.Unknown,
                    $"`{alias}` cihazın yazdığını taşıyor (değer uzayı açık); şema bir şey söylemiyor.");
        }

        if (column.Kind == ValueSpaceKind.Absent)
        {
            return new LiteralReach(
                rule.Name,
                space.Vendor,
                literal,
                ReachVerdict.Unreachable,
                $"`{alias}` bu vendor'ın hiçbir parser'ı tarafından doldurulmuyor.");
        }

        if (ColumnValueSpaces.CanSatisfy(column, literal.Operator, literal.Value))
        {
            // Metin ekseni "yok" dediyse ama kapalı uzayda VARSA, eşleme
            // tablosu cihazın sözcüğünü çeviriyor demektir.
            var normalized = string.Equals(literal.Verdict, "absent", StringComparison.Ordinal);

            return new LiteralReach(
                rule.Name,
                space.Vendor,
                literal,
                ReachVerdict.Reachable,
                normalized
                    ? $"`{alias}` kapalı uzayında `{literal.Value}` VAR ({string.Join(" · ", column.Values)}) — " +
                      "ham satırda geçmemesi önemli değil, eşleme tablosu cihazın sözcüğünü çeviriyor."
                    : gap,
                normalized);
        }

        return new LiteralReach(
            rule.Name,
            space.Vendor,
            literal,
            ReachVerdict.Unreachable,
            $"`{alias}` kapalı bir değer uzayı taşıyor ({string.Join(" · ", column.Values)}) " +
            $"ve `{literal.Operator}` ile `{literal.Value}` oradan üretilemiyor. " +
            $"Örneklem düzelse de bu kural eşleşmez.{gap}");
    }
}
