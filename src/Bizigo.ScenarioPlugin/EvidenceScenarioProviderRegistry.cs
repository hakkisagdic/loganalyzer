using System.Diagnostics.CodeAnalysis;
using Bizigo.Evidence;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.ScenarioPlugin;

/// <summary>
/// Kayıtlı sağlayıcı kümesini <b>DI'den keşfeden</b> kayıt.
///
/// <para>
/// Elle yazılmış bir id listesi tutmuyor ve bu kararın bedeli ölçüldü:
/// <c>Produces&lt;T&gt;</c> kapısı uçları elle yazılmış bir listeden topluyordu,
/// üç uç dosyası listede olmadığı için <b>16 uç kapıya hiç görünmedi ve üç test
/// de yeşildi.</b> Aynı kusurun buradaki karşılığı, kayıtlı olmayan bir
/// sağlayıcıya atıf yapan senaryonun yüklenmesi olurdu.
/// </para>
///
/// <para>
/// <b>Scoped, singleton değil.</b> <c>IEvidenceProvider</c> kayıtları scoped
/// (<c>IScopedQuery</c> taşıyorlar); singleton bir kayıt onları esir alırdı.
/// Burada yalnızca <c>Id</c> okunuyor ama ömrü ayırmak, ileride başka bir alan
/// okunduğunda sürpriz çıkarmıyor.
/// </para>
/// </summary>
public sealed class EvidenceScenarioProviderRegistry : IScenarioProviderRegistry
{
    private readonly HashSet<string> _ids;
    private readonly Dictionary<string, IScenarioEvidenceSchema> _schemas;

    public EvidenceScenarioProviderRegistry(
        IEnumerable<IEvidenceProvider> providers,
        IEnumerable<IScenarioEvidenceSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(schemas);

        _ids = [.. providers.Select(p => p.Id)];
        _schemas = schemas.ToDictionary(s => s.ProviderId, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> ProviderIds => _ids;

    public bool IsRegistered(string providerId) => _ids.Contains(providerId);

    public bool TryGetEvidenceSchema(string providerId, [NotNullWhen(true)] out IScenarioEvidenceSchema? schema) =>
        _schemas.TryGetValue(providerId, out schema);
}

public static class ScenarioServiceCollectionExtensions
{
    /// <summary>
    /// Senaryo plugin çekirdeği (T43).
    ///
    /// <para>
    /// Bugün tek kaydı sağlayıcı kaydı. Yükleyici ve katalog statik — durum
    /// taşımıyorlar, ve taşımamaları bilinçli: bir senaryo dosyasının
    /// doğrulaması, çalışan bir uygulamaya bağlı olmamalı ki CLI ve testler de
    /// aynı kapıdan geçebilsin.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBizigoScenarioPlugins(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IScenarioProviderRegistry, EvidenceScenarioProviderRegistry>();

        return services;
    }
}
