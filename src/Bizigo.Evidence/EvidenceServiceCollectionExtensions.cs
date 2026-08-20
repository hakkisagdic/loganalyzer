using Bizigo.Evidence.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Evidence;

public static class EvidenceServiceCollectionExtensions
{
    /// <summary>
    /// Kanıt katmanı (T34).
    ///
    /// <para>
    /// <b>Sağlayıcılar tek tek kaydediliyor ve toplayıcı onları listeden
    /// alıyor.</b> F5'te trace sağlayıcısı geldiğinde buraya bir
    /// <c>AddSingleton&lt;IEvidenceProvider, TraceProvider&gt;</c> satırı
    /// ekleniyor ve başka hiçbir yer değişmiyor — kabul kriterinin
    /// çalıştırılabilir hâli.
    /// </para>
    ///
    /// <para>
    /// Metrik, trace ve topoloji <b>bilerek kayıtlı değil</b>. Boş bir
    /// sağlayıcı kaydetmek onları "var ama sonuç yok" gibi gösterirdi; oysa
    /// doğru cümle "bu türe hiç bakılmadı". Ayrımı
    /// <see cref="EvidenceCollector"/> enum üzerinden kuruyor.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBizigoEvidence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // **Scoped, singleton değil.** `IScopedQuery` scoped (kontrol düzlemi
        // DbContext'ini ve denetim kaydını taşıyor); singleton bir sağlayıcı onu
        // esir alır ve ilk isteğin DbContext'i sürecin ömrü boyunca kullanılır.
        // Belirtisi geç ve kafa karıştırıcı olurdu: kanıt toplama bir noktada
        // kapalı bağlantıyla patlamaya başlar ve sebebi kanıt katmanında
        // görünmez.
        services.AddScoped<IEvidenceProvider, LogWindowProvider>();
        services.AddScoped<IEvidenceProvider, ChangeFeedProvider>();

        services.AddScoped<EvidenceCollector>();

        return services;
    }
}
