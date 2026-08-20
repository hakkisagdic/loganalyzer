namespace Bizigo.Storage.ClickHouse;

/// <summary>
/// Bir <c>events</c> kolonunun "dolu değil" hâlinin nasıl göründüğü.
///
/// <para>
/// Ayrı bir kavram olmasının sebebi, ClickHouse şemasında <c>NULL</c>
/// olmaması: her kolonun bir <b>varsayılan</b> değeri var ve o değer "bilgi
/// yok" demek. <c>src_ip = '::'</c>, <c>dst_port = 0</c>, <c>action = ''</c> —
/// üçü de aynı şeyi söylüyor ama üç farklı biçimde.
/// </para>
/// </summary>
public enum EventFieldKind
{
    /// <summary>Boş dize = bilgi yok.</summary>
    Text,

    /// <summary><c>0</c> = bilgi yok. OCSF'te <c>0</c> çoğu alanda "Unknown".</summary>
    Number,

    /// <summary><c>::</c> = bilgi yok (<c>EventNormalizer</c> çözülemeyeni oraya düşürüyor).</summary>
    Address,

    /// <summary>Boş <c>Map</c> = bilgi yok. Doluluk anahtar bazında ölçülüyor.</summary>
    Map,

    /// <summary>
    /// Her satırda tanım gereği dolu; doluluk ölçmenin anlamı yok
    /// (<c>ts</c>, <c>event_id</c>, <c>owner_group</c> …).
    /// </summary>
    Always,
}

/// <summary>
/// <c>events</c> kolonu → doluluk ölçüsü. <b>Tek tablo, iki tüketici</b> (T39).
///
/// <para>
/// Alan kapsamı ölçümü aynı soruyu iki yerde soruyor: <c>LogEvent</c>
/// üzerinde (ClickHouse'suz, kataloğun ne üretebildiği) ve <c>events_ocsf</c>
/// üzerinde (gerçekten ne yazılmış). İki taraf "dolu" tanımında ayrışırsa
/// karşılaştırma sessizce yalan söyler: bir kolon bir tarafta dolu, öbüründe
/// boş sayılır ve fark "yazma yolunda kayıp" diye raporlanır. O yüzden tanım
/// burada, tek kopya; iki taraf yalnızca <b>biçimini</b> değiştiriyor —
/// biri C# koşulu, öbürü SQL ifadesi.
/// </para>
/// </summary>
public static class EventFieldKinds
{
    private static readonly Dictionary<string, EventFieldKind> Kinds = new(StringComparer.Ordinal)
    {
        ["ts"] = EventFieldKind.Always,
        ["ingested_at"] = EventFieldKind.Always,
        ["time_source"] = EventFieldKind.Always,
        ["event_id"] = EventFieldKind.Always,
        ["owner_group"] = EventFieldKind.Always,
        ["source_id"] = EventFieldKind.Text,
        ["host"] = EventFieldKind.Text,
        ["vendor"] = EventFieldKind.Text,
        ["product"] = EventFieldKind.Text,
        ["parser_id"] = EventFieldKind.Text,
        ["parser_version"] = EventFieldKind.Text,
        ["parse_status"] = EventFieldKind.Always,
        ["parse_generation"] = EventFieldKind.Always,
        ["encoding_detected"] = EventFieldKind.Text,
        ["template_id"] = EventFieldKind.Text,
        ["signature_hash"] = EventFieldKind.Number,
        ["severity_num"] = EventFieldKind.Number,
        ["ocsf_class_uid"] = EventFieldKind.Number,
        ["ocsf_activity_id"] = EventFieldKind.Number,
        ["src_ip"] = EventFieldKind.Address,
        ["dst_ip"] = EventFieldKind.Address,
        ["src_port"] = EventFieldKind.Number,
        ["dst_port"] = EventFieldKind.Number,
        ["proto"] = EventFieldKind.Text,
        ["action"] = EventFieldKind.Text,
        ["outcome"] = EventFieldKind.Text,
        ["user_name"] = EventFieldKind.Text,
        ["attrs"] = EventFieldKind.Map,
        ["body"] = EventFieldKind.Always,
        ["raw_ref"] = EventFieldKind.Text,
    };

    /// <summary>
    /// <b>Bekçi:</b> yazıcının yazdığı her kolonun burada bir karşılığı var mı.
    ///
    /// <para>
    /// Kolon eklenip burası unutulursa ölçüm o kolonu <b>hiç sormaz</b> ve
    /// tablo tam görünür — bu depoda <c>Produces</c> kapısının elle yazılmış
    /// listesiyle birebir aynı hata sınıfı. Bir bekçinin sessizce atlaması
    /// bekçinin kendisinden tehlikeli.
    /// </para>
    /// </summary>
    /// <returns><c>events</c>'te olup burada olmayan kolonlar.</returns>
    public static IReadOnlyList<string> Unknown() =>
        [.. EventWriter.EventColumns.Where(column => !Kinds.ContainsKey(column))];

    public static IReadOnlyCollection<string> All => Kinds.Keys;

    public static EventFieldKind Of(string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);

        return Kinds.TryGetValue(column, out var kind)
            ? kind
            : throw new ArgumentOutOfRangeException(
                nameof(column),
                column,
                "Bu kolon EventFieldKinds'ta tanımlı değil; alan kapsamı ölçümü onu göremez.");
    }

    /// <summary>Verilen metin değeri "dolu" mu — <c>LogEvent</c> tarafı.</summary>
    public static bool IsPopulated(string column, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return Of(column) switch
        {
            EventFieldKind.Always => true,
            EventFieldKind.Number => value.Length > 0 && value != "0",
            EventFieldKind.Address => value.Length > 0 && value is not ("::" or "0.0.0.0"),
            _ => value.Length > 0,
        };
    }

    /// <summary>
    /// Aynı koşulun SQL hâli. <paramref name="expression"/> görünümdeki
    /// <b>kaynak</b> ifadesi (kolon adı) — takma ad değil, çünkü koşul kolonun
    /// tipine bağlı.
    /// </summary>
    public static string PopulatedSql(string column, string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        return Of(column) switch
        {
            EventFieldKind.Always => "1",
            EventFieldKind.Number => $"{expression} != 0",
            EventFieldKind.Address => $"{expression} != toIPv6('::')",
            EventFieldKind.Map => $"length({expression}) > 0",
            _ => $"{expression} != ''",
        };
    }
}
