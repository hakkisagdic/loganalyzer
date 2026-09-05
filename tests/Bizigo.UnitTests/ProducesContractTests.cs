using System.IO.Pipelines;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.UnitTests;

/// <summary>
/// Her ürün ucu bir <c>Produces&lt;T&gt;</c> taşımalı — ve bugün taşımayanlar
/// aşağıdaki <b>iki listeden</b> birinde duruyor.
///
/// <para>
/// <b>Neden bekçi gerekiyor:</b> yanıt tipi bildirilmeyen bir uç OpenAPI
/// belgesine gövdesiz iniyor, T14'ün ürettiği TypeScript'te <c>unknown</c>
/// kalıyor ve ekran tipi <b>elle</b> yazmak zorunda kalıyor. Elle yazılan tip
/// API ile sessizce ayrışır; T14'ün var olma sebebi tam olarak bunu önlemekti.
/// </para>
///
/// <para>
/// <b>Neden iki liste:</b> tek liste iki farklı şeyi taşıyordu — "henüz tipsiz,
/// ekran indikçe çıkacak" ile "hiçbir zaman tip almayacak". Karışınca
/// <b>"liste boşaldı mı" sorusunun cevabı asla evet olamıyordu</b> ve T27'nin
/// kabul kriteri sağlanamaz hâle geliyordu. <see cref="Pending"/> küçülüyor ve
/// boşalmadan F2 bitmiyor; <see cref="Exempt"/> ise gerekçesiyle sabit duruyor
/// ve <b>büyümesi testi kırıyor</b> (bkz. <see cref="ExpectedExemptCount"/>).
/// </para>
///
/// <para>
/// <b>Kapı denetlediği kümeyi kendisi buluyor.</b> Önceki hâli uçları elle
/// yazılmış bir <c>Map*</c> listesinden topluyordu; T21/T22/T24 indiğinde 16 uç
/// kapıya hiç görünmedi ve üç testin üçü de <b>geçti</b>. Bir bekçinin en
/// tehlikeli başarısızlık biçimi buydu: yeşil yanıyordu ve yeşilliği hiçbir şey
/// ifade etmiyordu. Artık <c>Bizigo.Api</c> derlemesindeki her
/// <c>IEndpointRouteBuilder</c> uzantısı <b>yansımayla bulunup çağrılıyor</b>,
/// yani unutulacak bir liste yok.
/// </para>
///
/// <para>
/// Uygulama gerçekten başlatılmıyor, yalnızca uçlar kaydediliyor. Servisler
/// çözülmüyor — kayıtlar yalnızca minimal API'nin parametreyi "servis mi gövde
/// mi" diye ayırt edebilmesi için var (kalıp <c>ParsersEndpointTests</c>'ten).
/// </para>
/// </summary>
public sealed class ProducesContractTests
{
    /// <summary>
    /// <b>Küçülen izin listesi.</b> Her satır bir eksik yanıt tipi; karşısındaki
    /// ticket onu kapatacak olan ekran.
    ///
    /// <para>
    /// Bir satır silinirken uca <c>Produces&lt;T&gt;</c> eklenmiş olmalı; test
    /// listede olup da tipi <b>olan</b> bir ucu da hata sayıyor, yani liste
    /// kendiliğinden bayatlayamıyor. <b>Boşalmadan F2 bitmiş sayılmıyor</b> (T27).
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> Pending = new(StringComparer.Ordinal)
    {
        // BOŞ — ve F2'nin bitiş şartlarından biri buydu (T27).
        //
        // Yirmi bir satırdan sıfıra indi; her satır, o ucu tüketen ekranla
        // birlikte gitti. Son satır `POST /v1/replay`'di ve sahipsizdi:
        // muafiyete taşımak "hiç tüketicisi olmayacak" demek olurdu ve
        // replay'in bir gün ekranı olacak, tiplendirmek ise tahmin değildi —
        // uç zaten `ReplayReport`'u döndürüyordu, yani domain tipi fiilen tel
        // sözleşmesiydi.
        //
        // Buraya satır eklemek serbest ama bedeli görünür:
        // `Izin_listesi_bosaldi_mi` kırmızı yanıyor.
    };

    /// <summary>
    /// <b>Kalıcı muafiyetler.</b> Bunların tüketicisi hiç olmayacak, dolayısıyla
    /// bir yanıt tipi yazmak <see cref="Pending"/>'in kaçındığı şeyi yapmak
    /// olurdu: tüketicisi olmayan bir tip tahmindir.
    ///
    /// <para>
    /// Muafiyet <b>bedava değil</b>. Buraya bir satır eklemek
    /// <see cref="ExpectedExemptCount"/>'u da değiştirmeyi gerektiriyor, yani
    /// kaçış kapısı sessizce genişleyemiyor — genişlemesi ayrı ve görünür bir
    /// karar oluyor.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["POST /v1/logs"] =
            "Collector'ın ingest ucu; UI istemcisinde tip düzeyinde dışlanmış (`ExcludedPath`).",
        ["POST /v1/changes/webhooks/{endpointId}"] =
            "CI sistemlerinin çağırdığı alıcı; UI tüketicisi yok.",

        // 204 dönen uçlar: gövde yok. Uydurulmuş bir yanıt tipi, olmayan bir
        // sözleşme vaat ederdi.
        ["DELETE /v1/alerts/rules/{id}"] = "204, gövdesiz.",
        ["DELETE /v1/alerts/maintenance/{id}"] = "204, gövdesiz.",
        ["DELETE /v1/alerts/channels/{id}"] = "204, gövdesiz.",
        ["DELETE /v1/changes/connectors/{id}"] = "204, gövdesiz.",
    };

    /// <summary>
    /// <see cref="Exempt"/> bu sayıda kalmalı.
    ///
    /// <para>
    /// Sabitin tek işlevi muafiyet listesini büyütmeyi <b>görünür</b> kılmak:
    /// yeni bir muafiyet eklemek bu satırı da değiştirmeyi gerektiriyor ve
    /// değişiklik incelemede tek başına göze çarpıyor. Küçülmesi de aynı şekilde
    /// bilinçli olmalı — bir uç tip kazandıysa muafiyetten çıkmalı, sabit de
    /// düşmeli.
    /// </para>
    /// </summary>
    private const int ExpectedExemptCount = 6;

    /// <summary>
    /// Kapının <b>hiç göremediği</b> uçlar — ve neden.
    ///
    /// <para>
    /// Kapsamını beyan etmeyen bir bekçi, kapsamının tam olduğunu <b>iddia
    /// etmiş</b> sayılıyor (T48). Aşağıdakiler <c>Program.cs</c> içinde satır içi
    /// kayıtlı: bir <c>Map*</c> uzantısından geçmiyorlar, dolayısıyla yansıma
    /// keşfi onları hiçbir zaman bulamaz. Bu bir eksiklik değil bir <b>sınır</b>;
    /// ama yazılmadığı sürece sınır olduğu bilinmiyor ve okuyan kişi kapının
    /// "bütün uçlar" dediğini sanıyor.
    /// </para>
    ///
    /// <para>
    /// Liste <see cref="ExpectedOutsideCount"/> ile sabit — <see cref="Exempt"/>
    /// emsali: kapsam dışına bir uç eklemek iki ayrı bilinçli hareket gerektiriyor.
    /// Ve <see cref="Kapi_kapsamini_beyan_ediyor"/> bunların gerçekten dışarıda
    /// kaldığını sınıyor: biri bir gün bir <c>Map*</c> uzantısına taşınırsa liste
    /// bayatlamış olur ve bayatlık sessiz kalmıyor.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> OutsideTheGate = new(StringComparer.Ordinal)
    {
        ["GET /"] = "Kök uç; Program.cs içinde satır içi, sürüm/ad döndürüyor.",
        ["GET /healthz"] = "MapHealthChecks — sağlık ucu, ürün sözleşmesi değil.",
        ["GET /internal/ingest/stats"] = "İç gözlem; Program.cs içinde satır içi.",
        ["GET /internal/discovery/stats"] = "İç gözlem; Program.cs içinde satır içi.",
        ["GET /openapi/{documentName}.json"] = "MapOpenApi; yalnızca Development'ta kayıtlı.",
    };

    /// <summary>
    /// <see cref="OutsideTheGate"/> bu sayıda kalmalı. Gerekçesi
    /// <see cref="ExpectedExemptCount"/> ile aynı: kapsamın daralması sessizce
    /// olamaz.
    /// </summary>
    private const int ExpectedOutsideCount = 5;

    /// <summary>
    /// <c>Bizigo.Api</c> içindeki <b>bütün</b> <c>IEndpointRouteBuilder</c>
    /// uzantıları — yansımayla.
    ///
    /// <para>
    /// Elle yazılmış bir liste yerine burayı kullanmanın tek sebebi var: bir
    /// gün eklenen uç dosyası listeye yazılmayı unutulabilir, ve o an kapı
    /// sessizce yeşil yanar. Bir kez oldu.
    /// </para>
    /// </summary>
    private static IReadOnlyList<MethodInfo> Registrars() =>
        // KAPSAM DEĞİŞMEDİ: yalnızca kompozisyon kökü. Uçlar yalnızca API'de
        // yaşıyor ve bu darlık bilerek — ürünün tamamına açmak, hiçbir uç
        // kaydetmeyen derlemeleri her koşumda taramak olurdu.
        ProductDiscovery.EndpointRegistrars([ProductDiscovery.CompositionRoot]);

    /// <summary>
    /// Minimal API'nin <b>kendisinin</b> bağladığı parametre tipleri. Bunlara
    /// sahte kayıt yazmak anlamsız: <c>RequestDelegateFactory</c> onları servis
    /// çıkarımına hiç sokmuyor, kendi özel yolundan bağlıyor.
    /// </summary>
    private static readonly Type[] BoundByTheFramework =
    [
        typeof(HttpContext), typeof(HttpRequest), typeof(HttpResponse),
        typeof(ClaimsPrincipal), typeof(Stream), typeof(PipeReader),
        typeof(IFormFile), typeof(IFormFileCollection), typeof(IFormCollection),
        typeof(IEndpointRouteBuilder), typeof(TimeProvider),
    ];

    /// <summary>
    /// <b>T48 — kapının asgari servis listesi artık elle tutulmuyor.</b>
    ///
    /// <para>
    /// Eski hâlinde burada elle yazılmış bir <c>typeof(...)</c> listesi vardı ve
    /// aynı delik <b>dört kez</b> açıldı (<c>AlertPreview</c>,
    /// <c>CatalogCoverageCache</c>, <c>ParserPublishGate</c>, <c>RcaAdmission</c>).
    /// Bir uç dosyası listede olmayan bir servis enjekte ettiğinde minimal API
    /// parametreyi "gövde mi servis mi" diye ayırt edemiyor ve o dosyanın
    /// <b>bütün</b> uçları kapıdan düşüyor. Her seferinde bulan kişi farklıydı;
    /// yani sorun dikkat değil, bağın yokluğuydu.
    /// </para>
    ///
    /// <para>
    /// Bağ şu: <b>uç dosyasının kendi metotlarının parametre tipleri</b>
    /// (<see cref="InjectedBy"/>) kaydedilecek kümeyi <b>türetiyor</b>. Handler
    /// ister özel statik metot ister lambda olsun, imzası o dosyanın
    /// metadata'sında duruyor — derleyicinin ürettiği <c>&lt;&gt;c</c> /
    /// <c>&lt;&gt;c__DisplayClass</c> tipleri de geziliyor. Yeni bir servis
    /// eklemek artık burada bir satır gerektirmiyor.
    /// </para>
    ///
    /// <para>
    /// <b>Neyin servis olduğunu tahmin etmiyoruz.</b> Boş bir kapta
    /// <c>IServiceProviderIsService</c>'e soruluyor: çerçevenin zaten tanıdığı
    /// tipler (<c>ILogger&lt;T&gt;</c>, <c>IOptions&lt;T&gt;</c>,
    /// <c>TimeProvider</c>, …) kaydedilmiyor, tanımadığı her şey kaydediliyor.
    /// Elle bir "çerçeve tipleri" listesi tutmak, kaldırdığımız listenin ikinci
    /// bir kopyası olurdu.
    /// </para>
    ///
    /// <para>
    /// <b>Kaçırdığı hâl:</b> handler'ın imzası uç dosyasının dışında bir tipte
    /// tanımlıysa (başka bir sınıfın metot grubu), türetme onu göremez. O hâlde
    /// <c>Map*</c> yine patlar — ama artık sessizce değil: hata
    /// <see cref="Registration.Blind"/>'a <b>dosya adıyla</b> düşüyor ve
    /// <see cref="Kapi_hicbir_uc_dosyasini_kaybetmiyor"/> kırmızı yanıyor.
    /// Türetmenin kırılganlığı bu yüzden güvenli: eksik türetme sessizlik değil
    /// gürültü üretiyor.
    /// </para>
    /// </summary>
    private static readonly Lazy<IReadOnlyList<Type>> Derived = new(DeriveServices);

    private static IReadOnlyList<Type> DeriveServices()
    {
        var probe = WebApplication.CreateBuilder();
        probe.Services.AddAuthorization();
        probe.Services.AddRouting();
        var known = probe.Build().Services.GetRequiredService<IServiceProviderIsService>();

        return [.. Registrars()
            .Select(static m => m.DeclaringType!)
            .Distinct()
            .SelectMany(InjectedBy)
            .Distinct()
            .Where(type => !known.IsService(type))
            .OrderBy(static t => t.FullName, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Bir uç dosyasının metotlarında geçen, servis olabilecek parametre tipleri
    /// — derleyicinin ürettiği lambda taşıyıcıları dahil.
    /// </summary>
    private static IEnumerable<Type> InjectedBy(Type endpointFile)
    {
        var found = new HashSet<Type>();
        Walk(endpointFile);
        return found;

        void Walk(Type type)
        {
            const BindingFlags Everything =
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            foreach (var parameter in type.GetMethods(Everything).SelectMany(static m => m.GetParameters()))
            {
                if (CouldBeInjected(parameter.ParameterType))
                {
                    found.Add(parameter.ParameterType);
                }
            }

            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                Walk(nested);
            }
        }
    }

    /// <summary>
    /// Yapısal eleme. <b>Gövde tipleri de geçiyor</b> ve bu bilinçli: bir istek
    /// kaydını "servis" diye kaydetmek bu testte zararsız (handler hiç
    /// çağrılmıyor, kapı yalnızca rota ve metadata'ya bakıyor), ama onu elemeye
    /// çalışmak "gövde mi servis mi" tahminini <b>kapının içine</b> geri koyardı
    /// — kaldırdığımız şeyin ta kendisi.
    /// </summary>
    private static bool CouldBeInjected(Type type) =>
        !type.IsValueType
        && !type.IsByRef
        && !type.IsPointer
        && !type.IsArray
        && !type.IsGenericParameter
        && !type.ContainsGenericParameters
        && type != typeof(string)
        && type != typeof(object)
        && !typeof(Delegate).IsAssignableFrom(type)
        && !BoundByTheFramework.Contains(type);

    /// <summary>
    /// Kapının bir koşumdaki <b>tam</b> sonucu: bulunan uçlar, hangi uç
    /// dosyasının kaç uç verdiği, ve <b>görülemeyen</b> dosyalar.
    /// </summary>
    /// <param name="Endpoints">Denetime giren rotalar.</param>
    /// <param name="PerFile">Uç dosyası → kaç uç. Sıfır olması da bir bulgu.</param>
    /// <param name="Blind">Uç dosyası → neden görülemedi. Boş olmalı.</param>
    /// <param name="Dropped">
    /// <c>RouteEndpoint</c> olmayan ve bu yüzden denetime girmeyen uç sayısı.
    /// Sessizce düşen her şey sayılıyor; sayılmayan şey yok sayılmış olurdu.
    /// </param>
    private sealed record Registration(
        IReadOnlyList<RouteEndpoint> Endpoints,
        IReadOnlyDictionary<string, int> PerFile,
        IReadOnlyDictionary<string, string> Blind,
        int Dropped);

    /// <summary>
    /// Keşif bir kez koşuyor ve bütün testler aynı sonuca bakıyor: hem ölçüm
    /// tutarlı oluyor hem de on altı uygulama kurulumu tek sefere iniyor.
    /// </summary>
    private static readonly Lazy<Registration> Discovery = new(Discover);

    private static WebApplication NewApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddRouting();

        // Servisler çözülmüyor — kayıtlar yalnızca minimal API'nin parametreyi
        // "servis mi gövde mi" diye ayırt edebilmesi için var. Çözülürse patlayan
        // fabrika, bir gün handler çağrılırsa `null` yerine anlaşılır bir hata
        // veriyor.
        foreach (var service in Derived.Value)
        {
            var captured = service;
            builder.Services.AddSingleton(captured, _ =>
                throw new InvalidOperationException(
                    $"{captured.Name} bu testte çözülmemeli — yalnızca kayıt sınanıyor."));
        }

        // Gerçek örnek: uçlar bunu kayıt anında çözüyor, sahte fırlatıcı patlar.
        builder.Services.AddSingleton(TimeProvider.System);

        return builder.Build();
    }

    /// <summary>
    /// Uçlar <b>uygulama başlatılmadan</b> kaydediliyor. Rotalar tembel
    /// kuruluyor: <c>Map*</c> çağrısı değil, veri kaynağının okunması patlıyor —
    /// bu yüzden her uzantıdan <b>sonra</b> okuyoruz, yoksa hata hangi dosyadan
    /// geldiği belli olmadan yukarı çıkıyor. Eski hâlin yanıltıcı olan yeri tam
    /// olarak buydu: mesaj parametre çıkarımını gösteriyor, dosyayı
    /// göstermiyordu.
    /// </summary>
    private static IReadOnlyList<Endpoint> Materialize(WebApplication app) =>
        [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)];

    private static string Key(MethodInfo registrar) =>
        $"{registrar.DeclaringType?.Name}.{registrar.Name}";

    private static Exception Root(Exception error) =>
        error is TargetInvocationException { InnerException: { } inner } ? inner : error;

    private static string Explain(MethodInfo registrar, Exception error)
    {
        var file = registrar.DeclaringType!;
        var injected = InjectedBy(file).Select(static t => t.Name).Order(StringComparer.Ordinal);

        return $"{file.Name} kapıya görünmüyor — {registrar.Name} kaydedildi ama uçları alınamadı." +
            $"\n  Hata: {error.Message}" +
            $"\n  Bu dosyada bulunup kaydedilen bağımlılıklar: {string.Join(", ", injected)}" +
            "\n  Hata bir parametreyi \"UNKNOWN\" ya da \"Body (Inferred)\" gösteriyorsa, o " +
            "parametrenin tipi yukarıdaki listede YOK demektir: handler'ın imzası bu dosyanın " +
            "dışında bir tipte tanımlı ve türetme onu görememiş.";
    }

    private static Registration Discover()
    {
        var registrars = Registrars();
        var perFile = new Dictionary<string, int>(StringComparer.Ordinal);
        var blind = new Dictionary<string, string>(StringComparer.Ordinal);

        var app = NewApp();
        IReadOnlyList<Endpoint> all = [];
        var known = 0;

        foreach (var registrar in registrars)
        {
            // Beklenmeyen imza SESSİZCE atlanmıyor. Atlanabilseydi, iki
            // parametreli yeni bir `Map*` kapıya yine görünmez olurdu — kapatmaya
            // çalıştığımız deliğin aynısı, başka kılıkta.
            if (registrar.GetParameters().Length != 1 || registrar.IsGenericMethodDefinition)
            {
                blind[Key(registrar)] =
                    $"{Key(registrar)} beklenmeyen imzada: kapı yalnızca tek parametreli, " +
                    "generic olmayan `Map*` uzantılarını çağırabiliyor. İmza bilinçli olarak " +
                    "değiştiyse bu test de güncellenmeli.";
                continue;
            }

            try
            {
                registrar.Invoke(null, [app]);
                var now = Materialize(app);
                perFile[Key(registrar)] = now.Count - known;
                known = now.Count;
                all = now;
            }
            catch (Exception error)
            {
                blind[Key(registrar)] = Explain(registrar, Root(error));

                // Bozulan uygulama sonraki her okumada aynı hatayı veriyor. Temiz
                // bir uygulamaya geçip ölçülebilmiş olanları geri kuruyoruz: bir
                // kör dosya, arkasındaki dosyaları da ölçülemez yapmasın — yoksa
                // ilk kırığın gölgesinde ikinci kırık görünmez kalırdı.
                app = NewApp();
                foreach (var done in registrars
                    .TakeWhile(m => m != registrar)
                    .Where(m => !blind.ContainsKey(Key(m))))
                {
                    done.Invoke(null, [app]);
                }

                all = Materialize(app);
                known = all.Count;
            }
        }

        var routes = all.OfType<RouteEndpoint>().ToArray();
        return new Registration(routes, perFile, blind, all.Count - routes.Length);
    }

    /// <summary>
    /// Uçların tamamı — keşif bir kez koştu, hepsi aynı sonuca bakıyor.
    /// </summary>
    private static IReadOnlyList<RouteEndpoint> Endpoints() => Discovery.Value.Endpoints;

    /// <summary>
    /// <c>METHOD /yol</c> — listelerin anahtarı. Rota deseninden kısıtlar
    /// (<c>{id:guid}</c>) çıkarılıyor: bir kısıt eklemek yanıt tipiyle ilgili
    /// değil, listeyi bozmamalı.
    ///
    /// <para>
    /// <b>Önek filtresi yok.</b> Eskiden yalnızca <c>/v1/</c> denetleniyordu ve
    /// bu, kapının ikinci kör noktasıydı: bir gün açılacak <c>/v2/</c> ya da
    /// önek dışı bir ürün ucu sessizce kapsam dışı kalırdı. Denetlenen küme
    /// artık "uç dosyalarının kaydettiği her şey" — <c>/internal/*</c>,
    /// <c>/healthz</c> ve <c>/</c> zaten <c>Program.cs</c> içinde satır içi
    /// kayıtlı, yani bir <c>Map*</c> uzantısından geçmiyorlar ve bu kümeye hiç
    /// girmiyorlar.
    /// </para>
    /// </summary>
    private static IEnumerable<(string Key, RouteEndpoint Endpoint)> ProductEndpoints() =>
        Endpoints().SelectMany(static e =>
            (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["?"])
                .Select(method => ($"{method} {StripConstraints(e.RoutePattern.RawText ?? string.Empty)}", e)));

    private static string StripConstraints(string pattern)
    {
        var trimmed = System.Text.RegularExpressions.Regex.Replace(pattern, @"\{([^}:]+)(:[^}]+)?\}", "{$1}");

        // MapGroup("/v1/x") + MapGet("/") deseni "/v1/x/" üretiyor; sondaki
        // eğik çizgi yolun kimliğine ait değil.
        return trimmed.Length > 1 && trimmed.EndsWith('/') ? trimmed[..^1] : trimmed;
    }

    /// <summary>
    /// Bir ucun 2xx yanıtı için bildirilmiş <b>gövde tipi</b> var mı.
    /// <c>.Produces(404)</c> gibi gövdesiz bildirimler sayılmıyor.
    /// </summary>
    private static bool DeclaresResponseType(RouteEndpoint endpoint) =>
        endpoint.Metadata
            .GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .Any(static m => m.StatusCode is >= 200 and < 300
                && m.Type is not null
                && m.Type != typeof(void));

    /// <summary>
    /// <b>Kapının kendisinin bekçisi.</b> Denetlenen küme, uç dosyalarının
    /// tamamından geliyor mu?
    ///
    /// <para>
    /// Bu test yansıma keşfinin gerçekten iş gördüğünü sabitliyor: bugün bilinen
    /// on üç uzantının hepsi bulunuyor ve hepsi çağrıldığı için hepsinin uçları
    /// denetime giriyor. Yeni bir uç dosyası eklendiğinde burada bir şey
    /// güncellemek gerekmiyor — sayı kendiliğinden artıyor ve <b>uçları da
    /// otomatik denetime giriyor</b>; kapatılan delik tam olarak buydu.
    /// </para>
    /// </summary>
    [Fact]
    public void Kapi_butun_uc_dosyalarini_kendisi_buluyor()
    {
        var names = Registrars().Select(static m => m.Name).ToArray();

        // Bugün var olanların hepsi. Bir dosya silinirse burası düşer ve silme
        // bilinçli bir hareket olur.
        Assert.Equal(
            [
                "MapAlertClosure", "MapAlerts", "MapAuth",
                "MapChangeConnectors", "MapChangeWebhooks", "MapChanges",
                "MapEvents",
                "MapNotificationChannels", "MapOtlpLogs", "MapParserAuthoring", "MapParsers",
                "MapPipelineHealth", "MapRca", "MapRcaRuns", "MapReplay", "MapSources",
            ],
            names);

        // Ve hepsi gerçekten uç üretiyor: keşif çalışsa da çağrı bir yerde
        // yutulsaydı küme boş kalırdı ve bütün testler anlamsız yere geçerdi.
        Assert.NotEmpty(ProductEndpoints());
    }

    [Fact]
    public void Her_urun_ucu_ya_yanit_tipi_bildiriyor_ya_bir_listede()
    {
        var missing = ProductEndpoints()
            .Where(static pair => !DeclaresResponseType(pair.Endpoint))
            .Select(static pair => pair.Key)
            .Where(key => !Pending.ContainsKey(key) && !Exempt.ContainsKey(key))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Yanıt tipi bildirmeyen uç(lar) hiçbir listede değil:\n  " +
            string.Join("\n  ", missing) +
            "\n\nYa uca `.Produces<T>()` ekleyin, ya `Pending`'e hangi ticket'ın " +
            "kapatacağıyla yazın, ya da gerçekten hiç tüketicisi olmayacaksa " +
            "`Exempt`'e ekleyip `ExpectedExemptCount`'u da güncelleyin.");
    }

    /// <summary>
    /// Listeler <b>yalnızca</b> gerçekten eksik olanları taşımalı. Kapatılan bir
    /// uç listede kalırsa liste kısalmayı bırakır ve boşluk yine görünmez olur —
    /// bu testin varlık sebebi tam olarak listelerin bayatlamasını engellemek.
    /// </summary>
    [Fact]
    public void Listeler_bayat_giris_tasimiyor()
    {
        var actual = ProductEndpoints().ToArray();
        var keys = actual.Select(static pair => pair.Key).ToHashSet(StringComparer.Ordinal);

        var vanished = Pending.Keys.Concat(Exempt.Keys)
            .Where(key => !keys.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            vanished.Length == 0,
            "Listelerde artık var olmayan uç(lar): " + string.Join(", ", vanished));

        var covered = actual
            .Where(pair => (Pending.ContainsKey(pair.Key) || Exempt.ContainsKey(pair.Key))
                && DeclaresResponseType(pair.Endpoint))
            .Select(static pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            covered.Length == 0,
            "Yanıt tipi kazanmış uç(lar) hâlâ listede: " + string.Join(", ", covered) +
            " — Pending/Exempt'ten silin.");
    }

    /// <summary>
    /// <b>F2'nin son kapısı</b> (T27 kabul kriteri): bekleme listesi boşaldı mı.
    ///
    /// <para>
    /// Liste "henüz tipsiz, ekran indikçe çıkacak" diyen uçları taşıyordu ve
    /// ticket'ın kendi ifadesiyle <b>boşalmadan F2 bitmiş sayılmıyor</b>.
    /// Kapatılması yirmi bir satırdan sıfıra indi: her satır, o ucu tüketen
    /// ekranla birlikte gitti — çünkü tüketicisi olmadan yazılan bir yanıt tipi
    /// tahmindir.
    /// </para>
    ///
    /// <para>
    /// <see cref="Exempt"/> <b>kapsam dışı</b>: onun boşalması beklenmiyor ve
    /// büyümesi <see cref="ExpectedExemptCount"/> ile ayrıca korunuyor. İkisini
    /// tek listede tutmak, "boşaldı mı" sorusunun cevabını asla evet
    /// yapamıyordu — T17 bu yüzden ayırdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Izin_listesi_bosaldi_mi()
    {
        Assert.True(
            Pending.Count == 0,
            "F2'nin bitiş şartlarından biri bu listenin boşalması. Kalan:\n  " +
            string.Join("\n  ", Pending.Select(entry => $"{entry.Key} — {entry.Value}")) +
            "\n\nBir ucu buraya eklemek yerine `.Produces<T>()` yazmayı deneyin: " +
            "sunucunun ne gönderdiği belliyse, sözleşmeye neyin gireceği " +
            "tüketicinin değil sunucunun kararıdır.");
    }

    /// <summary>
    /// <b>Kapı denetlediği kümeyi kendisi buluyor mu</b> (T27).
    ///
    /// <para>
    /// T17'de kapatılan yapısal delik buydu: bekçi uçları elle yazılmış bir
    /// <c>Map*</c> listesinden topluyordu, T21/T22/T24 indiğinde 16 uç ona hiç
    /// görünmedi ve üç testin üçü de geçti — yeşilliği hiçbir şey ifade
    /// etmiyordu.
    /// </para>
    ///
    /// <para>
    /// Bu test o deliğin <b>geri açılmasını</b> engelliyor: <c>Bizigo.Api</c>
    /// içindeki uç dosyalarının her biri kapıya en az bir rota vermek zorunda.
    /// Yeni bir uç dosyası eklenip yansıma onu bulamazsa — ya da bulunup
    /// çağrılırken bağımlılık eksikliğinden patlarsa — burada kırmızı yanıyor,
    /// listeye yazılmayı beklemeden.
    /// </para>
    /// </summary>
    [Fact]
    public void Kapi_uc_dosyalarini_kendisi_buluyor()
    {
        var registrars = Registrars();

        // Adı `Map` ile başlayan uzantı, `/v1` altında rota üretiyor olmalı.
        // İstisna: kimlik, sağlık ve iç gözlem uçları `/v1` altında değil.
        var declaringTypes = registrars
            .Select(m => m.DeclaringType!.Name)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        // Uç dosyası olduğu adından belli olan her sınıf keşfedilmiş olmalı.
        var endpointFiles = typeof(global::Program).Assembly
            .GetTypes()
            .Where(static t => t is { IsSealed: true, IsAbstract: true, IsPublic: true })
            .Where(static t => t.Name.EndsWith("Endpoints", StringComparison.Ordinal)
                || t.Name.EndsWith("Endpoint", StringComparison.Ordinal))
            .Select(static t => t.Name)
            .ToArray();

        Assert.NotEmpty(endpointFiles);

        var invisible = endpointFiles
            .Where(name => !declaringTypes.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            invisible.Length == 0,
            "Bu uç dosyaları kapıya hiç görünmüyor: " + string.Join(", ", invisible) +
            "\n\nAdı `Map` ile başlayan, ilk parametresi `IEndpointRouteBuilder` olan " +
            "bir uzantı metodu bekleniyor.");
    }

    /// <summary>
    /// <b>T48'in birinci kapısı: kapının göremediği uç dosyası var mı?</b>
    ///
    /// <para>
    /// Bulunmak yetmiyor: bağımlılığı kayıtlı olmayan bir <c>Map*</c> uzantısı
    /// rota kurulurken patlıyor ve o dosyanın <b>bütün</b> uçları denetlenmemiş
    /// kalıyor. Aynı delik dört kez açıldı ve her seferinde bulan kişi farklıydı.
    /// </para>
    ///
    /// <para>
    /// Eski hâlinde bu durum <c>Endpoints()</c> içinden yukarı fırlıyordu: on üç
    /// test birden düşüyor, mesaj bir <b>parametre adı</b> söylüyor
    /// (<c>admission | UNKNOWN</c>) ama <b>hangi dosya</b> ve <b>hangi servis</b>
    /// olduğunu söylemiyordu. Şimdi hata dosyaya bağlanıyor ve tek bir yerde,
    /// dosya adıyla, kırmızı yanıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Kapi_hicbir_uc_dosyasini_kaybetmiyor()
    {
        var found = Discovery.Value;

        Assert.True(
            found.Blind.Count == 0,
            $"Kapının göremediği {found.Blind.Count} uç dosyası var:\n\n" +
            string.Join("\n\n", found.Blind.Values.Order(StringComparer.Ordinal)));

        Assert.NotEmpty(found.Endpoints);
    }

    /// <summary>
    /// <b>T48'in ikinci kapısı: her uç dosyası kapıya kaç uç verdi?</b>
    ///
    /// <para>
    /// Eski ölçüt "toplam rota sayısı ≥ uzantı sayısı"ydı ve bu, on beş uçlu bir
    /// dosyanın tamamen kaybolmasını <b>gizleyebiliyordu</b>: kalan dosyalar
    /// sayıyı tek başına doldurur. Ölçüt artık dosya başına: sıfır uç veren bir
    /// uzantı, kapıdan geçmiş ama hiçbir şey denetletmemiş demek.
    /// </para>
    ///
    /// <para>
    /// <b>Kaçırdığı hâl:</b> iki uçlu bir dosyanın bir ucunu kaybetmesi burada
    /// görünmüyor — dosya hâlâ ≥1 veriyor. Uç <b>sayısını</b> dosya başına
    /// sabitlemek ise her yeni uçta bu testi güncellemek demekti ve altı ajan
    /// paralel uç ekliyor; sabitlenen sayı, güncellenmesi rutinleşen bir sayıya
    /// dönüşür ve rutin güncelleme bekçiyi kayıt olmaktan çıkarır. Alan
    /// sahiplerinin sabitlediği sayılar ayrı duruyor
    /// (<see cref="Olay_yuzeyi_uc_uctan_ibaret"/>).
    /// </para>
    /// </summary>
    [Fact]
    public void Her_uc_dosyasi_kapiya_en_az_bir_uc_veriyor()
    {
        var found = Discovery.Value;

        var silent = found.PerFile
            .Where(static entry => entry.Value == 0)
            .Select(static entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            silent.Length == 0,
            "Bu uzantı(lar) çağrıldı ama hiç uç kaydetmedi: " + string.Join(", ", silent) +
            " — kapıdan geçtiler, denetlenen bir şey bırakmadılar.");

        // Her uzantı ya ölçüldü ya kör sayıldı; ikisi arasında kaybolan yok.
        Assert.Equal(Registrars().Count, found.PerFile.Count + found.Blind.Count);
    }

    /// <summary>
    /// <b>Uç kaydedebilecek her uzantı keşfediliyor mu?</b>
    ///
    /// <para>
    /// <see cref="Registrars"/> adı <c>Map</c> ile başlayan uzantıları arıyor.
    /// Bu bir <b>konvansiyon</b>, ve konvansiyona uymayan bir uç dosyası bugün
    /// hiçbir teste görünmüyordu: <see cref="Kapi_butun_uc_dosyalarini_kendisi_buluyor"/>
    /// bulunanları listeyle karşılaştırıyor, bulunmayan zaten listede olmuyor ve
    /// karşılaştırma <b>geçiyor</b>. Yani "AddRcaRoutes" adında bir uzantı
    /// eklemek on altı testin hepsini yeşil bırakırdı.
    /// </para>
    ///
    /// <para>
    /// Ölçüt burada addan değil <b>imzadan</b> geliyor: ilk parametresi
    /// <c>IEndpointRouteBuilder</c> olan statik bir metot uç kaydedebilir,
    /// adı ne olursa olsun.
    /// </para>
    /// </summary>
    [Fact]
    public void Uc_kaydedebilecek_her_uzanti_kesfediliyor()
    {
        var discovered = Registrars().ToHashSet();

        var candidates = typeof(global::Program).Assembly
            .GetTypes()
            .Where(static t => !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .SelectMany(static t => t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(static m => !m.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .Where(static m => m.GetParameters() is [{ } first, ..]
                && typeof(IEndpointRouteBuilder).IsAssignableFrom(first.ParameterType))
            .Where(m => !discovered.Contains(m))
            .Select(static m => $"{m.DeclaringType?.Name}.{m.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            candidates.Length == 0,
            "Uç kaydedebilecek ama keşfedilmeyen metot(lar): " + string.Join(", ", candidates) +
            "\n\nKapı `Map` önekiyle arıyor. Adı uymayan bir uç dosyası kapıya hiç görünmez " +
            "ve bütün testler yeşil kalır — kapatılan delik tam olarak bu sınıftan.");
    }

    /// <summary>
    /// <b>T48'in üçüncü kapısı: kapı kendi kapsamını beyan ediyor mu?</b>
    ///
    /// <para>
    /// Kabul kriteri şöyleydi: <i>kapının göremediği bir uç kalırsa bu sayılıyor
    /// — beyan etmeyen bir kapı, kapsamını iddia etmiş sayılıyor.</i> Üç ayrı
    /// muhasebe var ve üçü de burada:
    /// </para>
    ///
    /// <list type="number">
    /// <item>Denetime giren her uç <b>bir</b> uç dosyasına yazılmış olmalı —
    /// toplamlar tutmalı.</item>
    /// <item><c>RouteEndpoint</c> olmadığı için sessizce düşen uç olmamalı.</item>
    /// <item>Kapının yapısı gereği hiç göremediği uçlar (<c>Program.cs</c> içinde
    /// satır içi kayıtlı olanlar) <see cref="OutsideTheGate"/>'te gerekçesiyle
    /// <b>yazılı</b> olmalı, sayısı sabit olmalı, ve gerçekten dışarıda
    /// kalmalı.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void Kapi_kapsamini_beyan_ediyor()
    {
        var found = Discovery.Value;

        Assert.Equal(found.Endpoints.Count, found.PerFile.Values.Sum());

        Assert.True(
            found.Dropped == 0,
            $"{found.Dropped} uç `RouteEndpoint` olmadığı için denetime hiç girmedi. " +
            "Sessizce düşen bir uç, kapının göremediği bir uçtur.");

        Assert.Equal(ExpectedOutsideCount, OutsideTheGate.Count);

        foreach (var (key, reason) in OutsideTheGate)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{key} kapsam dışılığı gerekçesiz.");
        }

        var keys = ProductEndpoints().Select(static pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var moved = OutsideTheGate.Keys
            .Where(keys.Contains)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            moved.Length == 0,
            "Kapsam dışı yazılan uç(lar) artık kapıya görünüyor: " + string.Join(", ", moved) +
            " — `OutsideTheGate`'ten silin, listeler bayatlamasın.");
    }

    /// <summary>
    /// T24/T25'in tükettiği değişiklik uçları listeden <b>çıkmış</b> olmalı.
    ///
    /// <para>
    /// Ayrıca imzalı alıcı listede <b>kalmalı</b>: onun muafiyeti geçici bir
    /// boşluk değil kalıcı bir karar — CI sistemleri çağırıyor, ekran değil.
    /// Ayrımı sabitlemezsek biri onu "eksik" sanıp kapatmaya çalışır.
    /// </para>
    /// </summary>
    [Fact]
    public void Degisiklik_uclari_yanit_tipi_tasiyor()
    {
        var changes = ProductEndpoints()
            .Where(static pair => pair.Key.Contains("/v1/changes", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(changes);

        foreach (var (key, endpoint) in changes)
        {
            if (Pending.ContainsKey(key) || Exempt.ContainsKey(key))
            {
                continue;
            }

            Assert.True(DeclaresResponseType(endpoint), $"{key} yanıt tipi bildirmiyor.");
        }

        Assert.Contains("POST /v1/changes/webhooks/{endpointId}", Exempt.Keys);
        Assert.DoesNotContain("GET /v1/changes", Pending.Keys);
        Assert.DoesNotContain("POST /v1/changes", Pending.Keys);

        // Uçların GERÇEKTEN kaydedildiğini sabitliyoruz. Bekçinin bulunmuş
        // deliği tam olarak buydu: `Endpoints()` içindeki `Map*` listesine
        // eklenmeyen bir uç kapıya hiç görünmüyor ve test yeşil yanıyor —
        // yeşilliği hiçbir şey ifade etmiyor. Aşağıdaki liste, o listeden bir
        // satır düşerse kırmızı yanıyor.
        var keys = changes.Select(static pair => pair.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var expected in new[]
        {
            "GET /v1/changes",
            "POST /v1/changes",
            "POST /v1/changes/webhooks/{endpointId}",
            "GET /v1/changes/connectors",
            "POST /v1/changes/connectors",
            "GET /v1/changes/connectors/{id}",
            "PUT /v1/changes/connectors/{id}",
            "DELETE /v1/changes/connectors/{id}",
            "POST /v1/changes/connectors/{id}/test",
            "GET /v1/changes/connectors/{id}/runs",
        })
        {
            Assert.True(
                keys.Contains(expected),
                $"{expected} bekçiye hiç görünmüyor — Endpoints() içindeki Map* listesinde eksik.");
        }
    }

    /// <summary>
    /// Muafiyet listesi sessizce büyüyemez.
    ///
    /// <para>
    /// <see cref="Pending"/> "bir gün kapanacak" demek ve boşalması T27'nin
    /// kabul kriteri. <see cref="Exempt"/> ise hiç kapanmayacak; ikisi tek listede
    /// dururken o kriter <b>sağlanamaz</b> hâldeydi. Ayırmanın bedeli, muafiyetin
    /// kolay bir kaçış kapısına dönüşmesi olurdu — sayının sabitlenmesi bunu
    /// engelliyor: yeni bir muafiyet, ayrı ve görünür bir karar.
    /// </para>
    /// </summary>
    [Fact]
    public void Muafiyet_listesi_sessizce_buyuyemez()
    {
        Assert.Equal(ExpectedExemptCount, Exempt.Count);

        // Muafiyet gerekçesiz olmaz: "neden hiç tüketicisi olmayacak" sorusunun
        // cevabı listede yazılı durmalı.
        foreach (var (key, reason) in Exempt)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(reason),
                $"{key} muafiyeti gerekçesiz.");
        }

        // İki liste ayrık olmalı; bir uç hem "bekliyor" hem "muaf" olamaz.
        Assert.Empty(Pending.Keys.Intersect(Exempt.Keys, StringComparer.Ordinal));
    }

    /// <summary>
    /// Ekranı inmiş uçlar <see cref="Pending"/>'den <b>çıkmış</b> olmalı. Bu
    /// test, listenin gerçekten küçüldüğünün ölçüsü; olmadan "bir gün ekleriz"
    /// sessizce kalıcı olabilir.
    ///
    /// <para>
    /// Uç <b>sayısı</b> burada sabitlenmiyor: o alanların sahibi başka ticket'lar
    /// ve yeni bir uç eklemeleri bu bekçiyi ilgilendirmiyor. Sabitlenen tek şey
    /// sözleşme — tip ya var, ya gerekçeli muafiyet.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("/v1/events")]
    [InlineData("/v1/alerts")]
    public void Ekrani_inmis_uclar_yanit_tipi_tasiyor(string prefix)
    {
        var endpoints = ProductEndpoints()
            .Where(pair => pair.Key.Contains(prefix, StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(endpoints);

        foreach (var (key, endpoint) in endpoints)
        {
            // 204 dönen silme uçları muaf; geri kalanı tip taşımalı.
            if (Exempt.ContainsKey(key))
            {
                continue;
            }

            Assert.True(DeclaresResponseType(endpoint), $"{key} yanıt tipi bildirmiyor.");
            Assert.DoesNotContain(key, Pending.Keys);
        }
    }

    /// <summary>
    /// T15/T16'nın uçları: üç tane, üçü de tipli. Sayı burada <b>sabitlenmiş</b>
    /// çünkü bu yüzeyin sahibi bu ticket — bir olay ucunun sessizce kaybolması
    /// ya da eklenmesi görünmeli.
    /// </summary>
    [Fact]
    public void Olay_yuzeyi_uc_uctan_ibaret()
    {
        var events = ProductEndpoints()
            .Where(static pair => pair.Key.Contains("/v1/events", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(3, events.Length);
    }

    /// <summary>
    /// T19'un tükettiği dört uç listeden <b>çıkmış</b> olmalı — ve listenin
    /// gerçekten küçüldüğünün ölçüsü bu.
    ///
    /// <para>
    /// Uç adları burada <b>elle</b> yazılı, <see cref="Pending"/>'den
    /// türetilmiyor: türetilseydi test kendi kendini onaylar, dört satır listeye
    /// geri eklendiğinde de yeşil yanardı.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("POST /v1/parsers/try")]
    [InlineData("POST /v1/parsers/drafts")]
    [InlineData("PUT /v1/parsers/drafts/{id}")]
    [InlineData("POST /v1/parsers/drafts/{id}/submit")]
    public void Parser_editorunun_uclari_yanit_tipi_tasiyor(string key)
    {
        var (_, endpoint) = Assert.Single(ProductEndpoints(), pair => pair.Key == key);

        Assert.True(DeclaresResponseType(endpoint), $"{key} yanıt tipi bildirmiyor.");
        Assert.DoesNotContain(key, Pending.Keys);
    }

    /// <summary>
    /// Replay ucu <b>tiplendi</b> — sahipsiz kalan son satır böyle kapandı.
    ///
    /// <para>
    /// Muafiyete taşımak yanlış olurdu: muafiyet "hiç tüketicisi olmayacak"
    /// demek ve replay'in bir gün ekranı olacak. Tiplendirmek de tahmin
    /// değildi, çünkü uç <b>zaten</b> <c>ReplayReport</c>'u döndürüyordu —
    /// yani domain tipi fiilen tel sözleşmesiydi ve bu, tiplenmemiş olmaktan
    /// kötüydü: tip yok ama sızıntı var.
    /// </para>
    ///
    /// <para>
    /// Hata yolları da tiplendi. Bir ucun başarı yolunun sözleşmesi olup hata
    /// yolunun olmaması, ekranın hatayı elle ayrıştırması demek — ve elle
    /// yazılan tip, T14'ün var olma sebebine aykırı.
    /// </para>
    /// </summary>
    [Fact]
    public void Replay_ucu_her_yolunda_yanit_tipi_tasiyor()
    {
        var (_, endpoint) = Assert.Single(ProductEndpoints(), pair => pair.Key == "POST /v1/replay");

        Assert.True(DeclaresResponseType(endpoint), "POST /v1/replay yanıt tipi bildirmiyor.");
        Assert.DoesNotContain("POST /v1/replay", Pending.Keys);
        Assert.DoesNotContain("POST /v1/replay", Exempt.Keys);

        // Başarı, geçersiz istek ve duruş: üçü de bildirilmiş olmalı.
        var declared = endpoint.Metadata
            .GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .Where(m => m.Type is not null && m.Type != typeof(void))
            .Select(m => m.StatusCode)
            .ToHashSet();

        Assert.Contains(StatusCodes.Status200OK, declared);
        Assert.Contains(StatusCodes.Status400BadRequest, declared);
        Assert.Contains(StatusCodes.Status409Conflict, declared);
    }
}
