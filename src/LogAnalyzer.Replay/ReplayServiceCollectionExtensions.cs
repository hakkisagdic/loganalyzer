using LogAnalyzer.Storage.ClickHouse;
using Microsoft.Extensions.DependencyInjection;

namespace LogAnalyzer.Replay;

public static class ReplayServiceCollectionExtensions
{
    public static IServiceCollection AddLogAnalyzerReplay(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ReplayStore>();
        services.AddSingleton<ReplayEngine>();

        return services;
    }
}
