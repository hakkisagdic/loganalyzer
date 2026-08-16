using System.Text.RegularExpressions;

namespace Bizigo.Parsing.Grok;

public enum GrokFieldType
{
    String = 0,
    Int,
    Long,
    Float,
    Double,
    Bool,
}

/// <summary>Bir <c>%{PATTERN:alan:tip}</c> referansının derlenmiş karşılığı.</summary>
public sealed record GrokCapture(string Field, string GroupName, GrokFieldType Type);

public enum GrokMatchOutcome
{
    Matched = 0,
    NoMatch,

    /// <summary>
    /// <c>matchTimeout</c> aşıldı. Geri izlemeli motora düşmüş bir pattern'de bu,
    /// ReDoS'un gerçek dünyadaki görünümüdür — çağıran taraf parser'ı karantinaya
    /// almalıdır (F1 §4.1 kademe 3).
    /// </summary>
    TimedOut,
}

public readonly record struct GrokMatchResult(GrokMatchOutcome Outcome, int Index, int Length)
{
    public bool Matched => Outcome == GrokMatchOutcome.Matched;
}

/// <summary>
/// Derlenmiş grok ifadesi. <see cref="IsLinearTime"/> doğruysa
/// <c>RegexOptions.NonBacktracking</c> ile derlenmiştir ve girdi uzunluğunda
/// doğrusaldır — hiçbir girdi süreci kilitleyemez.
/// </summary>
public sealed class CompiledGrok
{
    private readonly Regex _regex;

    internal CompiledGrok(
        string expression,
        string regexSource,
        Regex regex,
        IReadOnlyList<GrokCapture> captures,
        bool isLinearTime,
        string? fallbackReason)
    {
        Expression = expression;
        RegexSource = regexSource;
        _regex = regex;
        Captures = captures;
        IsLinearTime = isLinearTime;
        FallbackReason = fallbackReason;
    }

    /// <summary>Kullanıcının YAML'a yazdığı hali.</summary>
    public string Expression { get; }

    /// <summary>Genişletilmiş .NET düzenli ifadesi — <c>parser lint</c> bunu gösterir.</summary>
    public string RegexSource { get; }

    public IReadOnlyList<GrokCapture> Captures { get; }

    public bool IsLinearTime { get; }

    /// <summary>Doğrusal motora düşülemediyse nedeni (lookaround, geri referans, atomik grup).</summary>
    public string? FallbackReason { get; }

    public GrokMatchResult Match(string input, IDictionary<string, object?> into)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(into);

        Match match;
        try
        {
            match = _regex.Match(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return new GrokMatchResult(GrokMatchOutcome.TimedOut, -1, 0);
        }

        if (!match.Success)
        {
            return new GrokMatchResult(GrokMatchOutcome.NoMatch, -1, 0);
        }

        foreach (var capture in Captures)
        {
            var group = match.Groups[capture.GroupName];
            if (!group.Success || group.Length == 0)
            {
                continue;
            }

            // Aynı alan adı birden çok kez yakalanabilir (SYSLOGBASE2'de `timestamp`
            // iki alternatifte de var). İlk başarılı ve boş olmayan yakalama kazanır.
            if (into.TryGetValue(capture.Field, out var existing) && existing is not null)
            {
                continue;
            }

            into[capture.Field] = GrokValueConverter.Convert(group.Value, capture.Type);
        }

        return new GrokMatchResult(GrokMatchOutcome.Matched, match.Index, match.Length);
    }
}

public static class GrokValueConverter
{
    /// <summary>
    /// Dönüştürülemeyen değer <b>string olarak kalır</b>. Alternatif, satırı düşürmek
    /// olurdu; ağ cihazı loglarında tek bozuk alan yüzünden olay kaybetmek kabul edilemez.
    /// Kültür daima invariant — cihaz logunda ondalık ayracı asla yerel değildir.
    /// </summary>
    public static object Convert(string raw, GrokFieldType type) => type switch
    {
        GrokFieldType.Int => int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : raw,
        GrokFieldType.Long => long.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : raw,
        GrokFieldType.Float or GrokFieldType.Double => double.TryParse(raw,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : raw,
        GrokFieldType.Bool => ParseBool(raw),
        _ => raw,
    };

    private static object ParseBool(string raw) => raw switch
    {
        "true" or "TRUE" or "True" or "1" or "yes" or "YES" or "Yes" => true,
        "false" or "FALSE" or "False" or "0" or "no" or "NO" or "No" => false,
        _ => raw,
    };
}
