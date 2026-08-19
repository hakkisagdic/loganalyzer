using System.Globalization;
using System.Text.Json;

namespace Bizigo.Api.Webhooks;

/// <summary>
/// Bilinmeyen sağlayıcı eşlemesi için <b>küçük</b> bir JSON yol çözümleyicisi
/// (T24).
///
/// <para>
/// Desteklenen sözdizimi bilerek dar: <c>$.a.b</c>, <c>$.a[0].b</c>, <c>a.b</c>.
/// Süzgeç, joker, özyineleme (<c>..</c>) ve ifade <b>yok</b>. Gerekçe F1'in en
/// pahalı dersiyle aynı yöne bakıyor: yapılandırmadan gelen bir ifade dili,
/// dışarıdan gelen bir gövdenin işlenme süresini belirlemeye başlar. Burada her
/// yol sabit sayıda adım, girdi boyutundan bağımsız.
/// </para>
///
/// <para>
/// Yetmediği gün cevap yolu dilini büyütmek değil, o sağlayıcı için bir eşleme
/// yazmaktır — üç sağlayıcının eşlemesi zaten öyle duruyor.
/// </para>
/// </summary>
public static class JsonPathReader
{
    /// <summary>
    /// Yolu çözer ve <b>skaler</b> değeri metin olarak döndürür. Yol yoksa,
    /// <c>null</c>'a denk geliyorsa ya da bir nesne/dizi gösteriyorsa
    /// <see langword="null"/>.
    /// </summary>
    public static string? Read(JsonElement root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !TryResolve(root, path, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static bool TryResolve(JsonElement root, string path, out JsonElement result)
    {
        result = root;

        var span = path.AsSpan().Trim();

        if (span.StartsWith("$", StringComparison.Ordinal))
        {
            span = span[1..];
        }

        if (span.StartsWith(".", StringComparison.Ordinal))
        {
            span = span[1..];
        }

        var current = root;

        foreach (var range in Split(span))
        {
            var segment = span[range];

            if (segment.IsEmpty)
            {
                continue;
            }

            if (!Step(ref current, segment))
            {
                return false;
            }
        }

        result = current;
        return true;
    }

    /// <summary>Tek segment: <c>ad</c>, <c>ad[2]</c> ya da <c>[2]</c>.</summary>
    private static bool Step(ref JsonElement current, ReadOnlySpan<char> segment)
    {
        var bracket = segment.IndexOf('[');
        var name = bracket < 0 ? segment : segment[..bracket];

        if (!name.IsEmpty)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(name.ToString(), out var child))
            {
                return false;
            }

            current = child;
        }

        var rest = bracket < 0 ? default : segment[bracket..];

        while (!rest.IsEmpty)
        {
            var close = rest.IndexOf(']');

            if (rest[0] != '[' || close < 0)
            {
                return false;
            }

            // `NumberStyles.None` işareti ve boşluğu reddediyor: `[-1]` ve `[ 1]`
            // geçerli yol değil.
            if (!int.TryParse(rest[1..close], NumberStyles.None, CultureInfo.InvariantCulture, out var i)
                || current.ValueKind != JsonValueKind.Array
                || i >= current.GetArrayLength())
            {
                return false;
            }

            current = current[i];
            rest = rest[(close + 1)..];
        }

        return true;
    }

    private static List<Range> Split(ReadOnlySpan<char> span)
    {
        var ranges = new List<Range>();
        var start = 0;

        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] != '.')
            {
                continue;
            }

            ranges.Add(new Range(start, i));
            start = i + 1;
        }

        ranges.Add(new Range(start, span.Length));
        return ranges;
    }
}
