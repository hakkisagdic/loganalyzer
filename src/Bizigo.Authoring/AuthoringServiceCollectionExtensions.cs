using Bizigo.Parsing.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bizigo.Authoring;

public static class AuthoringServiceCollectionExtensions
{
    /// <summary>
    /// Parser yazarlığı (T18). <c>AddBizigoParsing</c> ve <c>AddControlPlane</c>
    /// çağrıldıktan sonra eklenmeli — ikisinin de tiplerine bağımlı.
    /// </summary>
    public static IServiceCollection AddBizigoAuthoring(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ParserPublishGate>();
        services.AddSingleton<ParserAuthoringService>();
        services.AddSingleton<PublishedParserLoader>();

        // Kapsam ölçümü pahalı; önbellek katalog anlık görüntüsüne bağlı (T20).
        services.AddSingleton<CatalogCoverageCache>();

        // Katalog kaynağını DEĞİŞTİRİYOR: artık repodaki dosyalara ek olarak
        // yayınlanmış taslaklar da okunuyor. `AddBizigoParsing`'in kaydettiği
        // yalnızca-dizin sürümünün yerini alıyor.
        services.Replace(ServiceDescriptor.Singleton<IParserCatalogSource>(
            sp => sp.GetRequiredService<PublishedParserLoader>()));

        return services;
    }
}
