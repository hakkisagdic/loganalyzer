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
    /// <param name="compositionRoot">
    /// Araçların aranacağı kök derleme — normalde <c>Bizigo.Api</c>. Gerekçesi
    /// <c>BizigoMcpServer</c> belgesinde: referans oku ters yöne bakıyor ve
    /// <c>AppDomain</c> taraması sessizce eksik kalıyor.
    /// </param>
    public static IMcpServerBuilder AddBizigoMcpCore(
        this IServiceCollection services,
        IConfiguration configuration,
        Assembly compositionRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(compositionRoot);

        var builder = services.AddMcpServer();

        // Seçenekler HTTP tarafında da `Apply`'den geçiyor. İkinci bir kurulum
        // yazmak, uyum kapısının ölçtüğü sunucu ile üretimde koşan sunucuyu
        // ayırırdı — kapının anlamını yok eden tek hareket bu olurdu.
        services
            .AddOptions<McpServerOptions>()
            .Configure<IServiceProvider>((options, provider) =>
                BizigoMcpServer.Apply(options, McpSurface.Product, compositionRoot, provider));

        return builder;
    }
}
