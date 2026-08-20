using System.Globalization;

namespace Bizigo.Storage.ClickHouse;

/// <param name="Vendor"><c>device_vendor_name</c> değeri.</param>
/// <param name="Rows">O vendor'ın satır sayısı.</param>
/// <param name="Populated">Görünüm takma adı → dolu satır sayısı.</param>
/// <param name="AttributeKeys"><c>unmapped</c> anahtarı → o anahtarı taşıyan satır sayısı.</param>
public sealed record VendorFieldCoverage(
    string Vendor,
    long Rows,
    IReadOnlyDictionary<string, long> Populated,
    IReadOnlyDictionary<string, long> AttributeKeys);

/// <summary>
/// <c>events_ocsf</c>'te <b>gerçekten</b> ne yazılı olduğunu sayar (T39).
///
/// <para>
/// Ölçümün ClickHouse'suz yarısı kataloğun <i>ne üretebildiğini</i> söylüyor;
/// burası yazma ve görünüm yolundan sonra <i>ne kaldığını</i>. İkisinin farkı
/// tek başına görünmeyen bir hata sınıfını yakalıyor: <c>LogEvent</c> alanı
/// dolduruyor ama kolon boş görünüyor. Böyle bir kayıp hata vermez, yalnızca
/// o alana vuran her Sigma kuralını sessizce sonuçsuz bırakır.
/// </para>
///
/// <para>
/// Kapsam grubu <b>zorunlu</b>: ölçüm yükleyicinin yazdığı satırları soruyor,
/// tablonun tamamını değil. Yanında duran kıyaslama verisi başka bir turdan ve
/// onu saymak oranları sessizce sulandırırdı.
/// </para>
/// </summary>
public sealed class FieldCoverageReader(ClickHouseContext context)
{
    private readonly ClickHouseContext _context = context
        ?? throw new ArgumentNullException(nameof(context));

    /// <param name="columns">
    /// Görünümün <c>kaynak → takma ad</c> çiftleri. Görünüm dosyasından
    /// okunuyor, elle yazılmıyor: elle yazılmış bir liste görünüme kolon
    /// eklendiğinde onu <b>hiç sormaz</b> ve tablo tam görünür.
    /// </param>
    public async Task<IReadOnlyList<VendorFieldCoverage>> ReadAsync(
        string ownerGroup,
        IReadOnlyList<(string Source, string Alias)> columns,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerGroup);
        ArgumentNullException.ThrowIfNull(columns);

        var scope = $"owner_group = {Literal(ownerGroup)}";
        var vendors = await VendorsAsync(scope, cancellationToken);
        var result = new List<VendorFieldCoverage>(vendors.Count);

        foreach (var (vendor, rows) in vendors)
        {
            var filter = $"{scope} AND device_vendor_name = {Literal(vendor)}";

            result.Add(new VendorFieldCoverage(
                vendor,
                rows,
                await PopulatedAsync(filter, columns, cancellationToken),
                await AttributeKeysAsync(filter, cancellationToken)));
        }

        return result;
    }

    private async Task<List<(string Vendor, long Rows)>> VendorsAsync(
        string scope, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(
            $"SELECT device_vendor_name, count() FROM events_ocsf WHERE {scope} GROUP BY device_vendor_name ORDER BY device_vendor_name",
            cancellationToken);

        return [.. rows.Select(static row => (row[0], long.Parse(row[1], CultureInfo.InvariantCulture)))];
    }

    /// <summary>
    /// Tek sorguda bütün kolonlar: kolon başına ayrı sorgu, otuz kolon × dört
    /// vendor = 120 tur demekti ve her turun kendi tarama maliyeti var.
    /// </summary>
    private async Task<Dictionary<string, long>> PopulatedAsync(
        string filter,
        IReadOnlyList<(string Source, string Alias)> columns,
        CancellationToken cancellationToken)
    {
        var measurable = columns
            .Where(column => EventFieldKinds.Of(column.Source) != EventFieldKind.Always)
            .ToList();

        if (measurable.Count == 0)
        {
            return [];
        }

        var projection = string.Join(
            ", ",
            measurable.Select(column =>
                $"countIf({EventFieldKinds.PopulatedSql(column.Source, column.Alias)})"));

        var rows = await QueryAsync(
            $"SELECT {projection} FROM events_ocsf WHERE {filter}", cancellationToken);

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        if (rows.Count == 0)
        {
            return counts;
        }

        for (var i = 0; i < measurable.Count; i++)
        {
            counts[measurable[i].Alias] = long.Parse(rows[0][i], CultureInfo.InvariantCulture);
        }

        return counts;
    }

    /// <summary>
    /// <c>unmapped</c> (yani <c>attrs</c>) anahtarlarının envanteri. Boş bir
    /// OCSF kolonunun karşılığı çoğu zaman buradadır: bilgi geldi, OCSF adıyla
    /// değil.
    /// </summary>
    private async Task<Dictionary<string, long>> AttributeKeysAsync(
        string filter, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(
            "SELECT arrayJoin(mapKeys(unmapped)) AS bizigo_key, count() " +
            $"FROM events_ocsf WHERE {filter} GROUP BY bizigo_key ORDER BY bizigo_key",
            cancellationToken);

        var keys = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            keys[row[0]] = long.Parse(row[1], CultureInfo.InvariantCulture);
        }

        return keys;
    }

    private async Task<List<string[]>> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var rows = new List<string[]>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new string[reader.FieldCount];

            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = reader.IsDBNull(i)
                    ? string.Empty
                    : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
            }

            rows.Add(row);
        }

        return rows;
    }

    private static string Literal(string value) =>
        "'" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
        + "'";
}
