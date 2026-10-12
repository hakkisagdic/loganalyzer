using LogAnalyzer.Ingest.Discovery;
using LogAnalyzer.Ingest.Otlp;
using LogAnalyzer.Ingest.Pipeline;
using LogAnalyzer.Ingest.Text;
using LogAnalyzer.Ingest.Wal;
using LogAnalyzer.Normalization;
using LogAnalyzer.Parsing.Grok;
using LogAnalyzer.Storage.Raw;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LogAnalyzer.Ingest;

public static class IngestServiceCollectionExtensions
{
    /// <summary>
    /// Ingest katmanının tamamı. API katmanı WAL'ı, kanalı ya da çözücüyü doğrudan
    /// görmez — yalnızca <see cref="IngestGateway"/>.
    /// </summary>
    public static IServiceCollection AddLogAnalyzerIngest(
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

        // Arşiv yükleyicisinin WAL'a bakışı (T04).
        services.AddSingleton<IRawSegmentSource, WalSegmentSource>();

        // Parse adımı (T06). `PassthroughSink` kıyaslama için duruyor ama
        // varsayılan artık gerçek dispatcher.
        // Varsayılan sayaç yalnızca ingest tek başına barındırıldığında devreye
        // girer; API bileşiminde ClickHouse yazıcısı (T07) önce kaydediliyor.
        services.TryAddSingleton<IParsedEventSink, CountingParsedEventSink>();
        services.TryAddSingleton<EventComposer>();
        services.TryAddSingleton<IIngestSink, ParsingSink>();

        services.AddHostedService<IngestPipeline>();
        services.AddHostedService<CatalogRefreshService>();

        services.AddLogAnalyzerDiscovery(configuration);

        return services;
    }

    /// <summary>Metric/trace composition; requires inventory and raw object-store services.</summary>
    public static IServiceCollection AddLogAnalyzerTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SignalOptions>(configuration.GetSection(SignalOptions.SectionName));
        services.AddSingleton<OtlpTelemetryDecoder>();
        services.TryAddSingleton<ISignalCheckpoints, SignalCheckpoints>();
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<SignalIngest>(sp,
            sp.GetRequiredService<LogAnalyzer.Contracts.ITelemetrySink>()));
        services.AddHostedService<SignalIngestService>();
        return services;
    }

    /// <summary>
    /// Python sidecar keşif yolu (T12 / K14).
    ///
    /// <para>
    /// <b>Sıcak yolda değil.</b> Kapalıyken (<c>Sidecar:Enabled=false</c>) geriye
    /// yalnızca <see cref="NullTemplateAnnotator"/> kalıyor; ne HTTP istemcisi ne
    /// arka plan işçisi kuruluyor. Ayarı kapatmak ile sidecar'ı öldürmek arasında
    /// ingest açısından hiçbir fark olmaması bilinçli.
    /// </para>
    /// </summary>
    public static IServiceCollection AddLogAnalyzerDiscovery(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new SidecarOptions();
        configuration.GetSection(SidecarOptions.SectionName).Bind(options);

        services.AddSingleton(options);
        services.AddSingleton<DiscoveryStats>();

        // Maskeleme sözlüğü grok kütüphanesiyle aynı statüde: **veri**. Bulunamazsa
        // sessizce devam etmek, keşfin neden hiç çalışmadığını günlerce gizler.
        //
        // K35'ten sonra bu kayıt `Enabled` kontrolünün **üstünde**: maskeleme artık
        // keşfin özel işi değil, sıcak yolun her olayda koştuğu adım. Sidecar kapalı
        // olsa bile `signature_hash` dolmak zorunda — RCA'nın en güçlü iki sinyalini
        // sidecar'dan kurtarmanın tamamı bu. Sözlük yoksa boru hattı **açılışta**
        // patlıyor; sessizce imzasız akmak, ticket'ın önlemek için var olduğu hata.
        services.AddSingleton(_ => MaskCatalog.LoadFromFile(options.MaskFile));

        if (!options.Enabled)
        {
            services.TryAddSingleton<ITemplateAnnotator, NullTemplateAnnotator>();
            return services;
        }

        services.AddSingleton(new TemplateCache(options.TemplateCacheCapacity));
        services.AddSingleton<DiscoveryQueue>();
        services.AddSingleton<SidecarClient>(_ => new SidecarClient(options));
        services.AddSingleton(sp => new SidecarCircuitBreaker(
            options,
            sp.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton<ITemplateAnnotator, DiscoveryAnnotator>();
        services.AddHostedService<DiscoveryWorker>();

        return services;
    }
}
