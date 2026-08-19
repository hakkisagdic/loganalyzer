using Bizigo.Alerting.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bizigo.Alerting;

public static class AlertingServiceCollectionExtensions
{
    /// <summary>
    /// Alarm motoru ve bildirim kanalları (T21, T22).
    ///
    /// <para>
    /// <c>AddBizigoDataPlane</c> ve <c>AddControlPlane</c> çağrıldıktan sonra
    /// eklenmeli — <see cref="Bizigo.Query.IScopedQuery"/> ve kontrol düzlemi
    /// fabrikası ikisinden geliyor.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBizigoAlerting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AlertingOptions();
        configuration.GetSection(AlertingOptions.SectionName).Bind(options);

        services.AddSingleton(options);
        services.AddSingleton<AlertingStats>();

        // Saat DI'dan geliyor. `DateTimeOffset.UtcNow`'ı doğrudan çağıran kod
        // testte ancak beklemeyle sınanabilir ve F1'in en pahalı dersi tam
        // olarak oydu. `TryAddSingleton`: başka bir modül kendi sahtesini
        // kaydettiyse onu ezmiyoruz.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<AlertEvaluator>();
        services.AddSingleton<AlertRuleService>();
        services.AddSingleton<AlertPreview>();
        services.AddSingleton<NotificationChannelService>();
        services.AddSingleton<IAlertQuerySource, ServiceScopeAlertQuerySource>();

        services.AddHttpClient(nameof(HttpNotificationChannel));

        services.AddSingleton<INotificationChannel, SlackChannel>();
        services.AddSingleton<INotificationChannel, TeamsChannel>();
        services.AddSingleton<INotificationChannel, WebhookChannel>();
        services.AddSingleton<INotificationChannel, EmailChannel>();
        services.AddSingleton<ISmtpTransport, SystemSmtpTransport>();

        // İki ayrı arka plan işçisi, tek değil: değerlendirme ClickHouse'a,
        // gönderim dışarıya bağlı. Tek döngüde birleştirmek, yavaş bir Slack
        // yanıtının kural değerlendirmesini geciktirmesi demek olurdu — yani
        // dış dünyanın arızası iç ölçümü bozardı.
        services.AddHostedService<AlertSchedulerWorker>();
        services.AddHostedService<NotificationDispatcher>();

        return services;
    }
}
