using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bizigo.Rca;

public static class RcaServiceCollectionExtensions
{
    /// <summary>
    /// RCA tetikleyicileri (T45). <c>AddControlPlane</c>'den sonra eklenmeli.
    /// </summary>
    public static IServiceCollection AddBizigoRcaTriggers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RcaAdmission>();

        // Kota kapısının VARSAYILANI hiçbir şeyi reddetmiyor. T46 kendi
        // uygulamasını `Replace` ile geçirecek; `TryAdd` kullanmak, T46'nın
        // kaydı önce gelirse sessizce iki kapı bırakırdı.
        services.TryAddSingleton<IRcaQuotaGate, AlwaysAllowQuotaGate>();

        return services;
    }
}
