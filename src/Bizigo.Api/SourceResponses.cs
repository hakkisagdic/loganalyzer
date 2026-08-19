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
