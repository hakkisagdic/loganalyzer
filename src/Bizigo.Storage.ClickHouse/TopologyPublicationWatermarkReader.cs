using System.Globalization;
using ClickHouse.Driver.Utility;

namespace Bizigo.Storage.ClickHouse;

/// <summary>Reads only the server-committed observed projection watermark.</summary>
public sealed class TopologyPublicationWatermarkReader(ClickHouseContext context)
{
    public async Task<ulong> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT committed_sequence FROM topology_publication_watermark FINAL WHERE id=1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Missing committed topology publication watermark.");
        var value = Convert.ToUInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Ambiguous committed topology publication watermark.");
        return value;
    }
}

/// <summary>Moves the CH committed watermark contiguously; holes never become visible.</summary>
public sealed class TopologyPublicationWatermarkWriter(
    TopologyPublicationWatermarkReader reader,
    ClickHouseContext context)
{
    public async Task CommitAsync(ulong sequence, CancellationToken cancellationToken = default)
    {
        var current = await reader.ReadAsync(cancellationToken);
        if (sequence == current) return;
        if (sequence != checked(current + 1))
            throw new InvalidDataException("Topology publication watermark must advance without gaps.");

        await using var connection = context.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO topology_publication_watermark (id, committed_sequence) VALUES (1,{sequence:UInt64})";
        command.AddParameter("sequence", sequence);
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken) != sequence)
            throw new IOException("Topology publication watermark acknowledgement is unavailable.");
    }
}
