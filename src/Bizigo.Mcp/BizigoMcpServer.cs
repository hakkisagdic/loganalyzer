using System.Reflection;
using Bizigo.Mcp.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bizigo.Mcp;

/// <summary>
/// Sunucu seçeneklerini kuran <b>tek</b> yer.
///
/// <para>
/// Üç tüketicisi var — HTTP taşıması (<c>AddBizigoMcp</c>), stdio taşıması
/// (<c>McpStdioHost</c>) ve uyum kapısı — ve üçü de <see cref="Apply"/>'den
/// geçiyor. <b>Bu bir tercih değil bir şart:</b> bu depoda iki liste sessizce
/// ayrışıyor, ve ayrışan iki sunucu kurulumu "kapı yeşildi ama üretimde başka
/// araçlar var" demek olurdu. Uyum kapısının ölçtüğü şeyin üretimde koşan şey
/// olması, kapının anlamının tamamı.
/// </para>
///
/// <para>
/// <b>Neden araç derlemeleri elle veriliyor.</b> Araçlar <c>Bizigo.Mcp</c>'nin
/// <b>aşağısında</b> yaşıyor (M03/M04/M05 kendi katmanlarında), yani buradan
/// referansları izleyerek onlara ulaşmak mümkün değil — referans oku ters yöne
/// bakıyor. <c>AppDomain</c>'i taramak da elendi: dokunulmamış bir derleme
/// yüklü olmuyor ve keşif onu <b>sessizce</b> atlıyor.
/// </para>
///
/// <para>
/// <b>Kompozisyon kökünü almak da yetmedi — ÖLÇÜLDÜ.</b> İlk imza kökü alıp
/// <c>GetReferencedAssemblies()</c> ile kapanışı çıkarıyordu; derleyici,
/// kodunda hiçbir tipine dokunulmayan <c>ProjectReference</c>'ı meta veriden
/// <b>buduyor</b> ve araç derlemesi tam da o yüzden görünmüyordu. Ayrıntı
/// <c>McpToolDiscovery</c> içinde.
/// </para>
///
/// <para>
/// Şimdiki hâl: derlemeler <b>tip adıyla</b> veriliyor
/// (<c>typeof(X).Assembly</c>), yani referans <b>gerçek</b> oluyor ve budama
/// tanım gereği olmuyor. <c>Bizigo.Mcp</c>'nin kendisi her zaman listede —
/// <c>server.info</c>'yu unutmak mümkün değil.
/// </para>
/// </summary>
public static class BizigoMcpServer
{
    /// <summary>Ürün yüzeyinin HTTP yolu.</summary>
    public const string HttpPath = "/mcp";

    /// <summary>
    /// Verilen derlemelerdeki araçlar <b>keşifle</b> bulunuyor — elle bir araç
    /// listesi yok. Elle olan tek şey <b>hangi derlemelere bakılacağı</b>.
    /// </summary>
    /// <param name="surface">Hangi yüzey.</param>
    /// <param name="toolAssemblies">
    /// Araç taşıyan derlemeler. <c>Bizigo.Mcp</c> her zaman ekleniyor.
    /// </param>
    /// <param name="services">Araçların bağımlılıklarını çözecek sağlayıcı.</param>
    public static IReadOnlyList<BizigoMcpTool> Tools(
        McpSurface surface,
        IEnumerable<Assembly> toolAssemblies,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(toolAssemblies);

        var types = McpToolDiscovery.ToolTypes(WithCore(toolAssemblies));
        var tools = McpToolDiscovery.Instantiate(types, surface, services);

        // `server.info` ilan edilen araç sayısını söylüyor; sayı ancak küme
        // tamamlandıktan sonra biliniyor. Kayıt sırasında doldurmak, aracın
        // kendisini saymayı unutan bir sayı üretirdi.
        foreach (var info in tools.OfType<ServerInfoTool>())
        {
            info.DeclaredToolCount = tools.Count;
        }

        return tools;
    }

    /// <summary>
    /// Taşımadan bağımsız sunucu seçenekleri. stdio ve uyum kapısı bunu
    /// doğrudan kullanıyor; HTTP tarafı <c>AddBizigoMcp</c> üzerinden aynı
    /// <see cref="Apply"/>'yi çağırıyor.
    /// </summary>
    public static McpServerOptions CreateOptions(
        McpSurface surface,
        McpBoundaryDeclaration boundary,
        IEnumerable<Assembly> toolAssemblies,
        IServiceProvider services)
    {
        var options = new McpServerOptions();

        Apply(options, surface, boundary, toolAssemblies, services);

        return options;
    }

    /// <summary>Seçenekleri yüzeye göre doldurur.</summary>
    /// <param name="options">Doldurulacak seçenekler.</param>
    /// <param name="surface">Hangi yüzey.</param>
    /// <param name="boundary">
    /// Yüzeyin <b>K6 beyanı</b> (M06). Beyansız bir sunucu kurulamıyor: tip
    /// <c>Unspecified</c> ile hiç var olamıyor, ve <c>external</c> beyan edilmiş
    /// bir ürün yüzeyi aşağıdaki kapıdan geçemiyor.
    /// </param>
    /// <param name="toolAssemblies">
    /// Araç <b>taşıyan</b> derlemeler; <c>Bizigo.Mcp</c> her zaman ekleniyor.
    /// </param>
    /// <param name="services">Araçların bağımlılıklarını çözecek sağlayıcı.</param>
    public static void Apply(
        McpServerOptions options,
        McpSurface surface,
        McpBoundaryDeclaration boundary,
        IEnumerable<Assembly> toolAssemblies,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(toolAssemblies);
        ArgumentNullException.ThrowIfNull(services);

        // K6 kapısı EN BAŞTA — ve `ServerInfo` doldurulmadan önce. Araçları
        // keşfedip sonra reddetmek, reddedilen bir sunucunun yarı kurulmuş
        // hâlini üretirdi; burada patlaması, o hâlin hiç oluşmaması demek.
        //
        // Beyan tel üzerinde İLAN EDİLMİYOR ve bu iki gerekçeyle: §8 —
        // tüketicisi olmayan bir alan uydurmak olurdu; ve istemciye kurumun ağ
        // topolojisi hakkında bilgi vermenin bir sebebi yok. Beyan kapıda
        // duruyor, uyum kapısı onu ölçüyor.
        McpBoundaryGate.Require(boundary, surface);

        // `ProtocolVersion` BİLEREK atanmıyor — ve bu, ilk yazılan hâlin
        // ÖLÇÜLEREK düzeltilmiş hâli.
        //
        // İlk hâli `= McpRevision.Supported` idi. Sonucu şu: SDK o revizyonu
        // TEK desteklenen sürüm hâline getiriyor ve `2025-11-25` konuşan bir
        // istemci el sıkışmada `UnsupportedProtocolVersionException` alıyor.
        // Uyum kapısının "anlaşma eski istemciyi indiriyor mu" testi bunu ilk
        // koşumda yakaladı.
        //
        // Yani sabiti buraya yazmak, planın §2'sinde ŞART olan sürüm
        // anlaşmasını kapatıyordu: bir çivi gibi görünen şey aslında bir
        // kısıtlamaydı.
        //
        // Doğru ayrım şu: `McpRevision.Supported` bir HEDEF, bir tavan değil.
        // Sunucu SDK'nın bildiği bütün revizyonlarla anlaşıyor; uyduğumuz
        // revizyonun yazılı sabitle aynı kaldığını `McpComplianceTests`
        // ÖLÇÜYOR — SDK bir gün başka bir revizyona geçtiğinde orası kırmızı
        // yanıyor. Kaymayı engelleyen şey kısıtlama değil ölçüm.
        options.ServerInfo = new Implementation
        {
            Name = McpSurfaces.WireName(surface),
            Title = surface is McpSurface.Simulator
                ? "Bizigo simülatör kontrolü"
                : "Bizigo Log Analyzer",
            Version = ServerVersion,
        };

        options.Capabilities = new ServerCapabilities
        {
            // YALNIZCA araçlar. Kaynaklar M07'de, istemler daha sonra.
            //
            // Desteklenmeyen bir yeteneği ilan etmek, istemciye olmayan bir
            // yolu göstermek demek: `resources/list` çağıran istemci protokol
            // hatası alıyor ve bunu bağlantı arızası sanıyor. Yetenek
            // anlaşmasının bütün amacı bu konuşmanın hiç olmaması.
            Tools = new ToolsCapability { ListChanged = false },
        };

        options.ToolCollection ??= [];
        options.ToolCollection.Clear();

        var tools = Tools(surface, toolAssemblies, services);

        RequireScopeResolverIfNeeded(tools, services);

        foreach (var tool in tools)
        {
            options.ToolCollection.Add(tool);
        }
    }

    /// <summary>
    /// Kimlik isteyen bir araç varsa kapsam çözücüsü <b>kurulumda</b> aranıyor.
    ///
    /// <para>
    /// <b>Neden burada, ilk çağrıda değil.</b> Eksik bir çözücü çağrı anında
    /// fark edilseydi arıza <i>"bu araç bende çalışmıyor"</i> diye görünürdü —
    /// yani kusurun kendisi değil belirtisi. Kurulumda patlamak sunucuyu hiç
    /// ayağa kaldırmıyor ve mesaj kusuru söylüyor. Kalıp
    /// <c>McpToolDiscovery.Instantiate</c>'ten: <i>"atlanmıyor, patlıyor"</i>.
    /// </para>
    ///
    /// <para>
    /// Bugün ürün yüzeyinde kimlik isteyen araç <b>yok</b> (<c>server.info</c>
    /// gerekçeli muaf), dolayısıyla bu kapı bugün hiçbir kurulumu düşürmüyor.
    /// M04'ün ilk aracıyla birlikte dişleniyor — ve o gün stdio tarafında
    /// (<c>bizigo mcp serve</c>, boş servis grafiği) <b>burada</b> duruyor.
    /// </para>
    /// </summary>
    private static void RequireScopeResolverIfNeeded(
        IReadOnlyList<BizigoMcpTool> tools,
        IServiceProvider services)
    {
        var demanding = tools.Where(static tool => tool.RequiresCallerIdentity).ToArray();

        if (demanding.Length == 0 || services.GetService(typeof(Contracts.IAccessScopeResolver)) is not null)
        {
            return;
        }

        throw new InvalidOperationException(
            McpCallerScope.MissingResolverMessage
            + $" Kimlik isteyen araçlar: {string.Join(", ", demanding.Select(static t => t.ToolName))}.");
    }

    /// <summary>
    /// Çağıranın verdiği derlemeler + <b>her zaman</b> <c>Bizigo.Mcp</c>.
    ///
    /// <para>
    /// Çekirdeğin kendisi <c>server.info</c>'yu taşıyor ve onu listeye yazmayı
    /// unutmak, uyum kapısını <b>öznesiz</b> bırakırdı: sıfır araçlı bir
    /// sunucuda şema testleri sıfır şema doğrular ve hepsi yeşil yanar.
    /// Örtük eklemek o unutmayı imkânsız kılıyor.
    /// </para>
    ///
    /// <para>
    /// Tekilleştirme <b>kimlik</b> üzerinden: aynı derleme iki kez verilirse
    /// araçları iki kez örneklenir ve <c>tools/list</c> aynı adı iki kez
    /// ilan ederdi — istemci tarafında tanımsız davranış.
    /// </para>
    /// </summary>
    private static IReadOnlyList<Assembly> WithCore(IEnumerable<Assembly> toolAssemblies)
    {
        var all = new List<Assembly> { typeof(BizigoMcpServer).Assembly };

        foreach (var assembly in toolAssemblies)
        {
            ArgumentNullException.ThrowIfNull(assembly);

            if (!all.Contains(assembly))
            {
                all.Add(assembly);
            }
        }

        return all;
    }

    /// <summary>
    /// Sunucu sürümü. Derlemenin bilgilendirici sürümünden; elle yazılan bir
    /// sabit ilk yükseltmede bayatlardı.
    /// </summary>
    private static string ServerVersion { get; } =
        typeof(BizigoMcpServer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BizigoMcpServer).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
