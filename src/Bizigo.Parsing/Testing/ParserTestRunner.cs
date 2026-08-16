using System.Globalization;
using Bizigo.Contracts;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Schema;

namespace Bizigo.Parsing.Testing;

/// <summary>Konsol ve fark raporlarında ortak değer biçimlendirmesi.</summary>
public static class ValueFormatter
{
    public static string Format(object? value) => value switch
    {
        null => "<yok>",
        string s => $"\"{s}\"",
        IEnumerable<object?> items => "[" + string.Join(", ", items.Select(Format)) + "]",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "<yok>",
    };
}

public sealed record ExpectationResult(string Key, object? Expected, object? Actual, bool Passed)
{
    public string Describe() => Passed
        ? $"  ✓ {Key} = {ValueFormatter.Format(Actual)}"
        : $"  ✗ {Key}{Environment.NewLine}      beklenen: {ValueFormatter.Format(Expected)}" +
          $"{Environment.NewLine}      gerçek  : {ValueFormatter.Format(Actual)}";
}

public sealed record ParserTestResult(
    string Name,
    int Line,
    bool Passed,
    IReadOnlyList<ExpectationResult> Expectations,
    ParseResult? Parse)
{
    public IEnumerable<ExpectationResult> Failures => Expectations.Where(static e => !e.Passed);
}

public sealed record ParserTestReport(string ParserId, string Path, IReadOnlyList<ParserTestResult> Tests)
{
    public bool Passed => Tests.All(static t => t.Passed);

    public int PassCount => Tests.Count(static t => t.Passed);

    public int FailCount => Tests.Count - PassCount;
}

/// <summary>
/// YAML'ın gömülü <c>tests</c> bloğunu koşturur.
///
/// <para>
/// Bu, kalite için tek en ucuz kaldıraç (F1 §3): parser'ı yazan kişi zaten elinde
/// örnek satırla çalışıyor, onu dosyaya yazması ek iş değil. Karşılığında her
/// motor değişikliği, her pattern kütüphanesi yükseltmesi ve her replay bütün
/// katalog üzerinde doğrulanabilir hale geliyor.
/// </para>
/// </summary>
public static class ParserTestRunner
{
    public static ParserTestReport Run(CompiledParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);

        var results = new List<ParserTestResult>(parser.Definition.Tests.Count);

        foreach (var test in parser.Definition.Tests)
        {
            var parse = parser.Parse(test.Input);
            var expectations = new List<ExpectationResult>(test.Expect.Count);

            foreach (var (key, expected) in test.Expect)
            {
                var actual = Resolve(parse, key);
                expectations.Add(new ExpectationResult(key, expected, actual, ValuesMatch(expected, actual)));
            }

            results.Add(new ParserTestResult(
                test.Name, test.Line, expectations.TrueForAll(static e => e.Passed), expectations, parse));
        }

        return new ParserTestReport(parser.Id, parser.Definition.SourcePath, results);
    }

    /// <summary>
    /// Beklenti anahtarları: <c>parse_status</c>, <c>tags</c>, <c>@timestamp</c>,
    /// <c>core.*</c>, <c>ocsf.*</c>, <c>otel.*</c>, <c>fields.*</c> ve önek verilmemişse
    /// doğrudan alan adı.
    /// </summary>
    private static object? Resolve(ParseResult parse, string key)
    {
        if (key == "parse_status")
        {
            return parse.Status switch
            {
                ParseStatus.Ok => "ok",
                ParseStatus.Partial => "partial",
                _ => "failed",
            };
        }

        if (key == "tags")
        {
            return parse.Tags.Cast<object?>().ToArray();
        }

        if (key == ParseContext.TimestampField)
        {
            return parse.Timestamp?.ToString("O", CultureInfo.InvariantCulture);
        }

        var separator = key.IndexOf('.', StringComparison.Ordinal);
        if (separator > 0)
        {
            var section = key[..separator];
            var name = key[(separator + 1)..];

            switch (section)
            {
                case "core":
                    return parse.Core.GetValueOrDefault(name);
                case "ocsf":
                    return parse.Ocsf.GetValueOrDefault(name);
                case "otel":
                    return parse.Otel.GetValueOrDefault(name);
                case "fields":
                    return parse.Fields.GetValueOrDefault(name);
            }
        }

        return parse.Fields.GetValueOrDefault(key);
    }

    /// <summary>
    /// YAML <c>53</c> yazdığında <c>long</c> okur; motor <c>convert: int</c> ile
    /// <c>int</c> üretir. Tip farkı yüzünden testin kırmızı yanması, kullanıcıya
    /// motorun iç ayrıntısını dert ettirmek olur — sayısal karşılaştırma değere bakar.
    /// </summary>
    public static bool ValuesMatch(object? expected, object? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        if (expected is object?[] expectedItems)
        {
            if (actual is not object?[] actualItems || expectedItems.Length != actualItems.Length)
            {
                return false;
            }

            for (var i = 0; i < expectedItems.Length; i++)
            {
                if (!ValuesMatch(expectedItems[i], actualItems[i]))
                {
                    return false;
                }
            }

            return true;
        }

        if (IsNumeric(expected) && IsNumeric(actual))
        {
            return Math.Abs(ToDouble(expected) - ToDouble(actual)) < 1e-9;
        }

        if (expected is bool || actual is bool)
        {
            return expected is bool eb && actual is bool ab && eb == ab;
        }

        return string.Equals(
            Convert.ToString(expected, CultureInfo.InvariantCulture),
            Convert.ToString(actual, CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    private static bool IsNumeric(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static double ToDouble(object value) =>
        Convert.ToDouble(value, CultureInfo.InvariantCulture);
}
