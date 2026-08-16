using System.Globalization;
using System.Text;
using Bizigo.Contracts;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;

namespace Bizigo.Ingest.Otlp;

public sealed class OtlpDecodeException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// OTLP <c>ExportLogsServiceRequest</c> → <see cref="RawRecord"/> listesi
/// (F1 §2.1).
///
/// <para>
/// .NET tarafında hazır bir OTLP <i>alıcı</i> paketi yok; mesaj sınıfları
/// <c>opentelemetry-proto</c>'dan üretiliyor ve çözüm bu tek dosyada duruyor.
/// gRPC (:4317) bilinçli olarak yok — HTTP tek başına yeterli, kaçış kapısı açık.
/// </para>
/// </summary>
public sealed class OtlpLogsDecoder
{
    public const string ProtobufContentType = "application/x-protobuf";
    public const string JsonContentType = "application/json";

    /// <summary>Kaynak anahtarı adayları, öncelik sırasıyla.</summary>
    private static readonly string[] SourceKeyCandidates =
    [
        "bizigo.source_key",   // envanterle birebir eşleşsin diye elle atanabilir
        "client.address",      // güncel semconv
        "net.peer.ip",         // stanza add_attributes
        "host.name",
        "host.id",
        "server.address",
    ];

    // OTLP/JSON: bilinmeyen alan reddedilmez. Collector sürümü bizden önde
    // olabilir; yeni bir alan yüzünden ingest durursa bu, veri kaybıdır.
    private static readonly JsonParser JsonReader =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    public IReadOnlyList<RawRecord> Decode(
        ReadOnlyMemory<byte> payload,
        string? contentType,
        DateTimeOffset receivedAt)
    {
        var request = Parse(payload, contentType);
        var records = new List<RawRecord>();

        foreach (var resourceLogs in request.ResourceLogs)
        {
            var resourceAttributes = Flatten(resourceLogs.Resource?.Attributes, "resource.");

            foreach (var scopeLogs in resourceLogs.ScopeLogs)
            {
                foreach (var logRecord in scopeLogs.LogRecords)
                {
                    records.Add(ToRawRecord(logRecord, resourceAttributes, receivedAt));
                }
            }
        }

        return records;
    }

    private static ExportLogsServiceRequest Parse(ReadOnlyMemory<byte> payload, string? contentType)
    {
        var type = (contentType ?? string.Empty).Split(';')[0].Trim();

        try
        {
            if (string.Equals(type, ProtobufContentType, StringComparison.OrdinalIgnoreCase))
            {
                return ExportLogsServiceRequest.Parser.ParseFrom(payload.Span);
            }

            if (string.Equals(type, JsonContentType, StringComparison.OrdinalIgnoreCase))
            {
                var json = Encoding.UTF8.GetString(payload.Span);
                return JsonReader.Parse<ExportLogsServiceRequest>(json);
            }
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new OtlpDecodeException("OTLP gövdesi çözülemedi.", ex);
        }
        catch (InvalidJsonException ex)
        {
            throw new OtlpDecodeException("OTLP/JSON gövdesi çözülemedi.", ex);
        }

        throw new OtlpDecodeException($"Desteklenmeyen Content-Type: '{type}'.");
    }

    private static RawRecord ToRawRecord(
        LogRecord logRecord,
        IReadOnlyDictionary<string, string> resourceAttributes,
        DateTimeOffset receivedAt)
    {
        var attributes = new Dictionary<string, string>(resourceAttributes, StringComparer.Ordinal);
        foreach (var (key, value) in Flatten(logRecord.Attributes, prefix: string.Empty))
        {
            attributes[key] = value;
        }

        return new RawRecord
        {
            EventId = Guid.CreateVersion7(receivedAt),
            ReceivedAt = receivedAt,
            ObservedAt = ToTimestamp(logRecord.TimeUnixNano) ?? ToTimestamp(logRecord.ObservedTimeUnixNano),
            SourceKey = ResolveSourceKey(attributes),
            TransportProto = attributes.GetValueOrDefault("bizigo.transport", "otlp-http"),
            TransportPeer = ResolvePeer(attributes),
            EncodingDeclared = attributes.GetValueOrDefault("bizigo.encoding", string.Empty),
            SeverityNumber = (byte)Math.Clamp((int)logRecord.SeverityNumber, 0, byte.MaxValue),
            Body = ExtractBody(logRecord.Body, attributes.GetValueOrDefault("bizigo.wire_encoding")),
            Attributes = attributes,
        };
    }

    /// <summary>
    /// Gövde string olarak geldiyse baytları hangi kodlamayla geri kazanacağımız.
    ///
    /// <para>
    /// syslog yolunda collector <c>iso-8859-1</c> ile çözüyor: bayt ↔ kod noktası
    /// eşlemesi birebir ve tersinir, yani <c>Latin1.GetBytes</c> orijinal bayt
    /// dizisini <b>aynen</b> geri veriyor. UTF-8 ile geri kodlamak burada
    /// 0x80–0xFF aralığındaki her baytı iki bayta çevirip veriyi bozardı —
    /// sessizce, çünkü sonuç yine geçerli bir dizi olurdu.
    /// </para>
    /// </summary>
    private static Encoding ResolveWireEncoding(string? name) =>
        string.IsNullOrEmpty(name) ? Encoding.UTF8 : LookupOrUtf8(name);

    private static Encoding LookupOrUtf8(string name)
    {
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            // Bilinmeyen ad ingest'i durdurmaz; UTF-8 varsayımına dönülür.
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// Gövdeyi orijinal baytlarıyla alır.
    ///
    /// <para>
    /// İki geçerli yol var. <c>bytes_value</c> doğrudan bayt taşır. syslog
    /// yolunda ise gövde <c>string_value</c> olarak gelir ve baytlar
    /// <c>bizigo.wire_encoding</c> ile geri kazanılır — collector'ın
    /// <c>iso-8859-1</c> çözümlemesi tersinir olduğu için bu kayıpsızdır.
    /// </para>
    ///
    /// <para>
    /// Tel kodlaması bildirilmemişse UTF-8 varsayılır. O durumda collector
    /// gövdeyi <c>utf-8</c> ile çözmüş olabilir ve geçersiz baytlar orada
    /// U+FFFD'ye dönmüştür — <b>geri alınamaz</b>. Yapılacak şey yapılandırmayı
    /// düzeltmektir; burada telafi etmeye çalışmak kaybı gizler.
    /// </para>
    /// </summary>
    private static ReadOnlyMemory<byte> ExtractBody(AnyValue? body, string? wireEncoding)
    {
        if (body is null)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        return body.ValueCase switch
        {
            AnyValue.ValueOneofCase.BytesValue => body.BytesValue.ToByteArray(),
            AnyValue.ValueOneofCase.StringValue => ResolveWireEncoding(wireEncoding).GetBytes(body.StringValue),
            AnyValue.ValueOneofCase.None => ReadOnlyMemory<byte>.Empty,
            _ => Encoding.UTF8.GetBytes(Format(body)),
        };
    }

    private static string ResolveSourceKey(IReadOnlyDictionary<string, string> attributes)
    {
        foreach (var candidate in SourceKeyCandidates)
        {
            if (attributes.TryGetValue(candidate, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var prefixed = "resource." + candidate;
            if (attributes.TryGetValue(prefixed, out var resourceValue) && !string.IsNullOrWhiteSpace(resourceValue))
            {
                return resourceValue;
            }
        }

        // Boş bırakmıyoruz: envanterde eşleşmeyen kaynak reddedilmez, `_unassigned`
        // grubuna düşer ve sağlık uyarısı üretir (F1 §8).
        return string.Empty;
    }

    private static string ResolvePeer(IReadOnlyDictionary<string, string> attributes)
    {
        var host = attributes.GetValueOrDefault("client.address")
            ?? attributes.GetValueOrDefault("net.peer.ip")
            ?? string.Empty;

        var port = attributes.GetValueOrDefault("client.port")
            ?? attributes.GetValueOrDefault("net.peer.port")
            ?? string.Empty;

        return string.IsNullOrEmpty(port) ? host : host + ":" + port;
    }

    private static DateTimeOffset? ToTimestamp(ulong unixNano) => unixNano == 0
        ? null
        : DateTimeOffset.UnixEpoch.AddTicks((long)(unixNano / 100));

    private static Dictionary<string, string> Flatten(
        IEnumerable<KeyValue>? attributes,
        string prefix)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (attributes is null)
        {
            return result;
        }

        foreach (var attribute in attributes)
        {
            result[prefix + attribute.Key] = Format(attribute.Value);
        }

        return result;
    }

    private static string Format(AnyValue? value) => value?.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => value.StringValue,
        AnyValue.ValueOneofCase.BoolValue => value.BoolValue ? "true" : "false",
        AnyValue.ValueOneofCase.IntValue => value.IntValue.ToString(CultureInfo.InvariantCulture),
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue.ToString("R", CultureInfo.InvariantCulture),
        AnyValue.ValueOneofCase.BytesValue => Convert.ToBase64String(value.BytesValue.Span),
        AnyValue.ValueOneofCase.ArrayValue => JsonFormatter.Default.Format(value.ArrayValue),
        AnyValue.ValueOneofCase.KvlistValue => JsonFormatter.Default.Format(value.KvlistValue),
        _ => string.Empty,
    };
}
