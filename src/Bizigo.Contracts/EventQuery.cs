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

/// <summary>
/// Zaman kovalarına bölünmüş olay sayımı (T23 önizlemesi).
///
/// <para>
/// <b>Bu tipin varlık sebebi tek bir tasarım kararı:</b> alarm önizlemesi
/// "eşiği değiştir, sayının nasıl değiştiğini gör" ekranı ve eşik her
/// değiştiğinde ClickHouse'a gitmek K16'nın uyardığı şeyin ta kendisi olurdu —
/// kaydırıcıyı sürükleyen bir kullanıcı saniyede onlarca sorgu üretir. Bunun
/// yerine <b>bir kez</b> histogram alınıyor; eşik karşılaştırması aynı veri
/// üzerinde, sorgusuz yapılıyor.
/// </para>
///
/// <para>
/// Yan kazanç: dönen şey eşikten <b>bağımsız</b>, yani önizleme sonucu
/// önbelleklenebiliyor ve üç kural tipi de aynı yanıttan besleniyor.
/// </para>
/// </summary>
public sealed record EventHistogramQuery
{
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }

    /// <summary>Kova genişliği. Kural tipine göre değerlendirme penceresiyle eşitleniyor.</summary>
    public required int BucketSeconds { get; init; }

    public string? FullText { get; init; }
    public IReadOnlyList<FieldFilter> Filters { get; init; } = [];
    public IReadOnlyList<string> SourceIds { get; init; } = [];

    /// <summary>Kapsam <b>daraltması</b>; kapsamı genişletemez.</summary>
    public IReadOnlyList<string> OwnerGroups { get; init; } = [];

    /// <summary>
    /// Kaynak başına ayrı seri. Sessizlik önizlemesi bunu istiyor: "hangi kaynak
    /// ne kadar sustu" sorusu toplam sayıdan çıkarılamıyor.
    /// </summary>
    public bool GroupBySource { get; init; }
}

/// <param name="SourceId"><see cref="EventHistogramQuery.GroupBySource"/> kapalıysa boş.</param>
public sealed record HistogramBucket(DateTimeOffset Start, string SourceId, long Count);

/// <summary>
/// Kaynak etkinliği sorgusunun penceresi (T21).
///
/// <para>
/// Ayrı bir tip, <see cref="EventQuery"/>'nin kısıtlanmış hâli değil: burada
/// tam metin, filtre ve sayfalama <b>yok</b>. Bu sorgunun tek işi kaynak başına
/// tek satır üretmek ve maliyeti kural sayısından bağımsız tutmak; alan filtresi
/// eklenebilseydi K16'nın uyardığı "tek kötü kural" buradan da girerdi.
/// </para>
/// </summary>
public sealed record SourceActivityWindow
{
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }

    /// <summary>Boşsa kapsamdaki tüm kaynaklar.</summary>
    public IReadOnlyList<string> SourceIds { get; init; } = [];

    /// <summary>Kapsam <b>daraltması</b>; kapsamı genişletemez.</summary>
    public IReadOnlyList<string> OwnerGroups { get; init; } = [];
}

/// <param name="LastEventAt">Olayın kendi zamanı — cihazın saati olabilir.</param>
/// <param name="LastIngestedAt">Bizim aldığımız an. "Susuyor mu" sorusunun cevabı bu.</param>
public sealed record SourceActivityRow(
    string OwnerGroup,
    string SourceId,
    DateTimeOffset LastEventAt,
    DateTimeOffset LastIngestedAt,
    long EventCount);

public sealed record ChangeQuery
{
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public IReadOnlyList<string> OwnerGroups { get; init; } = [];
    public IReadOnlyList<string> TargetIds { get; init; } = [];
    public IReadOnlyList<string> ChangeKinds { get; init; } = [];
    public int Limit { get; init; } = 500;
}
