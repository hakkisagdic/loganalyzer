using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.ControlPlane;

public static class ControlPlaneServiceCollectionExtensions
{
    public static IServiceCollection AddControlPlane(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // İki kayıt bir arada: istek başına scoped DbContext (uçlar) ve singleton
        // fabrika (arka plan işleri — yükleyici, scrub). `optionsLifetime` açıkça
        // singleton olmak ZORUNDA: varsayılan scoped kalırsa singleton fabrika onu
        // tüketemez ve konteyner doğrulaması açılışta patlar.
        services.AddDbContext<ControlPlaneDbContext>(
            options => Configure(options, connectionString),
            optionsLifetime: ServiceLifetime.Singleton);

        services.AddDbContextFactory<ControlPlaneDbContext>(
            options => Configure(options, connectionString),
            lifetime: ServiceLifetime.Singleton);

        services.AddSingleton<SourceDirectory>();

        return services;
    }

    /// <summary>
    /// Tek yerden yapılandırma: uygulama, göç aracı ve testler aynı ayarları kullanmalı.
    /// Ayrışırsa göçler bir yerde çalışıp başka yerde çalışmaz.
    /// </summary>
    public static DbContextOptionsBuilder Configure(
        DbContextOptionsBuilder options,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(
                    ControlPlaneDbContext.MigrationsHistoryTable, ControlPlaneDbContext.Schema))
            // Postgres tarafında snake_case: elle yazılan SQL ve psql oturumları
            // okunabilir kalsın (HasFilter("peer_address IS NOT NULL") buna dayanıyor).
            .UseSnakeCaseNamingConvention();
    }

    /// <summary>
    /// Bekleyen göçleri uygular. T01 kabul kriteri: uygulama ayağa kalkarken
    /// Postgres şeması hazır olmalı.
    /// </summary>
    public static async Task MigrateControlPlaneAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}
