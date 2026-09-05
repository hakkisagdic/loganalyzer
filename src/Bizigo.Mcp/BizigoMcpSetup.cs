using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Bizigo.Mcp;

/// <summary>
/// MCP çekirdeğinin DI kaydı — <b>taşımasız</b>.
///
/// <para>
/// Taşıma bilerek burada değil: HTTP taşıması ASP.NET Core istiyor ve onu bu
/// projeye bağlamak, stdio için buraya referans veren <c>Bizigo.Cli</c>'yi bir
/// web çatısına sürüklüyordu (gerekçe ve ölçüm <c>Bizigo.Mcp.csproj</c>
/// yorumunda). Taşımayı çağıran seçiyor:
/// <c>Bizigo.Api</c> → <c>.WithHttpTransport()</c>, <c>Bizigo.Cli</c> →
/// <c>McpStdioHost</c>.
/// </para>
/// </summary>
public static class BizigoMcpSetup
{
    /// <summary>
    /// MCP sunucusunu kaydeder ve seçeneklerini
    /// <see cref="BizigoMcpServer.Apply"/> ile doldurur.
    ///
    /// <para>
    /// Yalnızca <see cref="McpSurface.Product"/> yüzeyi. <c>bizigo-sim</c>
    /// buraya <b>bilerek</b> girmiyor: simülatör kontrolü ürün API'sinden
    /// sürülebilseydi, ürünün kendi süreci simülatör filosunu değiştirebilirdi.
    /// Simülatörün kendi süreci var (<c>sim/Bizigo.Simulators</c>) ve
    /// <c>bizigo-sim</c>'in HTTP yüzeyi oraya ait — M03'ün işi. stdio tarafında
    /// iki yüzey de bugün açık (<c>bizigo mcp serve --surface</c>).
    /// </para>
    /// </summary>
    /// <param name="services">Servis koleksiyonu.</param>
    /// <param name="configuration">Uygulama yapılandırması.</param>
    /// <param name="toolAssemblies">
    /// Araç <b>taşıyan</b> derlemeler — kompozisyon kökü değil.
    ///
    /// <para>
    /// Her biri <c>typeof(X).Assembly</c> ile verilmeli: bir tip adı yazmak
    /// referansı <b>gerçek</b> yapıyor. İlk imza kökü alıp referans kapanışını
    /// çıkarıyordu ve sessizce eksik çalışıyordu — derleyici, kodunda hiçbir
    /// tipine dokunulmayan projeyi meta veriden buduyor. Ayrıntı ve ölçüm
    /// <c>McpToolDiscovery</c> içinde.
    /// </para>
    ///
    /// <para>
    /// Boş bırakmak <b>geçerli</b>: <c>Bizigo.Mcp</c> her zaman ekleniyor, yani
    /// sunucu en azından <c>server.info</c>'yu ilan ediyor. "Çağıran unuttu"
    /// hâlini bir çalışma zamanı hatası değil bir <b>bekçi</b> tutuyor
    /// (<c>McpToolAssemblyTests</c>) — çünkü çalışma zamanında "unutuldu" ile
    /// "gerçekten yok" ayırt edilemez.
    /// </para>
    /// </param>
    public static IMcpServerBuilder AddBizigoMcpCore(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] toolAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(toolAssemblies);

        var builder = services.AddMcpServer();

        // Seçenekler HTTP tarafında da `Apply`'den geçiyor. İkinci bir kurulum
        // yazmak, uyum kapısının ölçtüğü sunucu ile üretimde koşan sunucuyu
        // ayırırdı — kapının anlamını yok eden tek hareket bu olurdu.
        services
            .AddOptions<McpServerOptions>()
            .Configure<IServiceProvider>((options, provider) =>
                BizigoMcpServer.Apply(options, McpSurface.Product, toolAssemblies, provider));

        return builder;
    }
}
