using Bizigo.Storage.ClickHouse;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Query;

/// <summary>
/// Veri düzleminin bileşimi (composition). Uygulama katmanı <see cref="EventReader"/>
/// gibi somut okuyucuları <b>adlandırmaz</b> — yalnızca <see cref="IScopedQuery"/>
/// görür.
///
/// Bu, mimari testin (T02) zorladığı kural: kayıt satırının kendisi bile API
/// derlemesinde okuyucu bağımlılığı yaratıyordu. Bileşimi buraya taşımak, uçların
/// kapsam kapısını atlamasını yapısal olarak imkânsız kılıyor.
/// </summary>
public static class QueryServiceCollectionExtensions
{
    public static IServiceCollection AddBizigoDataPlane(
        this IServiceCollection services,
        ClickHouseOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton<ClickHouseContext>();
        services.AddSingleton<ClickHouseMigrator>();
        services.AddSingleton<EventWriter>();
        services.AddSingleton<EventReader>();
        services.AddSingleton<ChangeEventReader>();

        services.AddScoped<IAuditSink, ControlPlaneAuditSink>();
        services.AddScoped<IScopedQuery, ScopedQuery>();

        return services;
    }

    /// <summary>ClickHouse göçlerini uygular; uygulanan/mevcut sayısını döner.</summary>
    public static async Task<(int Applied, int Existing)> MigrateDataPlaneAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.GetRequiredService<ClickHouseOptions>();
        var migrator = services.GetRequiredService<ClickHouseMigrator>();
        var result = await migrator.MigrateAsync(options.MigrationsDirectory, cancellationToken);

        return (result.Applied.Count, result.AlreadyApplied.Count);
    }
}
