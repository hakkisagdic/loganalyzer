using System.Globalization;
using Amazon.Runtime;
using Bizigo.Storage.ClickHouse;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;

namespace Bizigo.IntegrationTests;

/// <summary>
/// F1 geliştirme yığınının test karşılığı: ClickHouse + PostgreSQL + RustFS.
/// Sürümler <c>deploy/docker-compose.yml</c> ile aynı — test ile geliştirme ortamı
/// ayrışırsa testin değeri düşer.
/// </summary>
public sealed class DevStackFixture : IAsyncLifetime
{
    public const string ClickHouseImage = "clickhouse/clickhouse-server:26.7";
    public const string PostgresImage = "postgres:18-alpine";
    public const string RustFsImage = "rustfs/rustfs:1.0.0-rc.1";

    private const string S3AccessKey = "bizigoadmin";
    private const string S3SecretKey = "bizigoadmin";

    private readonly IContainer _clickHouse = new ContainerBuilder(ClickHouseImage)
        .WithEnvironment("CLICKHOUSE_DB", "bizigo")
        .WithEnvironment("CLICKHOUSE_USER", "bizigo")
        .WithEnvironment("CLICKHOUSE_PASSWORD", "bizigo")
        .WithEnvironment("CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", "1")
        .WithPortBinding(8123, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(8123).ForPath("/ping")))
        .Build();

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgresImage)
        .WithDatabase("bizigo")
        .WithUsername("bizigo")
        .WithPassword("bizigo")
        .Build();

    private readonly IContainer _rustFs = new ContainerBuilder(RustFsImage)
        .WithEnvironment("RUSTFS_VOLUMES", "/data/rustfs0")
        .WithEnvironment("RUSTFS_ADDRESS", "0.0.0.0:9000")
        .WithEnvironment("RUSTFS_CONSOLE_ENABLE", "false")
        .WithEnvironment("RUSTFS_ACCESS_KEY", S3AccessKey)
        .WithEnvironment("RUSTFS_SECRET_KEY", S3SecretKey)
        .WithEnvironment("RUSTFS_OBS_LOGGER_LEVEL", "warn")
        // Tek disk topolojisi — yalnızca test/geliştirme.
        .WithEnvironment("RUSTFS_UNSAFE_BYPASS_DISK_CHECK", "true")
        .WithPortBinding(9000, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/health")))
        .Build();

    public string ClickHouseConnectionString { get; private set; } = string.Empty;

    public string ClickHouseHttpUrl { get; private set; } = string.Empty;

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public string S3ServiceUrl { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(
            _clickHouse.StartAsync(),
            _postgres.StartAsync(),
            _rustFs.StartAsync());

        ClickHouseHttpUrl = string.Create(
            CultureInfo.InvariantCulture,
            $"http://{_clickHouse.Hostname}:{_clickHouse.GetMappedPublicPort(8123)}");

        ClickHouseConnectionString = ConnectionStringFor("bizigo");

        S3ServiceUrl = string.Create(
            CultureInfo.InvariantCulture,
            $"http://{_rustFs.Hostname}:{_rustFs.GetMappedPublicPort(9000)}");
    }

    /// <summary>
    /// S3 istemcisi. RustFS'e özel hiçbir çağrı yok — yalnızca S3 API (F1 §7.0 koruma #1).
    /// Bu yüzden aynı istemci SeaweedFS ya da gerçek S3 ile de çalışır.
    /// </summary>
    public AmazonS3Client CreateS3Client()
    {
        var config = new AmazonS3Config
        {
            ServiceURL = S3ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            // AWS SDK v4 varsayılan olarak CRC32 sağlama toplamı başlıkları ekliyor;
            // AWS dışı S3 uygulamaları bunu her zaman kabul etmiyor.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        return new AmazonS3Client(new BasicAWSCredentials(S3AccessKey, S3SecretKey), config);
    }

    public ClickHouseContext CreateClickHouseContext() =>
        new(new ClickHouseOptions { ConnectionString = ClickHouseConnectionString });

    /// <summary>
    /// Kendi veritabanına sahip bağlam.
    ///
    /// <c>schema_migrations</c> veritabanı genelinde <b>tek</b> tablo: paylaşılan
    /// veritabanında göç testleri birbirinin kaydını görür ve "hiç göç uygulanmamış"
    /// gibi mutlak beklentiler çalışma sırasına göre kırılır. Göç davranışını
    /// sınayan her test kendi veritabanını ister.
    /// </summary>
    public async Task<ClickHouseContext> CreateIsolatedClickHouseContextAsync(
        CancellationToken cancellationToken = default)
    {
        var database = "test_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE DATABASE \"{database}\"", cancellationToken);

        return new ClickHouseContext(new ClickHouseOptions
        {
            ConnectionString = ConnectionStringFor(database),
        });
    }

    private string ConnectionStringFor(string database) => string.Create(
        CultureInfo.InvariantCulture,
        $"Host={_clickHouse.Hostname};Port={_clickHouse.GetMappedPublicPort(8123)};" +
        $"Database={database};Username=bizigo;Password=bizigo");

    /// <summary>
    /// ClickHouse HTTP arayüzü üzerinden tek ifade. Sürücü tipleri yerine HTTP
    /// kullanılıyor: bu yardımcı yalnızca test iskelesi, ürün kodunun bağlantı
    /// yönetimiyle karışmaması gerekiyor.
    /// </summary>
    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(ClickHouseHttpUrl) };
        http.DefaultRequestHeaders.Add("X-ClickHouse-User", "bizigo");
        http.DefaultRequestHeaders.Add("X-ClickHouse-Key", "bizigo");

        using var response = await http.PostAsync(
            (Uri?)null, new StringContent(sql), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"ClickHouse '{sql}' reddetti: {body}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(
            _clickHouse.DisposeAsync().AsTask(),
            _postgres.DisposeAsync().AsTask(),
            _rustFs.DisposeAsync().AsTask());
    }
}

[CollectionDefinition(Name)]
public sealed class DevStackCollection : ICollectionFixture<DevStackFixture>
{
    public const string Name = "dev-stack";
}
