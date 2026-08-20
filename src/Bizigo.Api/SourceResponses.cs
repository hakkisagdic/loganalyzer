using System.Text.Json.Serialization;
using Bizigo.Query;

namespace Bizigo.Api;

/// <summary>
/// <c>GET /v1/sources</c> gövdesi.
///
/// <para>
/// Tip <b>T15 ile</b> geliyor, T17 ile değil: arama ekranının kaynak filtresi bu
/// listeden besleniyor. Yazma uçları (<c>POST /v1/sources</c>, <c>/csv</c>)
/// hâlâ tipsiz ve <c>ProducesContractTests</c> izin listesinde — onların
/// tüketicisi envanter ekranı (T17).
/// </para>
///
/// <para>
/// Liste zaten kapsam filtresinden geçmiş geliyor (<c>IScopedQuery</c>): bir
/// ekip başka bir ekibin cihazlarını bu açılır listede de görmüyor.
/// </para>
/// </summary>
public sealed record SourceResponse(
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("owner_group")] string OwnerGroup,
    [property: JsonPropertyName("peer_address")] string? PeerAddress,
    [property: JsonPropertyName("hostname")] string? Hostname,
    [property: JsonPropertyName("vendor")] string Vendor,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("parser_id")] string? ParserId,
    [property: JsonPropertyName("encoding")] string Encoding,
    [property: JsonPropertyName("source_class")] string SourceClass,
    [property: JsonPropertyName("enabled")] bool Enabled,
    /// <summary><c>parser_id</c> bağlı mı — dispatcher kademe 1.</summary>
    [property: JsonPropertyName("is_known_to_dispatcher")] bool IsKnownToDispatcher)
{
    public static SourceResponse From(SourceSummary source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new SourceResponse(
            source.SourceId,
            source.OwnerGroup,
            source.PeerAddress,
            source.Hostname,
            source.Vendor,
            source.Product,
            source.ParserId,
            source.Encoding,
            source.SourceClass,
            source.Enabled,
            source.IsKnownToDispatcher);
    }
}

public sealed record SourceListResponse(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("sources")] IReadOnlyList<SourceResponse> Sources);

/// <summary>
/// Kaynak başına <b>son görülme</b> ve olay sayısı (T17).
///
/// <para>
/// Veri <c>IScopedQuery.GetSourceActivityAsync</c>'ten geliyor — T21'in
/// sessizlik alarmının kullandığı sorgunun aynısı. İkinci bir sorgu yazmak, iki
/// farklı zaman kolonu seçimi ve iki farklı kapsam davranışı demek olurdu; ve
/// ayrıştıkları ancak alarm yanlış tetiklendiğinde fark edilirdi.
/// </para>
/// </summary>
public sealed record SourceActivityResponse(
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("owner_group")] string OwnerGroup,
    /// <summary>Olayın kendi zamanı — cihazın saati olabilir.</summary>
    [property: JsonPropertyName("last_event_at")] DateTimeOffset LastEventAt,
    /// <summary>Bizim aldığımız an. <b>"Susuyor mu" sorusunun cevabı bu.</b></summary>
    [property: JsonPropertyName("last_ingested_at")] DateTimeOffset LastIngestedAt,
    [property: JsonPropertyName("event_count")] long EventCount);

/// <summary>
/// <c>GET /v1/sources/activity</c> gövdesi.
///
/// <para>
/// <b>Yalnızca pencerede verisi olan kaynaklar</b> dönüyor. Hiç veri göndermemiş
/// bir kaynak burada görünmüyor — "yokluk" bilgisi envanter listesiyle
/// birleştirilerek elde ediliyor, çünkü olay tablosu var olmayan bir şeyi
/// listeleyemez. Ekran bu birleştirmeyi yapıp "hiç veri gelmedi" ile "N saattir
/// susuyor"u ayırıyor.
/// </para>
/// </summary>
public sealed record SourceActivityListResponse(
    [property: JsonPropertyName("from")] DateTimeOffset From,
    [property: JsonPropertyName("to")] DateTimeOffset To,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("sources")] IReadOnlyList<SourceActivityResponse> Sources);

/// <summary>
/// CSV yüklemesinin sonucu. Hata hâlinde <c>400</c> ve
/// <see cref="SourceCsvErrorResponse"/> dönüyor — bu tip yalnızca başarıyı
/// anlatıyor.
/// </summary>
public sealed record SourceCsvImportResponse(
    [property: JsonPropertyName("created")] int Created,
    [property: JsonPropertyName("updated")] int Updated,
    [property: JsonPropertyName("total")] int Total);

/// <summary>
/// CSV reddi. <c>details</c> <b>satır satır</b>: kullanıcı hangi satırın neden
/// reddedildiğini görmeden dosyayı düzeltemez ve "geçersiz CSV" tek başına
/// bunu söylemiyor.
/// </summary>
public sealed record SourceCsvErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("details")] IReadOnlyList<string> Details);
