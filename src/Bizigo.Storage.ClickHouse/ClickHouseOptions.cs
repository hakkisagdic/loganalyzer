namespace Bizigo.Storage.ClickHouse;

public sealed class ClickHouseOptions
{
    public const string SectionName = "ClickHouse";

    public string ConnectionString { get; set; } =
        "Host=localhost;Port=8123;Database=bizigo;Username=bizigo;Password=bizigo";

    public string MigrationsDirectory { get; set; } = "db/clickhouse";

    public string EventsTable { get; set; } = "events";

    public string ChangeEventsTable { get; set; } = "change_events";

    /// <summary>Toplu yazım eşiği (F1 §6: 10k satır / 2 sn, hangisi önce).</summary>
    public int BulkBatchSize { get; set; } = 100_000;

    public int BulkParallelism { get; set; } = 2;

    /// <summary>Tek sorgunun sunucu tarafı süre tavanı — gürültülü komşuya karşı (risk #6).</summary>
    public int QueryTimeoutSeconds { get; set; } = 60;

    /// <summary>Bir sorgunun döndürebileceği en fazla satır.</summary>
    public int MaxRowsPerQuery { get; set; } = 10_000;
}
