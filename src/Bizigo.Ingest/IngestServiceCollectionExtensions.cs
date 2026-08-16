using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Pipeline;
using Bizigo.Ingest.Text;
using Bizigo.Ingest.Wal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Bizigo.Ingest;

public static class IngestServiceCollectionExtensions
{
    /// <summary>
    /// Ingest katmanının tamamı. API katmanı WAL'ı, kanalı ya da çözücüyü doğrudan
    /// görmez — yalnızca <see cref="IngestGateway"/>.
    /// </summary>
    public static IServiceCollection AddBizigoIngest(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Legacy kod sayfaları: kayıt edilmezse windows-1254 ÇALIŞMA ANINDA patlar.
        // Statik kurucu da yapıyor, ama burada erken ve açıkça yapılıyor (F1 §2.4).
        EncodingDetector.RegisterCodePages();

        services.Configure<IngestOptions>(configuration.GetSection(IngestOptions.SectionName));
        services.Configure<WalOptions>(configuration.GetSection(WalOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<EncodingDetector>();
        services.AddSingleton<OtlpLogsDecoder>();
        services.AddSingleton<IngestStats>();
        services.AddSingleton<IngestChannel>();
        services.AddSingleton<WriteAheadLog>();
        services.AddSingleton<IngestGateway>();

        // T06 kendi sink'ini kaydedince bu düşer.
        services.TryAddSingleton<IIngestSink, PassthroughSink>();

        services.AddHostedService<IngestPipeline>();

        return services;
    }
}
