using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Bizigo.Ingest.Otlp;

/// <summary>OTLP's hex IDs and numeric enums differ from ordinary ProtoJSON.</summary>
public static class OtlpJsonCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonFormatter Formatter = new(JsonFormatter.Settings.Default.WithFormatEnumsAsIntegers(true));

    public static T Parse<T>(ReadOnlySpan<byte> bytes, MessageDescriptor descriptor) where T : IMessage<T>, new()
    {
        using var document = JsonDocument.Parse(Utf8.GetString(bytes));
        RejectDuplicateKeys(document.RootElement);
        var json = JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject
            ?? throw new JsonException("OTLP export must be an object.");
        Normalize(json, descriptor, incoming: true);
        return JsonParser.Default.Parse<T>(json.ToJsonString());
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON field.");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }

    public static JsonObject Format(IMessage message)
    {
        var json = JsonNode.Parse(Formatter.Format(message))!.AsObject();
        Normalize(json, message.Descriptor, incoming: false);
        return json;
    }

    private static void Normalize(JsonObject json, MessageDescriptor descriptor, bool incoming)
    {
        var fields = descriptor.Fields.InFieldNumberOrder().ToDictionary(f => f.JsonName, StringComparer.Ordinal);
        foreach (var (name, value) in json.ToArray())
        {
            // Only lowerCamelCase names participate. Original proto snake_case
            // names are unknown fields, never aliases accepted by JsonParser.
            if (!fields.TryGetValue(name, out var field))
            {
                json.Remove(name);
                continue;
            }
            if (value is null) continue;
            if (field.IsRepeated)
            {
                if (value is not JsonArray array) throw new JsonException("Repeated field must be an array.");
                for (var i = 0; i < array.Count; i++) array[i] = NormalizeValue(array[i], field, incoming);
            }
            else json[name] = NormalizeValue(value, field, incoming);
        }
    }

    private static JsonNode? NormalizeValue(JsonNode? node, FieldDescriptor field, bool incoming)
    {
        if (node is null) return null;
        if (field.FieldType == FieldType.Message)
        {
            if (node is not JsonObject child) throw new JsonException("Message must be an object.");
            Normalize(child, field.MessageType, incoming);
        }
        else if (field.FieldType == FieldType.Enum && incoming
                 && (node is not JsonValue scalar || !scalar.TryGetValue<int>(out _)))
            throw new JsonException("OTLP enums must be integer numbers.");
        else if (incoming && field.FieldType is FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64
                     or FieldType.UInt64 or FieldType.Fixed64)
        {
            if (node is not JsonValue integerValue) throw new JsonException("Integer field must be a number or string.");
            var text = integerValue.TryGetValue<string>(out var quoted) ? quoted : integerValue.ToJsonString();
            return JsonValue.Create(ExactInteger(text, field.FieldType is FieldType.UInt64 or FieldType.Fixed64));
        }
        else if (field.FieldType == FieldType.Bytes && field.JsonName is "traceId" or "spanId" or "parentSpanId")
        {
            if (node is not JsonValue textValue || !textValue.TryGetValue<string>(out var value))
                throw new JsonException("OTLP identifiers must be hex strings.");
            var bytes = incoming ? Convert.FromHexString(value) : Convert.FromBase64String(value);
            var expected = field.JsonName == "traceId" ? 16 : 8;
            if (bytes.Length != 0 && bytes.Length != expected) throw new JsonException("Invalid OTLP identifier length.");
            return JsonValue.Create(incoming ? Convert.ToBase64String(bytes) : Convert.ToHexStringLower(bytes));
        }
        return node.DeepClone();
    }

    // Google.Protobuf's numeric JSON path passes through double. Normalize the
    // original decimal token to a string first, including exact exponent forms.
    // Work is bounded by the input length; never allocate exponent-sized buffers.
    private static string ExactInteger(string text, bool unsigned)
    {
        var negative = text.StartsWith('-');
        var number = text.AsSpan();
        if (number.Length > 0 && number[0] is '-' or '+') number = number[1..];
        var exponentAt = number.IndexOfAny('e', 'E');
        var mantissa = exponentAt < 0 ? number : number[..exponentAt];
        var point = mantissa.IndexOf('.');
        if (mantissa.IsEmpty || (point >= 0 && mantissa[(point + 1)..].Contains('.')))
            throw new JsonException("Invalid integer.");
        var digits = mantissa.ToString().Replace(".", "", StringComparison.Ordinal);
        if (digits.Length == 0 || digits.Any(c => !char.IsAsciiDigit(c)))
            throw new JsonException("Invalid integer.");
        digits = digits.TrimStart('0');
        long exponent = 0;
        if (exponentAt >= 0 && !long.TryParse(number[(exponentAt + 1)..], NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent)) throw new JsonException("Integer exponent out of range.");
        if (digits.Length == 0) return "0";
        var fraction = point < 0 ? 0 : mantissa.Length - point - 1;
        if (exponent < -text.Length || exponent > text.Length + 20L)
            throw new JsonException("Integer out of range or fractional.");
        var shift = exponent - fraction;
        if (shift < 0)
        {
            var withoutZeros = digits.TrimEnd('0');
            if (-shift > digits.Length - withoutZeros.Length) throw new JsonException("Integer cannot be fractional.");
            digits = digits[..(digits.Length + (int)shift)];
        }
        else
        {
            if (digits.Length + shift > 20) throw new JsonException("Integer out of range.");
            digits += new string('0', (int)shift);
        }
        var canonical = negative ? "-" + digits : digits;
        var valid = unsigned
            ? ulong.TryParse(canonical, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
            : long.TryParse(canonical, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
        return valid ? canonical : throw new JsonException("Integer out of range.");
    }
}
