using Bizigo.Evidence;
using Bizigo.Query;
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

        Assert.Equal(2, collector.Providers.Count);
    }

    /// <summary>
    /// F3'te iki sağlayıcı kayıtlı; kalan üç tür <b>kayıtlı değil</b> ve bu
    /// bilerek. Boş bir sağlayıcı kaydetmek onları "var ama sonuç yok" gibi
    /// gösterirdi, oysa doğru cümle "bu türe hiç bakılmadı".
    /// </summary>
    [Fact]
    public void F5_turleri_kayitli_degil()
    {
        using var root = Build();
        using var scope = root.CreateScope();

        var collector = scope.ServiceProvider.GetRequiredService<EvidenceCollector>();

        Assert.Equal(
            [EvidenceKind.Log, EvidenceKind.Change],
            collector.Providers.Select(p => p.Kind).Order());

        Assert.Equal(
            [EvidenceKind.Metric, EvidenceKind.Trace, EvidenceKind.Topology],
            collector.UnregisteredKinds.Order());
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

        Assert.Equal(["change.feed", "logs.window"], ids);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }
}
