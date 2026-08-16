using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bizigo.Storage.Raw;

public static class RawServiceCollectionExtensions
{
    /// <summary>
    /// Ham arşiv katmanı. <see cref="IRawSegmentSource"/> burada kaydedilmiyor —
    /// onu WAL'ın sahibi olan ingest katmanı sağlıyor (bağımlılık yönü: arşiv
    /// ingest'i tanımaz).
    /// </summary>
    public static IServiceCollection AddBizigoRawArchive(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<RawStoreOptions>(configuration.GetSection(RawStoreOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IRawObjectStore, S3RawObjectStore>();
        services.AddSingleton<RawObjectBuilder>();
        services.AddSingleton<RawArchiveUploader>();
        services.AddSingleton<RawArchiveScrubber>();
        services.AddSingleton<RawReader>();
        services.AddSingleton<RawEventLocator>();

        // T07 gerçek eşleme deposunu kaydedince bu düşer.
        services.TryAddSingleton<IRawRefSink, NullRawRefSink>();

        services.AddHostedService<RawArchiveService>();

        return services;
    }
}
