using System.Globalization;
using System.Text;
using System.Text.Json;
using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Schema;

namespace Bizigo.Parsing.Engine;

/// <summary>Derlenmiş boru hattı adımı. Başarısızlıkta <c>false</c> döner; ne olacağını motor karar verir.</summary>
public interface ICompiledStep
{
    PipelineStep Definition { get; }

    bool Execute(ParseContext context, out string failureReason);
}

internal sealed class CompiledGrokStep(GrokStep definition, IReadOnlyList<CompiledGrok> patterns) : ICompiledStep
{
    public PipelineStep Definition => definition;

    public IReadOnlyList<CompiledGrok> Patterns => patterns;

    public bool Execute(ParseContext context, out string failureReason)
    {
        failureReason = string.Empty;

        if (!context.TryGetString(definition.Field, out var input))
        {
            failureReason = $"alan '{definition.Field}' yok";
            return false;
        }

        foreach (var pattern in patterns)
        {
            var result = pattern.Match(input, context.Fields);
            if (result.Matched)
            {
                return true;
            }

            if (result.Outcome == GrokMatchOutcome.TimedOut)
            {
                context.MarkTimedOut();
                failureReason = $"pattern zaman aşımına uğradı: {pattern.Expression}";
                return false;
            }
        }

        failureReason = $"hiçbir pattern eşleşmedi ({patterns.Count} denendi)";
        return false;
    }
}

internal sealed class CompiledKvStep(KvStep definition) : ICompiledStep
{
    public PipelineStep Definition => definition;

    public bool Execute(ParseContext context, out string failureReason)
    {
        failureReason = string.Empty;

        if (!context.TryGetString(definition.Field, out var input))
        {
            failureReason = $"alan '{definition.Field}' yok";
            return false;
        }

        var found = 0;

        foreach (var token in Tokenize(input, definition.Separator, definition.Quoted))
        {
            var assignIndex = token.IndexOf(definition.Assign, StringComparison.Ordinal);
            if (assignIndex <= 0)
            {
                continue;
            }

            var key = token[..assignIndex].Trim();
            var value = Unquote(token[(assignIndex + definition.Assign.Length)..].Trim());

            if (key.Length == 0)
            {
                continue;
            }

            if (definition.Include.Count > 0 && !definition.Include.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            if (definition.Exclude.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            context.Fields[definition.TargetPrefix is null ? key : definition.TargetPrefix + key] = value;
            found++;
        }

        if (found == 0)
        {
            failureReason = "anahtar=değer çifti bulunamadı";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Tırnak farkındalıklı bölme. FortiGate <c>msg="Denied by policy"</c> yazar;
    /// naif <c>Split(' ')</c> bu mesajı üç parçaya böler ve alanı sessizce bozar.
    /// </summary>
    private static IEnumerable<string> Tokenize(string input, string separator, bool quoted)
    {
        var builder = new StringBuilder();
        var inQuote = false;
        var i = 0;

        while (i < input.Length)
        {
            var c = input[i];

            if (quoted && c == '"')
            {
                inQuote = !inQuote;
                builder.Append(c);
                i++;
                continue;
            }

            if (!inQuote && MatchesAt(input, i, separator))
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }

                i += separator.Length;
                continue;
            }

            builder.Append(c);
            i++;
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static bool MatchesAt(string input, int index, string value) =>
        index + value.Length <= input.Length &&
        string.CompareOrdinal(input, index, value, 0, value.Length) == 0;

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}

internal sealed class CompiledJsonStep(JsonStep definition) : ICompiledStep
{
    public PipelineStep Definition => definition;

    public bool Execute(ParseContext context, out string failureReason)
    {
        failureReason = string.Empty;

        if (!context.TryGetString(definition.Field, out var input))
        {
            failureReason = $"alan '{definition.Field}' yok";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input);
        }
        catch (JsonException ex)
        {
            failureReason = "geçersiz JSON: " + ex.Message;
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                failureReason = "JSON kökü nesne değil";
                return false;
            }

            Flatten(document.RootElement, definition.TargetPrefix ?? string.Empty, context);
        }

        return true;
    }

    private void Flatten(JsonElement element, string prefix, ParseContext context)
    {
        foreach (var property in element.EnumerateObject())
        {
            var name = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object when definition.Flatten:
                    Flatten(property.Value, name, context);
                    break;
                case JsonValueKind.Number:
                    // Koşullu ifade kullanmak `long` ile `double`'ı ortak tipte
                    // birleştirip her tam sayıyı sessizce double yapardı.
                    if (property.Value.TryGetInt64(out var integer))
                    {
                        context.Fields[name] = integer;
                    }
                    else
                    {
                        context.Fields[name] = property.Value.GetDouble();
                    }

                    break;
                case JsonValueKind.True:
                    context.Fields[name] = true;
                    break;
                case JsonValueKind.False:
                    context.Fields[name] = false;
                    break;
                case JsonValueKind.Null:
                    break;
                case JsonValueKind.String:
                    context.Fields[name] = property.Value.GetString();
                    break;
                default:
                    context.Fields[name] = property.Value.GetRawText();
                    break;
            }
        }
    }
}

internal sealed class CompiledCsvStep(CsvStep definition) : ICompiledStep
{
    public PipelineStep Definition => definition;

    public bool Execute(ParseContext context, out string failureReason)
    {
        failureReason = string.Empty;

        if (!context.TryGetString(definition.Field, out var input))
        {
            failureReason = $"alan '{definition.Field}' yok";
            return false;
        }

        var values = Split(input, definition.Separator, definition.Quote).ToArray();

        if (values.Length < definition.Columns.Count)
        {
            failureReason = $"{definition.Columns.Count} kolon bekleniyordu, {values.Length} bulundu";
            return false;
        }

        for (var i = 0; i < definition.Columns.Count; i++)
        {
            var name = definition.Columns[i];
            if (name.Length == 0 || name == "_")
            {
                continue;
            }

            var value = definition.TrimWhitespace ? values[i].Trim() : values[i];
            context.Fields[name] = value;
        }

        return true;
    }

    private static IEnumerable<string> Split(string input, char separator, char quote)
    {
        var builder = new StringBuilder();
        var inQuote = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (c == quote)
            {
                // Kaçışlanmış tırnak: "" → "
                if (inQuote && i + 1 < input.Length && input[i + 1] == quote)
                {
                    builder.Append(quote);
                    i++;
                    continue;
                }

                inQuote = !inQuote;
                continue;
            }

            if (c == separator && !inQuote)
            {
                yield return builder.ToString();
                builder.Clear();
                continue;
            }

            builder.Append(c);
        }

        yield return builder.ToString();
    }
}

internal sealed class CompiledDateStep(DateStep definition) : ICompiledStep
{
    public PipelineStep Definition => definition;

    public bool Execute(ParseContext context, out string failureReason)
    {
        failureReason = string.Empty;

        if (!context.TryGetString(definition.Field, out var raw) || raw.Length == 0)
        {
            failureReason = $"alan '{definition.Field}' yok";
            return false;
        }

        var zone = ResolveZone(context);

        foreach (var format in definition.Formats)
        {
            if (TryParse(raw, format, zone, out var parsed))
            {
                context.Fields[definition.Target] = parsed;
                return true;
            }
        }

        failureReason = $"'{raw}' verilen {definition.Formats.Count} biçimin hiçbiriyle çözülemedi";
        return false;
    }

    private TimeZoneInfo ResolveZone(ParseContext context)
    {
        if (definition.TimezoneField is { } field &&
            context.TryGetString(field, out var value) &&
            TimeZoneResolver.Resolve(value) is { } fromField)
        {
            return fromField;
        }

        return TimeZoneResolver.Resolve(definition.DefaultTimezone) ?? TimeZoneInfo.Utc;
    }

    private static bool TryParse(string raw, string format, TimeZoneInfo zone, out DateTimeOffset value)
    {
        value = default;

        switch (format)
        {
            case "UNIX":
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                {
                    value = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    return true;
                }

                return false;

            case "UNIX_MS":
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis))
                {
                    value = DateTimeOffset.FromUnixTimeMilliseconds(millis);
                    return true;
                }

                return false;

            case "ISO8601":
                return DateTimeOffset.TryParse(
                    raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);

            case "SYSLOG":
                return TryParseSyslog(raw, zone, out value);

            default:
            {
                if (DateTimeOffset.TryParseExact(
                        raw, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var withOffset)
                    && FormatCarriesOffset(format))
                {
                    value = withOffset;
                    return true;
                }

                if (!DateTime.TryParseExact(
                        raw, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                {
                    return false;
                }

                value = ToOffset(local, zone);
                return true;
            }
        }
    }

    private static bool FormatCarriesOffset(string format) =>
        format.Contains('z', StringComparison.Ordinal) || format.Contains('K', StringComparison.Ordinal);

    /// <summary>
    /// RFC3164 zaman damgası yıl taşımaz (<c>Aug 16 10:00:00</c>). Bugünün yılını
    /// koyup geleceğe düşerse bir yıl geri alıyoruz — yılbaşı gecesi gelen logu
    /// 11 ay ileriye yazmamak için.
    /// </summary>
    private static bool TryParseSyslog(string raw, TimeZoneInfo zone, out DateTimeOffset value)
    {
        value = default;

        string[] formats = ["MMM d HH:mm:ss", "MMM  d HH:mm:ss", "MMM dd HH:mm:ss"];
        var now = DateTimeOffset.UtcNow;

        foreach (var format in formats)
        {
            if (!DateTime.TryParseExact(raw.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                continue;
            }

            var candidate = ToOffset(new DateTime(now.Year, parsed.Month, parsed.Day,
                parsed.Hour, parsed.Minute, parsed.Second, DateTimeKind.Unspecified), zone);

            if (candidate > now.AddDays(1))
            {
                candidate = ToOffset(new DateTime(now.Year - 1, parsed.Month, parsed.Day,
                    parsed.Hour, parsed.Minute, parsed.Second, DateTimeKind.Unspecified), zone);
            }

            value = candidate;
            return true;
        }

        return false;
    }

    private static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var offset = zone.IsInvalidTime(unspecified)
            ? zone.BaseUtcOffset
            : zone.GetUtcOffset(unspecified);

        return new DateTimeOffset(unspecified, offset);
    }
}

internal sealed class CompiledConvertStep(ConvertStep definition) : ICompiledStep
{
    public PipelineStep Definition => definition;

    public bool Execute(ParseContext context, out string failureReason)
    {
        var missing = new List<string>();

        foreach (var (field, type) in definition.Fields)
        {
            if (!context.Fields.TryGetValue(field, out var raw) || raw is null)
            {
                missing.Add(field);
                continue;
            }

            var text = raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
            context.Fields[field] = GrokValueConverter.Convert(text, type);
        }

        if (missing.Count == definition.Fields.Count && definition.Fields.Count > 0)
        {
            failureReason = "dönüştürülecek alanların hiçbiri yok: " + string.Join(", ", missing);
            return false;
        }

        failureReason = string.Empty;
        return true;
    }
}

internal sealed class CompiledDropStep(DropStep definition) : ICompiledStep
{
    public PipelineStep Definition => definition;

    public bool Execute(ParseContext context, out string failureReason)
    {
        failureReason = string.Empty;

        foreach (var field in definition.Fields)
        {
            context.Fields.Remove(field);
        }

        return true;
    }
}
