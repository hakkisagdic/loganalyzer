namespace Bizigo.Contracts;

public enum FilterOperator
{
    Equals,
    NotEquals,
    In,
    GreaterThan,
    LessThan,
    Contains,
    StartsWith,
}

/// <summary>
/// Tek bir alan filtresi. <b>Serbest SQL yok</b> — kapsam zorlaması (K17) yalnızca
/// sorgu API'sinde uygulandığı için, ham SQL kabul eden bir uç kapsam ayrımını
/// arka kapıdan deler (F1 §10.2, T10).
/// </summary>
public sealed record FieldFilter(string Field, FilterOperator Operator, IReadOnlyList<string> Values)
{
    public static FieldFilter Eq(string field, string value) =>
        new(field, FilterOperator.Equals, [value]);

    public static FieldFilter In(string field, params string[] values) =>
        new(field, FilterOperator.In, values);
}

/// <summary>Keyset sayfalama imleci. Offset kullanılmaz — derin sayfalarda çöker.</summary>
public sealed record EventCursor(DateTimeOffset Timestamp, Guid EventId);

public sealed record EventQuery
{
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }

    /// <summary>Tam metin araması (sparseGrams indeksi üzerinden).</summary>
    public string? FullText { get; init; }

    public IReadOnlyList<FieldFilter> Filters { get; init; } = [];

    /// <summary>Kapsam <b>daraltması</b>. Kullanıcının kapsamını genişletemez.</summary>
    public IReadOnlyList<string> OwnerGroups { get; init; } = [];

    public IReadOnlyList<string> SourceIds { get; init; } = [];
    public IReadOnlyList<ParseStatus> ParseStatuses { get; init; } = [];

    public EventCursor? After { get; init; }
    public int Limit { get; init; } = 200;
    public bool Ascending { get; init; }
}

public sealed record EventPage(IReadOnlyList<LogEvent> Events, EventCursor? Next, bool HasMore);

public sealed record ChangeQuery
{
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public IReadOnlyList<string> OwnerGroups { get; init; } = [];
    public IReadOnlyList<string> TargetIds { get; init; } = [];
    public IReadOnlyList<string> ChangeKinds { get; init; } = [];
    public int Limit { get; init; } = 500;
}
