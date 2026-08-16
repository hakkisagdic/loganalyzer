using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Bizigo.Parsing.Grok;

public sealed record GrokCompilerOptions
{
    /// <summary>
    /// Geri izlemeli motora düşen pattern'ler için eşleşme zaman aşımı (F1 §4.1).
    /// Doğrusal motorda pratikte tetiklenmez.
    /// </summary>
    public TimeSpan MatchTimeout { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Özyinelemeli genişletme derinliği — döngüsel pattern'lere karşı ikinci emniyet.</summary>
    public int MaxExpansionDepth { get; init; } = 64;
}

/// <summary>
/// Grok → .NET <see cref="Regex"/> derleyicisi.
///
/// <para>
/// Hazır kütüphane (<c>grok.net</c>) kullanılmadı: v2'den itibaren PCRE.NET (native)
/// üstünde koşuyor ve orada <c>NonBacktracking</c> eşdeğeri yok. Parser YAML'ı 50
/// kişilik kurumdan geliyor; kötü niyet gerekmiyor, dikkatsiz tek pattern ingest'i
/// durdurmaya yeter. Bu yüzden derleme <b>önce doğrusal motorla</b> denenir.
/// </para>
/// </summary>
public sealed class GrokCompiler
{
    private static readonly Regex GroupNameSanitizer = new("[^A-Za-z0-9_]", RegexOptions.CultureInvariant);

    private readonly GrokPatternLibrary _library;
    private readonly GrokCompilerOptions _options;
    private readonly ConcurrentDictionary<string, CompiledGrok> _cache = new(StringComparer.Ordinal);

    public GrokCompiler(GrokPatternLibrary library, GrokCompilerOptions? options = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _options = options ?? new GrokCompilerOptions();
    }

    public GrokPatternLibrary Library => _library;

    /// <summary>Aynı ifadenin ikinci derlemesi önbellekten gelir.</summary>
    public CompiledGrok Compile(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return _cache.GetOrAdd(expression, CompileCore);
    }

    /// <summary>Ek pattern tanımlarıyla yeni bir derleyici — parser'ın <c>pattern_definitions</c> bloğu.</summary>
    public GrokCompiler With(IReadOnlyDictionary<string, string>? extraPatterns)
    {
        if (extraPatterns is null || extraPatterns.Count == 0)
        {
            return this;
        }

        return new GrokCompiler(_library.With(extraPatterns), _options);
    }

    private CompiledGrok CompileCore(string expression)
    {
        var builder = new StringBuilder(expression.Length * 4);
        var captures = new List<GrokCapture>();
        var stack = new List<string>();
        var groupCounter = 0;

        Expand(expression, builder, captures, stack, ref groupCounter, expression);

        var source = builder.ToString();

        try
        {
            // ZAMAN AŞIMI YOK — bilinçli. `NonBacktracking` girdi uzunluğunda
            // doğrusal zaman garantisi veriyor, yani felç (catastrophic
            // backtracking) imkânsız ve korunacak bir şey kalmıyor. Buraya
            // `matchTimeout` konursa ölçülen tek şey DUVAR SAATİ olur: makine
            // swap'teyken işlem zaman dilimi alamaz, 50 ms dolar ve sağlıklı bir
            // pattern zaman aşımına uğrar.
            //
            // Bunun bedeli sadece cılız test değil: sonuç `parse_status=failed`
            // oluyor, yani "motor meşguldü" ile "bu satır bu parser'a uymuyor"
            // ayırt edilemiyor — satır keşif kuyruğuna düşüyor ve sağlıklı parser
            // karantinaya girebiliyor. (T08 raporu #10.)
            //
            // Satır uzunluğu collector'da sınırlı (max_log_size), dolayısıyla
            // doğrusal tarama sınırsız süremez.
            var linear = new Regex(
                source,
                RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                Regex.InfiniteMatchTimeout);

            return new CompiledGrok(expression, source, linear, captures, isLinearTime: true, fallbackReason: null);
        }
        catch (NotSupportedException ex)
        {
            // Lookaround / geri referans / atomik grup. Logstash'in IPV4 pattern'i
            // `(?<![0-9])` kullandığı için bu yol nadir değil, olağan.
            Regex backtracking;
            try
            {
                backtracking = new Regex(source, RegexOptions.CultureInvariant, _options.MatchTimeout);
            }
            catch (ArgumentException inner)
            {
                throw new GrokCompilationException(
                    $"Düzenli ifade geçersiz: {inner.Message}", expression, stack);
            }

            return new CompiledGrok(
                expression, source, backtracking, captures, isLinearTime: false, fallbackReason: ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw new GrokCompilationException($"Düzenli ifade geçersiz: {ex.Message}", expression, stack);
        }
    }

    private void Expand(
        string pattern,
        StringBuilder output,
        List<GrokCapture> captures,
        List<string> stack,
        ref int groupCounter,
        string rootExpression)
    {
        if (stack.Count > _options.MaxExpansionDepth)
        {
            throw new GrokCompilationException(
                $"Grok genişletme derinliği {_options.MaxExpansionDepth} aşıldı.", rootExpression, stack);
        }

        var i = 0;
        var inCharacterClass = false;

        while (i < pattern.Length)
        {
            var c = pattern[i];

            if (c == '\\' && i + 1 < pattern.Length)
            {
                var next = pattern[i + 1];
                switch (next)
                {
                    // Oniguruma `\h` = onaltılık basamak. .NET'te karşılığı yok.
                    case 'h':
                        output.Append(inCharacterClass ? "0-9a-fA-F" : "[0-9a-fA-F]");
                        break;
                    case 'H':
                        if (inCharacterClass)
                        {
                            throw new GrokCompilationException(
                                @"Karakter sınıfı içinde `\H` desteklenmiyor.", rootExpression, stack);
                        }

                        output.Append("[^0-9a-fA-F]");
                        break;
                    default:
                        output.Append(c).Append(next);
                        break;
                }

                i += 2;
                continue;
            }

            if (inCharacterClass)
            {
                if (c == '[' && TryReadPosixClass(pattern, i, out var posix, out var posixLength))
                {
                    output.Append(posix);
                    i += posixLength;
                    continue;
                }

                if (c == ']')
                {
                    inCharacterClass = false;
                }

                output.Append(c);
                i++;
                continue;
            }

            if (c == '[')
            {
                inCharacterClass = true;
                output.Append(c);
                i++;

                if (i < pattern.Length && pattern[i] == '^')
                {
                    output.Append('^');
                    i++;
                }

                // Sınıfın hemen başındaki ']' literaldir; .NET'te kaçışlamak gerekir.
                if (i < pattern.Length && pattern[i] == ']')
                {
                    output.Append("\\]");
                    i++;
                }

                continue;
            }

            if (c == '%' && i + 1 < pattern.Length && pattern[i + 1] == '{')
            {
                i = ExpandReference(pattern, i, output, captures, stack, ref groupCounter, rootExpression);
                continue;
            }

            if (c == '(' && i + 1 < pattern.Length && pattern[i + 1] == '?')
            {
                if (TryReadNamedGroupHeader(pattern, i, out var rawName, out var headerLength))
                {
                    var field = NormalizeFieldName(rawName);
                    var groupName = NextGroupName(ref groupCounter);
                    captures.Add(new GrokCapture(field, groupName, GrokFieldType.String));
                    output.Append("(?<").Append(groupName).Append('>');
                    i += headerLength;
                    continue;
                }

                output.Append("(?");
                i += 2;
                continue;
            }

            // Oniguruma `X?*` / `X?+` ("isteğe bağlının tekrarı") .NET'te
            // "Nested quantifier" hatası verir. Upstream sette gerçek bir örnek var:
            // SHOREWALL pattern'i `.?*TOS=` yazıyor ve niyeti açıkça `.*`.
            // Pattern dosyasına dokunmuyoruz (bkz. catalog/patterns/README.md),
            // çeviri burada yapılıyor.
            if (c == '?' && i + 1 < pattern.Length && (pattern[i + 1] == '*' || pattern[i + 1] == '+'))
            {
                output.Append('*');
                i += 2;
                continue;
            }

            output.Append(c);
            i++;
        }
    }

    private int ExpandReference(
        string pattern,
        int start,
        StringBuilder output,
        List<GrokCapture> captures,
        List<string> stack,
        ref int groupCounter,
        string rootExpression)
    {
        var close = pattern.IndexOf('}', start + 2);
        if (close < 0)
        {
            throw new GrokCompilationException(
                $"'%{{' kapanmamış (konum {start}).", rootExpression, stack);
        }

        var body = pattern[(start + 2)..close];
        var parts = body.Split(':');
        if (parts.Length > 3)
        {
            throw new GrokCompilationException(
                $"Geçersiz grok referansı '%{{{body}}}'. Beklenen: %{{PATTERN}}, %{{PATTERN:alan}} veya %{{PATTERN:alan:tip}}.",
                rootExpression,
                stack);
        }

        var name = parts[0].Trim();
        if (name.Length == 0)
        {
            throw new GrokCompilationException($"Grok referansında pattern adı boş: '%{{{body}}}'.", rootExpression, stack);
        }

        if (!_library.TryGet(name, out var referenced))
        {
            var suggestion = Suggest(name);
            throw new GrokCompilationException(
                $"Bilinmeyen grok pattern'i: '{name}'." + suggestion, rootExpression, stack);
        }

        if (stack.Contains(name, StringComparer.Ordinal))
        {
            throw new GrokCompilationException(
                $"Özyinelemeli grok pattern'i: '{name}'.", rootExpression, [.. stack, name]);
        }

        var hasField = parts.Length >= 2 && parts[1].Trim().Length > 0;
        string? groupName = null;

        if (hasField)
        {
            var field = NormalizeFieldName(parts[1]);
            var type = parts.Length == 3 ? ParseFieldType(parts[2], body, rootExpression, stack) : GrokFieldType.String;
            groupName = NextGroupName(ref groupCounter);
            captures.Add(new GrokCapture(field, groupName, type));
            output.Append("(?<").Append(groupName).Append('>');
        }
        else
        {
            output.Append("(?:");
        }

        stack.Add(name);
        Expand(referenced, output, captures, stack, ref groupCounter, rootExpression);
        stack.RemoveAt(stack.Count - 1);

        output.Append(')');
        _ = groupName;

        return close + 1;
    }

    private string Suggest(string name)
    {
        var candidates = _library.Names
            .Where(candidate => candidate.Contains(name, StringComparison.OrdinalIgnoreCase)
                || name.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToArray();

        return candidates.Length == 0 ? string.Empty : $" Bunu mu demek istediniz: {string.Join(", ", candidates)}?";
    }

    private static string NextGroupName(ref int counter) => "g" + counter++.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static GrokFieldType ParseFieldType(string raw, string body, string rootExpression, List<string> stack)
    {
        var type = raw.Trim();
        return type switch
        {
            "int" or "integer" => GrokFieldType.Int,
            "long" => GrokFieldType.Long,
            "float" => GrokFieldType.Float,
            "double" => GrokFieldType.Double,
            "bool" or "boolean" => GrokFieldType.Bool,
            "string" or "text" or "" => GrokFieldType.String,
            _ => throw new GrokCompilationException(
                $"Bilinmeyen grok tipi '{type}' ('%{{{body}}}'). Geçerli: int, long, float, double, bool, string.",
                rootExpression,
                stack),
        };
    }

    /// <summary>
    /// Logstash/ECS alan adlarını noktalı biçime indirger:
    /// <c>[source][ip]</c> → <c>source.ip</c>. Grup adı ayrıca üretildiği için
    /// burada .NET grup adı kısıtı yoktur; ad yalnızca alan sözlüğünün anahtarıdır.
    /// </summary>
    internal static string NormalizeFieldName(string raw)
    {
        var name = raw.Trim();
        if (name.Length == 0)
        {
            return name;
        }

        if (name.Contains('[', StringComparison.Ordinal))
        {
            var segments = new List<string>();
            var depth = 0;
            var current = new StringBuilder();

            foreach (var c in name)
            {
                switch (c)
                {
                    case '[':
                        depth++;
                        current.Clear();
                        break;
                    case ']':
                        depth--;
                        if (current.Length > 0)
                        {
                            segments.Add(current.ToString());
                            current.Clear();
                        }

                        break;
                    default:
                        if (depth > 0)
                        {
                            current.Append(c);
                        }

                        break;
                }
            }

            if (segments.Count > 0)
            {
                return string.Join('.', segments);
            }
        }

        // ECS setinde `[a][b][c]?` gibi kuyruğunda artık karakter kalmış adlar var.
        return name.TrimEnd('?', ' ');
    }

    private static bool TryReadNamedGroupHeader(string pattern, int index, out string name, out int length)
    {
        name = string.Empty;
        length = 0;

        // `(?<` veya `(?'` veya `(?P<`
        var cursor = index + 2;
        char terminator;

        if (cursor < pattern.Length && pattern[cursor] == 'P' && cursor + 1 < pattern.Length && pattern[cursor + 1] == '<')
        {
            cursor += 2;
            terminator = '>';
        }
        else if (cursor < pattern.Length && pattern[cursor] == '<')
        {
            // `(?<=` ve `(?<!` geriye bakıştır, adlandırılmış grup değil.
            if (cursor + 1 < pattern.Length && (pattern[cursor + 1] == '=' || pattern[cursor + 1] == '!'))
            {
                return false;
            }

            cursor++;
            terminator = '>';
        }
        else if (cursor < pattern.Length && pattern[cursor] == '\'')
        {
            cursor++;
            terminator = '\'';
        }
        else
        {
            return false;
        }

        var end = pattern.IndexOf(terminator, cursor);
        if (end < 0)
        {
            return false;
        }

        name = pattern[cursor..end];
        length = end - index + 1;

        // Sayısal ad `(?<1>` .NET'te geçerli sayılmaz; boş ad da anlamsız.
        return name.Length > 0;
    }

    private static bool TryReadPosixClass(string pattern, int index, out string replacement, out int length)
    {
        replacement = string.Empty;
        length = 0;

        if (index + 1 >= pattern.Length || pattern[index + 1] != ':')
        {
            return false;
        }

        var end = pattern.IndexOf(":]", index + 2, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        var name = pattern[(index + 2)..end];
        replacement = name switch
        {
            "alnum" => "a-zA-Z0-9",
            "alpha" => "a-zA-Z",
            "blank" => @" \t",
            "cntrl" => @"\x00-\x1f\x7f",
            "digit" => "0-9",
            "graph" => @"\x21-\x7e",
            "lower" => "a-z",
            "print" => @"\x20-\x7e",
            "punct" => @"!-/:-@\[-`{-~",
            "space" => @"\s",
            "upper" => "A-Z",
            "word" => @"\w",
            "xdigit" => "0-9a-fA-F",
            _ => string.Empty,
        };

        if (replacement.Length == 0)
        {
            return false;
        }

        length = end + 2 - index;
        return true;
    }

    /// <summary>Sözlük anahtarı olarak kullanılamayan karakterleri temizler — testler için açık.</summary>
    internal static string SanitizeGroupName(string raw) => GroupNameSanitizer.Replace(raw, "_");
}
