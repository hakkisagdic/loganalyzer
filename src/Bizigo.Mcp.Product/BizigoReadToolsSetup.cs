using Bizigo.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bizigo.Mcp.Product;

/// <summary>
/// Okuma araçlarının kompozisyon köküne <b>bağlanması</b>.
///
/// <para>
/// <b>Bu dosyanın birinci işi bir kayıt değil, ÖLÇÜLMÜŞ bir deliği kapatmak.</b>
/// </para>
///
/// <para>
/// <c>McpToolDiscovery</c> araçları kompozisyon kökünden başlayıp
/// <c>GetReferencedAssemblies()</c> ile geçişli olarak buluyor — ve o tablo
/// <b>yalnızca gerçekten kullanılan</b> referansları taşıyor. <c>csproj</c>'a
/// <c>ProjectReference</c> yazmak yetmiyor: derleyici, kodda hiçbir tipine
/// dokunulmayan referansı derleme meta verisinden <b>düşürüyor</b>.
/// </para>
///
/// <para>
/// <b>Ölçüm.</b> M04'te beş araç yazıldı, <c>Bizigo.Api</c>'ye ve
/// <c>Bizigo.Cli</c>'ye <c>ProjectReference</c> eklendi, çözüm <b>0 hata 0
/// uyarı</b> derlendi — ve uyum kapısı <b>yeşil kaldı</b>: sunucu hâlâ tek araç
/// (<c>server.info</c>) ilan ediyordu.
/// <c>strings Bizigo.Api.dll | grep Bizigo.Mcp.Product</c> <b>sıfır</b> döndü.
/// Yani keşif beş aracı atlamadı, <b>hiç görmedi</b>, ve hiçbir yerde hata
/// yoktu. §7'nin sınıfı.
/// </para>
///
/// <para>
/// <b>Bu delik depoda daha önce de ısırdı ve kayıtlı.</b>
/// <c>CompositionRootTests</c> belgesi: <i>"T44 ilgisiz bir referans ekleyip
/// <c>Bizigo.ScenarioPlugin</c>'i kompozisyon kökünün geçişli kapanışına
/// sokunca keşif bir fazla buldu... Referans eklenmeseydi kimse
/// görmeyecekti."</i> Aynı mekanizmanın diğer yönü: referansın <b>gerçek</b>
/// olmaması keşfi kör ediyor.
/// </para>
///
/// <para>
/// <b>Bu kaydın silinmesini ne yakalar.</b> İki bekçi, ikisi de bugün var:
/// </para>
/// <list type="number">
/// <item>
/// <c>CompositionRootTests</c> (T50) keşfettiği her <c>Add*</c> uzantısının bir
/// kompozisyon kökünün çağrı grafiğinde olmasını istiyor — ve o keşif referans
/// kapanışından değil <b>diskten</b> geliyor, yani tam olarak bu deliğe
/// bağışık. Çağrıyı silmek T50'yi kırmızı yakıyor.
/// </item>
/// <item>
/// <c>McpComplianceTests.Sunucunun_ilan_ettigi_araclar</c> beklenen araç
/// kümesini <b>elle</b> taşıyor. Derlemenin görünmez olması o kümeyi
/// küçültüyor ve kapı kırmızı yanıyor.
/// </item>
/// </list>
///
/// <para>
/// <b>Kalıcı çözüm burada değil.</b> Doğru düzeltme <c>AddBizigoMcpCore</c>'un
/// <i>araç derlemelerini</i> de açıkça almasıdır — M01'in kendi cümlesi
/// (<i>"geriye tek dürüst seçenek kalıyor: kökü çağıranın söylemesi"</i>) bir
/// adım eksik çıktı: kökü söylemek yetmiyor, <b>araç derlemelerini</b> söylemek
/// gerekiyor. O imza değişikliği M03 ve M05'i de ilgilendiriyor, dolayısıyla
/// koordinatörün kararı; buradaki çözüm o karar gelene kadar geçerli ve
/// ölçülmüş.
/// </para>
/// </summary>
public static class BizigoReadToolsSetup
{
    /// <summary>
    /// Okuma araçlarını kompozisyon köküne bağlar.
    ///
    /// <para>
    /// <b>Araç TİPLERİ burada kaydedilmiyor</b> ve bu bilinçli: keşif onları
    /// <c>ActivatorUtilities</c> ile kuruyor, DI'dan çözmüyor. Bir araç tipini
    /// buraya yazmak, M01'in "kayıt yok, sınıfı yaz" sözleşmesini iki yerden
    /// besleyen bir listeye çevirirdi — bu deponun dört kez ödediği ders.
    /// </para>
    ///
    /// <para>
    /// Kaydedilen tek şey <see cref="TimeProvider"/>: <c>logs.search</c>'ün
    /// varsayılan zaman penceresi ona bakıyor. <c>TryAdd</c> — birisi zaten
    /// kaydettiyse onun kararı kazanıyor, ve testte sabit bir saat verilebiliyor.
    /// Kaydedilmezse araç <c>TimeProvider.System</c>'e <b>sessizce</b> düşerdi:
    /// çalışır, ama "bu aracın saati nereden geliyor" sorusunun cevabı hiçbir
    /// yerde yazılı olmazdı.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBizigoReadTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // M07 · ABONELİK YAYIN KANALI.
        //
        // `McpResourceUpdates` SINGLETON olmak zorunda: canlı sunucuların
        // defterini tutuyor ve scoped olsaydı her istek kendi boş defterini
        // görürdü — yani hiçbir bildirim gönderilmezdi ve hiçbir şey kırmızı
        // yanmazdı.
        //
        // Dinleyici `IRcaRunChangeListener` olarak kaydediliyor, `RcaAdmission`
        // onu bir KOLEKSİYON olarak alıyor. Tek dinleyici için de koleksiyon
        // olması bilinçli: ikinci bir dinleyici (örn. arayüzün SSE kanalı)
        // eklendiğinde `RcaAdmission`'ın imzası değişmiyor.
        //
        // KAYIT BURADA, `AddBizigoMcp`'de DEĞİL: stdio taşıması bu uzantıdan
        // geçiyor ve HTTP tarafı da onu çağırıyor. Yeteneğin ilan edilip
        // edilmemesi kaydın varlığına bağlı (`McpStdioHost`), yani kaydı
        // atlamanın bedeli "abonelik yok" — sessiz bir yalan değil.
        services.TryAddSingleton<McpResourceUpdates>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRcaRunChangeListener, Resources.McpRcaRunChangeListener>());

        return services;
    }
}
