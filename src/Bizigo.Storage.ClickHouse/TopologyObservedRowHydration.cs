using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace Bizigo.Storage.ClickHouse;

/// <summary>One exact 40-column CH driver projection shared by repair and scoped readers.</summary>
public static class TopologyObservedRowHydration
{
    public static string SelectList { get; } = string.Join(", ",
        TopologyObservedRowDigest.FrozenColumns.Select((column, ordinal) => ordinal switch
        {
            >= 29 and <= 31 => $"toJSONString({column})",
            38 => "toInt64(toUnixTimestamp(ttl_at))",
            _ => column,
        }));

    public static object?[] ReadValues(DbDataReader reader)
    {
        if (reader.FieldCount < TopologyObservedRowDigest.FrozenColumnCount)
            throw new InvalidDataException("Observed physical-row hydration has an incompatible column layout.");
        var values = new object?[TopologyObservedRowDigest.FrozenColumnCount];
        for (var ordinal = 0; ordinal < values.Length; ordinal++)
        {
            var raw = reader.GetValue(ordinal);
            if (raw is null or DBNull) throw new InvalidDataException("Observed physical row contains null.");
            values[ordinal] = ordinal switch
            {
                6 or 39 => checked((byte)Convert.ToInt32(raw, CultureInfo.InvariantCulture)),
                8 => Convert.ToSingle(raw, CultureInfo.InvariantCulture),
                >= 19 and <= 24 => Convert.ToInt64(raw, CultureInfo.InvariantCulture),
                >= 25 and <= 28 or 37 => Convert.ToUInt64(raw, CultureInfo.InvariantCulture),
                >= 29 and <= 31 => JsonSerializer.Deserialize<string[]>(ReadString(raw))
                    ?? throw new InvalidDataException("Observed occurrence vector is null."),
                >= 32 and <= 35 => Convert.ToDecimal(raw, CultureInfo.InvariantCulture),
                38 => Convert.ToInt64(raw, CultureInfo.InvariantCulture),
                _ => ReadString(raw),
            };
        }
        return values;
    }

    private static string ReadString(object value) => value as string
        ?? throw new InvalidDataException("Observed physical-row String has unexpected driver type.");
}
