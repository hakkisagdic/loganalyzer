namespace Bizigo.Parsing.Grok;

public enum RedosSeverity
{
    /// <summary>Bulgu var ama pattern doğrusal motorda koşuyor — felç imkânsız.</summary>
    Info = 0,

    /// <summary>Geri izlemeli motorda, ama zaman aşımı koruması altında.</summary>
    Warning,

    /// <summary>Geri izlemeli motorda ve katastrofik geri izleme deseni içeriyor.</summary>
    Error,
}

public sealed record RedosFinding(RedosSeverity Severity, string Code, string Message, int Position, string Fragment);

/// <summary>
/// Derleme zamanı ReDoS linter'ı (F1 §4.1 kademe 2).
///
/// <para>
/// Kural, göründüğünden daha keskin: bir pattern <c>NonBacktracking</c> ile
/// derlenebildiyse zaten doğrusaldır ve bulgular <b>bilgi</b> düzeyine iner.
/// Asıl risk yalnızca geri izlemeli motora düşen pattern'lerde vardır. Bu ayrım
/// olmadan linter ya çok gürültülü ya da yanlış güven verici olur.
/// </para>
/// </summary>
public static class RedosLinter
{
    public static IReadOnlyList<RedosFinding> Inspect(CompiledGrok grok)
    {
        ArgumentNullException.ThrowIfNull(grok);

        var findings = new List<RedosFinding>();
        var source = grok.RegexSource;

        foreach (var (position, fragment) in FindNestedQuantifiers(source))
        {
            findings.Add(new RedosFinding(
                grok.IsLinearTime ? RedosSeverity.Info : RedosSeverity.Error,
                "GROK001",
                "İç içe sınırsız niceleyici — katastrofik geri izleme deseni ((a+)+ ailesi). " +
                (grok.IsLinearTime
                    ? "Bu pattern doğrusal motorda koştuğu için tehlikeli değil, yine de basitleştirilebilir."
                    : "Bu pattern geri izlemeli motorda koşuyor; girdi uzunluğunda üstel yavaşlama mümkün."),
                position,
                fragment));
        }

        foreach (var (position, fragment) in FindAdjacentGreedyWildcards(source))
        {
            findings.Add(new RedosFinding(
                grok.IsLinearTime ? RedosSeverity.Info : RedosSeverity.Warning,
                "GROK002",
                "Yan yana iki sınırsız açgözlü joker (.* .*). Eşleşmeyen girdide ikinci dereceden yavaşlar; " +
                "genellikle aradaki ayraç yazılmayı unutmuştur.",
                position,
                fragment));
        }

        if (!grok.IsLinearTime)
        {
            findings.Add(new RedosFinding(
                RedosSeverity.Warning,
                "GROK003",
                // KARANTİNA CÜMLESİ BİLEREK YOK. Eskiden "50 ms zaman aşımı ve
                // karantina devrede" yazıyordu; karantina hiçbir yerde sıcak yola
                // bağlı değil, yani bir bekçi operatöre yanlış söylüyordu (T05
                // ölçümü). Yerine "karantina devrede DEĞİL" yazmak da doğru
                // olmazdı: bir linter bulgusu, olmayan bir mekanizmanın
                // yokluğunu duyurmak için değil — ve karantina bağlandığı gün o
                // cümle yeniden yanlışlaşırdı. En az iddia eden hâl en
                // dayanıklısı: mesaj yalnızca bildiği şeyi söylüyor.
                "Pattern doğrusal motorla derlenemedi, geri izlemeli motora düşüldü " +
                $"(neden: {grok.FallbackReason}). 50 ms zaman aşımı devrede, " +
                "ama sıcak yolda tercih edilen bu değil.",
                0,
                Truncate(grok.Expression)));
        }

        return findings;
    }

    /// <summary>
    /// <c>(...)*</c> biçimindeki bir grubun gövdesi de sınırsız niceleyiciyle bitiyorsa
    /// katastrofik geri izleme adayıdır. Atomik gruplar <c>(?&gt;...)</c> muaftır —
    /// geri izleme zaten yasaklanmıştır.
    /// </summary>
    private static IEnumerable<(int Position, string Fragment)> FindNestedQuantifiers(string source)
    {
        var stack = new Stack<int>();
        var inClass = false;

        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];

            if (c == '\\')
            {
                i++;
                continue;
            }

            if (inClass)
            {
                if (c == ']')
                {
                    inClass = false;
                }

                continue;
            }

            switch (c)
            {
                case '[':
                    inClass = true;
                    break;
                case '(':
                    stack.Push(i);
                    break;
                case ')' when stack.Count > 0:
                {
                    var open = stack.Pop();
                    if (!IsUnboundedQuantifierAt(source, i + 1, out _))
                    {
                        break;
                    }

                    var isAtomic = open + 2 < source.Length && source[open + 1] == '?' && source[open + 2] == '>';
                    if (isAtomic)
                    {
                        break;
                    }

                    var body = source[(open + 1)..i];
                    if (BodyEndsWithUnboundedQuantifier(body))
                    {
                        yield return (open, Truncate(source[open..Math.Min(source.Length, i + 3)]));
                    }

                    break;
                }
            }
        }
    }

    private static bool BodyEndsWithUnboundedQuantifier(string body)
    {
        var trimmed = body.TrimEnd('?');           // tembel niceleyici de geri izler
        if (trimmed.Length == 0)
        {
            return false;
        }

        var last = trimmed[^1];
        if (last is '*' or '+')
        {
            return trimmed.Length < 2 || trimmed[^2] != '\\';
        }

        if (last == '}')
        {
            var open = trimmed.LastIndexOf('{');
            if (open >= 0)
            {
                var inner = trimmed[(open + 1)..^1];
                return inner.EndsWith(',') || inner.Contains(",}", StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static IEnumerable<(int Position, string Fragment)> FindAdjacentGreedyWildcards(string source)
    {
        var index = source.IndexOf(".*.*", StringComparison.Ordinal);
        while (index >= 0)
        {
            yield return (index, ".*.*");
            index = source.IndexOf(".*.*", index + 1, StringComparison.Ordinal);
        }
    }

    private static bool IsUnboundedQuantifierAt(string source, int index, out int length)
    {
        length = 0;
        if (index >= source.Length)
        {
            return false;
        }

        if (source[index] is '*' or '+')
        {
            length = 1;
            return true;
        }

        if (source[index] != '{')
        {
            return false;
        }

        var close = source.IndexOf('}', index);
        if (close < 0)
        {
            return false;
        }

        var inner = source[(index + 1)..close];
        if (!inner.EndsWith(',') && !inner.Contains(",}", StringComparison.Ordinal))
        {
            return false;
        }

        length = close - index + 1;
        return true;
    }

    private static string Truncate(string value) =>
        value.Length <= 80 ? value : value[..77] + "...";
}
