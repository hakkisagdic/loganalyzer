using Bizigo.Mcp;
using Bizigo.Mcp.Product;

namespace Bizigo.Api;

/// <summary>
/// MCP'nin <b>akışlanabilir HTTP</b> taşıması (M01) — kaydı ve ucu.
///
/// <para>
/// <b>Neden bu dosya burada, <c>Bizigo.Mcp</c> içinde değil.</b> İki ayrı
/// sebep var ve ikisi de ölçüldü:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Kapı görünürlüğü.</b> <c>ProducesContractTests</c> yalnızca
/// <c>Bizigo.Api</c> derlemesindeki <c>Map*</c> uzantılarını buluyor. Kayıt
/// başka bir derlemede olsaydı MCP uçları o kapıya <b>hiç görünmezdi</b> — ve
/// bu depoda görünmez kalan uçların bedeli ölçüldü (T21/T22/T24: 16 uç, üç test
/// de yeşil). Yerleşimle kapı atlamak, kapıyı silmenin sessiz hâli.
/// </item>
/// <item>
/// <b>ASP.NET bağımlılığının yeri.</b> HTTP taşıması <c>Microsoft.AspNetCore.App</c>
/// istiyor. Onu <c>Bizigo.Mcp</c>'ye koymak, stdio için oraya referans veren
/// <c>Bizigo.Cli</c>'yi bir web çatısına sürüklüyordu; sonucu birim test
/// paketinde beş ayrı CS0433 oldu. Web çatısı zaten burada.
/// </item>
/// </list>
///
/// <para>
/// <b>Uçlar OpenAPI belgesine girmiyor</b> (<c>ExcludeFromDescription</c>).
/// MCP JSON-RPC konuşuyor: aynı yol duruma göre tek bir yanıt ya da bir SSE
/// akışı döndürüyor, ve gövdenin şekli çağrılan <i>araca</i> bağlı. Bunu bir
/// REST yanıt tipiyle tarif etmek uydurma bir sözleşme yazmak olurdu (§8:
/// tüketicisi olmayan bir tip tahmindir) — üstelik T14'ün ürettiği TypeScript'e
/// kimsenin kullanmadığı bir tip inerdi. Aynı sebeple
/// <c>ProducesContractTests.Exempt</c>'te gerekçeli satırları var.
/// </para>
/// </summary>
public static class McpEndpoints
{
    /// <summary>
    /// MCP çekirdeğini ve <b>HTTP taşımasını</b> kaydeder.
    ///
    /// <para>
    /// Kompozisyon kökü burada belli olduğu için imza sade kalıyor:
    /// <c>Bizigo.Api</c> zaten kökün kendisi.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBizigoMcp(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // SIRA ÖNEMLİ VE GEREKÇESİ ÖLÇÜLDÜ. Bu çağrı, M04'ün araç derlemesini
        // keşfin referans kapanışına sokan şey: `csproj`'daki `ProjectReference`
        // TEK BAŞINA YETMİYOR, çünkü derleyici kodda hiç kullanılmayan referansı
        // meta veriden düşürüyor. Ölçüm ve iki bekçi
        // `BizigoReadToolsSetup` belgesinde.
        services.AddBizigoReadTools();

        services
            .AddBizigoMcpCore(configuration, typeof(Program).Assembly)
            .WithHttpTransport();

        return services;
    }

    /// <summary>
    /// Ürün yüzeyini (<c>bizigo</c>) <see cref="BizigoMcpServer.HttpPath"/>
    /// üzerinde yayınlar.
    /// </summary>
    public static IEndpointRouteBuilder MapBizigoMcp(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapMcp(BizigoMcpServer.HttpPath)

            // MCP istemcisi bir KULLANICI adına konuşuyor (plan §6). Kimliğin
            // uçtan uca taşınması M08'in işi; buradaki kapı onun ön şartı —
            // anonim bir MCP oturumu bütün kapsam kapılarını atlardı.
            .RequireAuthorization()

            .ExcludeFromDescription();

        return endpoints;
    }
}
