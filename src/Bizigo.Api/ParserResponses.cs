using System.Text.Json.Serialization;
using Bizigo.Authoring;
using Bizigo.ControlPlane;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Schema;
using Bizigo.Parsing.Testing;

namespace Bizigo.Api;

/// <summary>
/// Parser yazarlık yüzeyinin yanıt gövdeleri (T19).
///
/// <para>
/// Anonim nesne değil <b>adlandırılmış tip</b>: uç <c>Produces&lt;T&gt;</c> ile
/// bunları bildiriyor, OpenAPI belgesine şema olarak iniyorlar ve T14'ün
/// ürettiği TypeScript tarafında gövde <c>unknown</c> kalmıyor. Elle yazılan
/// tip API ile sessizce ayrışır; <c>ProducesContractTests</c> izin listesi
/// bunun için var ve bu dosya o listeden dört satır siliyor.
/// </para>
///
/// <para>
/// Alan adları <b>her yerde</b> <c>snake_case</c>: olay uçları ve
/// <c>/auth/me</c> böyle, ClickHouse kolonları böyle. Varsayılan camelCase
/// politikası <c>parse_status</c>'u <c>parseStatus</c> yapıp ekranın hangisini
/// bekleyeceğini her seferinde tahmin ettirirdi.
/// </para>
/// </summary>
public sealed record ParseIssueResponse(
    [property: JsonPropertyName("step")] string Step,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Bir satırın motordan geçmiş hâli.
///
/// <para>
/// <c>timed_out</c> ayrı bir alan ve <b>ayrı kalmak zorunda</b>: sıfırdan
/// farklıysa sonuç "uymadı" değil <b>"ölçülemedi"</b> demek.
/// <c>matchTimeout</c> duvar saatini ölçüyor, yani yüklü bir makinede sağlıklı
/// bir parser da zaman aşımına uğruyor (T08 raporu #10). İkisini karıştırmak
/// sağlıklı bir parser'ı karantinaya sokar — ekranın bu ikisini asla aynı
/// kutuda göstermemesinin sebebi bu.
/// </para>
/// </summary>
public sealed record ParseOutcomeResponse(
    [property: JsonPropertyName("parser_id")] string ParserId,
    [property: JsonPropertyName("parser_version")] string ParserVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("timed_out")] bool TimedOut,
    [property: JsonPropertyName("timestamp")] DateTimeOffset? Timestamp,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("fields")] IReadOnlyDictionary<string, string> Fields,
    [property: JsonPropertyName("core")] IReadOnlyDictionary<string, string> Core,
    [property: JsonPropertyName("ocsf")] IReadOnlyDictionary<string, string> Ocsf,
    [property: JsonPropertyName("otel")] IReadOnlyDictionary<string, string> Otel,
    [property: JsonPropertyName("issues")] IReadOnlyList<ParseIssueResponse> Issues)
{
    public static ParseOutcomeResponse From(ParseResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new ParseOutcomeResponse(
            result.ParserId,
            result.ParserVersion,
            // Sayı değil ad: `2` gövdesine bakan hiç kimse "partial" demiyor ve
            // enum sırası değiştiğinde sessizce başka bir durum gösterirdi.
            result.Status.ToString().ToLowerInvariant(),
            result.TimedOut,
            result.Timestamp,
            result.Tags,
            Flatten(result.Fields),
            Flatten(result.Core),
            Flatten(result.Ocsf),
            Flatten(result.Otel),
            [.. result.Issues.Select(static i => new ParseIssueResponse(i.Step, i.Message))]);
    }

    /// <summary>
    /// Alan sözlükleri <c>object?</c> taşıyor; gövdeye <b>metin</b> olarak
    /// iniyorlar.
    ///
    /// <para>
    /// <c>object</c> bırakmak OpenAPI'de tipsiz bir sözlük demek ve ekran yine
    /// tahmin ederdi. Metne çevirmek bilgi kaybı değil: motorun ürettiği değer
    /// zaten ClickHouse'a <c>attrs</c> içinde metin olarak yazılıyor ve
    /// <c>convert</c> adımının sonucu <c>parse_status</c> ile ayrıca görünüyor.
    /// Değeri olmayan alan <b>hiç yazılmıyor</b> — boş string yazmak
    /// "atanmamış" ile "boş atanmış"ı ayırt edilemez kılardı, ki T08 raporu #6
    /// tam olarak bu ayrımı geri kazandırdı.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> Flatten(
        IReadOnlyDictionary<string, object?> source)
    {
        var flattened = new Dictionary<string, string>(source.Count, StringComparer.Ordinal);

        foreach (var (key, value) in source)
        {
            if (value is null)
            {
                continue;
            }

            flattened[key] = Describe(value);
        }

        return flattened;
    }

    private static string Describe(object value) => value switch
    {
        string s => s,
        IEnumerable<object?> items => string.Join(", ", items.Select(static i => i?.ToString() ?? string.Empty)),
        IFormattable formattable =>
            formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}

/// <summary>Dispatcher kademesi ve o kademenin ürettiği sonuç.</summary>
/// <param name="Tier">
/// <c>inventory_bound</c> · <c>candidate</c> · <c>unmatched</c>.
/// </param>
/// <param name="Reason">
/// Kademenin <b>ne anlama geldiği</b>. Ekranın kendi cümlesini kurması, aynı
/// yorumun iki yerde tutulması demekti; kademe adı değişirse metin de burada
/// değişiyor.
/// </param>
/// <param name="Attempts">Kaç parser denendi. Kademe 2'de büyük bir sayı, ön filtrenin daralmadığını söylüyor.</param>
public sealed record ParserDispatchResponse(
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("attempts")] int Attempts,
    [property: JsonPropertyName("result")] ParseOutcomeResponse Result)
{
    public static ParserDispatchResponse From(DispatchResult dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        return new ParserDispatchResponse(
            Name(dispatch.Tier),
            Explain(dispatch.Tier),
            dispatch.Attempts,
            ParseOutcomeResponse.From(dispatch.Result));
    }

    private static string Name(DispatchTier tier) => tier switch
    {
        DispatchTier.InventoryBound => "inventory_bound",
        DispatchTier.Candidate => "candidate",
        _ => "unmatched",
    };

    /// <summary>
    /// Ticket'ın taşıyıcı gözlemi ikinci satırda: envanter bağı yerine literal
    /// filtreye düşen satır, parser doğru olsa bile <b>envanterin eksik</b>
    /// olduğunu söylüyor. Bu bilgi sonucun kendisi kadar değerli ve yalnızca
    /// burada görünüyor.
    /// </summary>
    private static string Explain(DispatchTier tier) => tier switch
    {
        DispatchTier.InventoryBound =>
            "Envanterde `source_id → parser_id` bağlı; satır doğrudan o parser'a gitti.",
        DispatchTier.Candidate =>
            "Envanter bağı yok ya da tutmadı; satır literal ön filtreden geçen adaylarla denendi. "
            + "Üretimde trafiğin buraya düşmesi envanterin eksik olduğunu gösterir.",
        _ =>
            "Hiçbir parser eşleşmedi. Satır reddedilmiyor — ham arşivde duruyor ve keşif kuyruğuna giriyor.",
    };
}

/// <summary>Şema/derleme hatası — <b>satır ve sütunla</b>.</summary>
public sealed record ParserSchemaErrorResponse(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("column")] int Column,
    [property: JsonPropertyName("message")] string Message)
{
    public static ParserSchemaErrorResponse From(ParserSchemaError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new ParserSchemaErrorResponse(error.Line, error.Column, error.Message);
    }
}

/// <param name="Blocking">
/// Yayını durduruyor mu. <c>GROK003</c> şiddet olarak uyarı ama yayında
/// <b>hata</b>: kataloğun tamamı doğrusal motorda derleniyor ve bu değişmez
/// F1'de dört ayrı daraltmayla kazanıldı. Şiddeti tek başına göstermek
/// kullanıcıya "bu sadece uyarı" dedirtirdi.
/// </param>
public sealed record ParserRedosFindingResponse(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("blocking")] bool Blocking,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("fragment")] string Fragment)
{
    public static ParserRedosFindingResponse From(RedosFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        return new ParserRedosFindingResponse(
            finding.Code,
            finding.Severity.ToString().ToLowerInvariant(),
            finding.Severity == RedosSeverity.Error || finding.Code == "GROK003",
            finding.Message,
            finding.Fragment);
    }
}

/// <summary>Tek bir beklentinin sonucu — beklenen ve gerçek yan yana.</summary>
public sealed record ParserExpectationResponse(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("expected")] string Expected,
    [property: JsonPropertyName("actual")] string Actual,
    [property: JsonPropertyName("passed")] bool Passed)
{
    public static ParserExpectationResponse From(ExpectationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // `ValueFormatter` CLI ile aynı biçimi üretiyor: yok olan değer
        // `<yok>`, metin tırnaklı. "alan yok" ile "alan boş" arasındaki farkı
        // ekranda da görünür kılan tek şey bu (T08 raporu #6).
        return new ParserExpectationResponse(
            result.Key,
            ValueFormatter.Format(result.Expected),
            ValueFormatter.Format(result.Actual),
            result.Passed);
    }
}

/// <param name="Line">Testin YAML içindeki satırı — editör oraya gidebilsin diye.</param>
public sealed record ParserTestCaseResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("expectations")] IReadOnlyList<ParserExpectationResponse> Expectations)
{
    public static ParserTestCaseResponse From(ParserTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new ParserTestCaseResponse(
            result.Name,
            result.Line,
            result.Passed,
            [.. result.Expectations.Select(ParserExpectationResponse.From)]);
    }
}

/// <summary>
/// Yayın kapısının kararı — editörün "neden yayınlanamıyor" cevabı.
///
/// <para>
/// <c>stage</c> hangi kapıda durulduğunu <b>açıkça</b> söylüyor. Hata
/// listesinin içeriğinden geri çıkarmak, mesaj metnini değiştiren ilk katkıda
/// sessizce yanlışlaşırdı.
/// </para>
/// </summary>
/// <param name="Stage"><c>passed</c> · <c>schema</c> · <c>redos</c> · <c>tests</c>.</param>
public sealed record PublishVerdictResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("parser_id")] string ParserId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("passing_tests")] int PassingTests,
    [property: JsonPropertyName("schema_errors")] IReadOnlyList<ParserSchemaErrorResponse> SchemaErrors,
    [property: JsonPropertyName("redos")] IReadOnlyList<ParserRedosFindingResponse> Redos,
    [property: JsonPropertyName("tests")] IReadOnlyList<ParserTestCaseResponse> Tests,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings)
{
    public static PublishVerdictResponse From(PublishVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return new PublishVerdictResponse(
            verdict.Ok,
            verdict.Stage.ToString().ToLowerInvariant(),
            verdict.ParserId,
            verdict.Version,
            verdict.PassingTests,
            [.. verdict.SchemaErrors.Select(ParserSchemaErrorResponse.From)],
            [.. verdict.RedosFindings.Select(ParserRedosFindingResponse.From)],
            [.. verdict.TestResults.Select(ParserTestCaseResponse.From)],
            verdict.Errors,
            verdict.Warnings);
    }
}

/// <summary>
/// <c>POST /v1/parsers/try</c> gövdesi.
///
/// <para>
/// Üç bölüm de <b>bağımsız</b> ve hepsi aynı istekte dönüyor. Taslağı denerken
/// aynı satırın <b>bugünkü katalogda</b> ne yaptığını görmek, bilgiyi ikiye
/// katlıyor: "taslağım bu satırı çözüyor ama canlıda satır hiçbir parser'a
/// düşmüyor" ile "zaten başka bir parser çözüyor" bambaşka iki durum ve ikisi
/// de yalnızca yan yana bakınca görülüyor.
/// </para>
/// </summary>
/// <param name="Mode"><c>draft</c> · <c>forced</c> · <c>dispatch</c>.</param>
/// <param name="Result">
/// Denenen parser'ın sonucu: taslak modunda taslağın, zorlanmış modda seçilen
/// katalog parser'ının. Taslak derlenemediyse <see langword="null"/>.
/// </param>
/// <param name="Draft">Taslak YAML verildiyse kapı kararı; verilmediyse <see langword="null"/>.</param>
/// <param name="Dispatch">Satır verildiyse dispatcher'ın kararı; zorlanmış modda <see langword="null"/>.</param>
public sealed record ParserTryResponse(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("result")] ParseOutcomeResponse? Result,
    [property: JsonPropertyName("draft")] PublishVerdictResponse? Draft,
    [property: JsonPropertyName("dispatch")] ParserDispatchResponse? Dispatch);

/// <summary>
/// Taslak kaydının kullanıcıya dönen hâli.
///
/// <para>
/// <c>gate</c> her kayıt/gönderim yanıtında dolu: T18'in kapıları taslak
/// kaydedilirken de koşuyor ve kullanıcının "şu an yayınlanabilir miyim"
/// sorusunun cevabı ayrı bir istek gerektirmemeli.
/// </para>
/// </summary>
public sealed record ParserAuthoringResponse(
    [property: JsonPropertyName("id")] Guid? Id,
    [property: JsonPropertyName("parser_id")] string ParserId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("verdict")] PublishVerdictResponse? Verdict)
{
    public static ParserAuthoringResponse From(AuthoringResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new ParserAuthoringResponse(
            result.Draft?.Id,
            result.Draft?.ParserId ?? string.Empty,
            result.Draft?.Version ?? string.Empty,
            // Durum adı, sayı değil: `ParserState` sırası değişirse gövde
            // sessizce başka bir durum gösterirdi. Yazım küçük harf ve
            // `GET /v1/parsers/drafts` ile birebir aynı (`draft`/`inreview`/…) —
            // aynı yüzeyde iki farklı yazım, ekranın hangisini bekleyeceğini
            // tahmin etmesi demek olurdu.
            (result.Draft?.State ?? ParserState.Draft).ToString().ToLowerInvariant(),
            result.Error,
            result.Verdict is null ? null : PublishVerdictResponse.From(result.Verdict));
    }
}
