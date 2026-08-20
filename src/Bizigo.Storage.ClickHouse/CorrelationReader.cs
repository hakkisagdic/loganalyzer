using System.Globalization;
using Bizigo.Contracts;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.Utility;

namespace Bizigo.Storage.ClickHouse;

/// <summary>
/// F3'ün deterministik korelasyonlarının SQL tarafı (T35).
///
/// <para>
/// <b>Sınır bilinçli: SQL toplar, C# karar verir.</b> Kümeleme ve sayım burada
/// (milyonlarca satır, ClickHouse'un işi); Poisson/z-score, eşik ve sıralama
/// çağıran tarafta. Sebebi test edilebilirlik: yanlış hesaplanmış bir z-score
/// hiçbir yerde hata vermez, yalnızca RCA'nın hipotez sıralamasını sessizce
/// bozar — ve onu ClickHouse'un içine gömersek yalnızca canlı veritabanıyla
/// sınanabilir hâle gelir.
/// </para>
///
/// <para>
/// <b>Her sorgu <see cref="ScopePredicate"/> istiyor</b> (K17). Kapsamsız bir
/// korelasyon, bir ekibin başka bir ekibin verisini kanıt olarak görmesi
/// demek — üstelik rapor onu doğru veri gibi sunar.
/// </para>
/// </summary>
public sealed class CorrelationReader(ClickHouseContext context)
{
    /// <summary>
    /// Ortak özniteliğin bakabileceği kolonların <b>izin listesi</b>. Kolon adı
    /// SQL'de parametreleştirilemiyor; tek savunma bu liste.
    ///
    /// <para>
    /// <c>attrs</c> anahtarları bilerek dışarıda: Map üzerinde tüm anahtarlar
    /// için lift hesaplamak pencere başına kardinalitesi bilinmeyen bir tarama
    /// olurdu. Gerekirse açıkça istenen anahtar eklenir — ölçülerek.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> LiftFields =
    [
        "source_id", "host", "vendor", "product", "parser_id", "proto", "action", "outcome",
    ];

    private readonly ClickHouseContext _context = context
        ?? throw new ArgumentNullException(nameof(context));

    private ClickHouseOptions Options => _context.Options;

    /// <summary>
    /// <b>İlk-görülen imza</b> — RCA'nın tek en güçlü sinyali.
    ///
    /// <para>
    /// Tabanda hiç görülmemiş, pencerede beliren imzalar. Anti-join
    /// <b>SQL'de</b>: taban penceresi on binlerce ayrı imza taşıyabilir ve
    /// hepsini C#'a taşımak, sonra bellekte çıkarmak boşuna trafik olurdu.
    /// </para>
    ///
    /// <para>
    /// <c>signature_hash != 0</c> koşulu kritik: <c>0</c> "imza yok" demek
    /// (16 KB maskeleme sınırını aşan satırlar, T29). Elenmezse hepsi tek bir
    /// sahte imzada toplanır ve o küme her pencerede "ilk kez görüldü" gibi
    /// davranır.
    /// </para>
    ///
    /// <para>
    /// T29'dan önce bu sorgu yazılamıyordu: <c>template_id</c> başarılı
    /// olayların yalnızca %1'inde doluydu ve bir imzanın <b>ilk</b> görülüşünde
    /// tanım gereği boştu — yani tam da aranan satırda kimlik yoktu.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<SignatureCount>> GetFirstSeenSignaturesAsync(
        CorrelationWindow window,
        ScopePredicate scope,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (scope.DeniesEverything)
        {
            return [];
        }

        var (where, parameters) = Filter(window, scope);

        var sql = $$"""
            SELECT signature_hash,
                   count() AS event_count,
                   min(ts) AS first_seen_at,
                   uniqExact(source_id) AS source_count,
                   any(body) AS sample_body
            FROM {{Options.EventsTable}}
            WHERE {{where}} AND signature_hash != 0
              AND ts >= {win_from:DateTime64(3)} AND ts < {win_to:DateTime64(3)}
            GROUP BY signature_hash
            HAVING signature_hash NOT IN (
                SELECT signature_hash
                FROM {{Options.EventsTable}}
                WHERE {{where}} AND signature_hash != 0
                  AND ts >= {base_from:DateTime64(3)} AND ts < {base_to:DateTime64(3)}
            )
            ORDER BY event_count DESC, signature_hash
            LIMIT {{Math.Clamp(limit, 1, Options.MaxRowsPerQuery)}}
            """;

        return await ReadAsync(sql, parameters, cancellationToken, reader => new SignatureCount(
            Convert.ToUInt64(reader["signature_hash"], CultureInfo.InvariantCulture),
            Convert.ToInt64(reader["event_count"], CultureInfo.InvariantCulture),
            Utc(reader["first_seen_at"]),
            Convert.ToInt32(reader["source_count"], CultureInfo.InvariantCulture),
            (string)reader["sample_body"]));
    }

    /// <summary>
    /// <b>Hacim sapması</b> — imza başına pencere ve taban sayımları.
    ///
    /// <para>
    /// Tek geçişte koşulu toplama (<c>countIf</c>) ile: iki ayrı sorgu + join
    /// aynı bölümleri iki kez okurdu. İki sayım <b>ham</b> dönüyor; pencere
    /// uzunluğu düzeltmesi ve z-score çağıranda, çünkü orada sınanabiliyor.
    /// </para>
    ///
    /// <para>
    /// T29'dan önce bu sinyal kurulamıyordu: başarılı olaylarda sayılar
    /// gerçeğin %1'iydi (<c>SampleRate</c>) ve Poisson bunun üstüne kurulamaz.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<SignatureVolume>> GetSignatureVolumeAsync(
        CorrelationWindow window,
        ScopePredicate scope,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (scope.DeniesEverything)
        {
            return [];
        }

        var (where, parameters) = Filter(window, scope);

        var sql = $$"""
            SELECT signature_hash,
                   countIf(ts >= {win_from:DateTime64(3)} AND ts < {win_to:DateTime64(3)}) AS window_count,
                   countIf(ts >= {base_from:DateTime64(3)} AND ts < {base_to:DateTime64(3)}) AS baseline_count,
                   any(body) AS sample_body
            FROM {{Options.EventsTable}}
            WHERE {{where}} AND signature_hash != 0
              AND ((ts >= {win_from:DateTime64(3)} AND ts < {win_to:DateTime64(3)})
                OR (ts >= {base_from:DateTime64(3)} AND ts < {base_to:DateTime64(3)}))
            GROUP BY signature_hash
            HAVING window_count > 0
            ORDER BY window_count DESC, signature_hash
            LIMIT {{Math.Clamp(limit, 1, Options.MaxRowsPerQuery)}}
            """;

        return await ReadAsync(sql, parameters, cancellationToken, reader => new SignatureVolume(
            Convert.ToUInt64(reader["signature_hash"], CultureInfo.InvariantCulture),
            Convert.ToInt64(reader["window_count"], CultureInfo.InvariantCulture),
            Convert.ToInt64(reader["baseline_count"], CultureInfo.InvariantCulture),
            (string)reader["sample_body"]));
    }

    /// <summary>
    /// <b>Ortak öznitelik</b> — alan değeri başına pencere ve taban sayımları.
    ///
    /// <para>
    /// "Hepsi aynı switch'in arkasında" sezgisi; lift'in kendisi (oranların
    /// oranı) çağıranda hesaplanıyor.
    /// </para>
    ///
    /// <para>
    /// Alanlar <see cref="LiftFields"/> izin listesinden geliyor ve SQL'e
    /// <b>tek tek</b> gömülüyor. Çağırandan gelen bir dizgi buraya asla
    /// ulaşmıyor.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<FieldValueCount>> GetAttributeLiftAsync(
        CorrelationWindow window,
        ScopePredicate scope,
        IReadOnlyList<string> fields,
        int limitPerField,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(fields);

        if (scope.DeniesEverything || fields.Count == 0)
        {
            return [];
        }

        var unknown = fields.Where(f => !LiftFields.Contains(f, StringComparer.Ordinal)).ToArray();
        if (unknown.Length > 0)
        {
            // Sessizce atlamak, kullanıcının uygulanmayan bir alana bakıldığını
            // sanması demek olurdu.
            throw new ArgumentException(
                $"Ortak öznitelik alanı izin listesinde değil: {string.Join(", ", unknown)}. " +
                $"İzin verilenler: {string.Join(", ", LiftFields)}",
                nameof(fields));
        }

        var (where, parameters) = Filter(window, scope);
        var perField = Math.Clamp(limitPerField, 1, 100);

        var blocks = fields.Select(field => $$"""
            SELECT '{{field}}' AS field, toString({{field}}) AS value,
                   countIf(ts >= {win_from:DateTime64(3)} AND ts < {win_to:DateTime64(3)}) AS window_count,
                   countIf(ts >= {base_from:DateTime64(3)} AND ts < {base_to:DateTime64(3)}) AS baseline_count
            FROM {{Options.EventsTable}}
            WHERE {{where}}
              AND ((ts >= {win_from:DateTime64(3)} AND ts < {win_to:DateTime64(3)})
                OR (ts >= {base_from:DateTime64(3)} AND ts < {base_to:DateTime64(3)}))
            GROUP BY value
            HAVING window_count > 0 AND value != ''
            ORDER BY window_count DESC
            LIMIT {{perField}}
            """);

        var sql = string.Join("\nUNION ALL\n", blocks);

        return await ReadAsync(sql, parameters, cancellationToken, reader => new FieldValueCount(
            (string)reader["field"],
            (string)reader["value"],
            Convert.ToInt64(reader["window_count"], CultureInfo.InvariantCulture),
            Convert.ToInt64(reader["baseline_count"], CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// <b>Yayılma sırası</b> — kaynak başına ilk bozulma anı.
    ///
    /// <para>
    /// "Bozulma"nın tanımı burada ve <b>imzadan bağımsız</b>: ayrıştırması
    /// başarısız olan ya da önem derecesi eşiğin altına inen olay. Syslog
    /// ölçeğinde küçük sayı daha kötü demek, o yüzden karşılaştırma
    /// <c>&lt;=</c>.
    /// </para>
    ///
    /// <para>
    /// <c>unreliable_time_count</c> raporun dürüstlüğü için: zamanı
    /// <c>parsed</c> olmayan bir olayın gerçek zamanı dakikalarca önce olabilir
    /// ve bu sinyalin tamamı <b>sıralama</b>. Kaç olayın böyle olduğunu
    /// söylemeden sıralamayı sunmak, ölçülmemiş bir kesinlik iddia etmek olurdu.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<SourceOnset>> GetPropagationAsync(
        CorrelationWindow window,
        ScopePredicate scope,
        byte severityAtOrBelow,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (scope.DeniesEverything)
        {
            return [];
        }

        var (where, parameters) = Filter(window, scope);
        parameters["severity_max"] = severityAtOrBelow;

        var sql = $$"""
            SELECT owner_group, source_id,
                   min(ts) AS first_degraded_at,
                   count() AS degraded_count,
                   countIf(time_source != 'parsed') AS unreliable_time_count
            FROM {{Options.EventsTable}}
            WHERE {{where}}
              AND ts >= {win_from:DateTime64(3)} AND ts < {win_to:DateTime64(3)}
              AND (toUInt8(parse_status) = 3
                OR (severity_num > 0 AND severity_num <= {severity_max:UInt8}))
            GROUP BY owner_group, source_id
            ORDER BY first_degraded_at ASC, source_id
            LIMIT {{Math.Clamp(limit, 1, Options.MaxRowsPerQuery)}}
            """;

        return await ReadAsync(sql, parameters, cancellationToken, reader => new SourceOnset(
            (string)reader["owner_group"],
            (string)reader["source_id"],
            Utc(reader["first_degraded_at"]),
            Convert.ToInt64(reader["degraded_count"], CultureInfo.InvariantCulture),
            Convert.ToInt64(reader["unreliable_time_count"], CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Ortak <c>WHERE</c> parçası ve parametreleri. Zaman aralıkları burada
    /// <b>eklenmiyor</b>: her sorgu iki pencereyi farklı biçimde kullanıyor
    /// (biri anti-join, biri koşullu toplama), ve tek bir aralık dayatmak
    /// ikisini de bozardı.
    /// </summary>
    private static (string Where, Dictionary<string, object> Parameters) Filter(
        CorrelationWindow window,
        ScopePredicate scope)
    {
        var conditions = new List<string> { scope.ToSqlFragment() };
        var parameters = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["win_from"] = window.From.UtcDateTime,
            ["win_to"] = window.To.UtcDateTime,
            ["base_from"] = window.BaselineFrom.UtcDateTime,
            ["base_to"] = window.BaselineTo.UtcDateTime,
        };

        if (scope.HasParameter)
        {
            parameters["scope_groups"] = scope.ParameterValue;
        }

        if (window.OwnerGroups.Count > 0)
        {
            conditions.Add("owner_group IN ({narrow_groups:Array(String)})");
            parameters["narrow_groups"] = window.OwnerGroups.ToArray();
        }

        if (window.SourceIds.Count > 0)
        {
            conditions.Add("source_id IN ({narrow_sources:Array(String)})");
            parameters["narrow_sources"] = window.SourceIds.ToArray();
        }

        return (string.Join(" AND ", conditions), parameters);
    }

    private async Task<IReadOnlyList<T>> ReadAsync<T>(
        string sql,
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken,
        Func<System.Data.Common.DbDataReader, T> map)
    {
        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = Options.QueryTimeoutSeconds;

        foreach (var (key, value) in parameters)
        {
            command.AddParameter(key, value);
        }

        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static DateTimeOffset Utc(object value) =>
        new(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc));
}
