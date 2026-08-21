using System.Globalization;
using Amazon.Runtime;
using Bizigo.Storage.ClickHouse;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.PostgreSql;

namespace Bizigo.IntegrationTests;

/// <summary>
/// F1 geliştirme yığınının test karşılığı: ClickHouse + PostgreSQL + RustFS.
/// Sürümler <c>deploy/docker-compose.yml</c> ile aynı — test ile geliştirme ortamı
/// ayrışırsa testin değeri düşer.
///
/// <para>
/// <b>Üçü tek yığın olarak kalkıyor</b> (bkz. <see cref="Project"/>): ortak bir
/// ağ ve ortak bir proje etiketi taşıyorlar. Testcontainers container'ları
/// birbirinden bağımsız açtığı için Docker Desktop ve Portainer onları rastgele
/// adlı, dağınık kutular olarak gösteriyordu; hangisinin bu koşuma ait olduğu ve
/// koşum yarıda kaldığında hangisinin artık olduğu görünmüyordu.
/// </para>
/// </summary>
public sealed class DevStackFixture : IAsyncLifetime
{
    public const string ClickHouseImage = "clickhouse/clickhouse-server:26.7";
    public const string PostgresImage = "postgres:18-alpine";
    public const string RustFsImage = "rustfs/rustfs:1.0.0-rc.1";

    /// <summary>
    /// Yığının görünen adı.
    ///
    /// <para>
    /// <c>com.docker.compose.project</c> etiketi <b>yalnızca görünüm</b> için:
    /// Docker Desktop ve Portainer bu etikete bakıp container'ları tek yığın
    /// altında gruplandırıyor. Yaşam döngüsü hâlâ Testcontainers'ın (Ryuk)
    /// elinde — ortada bir compose dosyası yok ve <c>docker compose</c> bu
    /// projeyi yönetmiyor. Etiketi "compose bunu yönetiyor" diye okumak yanlış
    /// olur; anlamı "bu container'lar birlikte doğdu, birlikte ölecek".
    /// </para>
    /// </summary>
    public const string Project = "bizigo-tests";

    private const string ComposeProjectLabel = "com.docker.compose.project";

    private const string S3AccessKey = "bizigoadmin";
    private const string S3SecretKey = "bizigoadmin";

    private readonly INetwork _network;
    private readonly IContainer _clickHouse;
    private readonly PostgreSqlContainer _postgres;
    private readonly IContainer _rustFs;

    public DevStackFixture()
    {
        // Ağ adı koşuma özgü: aynı makinede iki paket paralel koşarsa (yerelde
        // koordinatör + CI runner) sabit bir ad ikisini aynı ağa sokar ve
        // birinin temizliği diğerinin ağını siler.
        var run = Guid.NewGuid().ToString("N")[..8];

        // Container adları da bu ekten türüyor: Testcontainers'ın verdiği
        // rastgele ad (`recursing_chebyshev`) hangi kutunun neye ait olduğunu
        // söylemiyordu. Sabit bir ad ise ikinci bir koşumu "ad zaten kullanımda"
        // ile düşürürdü.

        _network = new NetworkBuilder()
            .WithName($"{Project}-{run}")
            .WithLabel(ComposeProjectLabel, Project)
            .Build();

        _clickHouse = new ContainerBuilder(ClickHouseImage)
            .WithName($"{Project}-clickhouse-{run}")
            .WithEnvironment("CLICKHOUSE_DB", "bizigo")
            .WithEnvironment("CLICKHOUSE_USER", "bizigo")
            .WithEnvironment("CLICKHOUSE_PASSWORD", "bizigo")
            .WithEnvironment("CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", "1")
            .WithPortBinding(8123, true)
            .WithNetwork(_network)
            .WithNetworkAliases("clickhouse")
            .WithLabel(ComposeProjectLabel, Project)
            .WithLabel("com.docker.compose.service", "clickhouse")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8123).ForPath("/ping")))
            .Build();

        _postgres = new PostgreSqlBuilder(PostgresImage)
            .WithName($"{Project}-postgres-{run}")
            .WithDatabase("bizigo")
            .WithUsername("bizigo")
            .WithPassword("bizigo")
            .WithNetwork(_network)
            .WithNetworkAliases("postgres")
            .WithLabel(ComposeProjectLabel, Project)
            .WithLabel("com.docker.compose.service", "postgres")
            .Build();

        _rustFs = new ContainerBuilder(RustFsImage)
            .WithName($"{Project}-rustfs-{run}")
            .WithEnvironment("RUSTFS_VOLUMES", "/data/rustfs0")
            .WithEnvironment("RUSTFS_ADDRESS", "0.0.0.0:9000")
            .WithEnvironment("RUSTFS_CONSOLE_ENABLE", "false")
            .WithEnvironment("RUSTFS_ACCESS_KEY", S3AccessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", S3SecretKey)
            .WithEnvironment("RUSTFS_OBS_LOGGER_LEVEL", "warn")
            // Tek disk topolojisi — yalnızca test/geliştirme.
            .WithEnvironment("RUSTFS_UNSAFE_BYPASS_DISK_CHECK", "true")
            .WithPortBinding(9000, true)
            .WithNetwork(_network)
            .WithNetworkAliases("rustfs")
            .WithLabel(ComposeProjectLabel, Project)
            .WithLabel("com.docker.compose.service", "rustfs")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/health")))
            .Build();
    }

    public string ClickHouseConnectionString { get; private set; } = string.Empty;

    public string ClickHouseHttpUrl { get; private set; } = string.Empty;

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public string S3ServiceUrl { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        // Ağ ÖNCE ve tek seferde kuruluyor. Üç container paralel kalkıyor; her
        // biri kendi başlangıcında ağı yaratmayı deneseydi üçü yarışırdı.
        await _network.CreateAsync();

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

    /// <summary>
    /// Tek değerlik sorgu, ClickHouse HTTP arayüzünden.
    ///
    /// <para>
    /// Sürücü tipleri kullanılmıyor çünkü <c>ClickHouseContext.CreateConnection</c>
    /// bilinçli olarak <c>internal</c>: ham sürücü erişimi yalnızca
    /// <c>Bizigo.Storage.ClickHouse</c> içinde olabilir (K17 mimari testi). Test
    /// iskelesinin o kuralı delmesi, kuralı anlamsız kılardı.
    /// </para>
    /// </summary>
    public async Task<string> QueryScalarAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var database = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(kv => kv.Length == 2 && kv[0].Trim().Equals("Database", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv[1].Trim())
            .FirstOrDefault() ?? "bizigo";

        using var http = new HttpClient { BaseAddress = new Uri(ClickHouseHttpUrl) };
        http.DefaultRequestHeaders.Add("X-ClickHouse-User", "bizigo");
        http.DefaultRequestHeaders.Add("X-ClickHouse-Key", "bizigo");
        http.DefaultRequestHeaders.Add("X-ClickHouse-Database", database);

        using var response = await http.PostAsync((Uri?)null, new StringContent(sql), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ClickHouse '{sql}' reddetti: {body}");
        }

        return body.TrimEnd('\n');
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
        try
        {
            await Task.WhenAll(
                _clickHouse.DisposeAsync().AsTask(),
                _postgres.DisposeAsync().AsTask(),
                _rustFs.DisposeAsync().AsTask());
        }
        finally
        {
            // Ağ container'lardan SONRA siliniyor: bağlı bir uç varken silme
            // reddedilir ve arkada sahipsiz bir ağ kalır.
            //
            // `finally` şart: container temizliği düştüğünde (Docker daemon
            // kapandı, container zaten yok) `Task.WhenAll` fırlıyordu ve ağ
            // silme satırına HİÇ gelinmiyordu — yani temizliğin başarısız
            // olduğu her koşum arkasında bir ağ bırakıyordu. §3'ün saydığı
            // görünmez birikmenin tam örneği.
            await _network.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class DevStackCollection : ICollectionFixture<DevStackFixture>
{
    public const string Name = "dev-stack";
}
