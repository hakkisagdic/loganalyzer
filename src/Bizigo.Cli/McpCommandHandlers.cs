using System.Reflection;
using Bizigo.Mcp;
using Bizigo.Simulators.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bizigo.Cli;

/// <summary>
/// <c>bizigo mcp serve</c> — MCP'nin <b>stdio</b> taşıması.
///
/// <para>
/// <b>Neden ayrı bir çalıştırılabilir değil.</b> stdio sunucusu için ikinci bir
/// host projesi açmak, M02'nin (<i>komut çekirdeği ve CLI paritesi</i>) tam
/// olarak kaçınmak için var olduğu ikinci kopyayı doğururdu: aynı komutların
/// bir kez CLI'da bir kez MCP host'unda kurulması. M01 yalnızca <b>girişi</b>
/// açıyor; komut çekirdeği M02'nin işi ve o geldiğinde bu dosyanın çözdüğü
/// servis grafiği ortak çekirdeği de taşıyacak.
/// </para>
///
/// <para>
/// <b>stdout protokolün kendisi.</b> Bu komut hiçbir şey yazdırmıyor — tek bir
/// bilgi satırı bile JSON-RPC akışını bozar ve istemci bunu ayrıştırma hatası
/// olarak görür, yani ürün hakkında hiçbir şey söylemeyen bir arıza. Günlükler
/// <c>--verbose</c> ile <b>stderr</b>'e açılıyor.
/// </para>
/// </summary>
public static class McpCommandHandlers
{
    /// <summary>
    /// Sunucuyu koşturur. Süreç kapanana kadar dönmez.
    /// </summary>
    /// <param name="surfaceName">
    /// <c>bizigo</c> ya da <c>bizigo-sim</c>. Tanınmayan ad <b>reddediliyor</b>:
    /// bir varsayılana düşmek, yüzeyini yanlış yazan çağrıyı sessizce ürün
    /// verisi kümesine bağlardı.
    /// </param>
    /// <param name="verbose">Günlükleri stderr'e ver.</param>
    /// <param name="cancellationToken">İptal.</param>
    public static async Task<int> ServeAsync(
        string surfaceName,
        bool verbose,
        CancellationToken cancellationToken)
    {
        if (McpSurfaces.Parse(surfaceName) is not { } surface)
        {
            await Console.Error.WriteLineAsync(
                $"Bilinmeyen MCP yüzeyi: '{surfaceName}'. "
                + $"Beklenen: '{McpSurfaces.ProductName}' ya da '{McpSurfaces.SimulatorName}'.")
                .ConfigureAwait(false);

            return 2;
        }

        using var loggerFactory = verbose
            ? LoggerFactory.Create(logging => logging
                .SetMinimumLevel(LogLevel.Debug)

                // stderr — stdout protokolün. Konsol sağlayıcısının varsayılanı
                // stdout ve orada bırakmak akışı ilk günlük satırında bozardı.
                .AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace))
            : LoggerFactory.Create(static logging => logging.ClearProviders());

        await using var services = BuildServices();

        await McpStdioHost.RunAsync(
            surface,
            Assembly.GetExecutingAssembly(),
            services,
            loggerFactory,
            cancellationToken).ConfigureAwait(false);

        return 0;
    }

    /// <summary>
    /// Yüzeyin araçlarının ihtiyaç duyduğu servisler.
    ///
    /// <para>
    /// <b>Bu metot aynı zamanda bir DERLEME BAĞI, ve bu ikinci işi kasıtlı</b>
    /// (M03/M04). Derleyici, kodunda hiçbir tipine dokunulmayan bir
    /// <c>ProjectReference</c>'ı meta veriden <b>buduyor</b>; budanan derlemeyi
    /// <c>McpToolDiscovery.ProductAssemblies</c> referans tablosunda
    /// <b>göremiyor</b> ve araçları <b>sessizce</b> ilan edilmiyor — hata yok,
    /// sayaç yok, uyum kapısı yeşil.
    /// </para>
    ///
    /// <para>
    /// <b>Ölçüldü, ve tuzağın canlı örneği bu depoda duruyor:</b>
    /// <c>bizigo.dll</c>'in derleme referansları arasında
    /// <c>Bizigo.Simulators</c> <b>var</b> ama <c>Bizigo.Query</c> <b>yok</b> —
    /// ikisinin de <c>ProjectReference</c>'ı olmasına rağmen. Simülatör bugün
    /// ayakta çünkü <c>FleetCommandHandlers</c> <c>bizigo fleet apply</c> için
    /// <c>FleetStore</c>'a dokunuyor. <b>Yani bugün çalışması bir güvence değil
    /// tesadüf:</b> o komut kaldırılırsa ya da başka bir derlemeye taşınırsa
    /// yedi <c>sim.*</c> aracı ilan edilmez.
    /// </para>
    ///
    /// <para>
    /// Buradaki çağrı bağı tesadüften çıkarıyor: araçların ilan edilmesi
    /// artık <b>araçların kaydına</b> bağlı, filo komutunun varlığına değil.
    /// </para>
    ///
    /// <para>
    /// ⚠ <b>GEÇİCİ.</b> M05 <c>AddBizigoMcpCore</c>'a araç derlemelerini
    /// <b>açıkça</b> aldıracak; o gün doğru cevap bir çağrı yan etkisi değil
    /// <c>SimulatorMcpSetup.ToolAssembly</c>'nin o listeye verilmesi olacak ve
    /// bu paragraf silinecek.
    /// </para>
    /// </summary>
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        // KAYIT YÜZEYE BAĞLI DEĞİL — ve ilk hâli öyleydi, ÖLÇÜLEREK düzeltildi.
        //
        // `McpToolDiscovery.Instantiate` bulduğu HER aracı KURUYOR, yüzeye göre
        // ancak kurduktan SONRA eliyor — `Surface` bir örnek özelliği, yani
        // örneklemeden okunamıyor. Dolayısıyla bir yüzeyi sunmak, keşfin
        // ulaştığı BÜTÜN araçların bağımlılıklarını istiyor.
        //
        // Kayıt `if (surface is Simulator)` ile sınırlıyken ölçülen sonuç:
        // `bizigo mcp serve --surface bizigo` (ÜRÜN yüzeyi) hiç ayağa
        // kalkmıyordu — keşif `Bizigo.Simulators`'a ulaşıp yedi aracı kurmaya
        // çalışıyor ve `SimulatorMcpContext` kayıtlı olmadığı için patlıyordu.
        // Aynı kusur `McpIdentityTests.Kapsam_cozucusu_kayitli_degilse_kurulum_patliyor`'u
        // da düşürdü: o test kökü `Bizigo.UnitTests` veriyor ve oradan da
        // simülatöre ulaşılıyor.
        //
        // Kaydın kendisi bir şey İLAN ETMİYOR: araçlar yüzeylerini kendileri
        // beyan ediyor ve ürün yüzeyinde hiçbiri ilan edilmiyor. Burada olan
        // tek şey, kurulabilir olmaları.
        services.AddBizigoSimulatorTools();

        return services.BuildServiceProvider();
    }
}
