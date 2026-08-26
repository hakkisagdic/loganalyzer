using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bizigo.Rca.Models;

public static class ModelServiceCollectionExtensions
{
    /// <summary>
    /// Model sağlayıcısı ve K6'nın kapısı (T42).
    ///
    /// <para>
    /// <b>Uç burada DOĞRULANMIYOR</b> ve bu bilinçli: doğrulama ağ çözümlemesi
    /// yapıyor, kayıt anı ise süreç başlangıcı. Kapıyı kayıt anına koymak DNS
    /// erişilemediğinde API'nin hiç ayağa kalkmaması demekti — RCA dışındaki
    /// her uç da düşerdi. Doğrulama <see cref="ModelBoundaryGate.VerifyAsync"/>
    /// ile <b>kullanım anında</b> koşuyor ve reddi bir sonuç olarak dönüyor.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBizigoModelProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<ModelEndpointOptions>()
            .Bind(configuration.GetSection(ModelEndpointOptions.SectionName));

        services.TryAddSingleton<IEndpointAddressResolver, DnsEndpointAddressResolver>();
        services.TryAddSingleton<ModelBoundaryGate>();
        services.AddHttpClient<IModelProvider, OpenAiCompatibleModelProvider>();

        return services;
    }
}
