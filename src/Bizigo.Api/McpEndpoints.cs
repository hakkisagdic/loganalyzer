using Bizigo.Mcp;

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
    /// <b>Ürün yüzeyinin araç derlemeleri — üretimin beyanı.</b>
    ///
    /// <para>
    /// Her giriş <c>typeof(X).Assembly</c> biçiminde olmalı. Bir dizgi ya da
    /// <c>Assembly.Load</c> yazmak, düzeltilen kusuru geri getirirdi: tip adı
    /// yazmak referansı <b>gerçek</b> yapıyor ve derleyicinin budaması
    /// imkânsızlaşıyor.
    /// </para>
    ///
    /// <para>
    /// <c>Bizigo.Mcp</c> burada <b>yok</b> ve olmamalı — çekirdek her zaman
    /// örtük olarak ekleniyor (<c>BizigoMcpServer.WithCore</c>). Buraya
    /// yazmak, unutulabilir bir şeyi unutulabilir listeye koymak olurdu.
    /// </para>
    ///
    /// <para>
    /// Bugün <b>boş</b>: ürün araçları M03/M04/M05 ile geliyor. Boşluğun
    /// kendisi bir bekçiyle korunuyor (<c>McpToolAssemblyTests</c>): derlenmiş
    /// çıktıda araç taşıyan bir derleme varsa ve burada değilse <b>kırmızı</b>.
    /// </para>
    /// </summary>
    public static IReadOnlyList<System.Reflection.Assembly> ToolAssemblies { get; } = [];

    /// <summary>
    /// MCP çekirdeğini ve <b>HTTP taşımasını</b> kaydeder.
    /// </summary>
    public static IServiceCollection AddBizigoMcp(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddBizigoMcpCore(configuration, [.. ToolAssemblies])
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
            //
            // M09: şema AÇIKÇA veriliyor. Varsayılana bırakmak iki şeyi birden
            // bozardı — meydan okuma `WWW-Authenticate: Bearer
            // resource_metadata="…"` taşımaz (RFC 9728) ve token API'nin
            // kitlesiyle doğrulanır, yani API için basılmış bir token burada da
            // geçerdi (RFC 8707). Bu uç, şemasını belirten TEK uç ve öyle
            // kalmalı: diğer 45 çağrı varsayılanda duruyor.
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(BizigoAuthSchemes.Mcp)
                .RequireAuthenticatedUser())

            .ExcludeFromDescription();

        return endpoints;
    }
}
