using System.Reflection;
using System.Runtime.CompilerServices;
using Bizigo.Alerting;
using Bizigo.Api;
using Bizigo.Api.Connectors;
using Bizigo.Api.Webhooks;
using Bizigo.Authoring;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Discovery;
using Bizigo.Ingest.Pipeline;
using Bizigo.Ingest.Wal;
using Bizigo.Parsing.Dispatch;
using Bizigo.Query;
using Bizigo.Replay;
using Bizigo.Storage.Raw;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
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
        ["POST /v1/replay"] = "T19 — replay ekranı",
        ["GET /v1/parsers"] = "T18 — parser editörü",
        ["GET /v1/parsers/{id}"] = "T18 — parser editörü",
        ["POST /v1/parsers/try"] = "T18 — parser editörü",
        ["GET /v1/parsers/drafts"] = "T18 — parser editörü",
        ["POST /v1/parsers/drafts"] = "T18 — parser editörü",
        ["PUT /v1/parsers/drafts/{id}"] = "T18 — parser editörü",
        ["POST /v1/parsers/drafts/{id}/submit"] = "T18 — parser editörü",
        ["POST /v1/parsers/drafts/{id}/return"] = "T18 — parser editörü",
        ["POST /v1/parsers/drafts/{id}/publish"] = "T18 — parser editörü",
        ["POST /v1/parsers/{parserId}/rollback"] = "T18 — parser editörü",
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
        [.. typeof(global::Program).Assembly
            .GetTypes()
            // Statik sınıf = sealed + abstract.
            .Where(static t => t is { IsSealed: true, IsAbstract: true })
            .SelectMany(static t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(static m => m.IsDefined(typeof(ExtensionAttribute), inherit: false))
            .Where(static m => m.Name.StartsWith("Map", StringComparison.Ordinal))
            .Where(static m => m.GetParameters() is [{ } first, ..]
                && typeof(IEndpointRouteBuilder).IsAssignableFrom(first.ParameterType))
            .OrderBy(static m => m.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Uçların tamamı. Servisler <b>çözülürse patlayan</b> fabrikalarla
    /// kaydediliyor: handler'lar hiç çağrılmıyor ve bir gün çağrılırsa
    /// <c>null</c> yerine anlaşılır bir hata çıkıyor.
    /// </summary>
    private static IReadOnlyList<RouteEndpoint> Endpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddRouting();

        foreach (var type in new[]
        {
            typeof(IScopedQuery), typeof(ICurrentUser), typeof(RawEventLocator),
            typeof(ControlPlaneDbContext), typeof(IDbContextFactory<ControlPlaneDbContext>),
            typeof(ReplayEngine), typeof(ParserAuthoringService), typeof(PublishedParserLoader),
            typeof(ParserCatalog), typeof(DispatchStats), typeof(Dispatcher),
            typeof(IngestGateway), typeof(IngestStats), typeof(WriteAheadLog),
            typeof(DiscoveryStats), typeof(SidecarOptions),

            // Uc dosyalarinin bagimliliklari. Kayit edilmezlerse parametre
            // cikarimi "govde mi servis mi" diyemiyor ve `Map*` cagrisi
            // patliyor -- yani o uc dosyasi kapiya hic gorunmuyor.
            //
            // `TimeProvider` bilerek listede YOK: ASP.NET onu kendi kurulumunda
            // cozuyor ve zehirlemek `WebApplicationBuilder.Build()`'i patlatiyor.
            typeof(AlertRuleService), typeof(NotificationChannelService),
            typeof(AlertingOptions), typeof(AlertingStats), typeof(AlertPreview),
            typeof(IChangeWebhookRegistry), typeof(ChangeWebhookOptions),
            typeof(ChangeWebhookDeliveryLog), typeof(ChangeConnectorService),
        })
        {
            var captured = type;
            builder.Services.AddSingleton(captured, _ =>
                throw new InvalidOperationException(
                    $"{captured.Name} bu testte çözülmemeli — yalnızca kayıt sınanıyor."));
        }

        // Gerçek örnek: uçlar bunu kayıt anında çözüyor, sahte fırlatıcı patlar.
        builder.Services.AddSingleton(TimeProvider.System);

        var app = builder.Build();

        foreach (var registrar in Registrars())
        {
            // Beklenmeyen imza SESSİZCE atlanmıyor. Atlanabilseydi, iki
            // parametreli yeni bir `Map*` kapıya yine görünmez olurdu — kapatmaya
            // çalıştığımız deliğin aynısı, başka kılıkta.
            if (registrar.GetParameters().Length != 1 || registrar.IsGenericMethodDefinition)
            {
                throw new InvalidOperationException(
                    $"{registrar.DeclaringType?.Name}.{registrar.Name} beklenmeyen imzada: " +
                    "kapı yalnızca tek parametreli, generic olmayan `Map*` uzantılarını çağırabiliyor. " +
                    "İmza bilinçli olarak değiştiyse bu test de güncellenmeli.");
            }

            try
            {
                registrar.Invoke(null, [app]);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                throw new InvalidOperationException(
                    $"{registrar.Name} kayıt sırasında patladı: {error.InnerException.Message}. " +
                    "Muhtemelen bir bağımlılığı `Endpoints()` içindeki kayıt listesinde yok.",
                    error.InnerException);
            }
        }

        return [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()];
    }

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
    /// on iki uzantının hepsi bulunuyor ve hepsi çağrıldığı için hepsinin uçları
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
                "MapAlerts", "MapAuth", "MapChangeConnectors", "MapChangeWebhooks", "MapChanges",
                "MapEvents",
                "MapNotificationChannels", "MapOtlpLogs", "MapParserAuthoring", "MapParsers",
                "MapPipelineHealth", "MapReplay", "MapSources",
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
}
