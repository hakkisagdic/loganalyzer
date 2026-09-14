using System.Reflection;
using Bizigo.Contracts.Security;
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
    /// HTTP yüzeyinin K6 beyanının okunduğu yapılandırma anahtarı.
    ///
    /// <para>
    /// Kalıp T42'nin <c>Rca:Model:DataBoundary</c>'sinin aynısı ve
    /// <c>appsettings.json</c>'da <b>açıkça yazılı</b> duruyor: eksik bir
    /// anahtar <c>Unspecified</c> demek ve MCP hiç ayağa kalkmıyor.
    /// </para>
    /// </summary>
    public const string DataBoundaryKey = "Mcp:DataBoundary";

    /// <summary>
    /// HTTP taşımasının beyanını yapılandırmadan okur.
    ///
    /// <para>
    /// <b>Tanınmayan bir değer sessizce <c>Unspecified</c>'a düşmüyor</b>:
    /// <c>"Internal "</c> yazan bir yapılandırma ile hiç yazmayan bir
    /// yapılandırma aynı hatayı verirse, birincinin sahibi anahtarı aramaya
    /// gider ve orada bulur. Ayrı mesaj, ayrı arama.
    /// </para>
    /// </summary>
    public static McpBoundaryDeclaration ReadBoundary(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[DataBoundaryKey];

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                $"`{DataBoundaryKey}` yapılandırılmamış. MCP sunucusu ağ sınırını BEYAN "
                + "etmeden kurulamıyor (K6): beyansız bir yüzey 'iç ağ' sayılsaydı, "
                + "kurumun en büyük sözü hiç kimse karar vermeden boşa çıkardı. "
                + "`Internal` ya da `External` yazın.");
        }

        if (!Enum.TryParse<DataBoundary>(raw, ignoreCase: true, out var boundary))
        {
            throw new InvalidOperationException(
                $"`{DataBoundaryKey}` çözümlenemedi: '{raw}'. Beklenen: "
                + $"`{nameof(DataBoundary.Internal)}` ya da `{nameof(DataBoundary.External)}`.");
        }

        return McpBoundaryDeclaration.Declare(boundary, $"yapılandırma: `{DataBoundaryKey}`");
    }

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

        // Beyan BURADA okunuyor, `Configure` geri çağrısının içinde DEĞİL — ve
        // fark ölçülebilir: geri çağrı `McpServerOptions` ilk çözüldüğünde
        // koşuyor, yani eksik bir `Mcp:DataBoundary` ilk MCP isteğinde
        // patlardı. Burada okumak hatayı KAYIT ANINA, yani uygulama
        // kurulumuna çekiyor: yanlış yapılandırılmış bir sunucu hiç
        // başlamıyor, yarım başlamıyor.
        var boundary = ReadBoundary(configuration);

        // Seçenekler HTTP tarafında da `Apply`'den geçiyor. İkinci bir kurulum
        // yazmak, uyum kapısının ölçtüğü sunucu ile üretimde koşan sunucuyu
        // ayırırdı — kapının anlamını yok eden tek hareket bu olurdu.
        services
            .AddOptions<McpServerOptions>()
            .Configure<IServiceProvider>((options, provider) =>
                BizigoMcpServer.Apply(options, McpSurface.Product, boundary, toolAssemblies, provider));

        return builder;
    }
}
