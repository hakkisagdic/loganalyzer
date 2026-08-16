using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bizigo.Parsing;

public sealed class ParsingOptions
{
    public const string SectionName = "Parsing";

    /// <summary>Taban grok seti (Logstash kopyası — veri, kod değil).</summary>
    public string PatternDirectory { get; set; } = "catalog/patterns/legacy";

    /// <summary>
    /// Taban setin üstüne binen lookaround'suz kaplama. Boş bırakılırsa yalnızca
    /// taban set kullanılır — o durumda pattern'lerin çoğu doğrusal motorda
    /// derlenemez ve geri izlemeye düşer.
    /// </summary>
    public string PatternOverlayDirectory { get; set; } = "catalog/patterns/bizigo-v1";

    /// <summary>YAML parser plugin'lerinin kökü.</summary>
    public string ParserDirectory { get; set; } = "catalog/parsers";

    /// <summary>Eşleme tablolarının kökü.</summary>
    public string MappingDirectory { get; set; } = "catalog/mappings";

    /// <summary>Envanter anlık görüntüsünün tazelenme sıklığı.</summary>
    public TimeSpan InventoryRefreshInterval { get; set; } = TimeSpan.FromMinutes(1);
}

public static class ParsingServiceCollectionExtensions
{
    public static IServiceCollection AddBizigoParsing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ParsingOptions>(configuration.GetSection(ParsingOptions.SectionName));

        services.AddSingleton<GrokCompiler>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ParsingOptions>>().Value;
            return new GrokCompiler(GrokPatternLibrary.LoadWithOverlay(
                options.PatternDirectory, options.PatternOverlayDirectory));
        });

        services.AddSingleton<MappingTableCatalog>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ParsingOptions>>().Value;
            return MappingTableCatalog.LoadFromDirectory(options.MappingDirectory);
        });

        services.AddSingleton<ParserCompiler>();
        services.AddSingleton<ParserCatalog>();
        services.AddSingleton<DispatchStats>();
        services.AddSingleton<Dispatcher>();

        return services;
    }
}
