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

        // F3'ün beş deterministik korelasyonu (T35). Beşi de ayrı sağlayıcı ve
        // toplayıcı hiçbirini tanımıyor — T34'ün taşıyıcı iddiasının karşılığı:
        // bu beş satır eklendiğinde `EvidenceCollector` değişmedi.
        services.AddScoped<IEvidenceProvider, FirstSeenSignatureProvider>();
        services.AddScoped<IEvidenceProvider, VolumeDeviationProvider>();
        services.AddScoped<IEvidenceProvider, SilenceProvider>();
        services.AddScoped<IEvidenceProvider, AttributeLiftProvider>();
        services.AddScoped<IEvidenceProvider, PropagationProvider>();

        services.AddScoped<EvidenceCollector>();

        // Kanıt paketi (T36). Fabrika scoped — `IScopedQuery`'yi tutuyor.
        // Depo, `IDbContextFactory` üzerinden çalıştığı için singleton olabilirdi
        // ama scoped bırakıldı: fabrika ile aynı ömürde durmaları, ileride
        // ikisinin arasına bir denetim kaydı girdiğinde sürpriz çıkarmıyor.
        services.AddScoped<EvidenceBundleFactory>();

        // Aynı örnek, iki isim. Arayüz T38'in kapatma yolunun üretimi
        // ÇAĞIRDIĞINI sınayabilmesi için var; ikinci bir uygulama yok ve
        // olmamalı (§9).
        services.AddScoped<IEvidenceBundleSource>(sp => sp.GetRequiredService<EvidenceBundleFactory>());
        services.AddScoped<EvidenceBundleStore>();

        // Altın küme ve alarm kapatma (T38). Kapatma servisi kanıt fabrikasını
        // tutuyor — o da `IScopedQuery` taşıyor — dolayısıyla scoped olmak
        // ZORUNDA. Singleton yapılsaydı `ArchitectureTests`'in ömür bekçisi
        // kırmızı yanardı; T26'da tam olarak bu kusur sessizce yaşamıştı.
        services.AddScoped<GoldenReviewStore>();
        services.AddScoped<AlertClosureService>();

        return services;
    }
}
