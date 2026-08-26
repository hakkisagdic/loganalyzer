using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bizigo.Rca;

public static class RcaServiceCollectionExtensions
{
    /// <summary>
    /// RCA tetikleyicileri (T45). <c>AddControlPlane</c>'den sonra eklenmeli.
    /// </summary>
    public static IServiceCollection AddBizigoRcaTriggers(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RcaAdmission>();

        var quota = new RcaQuotaOptions();
        configuration?.GetSection(RcaQuotaOptions.SectionName).Bind(quota);
        services.AddSingleton(quota);

        // T46 kapıyı dolduruyor. `Replace`, `TryAdd` DEĞİL: T45 varsayılanı
        // `TryAddSingleton` ile kaydediyor ve burada da `TryAdd` kullanmak,
        // çağrı sırasına göre sessizce hiçbir şeyi reddetmeyen kapıyı bırakırdı
        // — kota yapılandırılmış görünüp hiç çalışmazdı.
        services.Replace(ServiceDescriptor.Singleton<IRcaQuotaGate, RcaQuotaGate>());

        // Kullanım sorgusu kapının kendisinden ayrı çözülebilmeli: ekran ve
        // okuma yolu kotayı "kontrol etmek" için değil "göstermek" için istiyor.
        services.AddSingleton<RcaQuotaGate>();

        return services;
    }
}
