using System.Text.Json.Serialization;

namespace Bizigo.Api;

/// <summary>
/// Katalog yönetim ekranının yanıt gövdeleri (T20).
///
/// <para>
/// Adlandırılmış tipler, çünkü gövde OpenAPI belgesine şema olarak inmezse
/// üretilen TypeScript'te <c>unknown</c> kalıyor ve ekran tipi elle yazmak
/// zorunda kalıyor — <c>ProducesContractTests</c>'in var olma sebebi bu.
/// </para>
///
/// <para>
/// <c>JsonPropertyName</c> zorunlu: varsayılan camelCase <c>parser_id</c>'yi
/// <c>parserId</c> yapar ve F1'den beri yerleşik snake_case sözleşmesini kırar.
/// </para>
/// </summary>
public sealed record ParserSummaryResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("vendor")] string Vendor,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("specificity")] int Specificity,

    /// <summary>
    /// Bu parser'daki doğrusal olmayan grok sayısı — yani <c>GROK003</c>.
    /// Listede taşınıyor, çünkü ekranın uyarıyı parser başına gösterebilmesi
    /// için tek tek detay çekmesi gerekseydi katalog açılışı N istek atardı.
    /// </summary>
    [property: JsonPropertyName("backtracking_groks")] int BacktrackingGroks);

/// <param name="BacktrackingGroks">
/// Katalog genelinde <c>GROK003</c> sayacı. F1'de 21'den 0'a indirildi ve o
/// kazanım sessizce kaybedilmesin diye burada duruyor; sıfırdan farklıysa ekran
/// <b>uyarı</b> üretiyor, sayıyı sessizce göstermiyor.
/// </param>
public sealed record ParserListResponse(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("parsers")] ParserSummaryResponse[] Parsers,
    [property: JsonPropertyName("backtracking_groks")] int BacktrackingGroks);

/// <param name="SourceLabels">
/// Dispatcher kademe 3'ün etiket eşlemesi: <c>etiket → değer</c>. Liste değil
/// sözlük, çünkü ekranda "hangi etiket hangi değere bakıyor" sorusunun cevabı
/// çiftin kendisi.
/// </param>
public sealed record ParserMatchResponse(
    [property: JsonPropertyName("transport")] string[] Transport,
    [property: JsonPropertyName("contains")] string[] Contains,
    [property: JsonPropertyName("source_labels")] IReadOnlyDictionary<string, string> SourceLabels);

/// <param name="FallbackReasons">
/// Hangi ifadenin neden doğrusal motora sığmadığı. Sayı tek başına "bir şey
/// bozuldu" der; sebep, hangi pattern'in daraltılacağını söyler.
/// </param>
public sealed record ParserGrokResponse(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("backtracking")] int Backtracking,
    [property: JsonPropertyName("fallback_reasons")] string[] FallbackReasons);

public sealed record ParserDetailResponse(
    [property: JsonPropertyName("summary")] ParserSummaryResponse Summary,
    [property: JsonPropertyName("match")] ParserMatchResponse Match,
    [property: JsonPropertyName("steps")] int Steps,
    [property: JsonPropertyName("groks")] ParserGrokResponse Groks);

/// <param name="State"><c>draft</c> | <c>inreview</c> | <c>published</c> | <c>retired</c>.</param>
/// <param name="Quarantined">Sürekli zaman aşımı veren parser karantinada.</param>
public sealed record ParserDraftResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("parser_id")] string ParserId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("vendor")] string Vendor,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("passing_tests")] int PassingTests,
    [property: JsonPropertyName("quarantined")] bool Quarantined,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt);

public sealed record ParserDraftListResponse(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("drafts")] ParserDraftResponse[] Drafts);

/// <param name="Yaml">Taslağın gövdesi — editör bir taslağı bununla yeniden açıyor (T19).</param>
/// <param name="UpdatedAt">Gövdenin son değiştiği an.</param>
/// <param name="PreviousYaml">
/// Aynı <c>parser_id</c> için hâlen yayında olan sürümün gövdesi; yoksa
/// <see langword="null"/>. <b>İkisi birlikte dönüyor</b>, çünkü fark görünümü
/// ikisini de istiyor ve ayrı iki istekle çekmek, ekranın iki farklı ana ait
/// sürümü karşılaştırabilmesi demekti — inceleme sırasında yayın değişirse
/// kullanıcı olmayan bir farka bakar.
/// </param>
public sealed record ParserDraftDetailResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("parser_id")] string ParserId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("yaml")] string Yaml,
    [property: JsonPropertyName("verdict")] PublishVerdictResponse Verdict,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("previous_version")] string? PreviousVersion,
    [property: JsonPropertyName("previous_yaml")] string? PreviousYaml);

/// <param name="Errors">Yayını engelleyen sebepler.</param>
/// <param name="Warnings">Engellemiyor ama inceleyenin görmesi gereken bulgular.</param>
public sealed record PublishVerdictResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("passing_tests")] int PassingTests,
    [property: JsonPropertyName("errors")] string[] Errors,
    [property: JsonPropertyName("warnings")] string[] Warnings);

public sealed record ParserAuthoringResponse(
    [property: JsonPropertyName("id")] Guid? Id,
    [property: JsonPropertyName("parser_id")] string? ParserId,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("verdict")] PublishVerdictResponse? Verdict);

/// <param name="Shadowed">
/// Depodaki dosyanın üstüne binen yayınlanmış parser'ların kimlikleri. Sayı
/// değil <b>liste</b>: katalog ekranında "bu parser dosyadan değil veritabanından
/// geliyor" bilgisi, sayının kendisinden daha çok işe yarıyor — hangi dosyanın
/// artık okunmadığını söylüyor.
/// </param>
public sealed record CatalogReloadResponse(
    [property: JsonPropertyName("loaded")] int Loaded,
    [property: JsonPropertyName("from_repository")] int FromRepository,
    [property: JsonPropertyName("from_database")] int FromDatabase,
    [property: JsonPropertyName("shadowed")] string[] Shadowed,
    [property: JsonPropertyName("errors")] string[] Errors);

public sealed record ParserPublishResponse(
    [property: JsonPropertyName("draft")] ParserAuthoringResponse Draft,
    [property: JsonPropertyName("catalog")] CatalogReloadResponse Catalog);

public sealed record ParserCoverageEntryResponse(
    [property: JsonPropertyName("parser_id")] string ParserId,
    [property: JsonPropertyName("wins")] int Wins);

/// <summary>
/// Altın örnek kapsamı (T20 kabul kriteri).
///
/// <para>
/// F1'de bu oran <c>86/1/0</c>'dı ve kataloğun sağlığının tek sayısal
/// göstergesi. <see cref="Stale"/> ölçümün katalogdan eski olduğunu söylüyor —
/// bayat bir oranı taze gibi göstermek, göstergenin kendisini işe yaramaz kılardı.
/// </para>
/// </summary>
public sealed record CatalogCoverageResponse(
    [property: JsonPropertyName("ok")] int Ok,
    [property: JsonPropertyName("partial")] int Partial,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("measured_at")] DateTimeOffset MeasuredAt,
    [property: JsonPropertyName("stale")] bool Stale,
    [property: JsonPropertyName("by_parser")] ParserCoverageEntryResponse[] ByParser);
