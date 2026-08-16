namespace Bizigo.Parsing.Grok;

/// <summary>
/// Grok ifadesi derlenemedi. Mesaj <b>kullanıcıya gösterilir</b> — parser YAML'ını
/// yazan kişi bu metni okuyacak, o yüzden hangi pattern'in nerede patladığını söyler.
/// </summary>
public sealed class GrokCompilationException : Exception
{
    public GrokCompilationException(string message)
        : base(message)
    {
    }

    public GrokCompilationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GrokCompilationException(string message, string expression, IReadOnlyList<string> patternStack)
        : base(Describe(message, expression, patternStack))
    {
        Expression = expression;
        PatternStack = patternStack;
    }

    public string Expression { get; } = string.Empty;

    /// <summary>Genişletme yığını: <c>SYSLOGLINE → SYSLOGBASE → SYSLOGTIMESTAMP</c>.</summary>
    public IReadOnlyList<string> PatternStack { get; } = [];

    private static string Describe(string message, string expression, IReadOnlyList<string> patternStack)
    {
        if (patternStack.Count == 0)
        {
            return $"{message} (ifade: {expression})";
        }

        return $"{message} (ifade: {expression}; genişletme yolu: {string.Join(" → ", patternStack)})";
    }
}
