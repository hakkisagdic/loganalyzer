using System.Globalization;
using System.Net;
using System.Text;
using Bizigo.Contracts;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.Utility;

namespace Bizigo.Storage.ClickHouse;

/// <summary>
/// <c>events</c> okuma yolu. <b>Her</b> metot bir <see cref="ScopePredicate"/>
/// istiyor — kapsamsız okuma yazmak imza değiştirmeyi gerektirir (K17).
/// </summary>
public sealed class EventReader(ClickHouseContext context)
{
    private readonly ClickHouseContext _context = context
        ?? throw new ArgumentNullException(nameof(context));

    private ClickHouseOptions _options => _context.Options;

    /// <summary>
    /// Filtrelenebilir alanların <b>izin listesi</b>. Kolon adı SQL'de
    /// parametreleştirilemediği için tek savunma bu liste. Listede olmayan alan
    /// istisna fırlatır — sessizce yok sayılmaz, yoksa kullanıcı filtresinin
    /// uygulandığını sanır.
    /// </summary>
    private static readonly Dictionary<string, string> FilterableColumns = new(StringComparer.Ordinal)
    {
        ["source_id"] = "String",
        ["host"] = "String",
        ["vendor"] = "String",
        ["product"] = "String",
        ["parser_id"] = "String",
        ["parser_version"] = "String",
        ["template_id"] = "String",
        // "Bu imzadan başka nerede var?" — kanıt satırından ham loga inen yol
        // (RCA raporunun `drilldown_query`'si). Değer ondalık UInt64 olarak
        // veriliyor; kolonun tanımı 0006 göçünde.
        ["signature_hash"] = "UInt64",
        ["proto"] = "String",
        ["action"] = "String",
        ["outcome"] = "String",
        ["user_name"] = "String",
        ["owner_group"] = "String",
        // RCA'nın sık sorusu: "yalnızca cihazın kendi zamanını taşıyan olaylar".
        ["time_source"] = "String",
        ["severity_num"] = "UInt8",
        ["src_port"] = "UInt16",
        ["dst_port"] = "UInt16",
        ["ocsf_class_uid"] = "UInt32",
        ["ocsf_activity_id"] = "UInt16",
        ["src_ip"] = "String",
        ["dst_ip"] = "String",
    };

    /// <summary>
    /// Okuma sütun listesi. <c>internal</c>: replay de aynı listeyi kullanıyor —
    /// iki kopya, bir kolon eklendiği gün sessizce ayrışırdı.
    /// </summary>
    internal const string SelectColumns = """
        ts, ingested_at, time_source, event_id, owner_group, source_id, host, vendor, product,
        parser_id, parser_version, toUInt8(parse_status) AS parse_status_num, parse_generation,
        encoding_detected, template_id, signature_hash, severity_num, ocsf_class_uid, ocsf_activity_id,
        toString(src_ip) AS src_ip_s, toString(dst_ip) AS dst_ip_s, src_port, dst_port,
        proto, action, outcome, user_name,
        mapKeys(attrs) AS attr_keys, mapValues(attrs) AS attr_values,
        body, raw_ref
        """;

    public async Task<EventPage> SearchAsync(
        EventQuery query,
        ScopePredicate scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Kapsam boşsa sunucuya hiç gitmiyoruz. Aynı sonucu "WHERE 0" da verirdi
        // ama boş kapsamda sorgu maliyeti ödemenin anlamı yok.
        if (scope.DeniesEverything)
        {
            return new EventPage([], null, false);
        }

        var limit = Math.Clamp(query.Limit, 1, _options.MaxRowsPerQuery);
        var builder = new QueryBuilder(scope);
        builder.AddTimeRange(query.From, query.To);
        builder.AddScope();
        builder.AddInList("source_id", query.SourceIds);
        builder.AddOwnerGroupNarrowing(query.OwnerGroups);
        builder.AddParseStatuses(query.ParseStatuses);
        builder.AddFullText(query.FullText);

        foreach (var filter in query.Filters)
        {
            builder.AddFieldFilter(filter, FilterableColumns);
        }

        builder.AddKeyset(query.After, query.Ascending);

        var order = query.Ascending ? "ASC" : "DESC";
        var sql = $"""
            SELECT {SelectColumns}
            FROM {_options.EventsTable}
            WHERE {builder.Where}
            ORDER BY ts {order}, event_id {order}
            LIMIT {limit + 1}
            """;

        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _options.QueryTimeoutSeconds;
        builder.Apply(command);

        var results = new List<LogEvent>(limit + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(Map(reader));
            }
        }

        var hasMore = results.Count > limit;
        if (hasMore)
        {
            results.RemoveAt(results.Count - 1);
        }

        var next = hasMore && results.Count > 0
            ? new EventCursor(results[^1].Timestamp, results[^1].EventId)
            : null;

        return new EventPage(results, next, hasMore);
    }

    public async Task<LogEvent?> GetByIdAsync(
        Guid eventId,
        ScopePredicate scope,
        CancellationToken cancellationToken = default)
    {
        if (scope.DeniesEverything)
        {
            return null;
        }

        var builder = new QueryBuilder(scope);
        builder.AddScope();
        builder.AddRaw("event_id = {event_id:UUID}", "event_id", eventId);

        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM {_options.EventsTable} WHERE {builder.Where} LIMIT 1";
        command.CommandTimeout = _options.QueryTimeoutSeconds;
        builder.Apply(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    /// <summary>
    /// Görünüm adı → (tablo, kimlik kolonu). <b>İzin listesi</b>: görünüm adı
    /// SQL'e gömüldüğü için çağırandan gelen bir dizgi asla buraya ulaşmıyor.
    /// </summary>
    private static readonly Dictionary<EventViewKind, (string Table, string IdColumn)> Views = new()
    {
        [EventViewKind.Ocsf] = ("events_ocsf", "uid"),
        [EventViewKind.Otel] = ("events_otel", "LogRecordUID"),
    };

    /// <summary>
    /// Bir olayın OCSF/OTel görünümündeki hâli.
    ///
    /// <para>
    /// <c>SELECT *</c> bilinçli: kolon adları <b>görünümün kendisinden</b>
    /// geliyor. Burada elle yazmak, eşlemenin ikinci bir kopyası olurdu ve
    /// görünüme bir alan eklendiği gün sessizce ayrışırdı (K8).
    /// </para>
    ///
    /// <para>
    /// Kapsam filtresi görünümde değil burada: görünümler <c>owner_group</c>
    /// kolonunu aynen taşıyor, zorlama K17'nin tek kapısında kalıyor.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<EventFieldView>> GetViewAsync(
        Guid eventId,
        EventViewKind view,
        ScopePredicate scope,
        CancellationToken cancellationToken = default)
    {
        if (scope.DeniesEverything)
        {
            return [];
        }

        if (!Views.TryGetValue(view, out var target))
        {
            throw new ArgumentOutOfRangeException(nameof(view), view, "Bilinmeyen görünüm.");
        }

        var builder = new QueryBuilder(scope);
        builder.AddScope();
        builder.AddRaw($"{target.IdColumn} = {{event_id:UUID}}", "event_id", eventId);

        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {target.Table} WHERE {builder.Where} LIMIT 1";
        command.CommandTimeout = _options.QueryTimeoutSeconds;
        builder.Apply(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return [];
        }

        var fields = new List<EventFieldView>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            fields.Add(new EventFieldView(reader.GetName(i), FormatViewValue(reader.GetValue(i))));
        }

        return fields;
    }

    /// <summary>
    /// Görünüm değerini metne çeviriyor.
    ///
    /// <para>
    /// <c>Convert.ToString</c> tek başına yetmiyor: harita kolonu tip adını,
    /// IPv4-eşlemeli adres <c>::ffff:10.1.2.3</c> biçimini verirdi. İkisi de
    /// ekranda okunamaz.
    /// </para>
    /// </summary>
    internal static string FormatViewValue(object? value) => value switch
    {
        null or DBNull => string.Empty,
        string s => s,
        IPAddress ip => FormatAddress(ip),
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            .ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        System.Collections.IDictionary map => string.Join(
            ", ",
            map.Keys.Cast<object>()
                .Select(k => $"{k}={FormatViewValue(map[k])}")
                .Order(StringComparer.Ordinal)),
        System.Collections.IEnumerable list => string.Join(
            ", ",
            list.Cast<object?>().Select(FormatViewValue)),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>
    /// IPv4 adresleri kolonda IPv6-eşlemeli duruyor; kullanıcı <c>10.1.2.3</c>
    /// bekliyor, <c>::ffff:10.1.2.3</c> değil.
    /// </summary>
    internal static string FormatAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();

    public async Task<long> CountAsync(
        EventQuery query,
        ScopePredicate scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (scope.DeniesEverything)
        {
            return 0;
        }

        var builder = new QueryBuilder(scope);
        builder.AddTimeRange(query.From, query.To);
        builder.AddScope();
        builder.AddInList("source_id", query.SourceIds);
        builder.AddOwnerGroupNarrowing(query.OwnerGroups);
        builder.AddParseStatuses(query.ParseStatuses);
        builder.AddFullText(query.FullText);

        foreach (var filter in query.Filters)
        {
            builder.AddFieldFilter(filter, FilterableColumns);
        }

        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count() FROM {_options.EventsTable} WHERE {builder.Where}";
        command.CommandTimeout = _options.QueryTimeoutSeconds;
        builder.Apply(command);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Kapsam dışında kaç eşleşme olduğunu <b>sayar</b> — içeriği döndürmez.
    /// RCA raporundaki "kapsamınız dışında N ilişkili olay var" satırının kaynağı
    /// (RCA özelliği §3.2). Bilgi sızdırmadan yanlış güveni engelliyor.
    /// </summary>
    public async Task<long> CountOutOfScopeAsync(
        EventQuery query,
        ScopePredicate scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (scope.IsUnrestricted)
        {
            return 0;
        }

        var builder = new QueryBuilder(scope);
        builder.AddTimeRange(query.From, query.To);
        builder.AddNegatedScope();
        builder.AddFullText(query.FullText);

        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count() FROM {_options.EventsTable} WHERE {builder.Where}";
        command.CommandTimeout = _options.QueryTimeoutSeconds;
        builder.Apply(command);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Bir kova genişliği için en fazla kaç kova döneceği.
    ///
    /// <para>
    /// Sınır hem sorgunun hem yanıtın maliyeti: 1 saniyelik kovayla 7 günlük bir
    /// pencere 604.800 satır döndürürdü ve bunu isteyen ekran bir kaydırıcıyı
    /// sürükleyen kullanıcı olurdu (K16).
    /// </para>
    /// </summary>
    public const int MaxHistogramBuckets = 720;

    /// <summary>
    /// Zaman kovalarına bölünmüş sayım — alarm önizlemesinin tek sorgusu (T23).
    ///
    /// <para>
    /// Eşik burada <b>uygulanmıyor</b>, bilerek: dönen histogram eşikten bağımsız
    /// olduğu için kullanıcı eşiği değiştirdiğinde yeni bir sorgu gerekmiyor.
    /// Aksi tasarımda kaydırıcıyı sürükleyen tek bir kullanıcı ClickHouse'a
    /// saniyede onlarca sorgu atardı.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<HistogramBucket>> GetHistogramAsync(
        EventHistogramQuery query,
        ScopePredicate scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (scope.DeniesEverything)
        {
            return [];
        }

        var span = query.To - query.From;
        if (span <= TimeSpan.Zero)
        {
            return [];
        }

        // Kova genişliği talep edilenden DAR olamaz, ama sınırı aşmamak için
        // genişleyebilir: kullanıcıya "isteğin reddedildi" demek yerine daha kaba
        // bir çözünürlük vermek, önizleme ekranında doğru davranış.
        var minimum = (int)Math.Ceiling(span.TotalSeconds / MaxHistogramBuckets);
        var bucket = Math.Max(Math.Max(query.BucketSeconds, 1), minimum);

        var builder = new QueryBuilder(scope);
        builder.AddTimeRange(query.From, query.To);
        builder.AddScope();
        builder.AddInList("source_id", query.SourceIds);
        builder.AddOwnerGroupNarrowing(query.OwnerGroups);
        builder.AddFullText(query.FullText);

        foreach (var filter in query.Filters)
        {
            builder.AddFieldFilter(filter, FilterableColumns);
        }

        // `bucket` doğrudan SQL'e giriyor. Güvenli, çünkü bir `int` ve yukarıda
        // sınırlandı — ClickHouse INTERVAL'ı parametre kabul etmiyor, dolayısıyla
        // tek alternatif buydu ve değerin kullanıcı metni OLMAMASI şart.
        var interval = bucket.ToString(CultureInfo.InvariantCulture);
        var source = query.GroupBySource ? "source_id" : "''";

        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT toStartOfInterval(ts, INTERVAL {interval} SECOND) AS bucket_start,
                   {source} AS bucket_source,
                   count() AS bucket_count
            FROM {_options.EventsTable}
            WHERE {builder.Where}
            GROUP BY bucket_start, bucket_source
            ORDER BY bucket_start
            """;
        command.CommandTimeout = _options.QueryTimeoutSeconds;
        builder.Apply(command);

        var rows = new List<HistogramBucket>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new HistogramBucket(
                new DateTimeOffset(DateTime.SpecifyKind((DateTime)reader["bucket_start"], DateTimeKind.Utc)),
                (string)reader["bucket_source"],
                Convert.ToInt64(reader["bucket_count"], CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    /// <summary>
    /// Kaynak başına <b>son görülme</b> ve olay sayısı — <b>tek</b> sorguda, kaynak
    /// sayısından bağımsız (T21).
    ///
    /// <para>
    /// <b>Neden burada ve neden tek:</b> "bu kaynaktan en son ne zaman veri geldi"
    /// sorusunun üç ayrı yerde cevaplanması gerekiyordu — sessizlik alarmı,
    /// envanter listesi ve boru hattı sağlığı. Üç kopya, üç farklı zaman kolonu
    /// seçimi ve üç farklı kapsam davranışı demekti. Tek yüzey var, üçü de bunu
    /// çağırıyor.
    /// </para>
    ///
    /// <para>
    /// <b>İki zaman damgası birden dönüyor, çünkü ikisi farklı soru:</b>
    /// <c>last_event_at</c> olayın kendi zamanı (cihazın saati olabilir),
    /// <c>last_ingested_at</c> bizim onu aldığımız an. Sessizlik "cihazdan haber
    /// aldık mı" sorusu olduğu için <b>ingested</b> olanı kullanmalı; cihaz saati
    /// kayan bir kaynak aksi halde susmuş görünürdü.
    /// </para>
    ///
    /// <para>
    /// ⚠️ Tarama sınırı yine de <c>ts</c> üzerinde: sıralama anahtarı
    /// <c>(owner_group, source_id, ts)</c> ve <c>ingested_at</c> üzerinden
    /// filtrelemek tam tarama olurdu. Bunun bilinen bedeli, saati pencerenin
    /// dışına düşecek kadar yanlış olan bir kaynağın <b>hiç</b> görünmemesi —
    /// yani susmuş sayılması. Bu bir yanlış alarm değil, doğru bir bulgu:
    /// o kaynağın verisi kimsenin bakacağı yerde durmuyor.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<SourceActivityRow>> GetSourceActivityAsync(
        SourceActivityWindow window,
        ScopePredicate scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (scope.DeniesEverything)
        {
            return [];
        }

        var builder = new QueryBuilder(scope);
        builder.AddTimeRange(window.From, window.To);
        builder.AddScope();
        builder.AddInList("source_id", window.SourceIds);
        builder.AddOwnerGroupNarrowing(window.OwnerGroups);

        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT owner_group, source_id,
                   max(ts) AS last_event_at,
                   max(ingested_at) AS last_ingested_at,
                   count() AS event_count
            FROM {_options.EventsTable}
            WHERE {builder.Where}
            GROUP BY owner_group, source_id
            """;
        command.CommandTimeout = _options.QueryTimeoutSeconds;
        builder.Apply(command);

        var rows = new List<SourceActivityRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SourceActivityRow(
                (string)reader["owner_group"],
                (string)reader["source_id"],
                new DateTimeOffset(DateTime.SpecifyKind((DateTime)reader["last_event_at"], DateTimeKind.Utc)),
                new DateTimeOffset(DateTime.SpecifyKind((DateTime)reader["last_ingested_at"], DateTimeKind.Utc)),
                Convert.ToInt64(reader["event_count"], CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    internal static LogEvent Map(System.Data.Common.DbDataReader reader)
    {
        var keys = (string[])reader["attr_keys"];
        var values = (string[])reader["attr_values"];
        var attrs = new Dictionary<string, string>(keys.Length, StringComparer.Ordinal);
        for (var i = 0; i < keys.Length && i < values.Length; i++)
        {
            attrs[keys[i]] = values[i];
        }

        return new LogEvent
        {
            Timestamp = new DateTimeOffset(DateTime.SpecifyKind((DateTime)reader["ts"], DateTimeKind.Utc)),
            IngestedAt = new DateTimeOffset(DateTime.SpecifyKind((DateTime)reader["ingested_at"], DateTimeKind.Utc)),
            TimeSource = (string)reader["time_source"],
            EventId = (Guid)reader["event_id"],
            OwnerGroup = (string)reader["owner_group"],
            SourceId = (string)reader["source_id"],
            Host = (string)reader["host"],
            Vendor = (string)reader["vendor"],
            Product = (string)reader["product"],
            ParserId = (string)reader["parser_id"],
            ParserVersion = (string)reader["parser_version"],
            ParseStatus = (ParseStatus)Convert.ToByte(reader["parse_status_num"], CultureInfo.InvariantCulture),
            ParseGeneration = Convert.ToUInt32(reader["parse_generation"], CultureInfo.InvariantCulture),
            EncodingDetected = (string)reader["encoding_detected"],
            TemplateId = (string)reader["template_id"],
            SignatureHash = Convert.ToUInt64(reader["signature_hash"], CultureInfo.InvariantCulture),
            SeverityNum = Convert.ToByte(reader["severity_num"], CultureInfo.InvariantCulture),
            OcsfClassUid = Convert.ToUInt32(reader["ocsf_class_uid"], CultureInfo.InvariantCulture),
            OcsfActivityId = Convert.ToUInt16(reader["ocsf_activity_id"], CultureInfo.InvariantCulture),
            SrcIp = IPAddress.Parse((string)reader["src_ip_s"]),
            DstIp = IPAddress.Parse((string)reader["dst_ip_s"]),
            SrcPort = Convert.ToUInt16(reader["src_port"], CultureInfo.InvariantCulture),
            DstPort = Convert.ToUInt16(reader["dst_port"], CultureInfo.InvariantCulture),
            Proto = (string)reader["proto"],
            Action = (string)reader["action"],
            Outcome = (string)reader["outcome"],
            UserName = (string)reader["user_name"],
            Attrs = attrs,
            Body = (string)reader["body"],
            RawRef = (string)reader["raw_ref"],
        };
    }

    /// <summary>
    /// WHERE parçalarını ve parametreleri biriktirir. Kolon adları izin listesinden,
    /// değerler <b>daima</b> parametre — string birleştirmeyle değer gömülmüyor.
    /// </summary>
    private sealed class QueryBuilder(ScopePredicate scope)
    {
        private readonly List<string> _conditions = [];
        private readonly Dictionary<string, object> _parameters = new(StringComparer.Ordinal);
        private int _counter;

        public string Where => _conditions.Count == 0 ? "1" : string.Join(" AND ", _conditions);

        public void AddScope()
        {
            _conditions.Add(scope.ToSqlFragment());
            if (scope.HasParameter)
            {
                _parameters["scope_groups"] = scope.ParameterValue;
            }
        }

        public void AddNegatedScope()
        {
            if (scope.DeniesEverything)
            {
                _conditions.Add("1");
                return;
            }

            _conditions.Add("NOT (" + scope.ToSqlFragment() + ")");
            if (scope.HasParameter)
            {
                _parameters["scope_groups"] = scope.ParameterValue;
            }
        }

        public void AddTimeRange(DateTimeOffset from, DateTimeOffset to)
        {
            _conditions.Add("ts >= {ts_from:DateTime64(3)} AND ts < {ts_to:DateTime64(3)}");
            _parameters["ts_from"] = from.UtcDateTime;
            _parameters["ts_to"] = to.UtcDateTime;
        }

        public void AddRaw(string condition, string parameterName, object value)
        {
            _conditions.Add(condition);
            _parameters[parameterName] = value;
        }

        public void AddInList(string column, IReadOnlyList<string> values)
        {
            if (values.Count == 0)
            {
                return;
            }

            var name = Next(column);
            _conditions.Add($"{column} IN ({{{name}:Array(String)}})");
            _parameters[name] = values.ToArray();
        }

        /// <summary>
        /// Kullanıcının istediği grup daraltması. <see cref="ScopePredicate.From"/>
        /// zaten kesişimi almış olabilir; bu ek koşul yalnızca daraltmayı uygular ve
        /// kapsamı <b>genişletemez</b> çünkü scope koşulu ayrıca AND'leniyor.
        /// </summary>
        public void AddOwnerGroupNarrowing(IReadOnlyList<string> groups) => AddInList("owner_group", groups);

        public void AddParseStatuses(IReadOnlyList<ParseStatus> statuses)
        {
            if (statuses.Count == 0)
            {
                return;
            }

            var name = Next("parse_status");
            _conditions.Add($"toUInt8(parse_status) IN ({{{name}:Array(UInt8)}})");
            _parameters[name] = statuses.Select(s => (byte)s).ToArray();
        }

        /// <summary>
        /// Tam metin. <c>sparseGrams</c> indeksi alt dizi aramasını hızlandırıyor.
        ///
        /// ⚠️ Büyük/küçük harf DUYARLI. İndekste <c>preprocessor = lowerUTF8()</c>
        /// kullanılmadı çünkü lowerUTF8 Türkçe İ/ı'da bayt uzunluğu değiştiği için
        /// hatalı sonuç verebiliyor ve skip index'te bu <b>yanlış negatif</b> demek.
        /// Duyarsız arama kararı ölçümle verilecek (F1 §15 kalem 4).
        /// </summary>
        public void AddFullText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var name = Next("ft");
            _conditions.Add($"positionUTF8(body, {{{name}:String}}) > 0");
            _parameters[name] = text;
        }

        public void AddFieldFilter(FieldFilter filter, IReadOnlyDictionary<string, string> allowed)
        {
            ArgumentNullException.ThrowIfNull(filter);

            if (!allowed.TryGetValue(filter.Field, out var clickHouseType))
            {
                throw new ArgumentException(
                    $"'{filter.Field}' filtrelenebilir alan değil. İzin verilenler: " +
                    string.Join(", ", allowed.Keys.Order(StringComparer.Ordinal)),
                    nameof(filter));
            }

            if (filter.Values.Count == 0)
            {
                throw new ArgumentException($"'{filter.Field}' filtresi değersiz.", nameof(filter));
            }

            // IP kolonları String olarak filtreleniyor; toString(...) ile karşılaştırılır.
            var column = filter.Field is "src_ip" or "dst_ip" ? $"toString({filter.Field})" : filter.Field;
            var name = Next(filter.Field);

            switch (filter.Operator)
            {
                case FilterOperator.Equals:
                    _conditions.Add($"{column} = {{{name}:{clickHouseType}}}");
                    _parameters[name] = filter.Values[0];
                    break;
                case FilterOperator.NotEquals:
                    _conditions.Add($"{column} != {{{name}:{clickHouseType}}}");
                    _parameters[name] = filter.Values[0];
                    break;
                case FilterOperator.In:
                    _conditions.Add($"{column} IN ({{{name}:Array({clickHouseType})}})");
                    _parameters[name] = filter.Values.ToArray();
                    break;
                case FilterOperator.GreaterThan:
                    _conditions.Add($"{column} > {{{name}:{clickHouseType}}}");
                    _parameters[name] = filter.Values[0];
                    break;
                case FilterOperator.LessThan:
                    _conditions.Add($"{column} < {{{name}:{clickHouseType}}}");
                    _parameters[name] = filter.Values[0];
                    break;
                case FilterOperator.Contains:
                    _conditions.Add($"positionUTF8({column}, {{{name}:String}}) > 0");
                    _parameters[name] = filter.Values[0];
                    break;
                case FilterOperator.StartsWith:
                    _conditions.Add($"startsWith({column}, {{{name}:String}})");
                    _parameters[name] = filter.Values[0];
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(filter), filter.Operator, "Bilinmeyen operatör.");
            }
        }

        public void AddKeyset(EventCursor? cursor, bool ascending)
        {
            if (cursor is null)
            {
                return;
            }

            // (ts, event_id) demeti üzerinde keyset — offset derin sayfalarda çöker.
            var op = ascending ? ">" : "<";
            _conditions.Add(
                $"(ts, event_id) {op} ({{cursor_ts:DateTime64(3)}}, {{cursor_id:UUID}})");
            _parameters["cursor_ts"] = cursor.Timestamp.UtcDateTime;
            _parameters["cursor_id"] = cursor.EventId;
        }

        public void Apply(ClickHouseCommand command)
        {
            foreach (var (key, value) in _parameters)
            {
                command.AddParameter(key, value);
            }
        }

        private string Next(string prefix)
        {
            var sanitized = new StringBuilder(prefix.Length);
            foreach (var c in prefix)
            {
                sanitized.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
            }

            return string.Create(CultureInfo.InvariantCulture, $"p_{sanitized}_{_counter++}");
        }
    }
}
