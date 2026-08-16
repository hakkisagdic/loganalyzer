using ClickHouse.Driver;
using ClickHouse.Driver.ADO;

namespace Bizigo.Storage.ClickHouse;

/// <summary>
/// Tek bir uzun ömürlü <see cref="ClickHouseClient"/> tutar; bağlantılar ondan
/// türetilir.
///
/// Neden: <c>new ClickHouseConnection(connStr)</c> her çağrıda kendi
/// <c>HttpClient</c>'ını kuruyor. Ingest hızında bu soket tükenmesine yol açar —
/// klasik ve teşhisi zor bir arıza. İstemci paylaşıldığında bağlantı havuzu da
/// paylaşılıyor.
///
/// <b>Bu tip bu derlemenin dışına sızmaz.</b> Mimari test (T02) bunu zorluyor.
/// </summary>
public sealed class ClickHouseContext : IDisposable
{
    private readonly ClickHouseClient _client;

    public ClickHouseContext(ClickHouseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _client = new ClickHouseClient(options.ConnectionString);
    }

    public ClickHouseOptions Options { get; }

    internal ClickHouseClient Client => _client;

    internal ClickHouseConnection CreateConnection() => _client.CreateConnection();

    public Task<bool> PingAsync(CancellationToken cancellationToken = default) =>
        _client.PingAsync(null, cancellationToken);

    public void Dispose() => _client.Dispose();
}
