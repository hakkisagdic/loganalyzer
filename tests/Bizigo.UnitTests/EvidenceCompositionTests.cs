using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bizigo.UnitTests;

/// <summary>
/// Kanıt katmanının DI bileşimi (T34).
///
/// <para>
/// Buradaki asıl test <see cref="Saglayicilar_esir_bagimlilik_uretmiyor"/>:
/// <c>IScopedQuery</c> <b>scoped</b> (kontrol düzlemi DbContext'ini ve denetim
/// kaydını taşıyor). Sağlayıcılar singleton kaydedilseydi onu esir alırlardı ve
/// ilk isteğin DbContext'i sürecin ömrü boyunca kullanılırdı — belirtisi geç ve
/// yanlış yere işaret eden bir arıza. İlk yazımda tam olarak bu hata vardı.
/// </para>
/// </summary>
public sealed class EvidenceCompositionTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // Üretimdeki ömürle aynı: scoped.
        services.AddScoped<IScopedQuery, RecordingScopedQuery>();

        // Kanıt paketi deposu (T36) kontrol düzlemine yazıyor. Bellek içi bir
        // fabrika yetiyor: sınanan şey bağlantı değil, grafiğin kurulabilmesi.
        services.AddSingleton<IDbContextFactory<ControlPlaneDbContext>>(
            _ => new InMemoryControlPlaneFactory());

        services.AddBizigoEvidence();

        // `ValidateScopes` bu testin tamamı: esir bağımlılığı yakalayan şey bu.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    [Fact]
    public void Saglayicilar_esir_bagimlilik_uretmiyor()
    {
        using var root = Build();
        using var scope = root.CreateScope();

        var collector = scope.ServiceProvider.GetRequiredService<EvidenceCollector>();

        // Önceki sekiz sağlayıcıya iki metrik ve iki trace sağlayıcısı eklendi.
        // Sayının burada yazılı olması bilinçli: bir
        // sağlayıcı sessizce düşerse rapor onu hiç aramaz ve eksikliği
        // yalnızca bir RCA raporunun zayıflığı olarak, aylar sonra görünür.
        Assert.Equal(12, collector.Providers.Count);
    }

    /// <summary>
    /// Sprint04'te beş tür gerçek sağlayıcılarla kayıtlı; muaf tür kalmadı.
    /// </summary>
    [Fact]
    public void Bes_tur_kayitli_ve_muaf_tur_yok()
    {
        using var root = Build();
        using var scope = root.CreateScope();

        var collector = scope.ServiceProvider.GetRequiredService<EvidenceCollector>();

        // Topoloji hâlâ envanter öznitelikleri üzerinden; Metric/Trace gerçek
        // sağlayıcılarının kaydı ayrı kimlik bekçisiyle de doğrulanır.
        Assert.Equal(
            [EvidenceKind.Log, EvidenceKind.Change, EvidenceKind.Metric, EvidenceKind.Trace, EvidenceKind.Topology],
            collector.Providers.Select(p => p.Kind).Distinct().Order());

        Assert.Empty(collector.UnregisteredKinds);

        Assert.Equal(EvidenceKinds.Exempt.Order(), collector.UnregisteredKinds.Order());
    }

    /// <summary>
    /// Sağlayıcı kimlikleri <b>benzersiz ve kalıcı</b>. Kanıt paketi saklandığı
    /// için (T36) bu dizgiler şema kadar kalıcı: değişirlerse geçmiş paketler
    /// kaynaklarını kaybeder ve bunu hiçbir şey kırmızı yakmaz.
    /// </summary>
    [Fact]
    public void Saglayici_kimlikleri_sabit()
    {
        using var root = Build();
        using var scope = root.CreateScope();

        var ids = scope.ServiceProvider.GetRequiredService<EvidenceCollector>()
            .Providers.Select(p => p.Id).Order().ToArray();

        Assert.Equal(
            [
                "change.feed",
                "logs.attribute-lift",
                "logs.first-seen",
                "logs.propagation",
                "logs.silence",
                "logs.volume",
                "logs.window",
                "metrics.baseline",
                "metrics.threshold",
                "topology.shared-attribute",
                "traces.error-propagation",
                "traces.service-dependency",
            ],
            ids);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }
}
