using System.Globalization;
using Bizigo.Contracts;
using ClickHouse.Driver.Utility;

namespace Bizigo.Storage.ClickHouse;

/// <param name="Partition">ClickHouse bölüm kimliği (<c>toYYYYMMDD(ts)</c>).</param>
/// <param name="Rows">Bölümdeki satır sayısı.</param>
public sealed record PartitionInfo(string Partition, long Rows);

/// <summary>
/// Replay'in ClickHouse tarafı: gölge tablo, bölüm değiştirme, bölüm okuma
/// (F1 §7.2).
///
/// <para>
/// <b>Neden <c>REPLACE PARTITION</c>:</b> atomik ve sorgu tarafında sıfır maliyet.
/// Değerlendirilen alternatif <c>ReplacingMergeTree(parse_generation)</c> satır
/// granülerliğinde şıktı ama <b>her sorguya</b> <c>FINAL</c> maliyeti bindirirdi
/// — replay yılda birkaç kez, sorgu saniyede binlerce.
/// </para>
///
/// <para>
/// Granülerlik bir gün. Replay zaten "parser düzeldi, geçmişi yeniden işle"
/// işidir; gün altı bir çözünürlüğe ihtiyaç duyan bir senaryo yok.
/// </para>
/// </summary>
public sealed class ReplayStore(ClickHouseContext context)
{
    private readonly ClickHouseContext _context = context
        ?? throw new ArgumentNullException(nameof(context));

    /// <summary>Aralıktaki bölümler ve satır sayıları.</summary>
    public async Task<IReadOnlyList<PartitionInfo>> ListPartitionsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT toString(toYYYYMMDD(ts)) AS part, count() AS rows
            FROM {{{_context.Options.EventsTable}}}
            WHERE ts >= {from:DateTime64(3)} AND ts < {to:DateTime64(3)}
            GROUP BY part
            ORDER BY part
            """;
        command.AddParameter("from", from.UtcDateTime);
        command.AddParameter("to", to.UtcDateTime);

        var result = new List<PartitionInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PartitionInfo(reader.GetString(0), Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)));
        }

        return result;
    }

    /// <summary>
    /// <c>events</c> ile <b>aynı yapıda</b> boş gölge tablo. <c>CREATE TABLE … AS</c>
    /// kullanılıyor: şema elle tekrarlanırsa bir kolon eklendiği gün replay
    /// sessizce eksik yazar.
    /// </summary>
    public async Task CreateShadowAsync(string shadowTable, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
            $"CREATE TABLE IF NOT EXISTS {Quote(shadowTable)} AS {_context.Options.EventsTable}",
            cancellationToken);
    }

    public Task DropShadowAsync(string shadowTable, CancellationToken cancellationToken = default) =>
        ExecuteAsync($"DROP TABLE IF EXISTS {Quote(shadowTable)}", cancellationToken);

    /// <summary>
    /// Gölge tablodaki bölümü canlı tabloya <b>atomik</b> olarak taşır.
    ///
    /// <para>
    /// Bu tek ifade replay'in "canlı ingest bozulmuyor" iddiasının dayanağı:
    /// bölüm ya tamamen eski ya tamamen yeni; ara durum yok, sorgu yarım veri
    /// görmüyor.
    /// </para>
    /// </summary>
    public Task ReplacePartitionAsync(
        string shadowTable,
        string partition,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            $"ALTER TABLE {_context.Options.EventsTable} REPLACE PARTITION {Literal(partition)} FROM {Quote(shadowTable)}",
            cancellationToken);

    /// <summary>
    /// Bölümdeki satırları okur. Filtreli replay'de <b>filtre dışı</b> satırları
    /// gölge tabloya değiştirmeden kopyalamak için — <c>REPLACE PARTITION</c>
    /// bölümün tamamını değiştirdiği için kopyalanmayan satır <b>kaybolur</b>.
    /// </summary>
    public async Task<IReadOnlyList<LogEvent>> ReadPartitionAsync(
        string partition,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {EventReader.SelectColumns}
            FROM {_context.Options.EventsTable}
            WHERE toYYYYMMDD(ts) = {Literal(partition)}
            """;

        var events = new List<LogEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(EventReader.Map(reader));
        }

        return events;
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Tablo/bölüm adları parametreleştirilemiyor (ClickHouse tanımlayıcıyı
    /// parametre olarak almıyor), o yüzden <b>beyaz listeleniyor</b>: yalnızca
    /// harf, rakam ve alt çizgi. Replay adları zaten bizim ürettiğimiz değerler.
    /// </summary>
    private static string Quote(string identifier)
    {
        foreach (var c in identifier)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                throw new ArgumentException($"Geçersiz tablo adı: '{identifier}'.", nameof(identifier));
            }
        }

        return identifier;
    }

    private static string Literal(string partition)
    {
        foreach (var c in partition)
        {
            if (!char.IsAsciiDigit(c))
            {
                throw new ArgumentException($"Geçersiz bölüm: '{partition}'.", nameof(partition));
            }
        }

        return partition;
    }
}
