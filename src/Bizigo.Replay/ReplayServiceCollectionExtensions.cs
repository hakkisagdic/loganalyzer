using Bizigo.Storage.ClickHouse;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Replay;

public static class ReplayServiceCollectionExtensions
{
    public static IServiceCollection AddBizigoReplay(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ReplayStore>();
        services.AddSingleton<ReplayEngine>();

        return services;
    }
}
