using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Bizigo.Contracts;

/// <summary>
/// <see cref="RawRecord"/> ↔ NDJSON satırı.
///
/// <para>
/// <b>Tek format kararı:</b> WAL'a yazılan bayt dizisi ile ham arşive
/// (RustFS/S3) yüklenen NDJSON satırı <b>aynı</b> formattır (F1 §7.1).
/// Böylece yükleyici bir dönüştürücü değil, bir kopyalayıcıdır: ikinci bir
/// serileştirici bakılmaz ve iki formatın sessizce ayrışması mümkün olmaz.
/// </para>
///
/// <para>
/// <c>owner_group</c> ve <c>source_id</c> WAL aşamasında boş yazılır; dispatcher
/// çözünce yükleyici satırı bu alanlar dolu olarak yeniden yazar. Alanların
/// varlığı formatın parçası, değerlerinin dolu olması değil.
/// </para>
/// </summary>
public static class RawRecordCodec
{
    public static void Write(IBufferWriter<byte> output, RawRecord record)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(record);

        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteString("event_id", record.EventId);
        writer.WriteString("received_at", record.ReceivedAt.UtcDateTime);

        if (record.ObservedAt is { } observed)
        {
            writer.WriteString("observed_at", observed.UtcDateTime);
        }

        writer.WriteString("source_key", record.SourceKey);
        writer.WriteString("source_id", record.SourceId);
        writer.WriteString("owner_group", record.OwnerGroup);

        writer.WriteStartObject("transport");
        writer.WriteString("proto", record.TransportProto);
        writer.WriteString("peer", record.TransportPeer);
        writer.WriteEndObject();

        writer.WriteString("encoding_declared", record.EncodingDeclared);

        if (record.SeverityNumber != 0)
        {
            writer.WriteNumber("severity_number", record.SeverityNumber);
        }

        if (record.Attributes.Count > 0)
        {
            writer.WriteStartObject("attrs");
            foreach (var (key, value) in record.Attributes)
            {
                writer.WriteString(key, value);
            }

            writer.WriteEndObject();
        }

        // Base64: JSON string'e sığmayan baytlar (geçersiz UTF-8, NUL) kayıpsız taşınsın.
        writer.WriteBase64String("raw_b64", record.Body.Span);
        writer.WriteEndObject();
        writer.Flush();
    }

    public static RawRecord Read(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("attrs", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in attrs.EnumerateObject())
            {
                attributes[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        var hasTransport = root.TryGetProperty("transport", out var transport);

        return new RawRecord
        {
            EventId = root.GetProperty("event_id").GetGuid(),
            ReceivedAt = new DateTimeOffset(root.GetProperty("received_at").GetDateTime(), TimeSpan.Zero),
            ObservedAt = root.TryGetProperty("observed_at", out var observed)
                ? new DateTimeOffset(observed.GetDateTime(), TimeSpan.Zero)
                : null,
            SourceKey = root.GetProperty("source_key").GetString() ?? string.Empty,
            SourceId = ReadString(root, "source_id"),
            OwnerGroup = ReadString(root, "owner_group"),
            TransportProto = hasTransport ? ReadString(transport, "proto") : string.Empty,
            TransportPeer = hasTransport ? ReadString(transport, "peer") : string.Empty,
            EncodingDeclared = ReadString(root, "encoding_declared"),
            SeverityNumber = root.TryGetProperty("severity_number", out var severity)
                ? (byte)severity.GetInt32()
                : (byte)0,
            Body = root.GetProperty("raw_b64").GetBytesFromBase64(),
            Attributes = attributes,
        };
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    /// <summary>Tek kaydın NDJSON satırı — satır sonu dahil değil.</summary>
    public static byte[] ToLine(RawRecord record)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        Write(buffer, record);
        return buffer.WrittenSpan.ToArray();
    }

    public static string Describe(RawRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"RawRecord({record.EventId}, {record.SourceKey}, {record.Body.Length} bayt)");
    }
}
