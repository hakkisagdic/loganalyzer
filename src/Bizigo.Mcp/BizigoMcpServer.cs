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
    /// Verilen derlemelerdeki <b>kaynaklar</b> — araçlarla aynı keşifle, aynı
    /// derleme listesinden.
    ///
    /// <para>
    /// <b>Kaynaklar için İKİNCİ bir derleme listesi yok</b> ve bu bilinçli.
    /// Yüzey ayrımı zaten bu listede yapısal (<c>McpEndpoints.ToolAssemblies</c>,
    /// <c>McpCommandHandlers.ToolAssembliesFor</c>): <c>bizigo-sim</c>'e ürün
    /// derlemesi hiç verilmiyor, dolayısıyla simülatör yüzeyi ürün <b>kaynağı</b>
    /// da göremiyor — araç tarafındaki iddianın aynısı, bedava.
    /// </para>
    ///
    /// <para>
    /// İkinci bir liste yazmak, ayrışması <b>sessiz</b> olacak bir kopya
    /// olurdu: bir derleme araç listesinde olup kaynak listesinde olmadığında
    /// kaynakları <i>hiç ilan edilmiyor</i> ve hiçbir şey kırmızı yanmıyor —
    /// bu deponun dört kez ödediği delik. Kaynak taşıyan bir derlemenin araç
    /// taşımıyor olması ise sorun değil: keşif boş küme döndürür ve yetenek
    /// ilanı <see cref="Apply"/>'de gerçeğe bağlı.
    /// </para>
    /// </summary>
    public static IReadOnlyList<BizigoMcpResource> Resources(
        McpSurface surface,
        IEnumerable<Assembly> toolAssemblies,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(toolAssemblies);

        var types = McpPrimitiveDiscovery.Types<BizigoMcpResource>(WithCore(toolAssemblies));

        return McpPrimitiveDiscovery.Instantiate<BizigoMcpResource>(
            types,
            surface,
            static resource => resource.Surface,
            services);
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
        IServiceProvider services,
        bool subscriptionsDeliverable = false)
    {
        var options = new McpServerOptions();

        Apply(options, surface, boundary, toolAssemblies, services, subscriptionsDeliverable);

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
    /// <param name="subscriptionsDeliverable">
    /// Bu taşıma abonelik bildirimini <b>gönderebiliyor mu</b> — yani canlı bir
    /// sunucu örneği <see cref="McpResourceUpdates"/> defterinde tutulabiliyor mu.
    ///
    /// <para>
    /// <b>Varsayılan <see langword="false"/> ve bu bilinçli:</b> burada tehlikeli
    /// taraf <i>fazla ilan etmek</i>. Gönderilemeyecek bir aboneliği ilan eden
    /// sunucu, istemciyi belgeyi bir daha sormamaya ikna ediyor — sessizce bayat
    /// veri. Yeni bir taşıma eklendiğinde unutulan bayrak, o taşımayı
    /// <i>abonelik yok</i> tarafına düşürüyor; tersi olsaydı unutkanlığın bedeli
    /// yanlış bir söz olurdu. Kalıp <see cref="McpSurface.Unspecified"/>'ınkiyle
    /// aynı.
    /// </para>
    ///
    /// <para>
    /// stdio <see langword="true"/> veriyor (tek, uzun ömürlü sunucu);
    /// akışlanabilir HTTP <b>vermiyor</b> — gerekçe ve ölçüm
    /// <see cref="McpResourceUpdates"/> belgesinde.
    /// </para>
    /// </param>
    public static void Apply(
        McpServerOptions options,
        McpSurface surface,
        McpBoundaryDeclaration boundary,
        IEnumerable<Assembly> toolAssemblies,
        IServiceProvider services,
        bool subscriptionsDeliverable = false)
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

        // KAYNAKLAR (M07). Yetenek ilanı KEŞFEDİLEN KÜMEYE bağlı — sabit değil.
        //
        // `resources` ilan edip hiç kaynak sunmayan bir sunucu istemciye olmayan
        // bir söz veriyor: istemci `resources/list` çağırıyor, boş liste alıyor
        // ve bunu "bugün belge yok" diye okuyor — oysa doğrusu "bu sunucu belge
        // sunmuyor". Tersi daha kötü: kaynak sunup yeteneği ilan etmeyen bir
        // sunucuda istemci kanalı HİÇ denemiyor, yani üç belge türü yazılmış ama
        // ulaşılamaz kalıyor ve hiçbir şey kırmızı yanmıyor.
        var resources = Resources(surface, toolAssemblies, services);

        RequireScopeResolverIfNeeded(resources, services);

        if (resources.Count == 0)
        {
            // BOŞ KOLEKSİYON BIRAKILMIYOR, VE BU ÖLÇÜLDÜ.
            //
            // İlk hâli `options.ResourceCollection ??= []` idi ve yüzey ayrımı
            // buna güveniyordu: simülatörün kaynağı yok, dolayısıyla yeteneği de
            // ilan edilmez sanılıyordu. Ölçüm tersini gösterdi — `bizigo-sim`
            // el sıkışmasında `ResourcesCapability { ListChanged = True }`
            // çıktı. Sebebi SDK: KOLEKSİYONUN VARLIĞI yeteneği ilan ettiriyor,
            // içi boş olsa bile.
            //
            // Yani "ilan etmiyorum" niyeti tek başına yetmiyordu; ilan
            // edilmemesi için koleksiyonun HİÇ OLMAMASI gerekiyor. Kendi
            // yazdığım `Capabilities.Resources` koşulu doğruydu ve YETERSİZDİ —
            // bu deponun §7'de tarif ettiği sınıf: niyet doğru, mekanizma
            // eksik, ve fark ancak tel üzerinden ölçülünce görünüyor.
            options.ResourceCollection = null;

            return;
        }

        options.ResourceCollection ??= [];
        options.ResourceCollection.Clear();

        foreach (var resource in resources)
        {
            options.ResourceCollection.Add(resource);
        }

        options.Capabilities.Resources = new ResourcesCapability
        {
            // ABONELİK GERÇEĞE BAĞLI, VE İKİ KOŞULA BİRDEN.
            //
            // 1) Bir kaynak abonelik destekliyor mu (`SupportsSubscription`).
            // 2) Bu TAŞIMA bildirimi gönderebiliyor mu
            //    (`subscriptionsDeliverable`).
            //
            // İkincisi ölçülerek eklendi: çivilediğimiz revizyon (2026-07-28,
            // SEP-2567) `Mcp-Session-Id`'yi kaldırdı, yani akışlanabilir HTTP'de
            // OTURUM YOK ve tutulacak bir sunucu örneği de yok. O taşımada
            // `subscribe` ilan etmek, gönderilemeyecek bir bildirimi vaat etmek
            // olur — istemci belgeyi bir daha SORMAZ ve sonuç sessizce bayat
            // veri. Kalıp SDK'nın kendi kararından: o da `listChanged`'ı
            // "onurlandırmasının yolu olmadığı" yanıtlarda bastırıyor.
            //
            // ÖLÇÜLDÜ (ham JSON-RPC, `McpResourceSubscriptionTests`): bu bayrak
            // `false` iken sunucu `subscriptions/listen`'in
            // `resourceSubscriptions` isteğini ONAYLAMIYOR (`notifications:{}`
            // dönüyor). Yani bayrak bir süsleme değil, aboneliğin kabul
            // edilmesinin şartı.
            Subscribe = subscriptionsDeliverable
                && resources.Any(static resource => resource.SupportsSubscription),

            // `ListChanged` BİLEREK ATANMIYOR: SDK onu koleksiyonun kendisinden
            // türetiyor ve `true` yazıyor (koleksiyon gözlemlenebilir, bildirim
            // bağlı). Buraya `false` yazmak ÖLÇÜLDÜ ve tel üzerinde `True`
            // çıktı — yani yazdığım değer değil SDK'nın değeri ilan ediliyordu.
            // Ezilen bir değeri yazmaya devam etmek, kodda duran ama gerçekle
            // ilgisi olmayan bir iddia bırakmak olurdu.
        };
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
        IServiceProvider services) =>
        RequireScopeResolver(
            tools.Where(static tool => tool.RequiresCallerIdentity).Select(static tool => tool.ToolName),
            services);

    /// <summary>
    /// Aynı kapı kaynak kanalında. <b>İkinci bir mesaj yazılmadı</b>: aynı
    /// kusurun iki farklı arıza gibi okunması, bu deponun §7'de adı konmuş
    /// hâli.
    /// </summary>
    private static void RequireScopeResolverIfNeeded(
        IReadOnlyList<BizigoMcpResource> resources,
        IServiceProvider services) =>
        RequireScopeResolver(
            resources
                .Where(static resource => resource.RequiresCallerIdentity)
                .Select(static resource => McpResourceUri.Fixed(resource.Kind)),
            services);

    private static void RequireScopeResolver(IEnumerable<string> demanding, IServiceProvider services)
    {
        var names = demanding.ToArray();

        if (names.Length == 0 || services.GetService(typeof(Contracts.IAccessScopeResolver)) is not null)
        {
            return;
        }

        throw new InvalidOperationException(
            McpCallerScope.MissingResolverMessage
            + $" Kimlik isteyenler: {string.Join(", ", names)}.");
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
