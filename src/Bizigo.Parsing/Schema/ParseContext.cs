using Bizigo.Contracts;

namespace Bizigo.Parsing.Schema;

public sealed record ParseIssue(string Step, string Message);

/// <summary>
/// Boru hattı boyunca taşınan durum. Alan değerleri <see cref="object"/> —
/// grok/convert adımları sayı ve boolean üretebiliyor, sonradan string'e
/// çevirmek tip bilgisini kaybettirir.
/// </summary>
public sealed class ParseContext
{
    /// <summary>Ayrıştırılacak ham satırın oturduğu alan; boru hattının girdisi.</summary>
    public const string MessageField = "message";

    /// <summary><c>date</c> adımının varsayılan hedefi.</summary>
    public const string TimestampField = "@timestamp";

    private readonly List<string> _tags = [];
    private readonly List<ParseIssue> _issues = [];

    public ParseContext(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Input = input;
        Fields = new Dictionary<string, object?>(StringComparer.Ordinal) { [MessageField] = input };
    }

    public string Input { get; }

    public Dictionary<string, object?> Fields { get; }

    public IReadOnlyList<string> Tags => _tags;

    public IReadOnlyList<ParseIssue> Issues => _issues;

    /// <summary>Bir adım <see cref="OnFailure.Fail"/> ile patladı — boru hattı durur.</summary>
    public bool Aborted { get; private set; }

    /// <summary>En az bir adım atlandı — sonuç <c>partial</c>.</summary>
    public bool Degraded { get; private set; }

    /// <summary>Bir grok adımı zaman aşımına uğradı — parser karantina adayı.</summary>
    public bool TimedOut { get; private set; }

    public void AddTag(string tag)
    {
        if (!_tags.Contains(tag, StringComparer.Ordinal))
        {
            _tags.Add(tag);
        }
    }

    public void Abort(string step, string message)
    {
        Aborted = true;
        _issues.Add(new ParseIssue(step, message));
    }

    public void Degrade(string step, string message)
    {
        Degraded = true;
        _issues.Add(new ParseIssue(step, message));
    }

    public void MarkTimedOut() => TimedOut = true;

    public bool TryGetString(string field, out string value)
    {
        value = string.Empty;
        if (!Fields.TryGetValue(field, out var raw) || raw is null)
        {
            return false;
        }

        value = raw as string
            ?? System.Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
        return true;
    }

    public ParseStatus Status => Aborted ? ParseStatus.Failed
        : Degraded ? ParseStatus.Partial
        : ParseStatus.Ok;
}
