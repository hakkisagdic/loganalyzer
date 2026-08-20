using Bizigo.Ingest.Discovery;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Pipeline;
using Bizigo.Ingest.Text;
using Bizigo.Ingest.Wal;
using Bizigo.Normalization;
using Bizigo.Parsing.Grok;
using Bizigo.Storage.Raw;
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

        // Arşiv yükleyicisinin WAL'a bakışı (T04).
        services.AddSingleton<IRawSegmentSource, WalSegmentSource>();

        // Parse adımı (T06). `PassthroughSink` kıyaslama için duruyor ama
        // varsayılan artık gerçek dispatcher.
        // Varsayılan sayaç yalnızca ingest tek başına barındırıldığında devreye
        // girer; API bileşiminde ClickHouse yazıcısı (T07) önce kaydediliyor.
        services.TryAddSingleton<IParsedEventSink, CountingParsedEventSink>();
        services.TryAddSingleton<IIngestSink, ParsingSink>();

        services.AddHostedService<IngestPipeline>();
        services.AddHostedService<CatalogRefreshService>();

        services.AddBizigoDiscovery(configuration);

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
    public static IServiceCollection AddBizigoDiscovery(
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
