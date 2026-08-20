using Bizigo.Api.Webhooks;
using Bizigo.Contracts.Security;
using Bizigo.Devices;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bizigo.Api.Connectors;

public static class ChangeConnectorServiceCollectionExtensions
{
    /// <summary>
    /// Connector yapılandırması, zamanlayıcı ve saklama temizliği (T25).
    ///
    /// <para>
    /// <b>Sıra önemli:</b> bu çağrı <c>AddChangeWebhooks</c>'tan sonra gelmeli —
    /// kontrol düzlemi destekli kayıt defteri, yapılandırma dosyasından gelen
    /// kayıt defterini yedek olarak tüketiyor.
    /// </para>
    /// </summary>
    public static IServiceCollection AddChangeConnectors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new ChangeConnectorOptions();
        configuration.GetSection(ChangeConnectorOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        // Gizli bilgi koruyucusu ürün geneli TEK (T25). Alarm modülü de aynı
        // kaydı yapıyor; `TryAdd` hangisinin önce geldiğini önemsiz kılıyor.
        services.TryAddSingleton(_ => new SecretProtector(
            configuration[$"{SecretProtectionOptions.SectionName}:SecretKey"]));

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IChangeConnectorRunner, WebhookConnectorRunner>();

        // Cihaz config toplayıcısı (T26). Bu kayıt, T25'in "toplayıcısı olmayan
        // tip etkinleştirilemiyor" kapısını `DeviceConfig` için AÇIYOR — kapının
        // gerçekten açıldığını bir birim testi sınıyor.
        services.AddSingleton<IDeviceTransport, SshDeviceTransport>();
        services.AddSingleton<IConfigCollector, FortiGateCollector>();
        services.AddSingleton<IConfigCollector, CiscoAsaCollector>();
        services.AddSingleton<IConfigCollector, MikroTikCollector>();
        services.AddSingleton(sp => new DeviceConfigService(
            sp.GetRequiredService<IDeviceTransport>(),
            sp.GetRequiredService<IEnumerable<IConfigCollector>>(),
            options.MaxDeviceConcurrency));
        services.AddSingleton<IChangeConnectorRunner, DeviceConfigRunner>();
        services.AddSingleton<ChangeConnectorService>();

        // Webhook uçları artık ÖNCE veritabanından çözülüyor (K34). Kayıt burada
        // çünkü kontrol düzlemi destekli defter connector servisine bağımlı.
        services.AddSingleton<IChangeWebhookRegistry, ControlPlaneWebhookRegistry>();

        services.AddSingleton<ChangeConnectorScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<ChangeConnectorScheduler>());

        services.AddSingleton<ChangeRetentionWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<ChangeRetentionWorker>());

        return services;
    }
}
