using System.Net;
using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.Storage.Raw;

namespace Bizigo.Api;

/// <summary>
/// <c>/v1/events/*</c> yanıt gövdeleri.
///
/// <para>
/// Anonim nesne değil <b>adlandırılmış tip</b>: uç <c>Produces&lt;T&gt;</c> ile
/// bunları bildiriyor, OpenAPI belgesine şema olarak iniyorlar ve T14'ün
/// ürettiği TypeScript tarafında gövde <c>unknown</c> kalmıyor. Bir yanıt tipi
/// ancak <b>tüketicisi</b> varken yazılabilir; bunların tüketicisi T15/T16.
/// </para>
///
/// <para>
/// Alan adları <b>her yerde</b> <c>snake_case</c> ve bu bilinçli:
/// <c>Results.Ok(logEvent)</c> varsayılan camelCase politikasıyla
/// <c>ownerGroup</c> üretiyordu, oysa API'nin geri kalanı (<c>/auth/me</c>, ham
/// iniş, sağlık uçları) ve ClickHouse kolonları <c>owner_group</c>. İki
/// adlandırmayı yan yana taşımak, ekranın hangisini bekleyeceğini her seferinde
/// tahmin etmesi demekti. Aynısı <b>istek</b> tarafı için de geçerli:
/// <see cref="EventSearchRequest"/> imleci <c>after_timestamp</c> adıyla
/// döndürüyor, dolayısıyla aynı adla kabul etmek zorunda.
/// </para>
/// </summary>
public sealed record EventResponse(
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("ts")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("time_source")] string TimeSource,
    [property: JsonPropertyName("ingested_at")] DateTimeOffset IngestedAt,
    [property: JsonPropertyName("owner_group")] string OwnerGroup,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("vendor")] string Vendor,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("parser_id")] string ParserId,
    [property: JsonPropertyName("parser_version")] string ParserVersion,
    [property: JsonPropertyName("parse_status")] string ParseStatus,
    [property: JsonPropertyName("parse_generation")] uint ParseGeneration,
    [property: JsonPropertyName("encoding_detected")] string EncodingDetected,
    [property: JsonPropertyName("template_id")] string TemplateId,
    [property: JsonPropertyName("severity_num")] byte SeverityNum,
    [property: JsonPropertyName("ocsf_class_uid")] uint OcsfClassUid,
    [property: JsonPropertyName("ocsf_activity_id")] ushort OcsfActivityId,
    [property: JsonPropertyName("src_ip")] string SrcIp,
    [property: JsonPropertyName("dst_ip")] string DstIp,
    [property: JsonPropertyName("src_port")] ushort SrcPort,
    [property: JsonPropertyName("dst_port")] ushort DstPort,
    [property: JsonPropertyName("proto")] string Proto,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("user_name")] string UserName,
    [property: JsonPropertyName("attrs")] IReadOnlyDictionary<string, string> Attrs,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("raw_ref")] string RawRef)
{
    public static EventResponse From(LogEvent source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new EventResponse(
            source.EventId,
            source.Timestamp,
            source.TimeSource,
            source.IngestedAt,
            source.OwnerGroup,
            source.SourceId,
            source.Host,
            source.Vendor,
            source.Product,
            source.ParserId,
            source.ParserVersion,
            // Sayı değil ad: `2` gövdesine bakan hiç kimse "partial" demiyor ve
            // enum sırası değiştiğinde sessizce başka bir durum gösterirdi.
            source.ParseStatus.ToString().ToLowerInvariant(),
            source.ParseGeneration,
            source.EncodingDetected,
            source.TemplateId,
            source.SeverityNum,
            source.OcsfClassUid,
            source.OcsfActivityId,
            Format(source.SrcIp),
            Format(source.DstIp),
            source.SrcPort,
            source.DstPort,
            source.Proto,
            source.Action,
            source.Outcome,
            source.UserName,
            source.Attrs,
            source.Body,
            source.RawRef);
    }

    /// <summary>
    /// IPv4 adresleri kolonda IPv6-eşlemeli duruyor. <c>::ffff:10.1.2.3</c>
    /// göstermek doğru ama okunmuyor; ayrıca kullanıcının kopyalayıp filtreye
    /// yapıştırdığı değerin çalışması gerekiyor.
    /// </summary>
    private static string Format(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
}

/// <summary>
/// Keyset imleci. Alan adları istekteki karşılıklarıyla <b>birebir aynı</b>:
/// ekran bir sonraki sayfayı istemek için bu nesneyi olduğu gibi geri gönderiyor.
/// </summary>
public sealed record EventCursorResponse(
    [property: JsonPropertyName("after_timestamp")] DateTimeOffset AfterTimestamp,
    [property: JsonPropertyName("after_event_id")] Guid AfterEventId);

public sealed record EventSearchResponse(
    [property: JsonPropertyName("events")] IReadOnlyList<EventResponse> Events,
    [property: JsonPropertyName("next")] EventCursorResponse? Next,
    [property: JsonPropertyName("has_more")] bool HasMore);

/// <summary>Görünümden okunan tek alan (<c>db/clickhouse/0003_ocsf_otel_views.sql</c>).</summary>
public sealed record EventFieldResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value);

/// <summary>
/// Olay detayı: <c>core</c> alanları + OCSF ve OTel görünümleri.
///
/// <para>
/// Üç görünüm <b>tek istekte</b> geliyor. Sekme değiştirmek yeni bir istek
/// gerektirseydi, kullanıcı sekmeye bastığında boş ekran görürdü ve üç ayrı
/// hata durumu ele alınması gerekirdi.
/// </para>
/// </summary>
public sealed record EventDetailResponse(
    [property: JsonPropertyName("event")] EventResponse Event,
    [property: JsonPropertyName("ocsf")] IReadOnlyList<EventFieldResponse> Ocsf,
    [property: JsonPropertyName("otel")] IReadOnlyList<EventFieldResponse> Otel);

/// <summary>
/// Ham iniş gövdesi (T16). <c>raw_b64</c> <b>orijinal baytlar</b>: metne
/// çevirmek, kodlama tespiti yanlışsa düzeltilecek olan şeyi bozardı (K4).
/// </summary>
public sealed record EventRawResponse(
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("object_key")] string ObjectKey,
    [property: JsonPropertyName("objects_scanned")] int ObjectsScanned,
    [property: JsonPropertyName("received_at")] DateTimeOffset ReceivedAt,
    [property: JsonPropertyName("source_key")] string SourceKey,
    [property: JsonPropertyName("transport")] EventRawTransport Transport,
    [property: JsonPropertyName("encoding_declared")] string EncodingDeclared,
    [property: JsonPropertyName("encoding_detected")] string EncodingDetected,
    [property: JsonPropertyName("raw_b64")] string RawBase64)
{
    /// <param name="detectedEncoding">
    /// Boru hattının <b>tespit ettiği</b> kodlama — olay satırından geliyor,
    /// manifestten değil. İkisi ayrı: <c>encoding_declared</c> envanterin
    /// iddiası, bu ise bayta bakılarak bulunan. Ekranda ikisi yan yana
    /// durmadan "windows-1254 doğru mu çözüldü" sorusu cevaplanamıyor.
    /// </param>
    public static EventRawResponse From(RawLookup lookup, string detectedEncoding)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        return new EventRawResponse(
            lookup.Record.EventId,
            lookup.ObjectKey,
            lookup.ObjectsScanned,
            lookup.Record.ReceivedAt,
            lookup.Record.SourceKey,
            new EventRawTransport(lookup.Record.TransportProto, lookup.Record.TransportPeer),
            lookup.Record.EncodingDeclared,
            detectedEncoding,
            Convert.ToBase64String(lookup.Record.Body.Span));
    }
}

public sealed record EventRawTransport(
    [property: JsonPropertyName("proto")] string Proto,
    [property: JsonPropertyName("peer")] string Peer);
