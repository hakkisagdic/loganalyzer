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
/// <c>/v1/*</c> altındaki her uç bir <c>Produces&lt;T&gt;</c> taşımalı — ve
/// bugün taşımayanlar aşağıda <b>açık bir izin listesinde</b> duruyor.
///
/// <para>
/// <b>Neden bekçi gerekiyor:</b> yanıt tipi bildirilmeyen bir uç OpenAPI
/// belgesine gövdesiz iniyor, T14'ün ürettiği TypeScript'te <c>unknown</c>
/// kalıyor ve ekran tipi <b>elle</b> yazmak zorunda kalıyor. Elle yazılan tip
/// API ile sessizce ayrışır; T14'ün var olma sebebi tam olarak bunu önlemekti.
/// </para>
///
/// <para>
/// <b>Neden izin listesi:</b> bir yanıt tipini tüketicisi olmadan yazmak tahmin
/// üretiyor — hangi alanların sözleşmeye girdiğine ekran karar vermeli. Bu
/// yüzden tip, uçu gerçekten tüketen ticket ile birlikte geliyor. Listenin
/// işlevi boşluğu <b>görünür</b> kılmak: ekranlar indikçe kısalıyor ve liste
/// boşalmadan F2 bitmiş sayılmıyor (T27).
/// </para>
///
/// <para>
/// Kalıp <c>ParsersEndpointTests</c>'ten: uygulama gerçekten başlatılmıyor,
/// yalnızca uçlar kaydediliyor. Servisler çözülmüyor — kayıtlar yalnızca minimal
/// API'nin parametreyi "servis mi gövde mi" diye ayırt edebilmesi için var.
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
    /// kendiliğinden bayatlayamıyor.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> Pending = new(StringComparer.Ordinal)
    {
        ["POST /v1/logs"] = "Collector'ın ingest ucu; UI tüketicisi yok (istemcide de dışlanmış).",
        ["POST /v1/sources"] = "T17 — kaynak envanteri ekranı",
        ["POST /v1/sources/csv"] = "T17 — kaynak envanteri ekranı",
        ["POST /v1/changes/webhooks/{endpointId}"] =
            "CI sistemlerinin çağırdığı imzalı alıcı; UI tüketicisi yok — POST /v1/logs ile aynı sınıf.",
        ["DELETE /v1/changes/connectors/{id}"] =
            "204, gövdesiz. Uydurulmuş bir yanıt tipi olmayan bir sözleşme vaat ederdi.",
        ["GET /v1/health/pipeline"] = "T20 — boru hattı sağlık ekranı",
        ["POST /v1/replay"] = "T19 — replay ekranı",
        ["POST /v1/parsers/try"] = "T19 — parser editörü (yazar yüzeyi)",
        ["POST /v1/parsers/drafts"] = "T19 — parser editörü (yazar yüzeyi)",
        ["PUT /v1/parsers/drafts/{id}"] = "T19 — parser editörü (yazar yüzeyi)",
        ["POST /v1/parsers/drafts/{id}/submit"] = "T19 — parser editörü (yazar yüzeyi)",

        // Alarm uçlarının on ikisi T23'te tipini kazandı ve bu listeden çıktı —
        // ekran indi, tip artık tahmin değil. Kalan üçü gövdesiz 204: uydurulmuş
        // bir yanıt tipi olmayan bir sözleşme vaat ederdi.
        ["DELETE /v1/alerts/rules/{id}"] = "204, gövdesiz.",
        ["DELETE /v1/alerts/maintenance/{id}"] = "204, gövdesiz.",
        ["DELETE /v1/alerts/channels/{id}"] = "204, gövdesiz.",
    };

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

            // T21/T22/T24/T25 uçlarının bağımlılıkları. Bunlar eklenmeden
            // `MapAlerts`/`MapChangeWebhooks` parametre çıkarımında patlıyordu ve
            // o uç dosyaları kapıya HİÇ görünmüyordu.
            //
            // `TimeProvider` bilerek listede YOK: ASP.NET onu kendi kurulumunda
            // çözüyor ve zehirlemek `WebApplicationBuilder.Build()`'i patlatıyor.
            typeof(AlertRuleService), typeof(NotificationChannelService),
            typeof(AlertingOptions), typeof(AlertingStats), typeof(AlertPreview),
            typeof(IChangeWebhookRegistry), typeof(ChangeWebhookOptions),
            typeof(ChangeWebhookDeliveryLog), typeof(ChangeConnectorService),

            // T20'nin kapsam ucu. Kaydedilmezse `CatalogCoverageCache` gövde
            // parametresi sanılıyor ve `MapParserAuthoring` çıkarımda patlıyor.
            typeof(CatalogCoverageCache), typeof(ParserPublishGate),
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

        app.MapOtlpLogs();
        app.MapEvents();
        app.MapSources();
        app.MapChanges();
        // Bu iki satırın eksikliği bekçiyi sessizce delerdi: kaydedilmeyen uç
        // kapıya hiç görünmüyor ve test yeşil yanıyor. Yeşilliği bir şey ifade
        // etmiyor.
        app.MapChangeWebhooks();
        app.MapChangeConnectors();
        app.MapPipelineHealth();
        app.MapReplay();
        app.MapParsers();
        app.MapParserAuthoring();
        app.MapAlerts();
        app.MapNotificationChannels();

        return [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()];
    }

    /// <summary>
    /// <c>METHOD /yol</c> — izin listesinin anahtarı. Rota deseninden kısıtlar
    /// (<c>{id:guid}</c>) çıkarılıyor: bir kısıt eklemek yanıt tipiyle ilgili
    /// değil, listeyi bozmamalı.
    /// </summary>
    private static IEnumerable<(string Key, RouteEndpoint Endpoint)> V1Endpoints() =>
        Endpoints()
            .Where(static e => (e.RoutePattern.RawText ?? string.Empty).StartsWith("/v1/", StringComparison.Ordinal))
            .SelectMany(static e =>
                (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["?"])
                    .Select(method => ($"{method} {StripConstraints(e.RoutePattern.RawText!)}", e)));

    private static string StripConstraints(string pattern)
    {
        var trimmed = System.Text.RegularExpressions.Regex.Replace(pattern, @"\{([^}:]+)(:[^}]+)?\}", "{$1}");

        // MapGroup("/v1/x") + MapGet("/") deseni "/v1/x/" üretiyor; sondaki
        // eğik çizgi yolun kimliğine ait değil.
        return trimmed.Length > 4 && trimmed.EndsWith('/') ? trimmed[..^1] : trimmed;
    }

    /// <summary>
    /// Bir ucun 200 yanıtı için bildirilmiş <b>gövde tipi</b> var mı.
    /// <c>.Produces(404)</c> gibi gövdesiz bildirimler sayılmıyor.
    /// </summary>
    private static bool DeclaresResponseType(RouteEndpoint endpoint) =>
        endpoint.Metadata
            .GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .Any(static m => m.StatusCode is >= 200 and < 300
                && m.Type is not null
                && m.Type != typeof(void));

    [Fact]
    public void V1_altindaki_her_uc_ya_yanit_tipi_bildiriyor_ya_izin_listesinde()
    {
        var missing = V1Endpoints()
            .Where(static pair => !DeclaresResponseType(pair.Endpoint))
            .Select(static pair => pair.Key)
            .Where(key => !Pending.ContainsKey(key))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Yanıt tipi bildirmeyen uç(lar) izin listesinde değil:\n  " +
            string.Join("\n  ", missing) +
            "\n\nYa uca `.Produces<T>()` ekleyin ya da ProducesContractTests.Pending'e " +
            "hangi ticket'ın kapatacağıyla birlikte yazın.");
    }

    /// <summary>
    /// Liste <b>yalnızca</b> gerçekten eksik olanları taşımalı. Kapatılan bir uç
    /// listede kalırsa liste kısalmayı bırakır ve boşluk yine görünmez olur —
    /// bu testin varlık sebebi tam olarak listenin bayatlamasını engellemek.
    /// </summary>
    [Fact]
    public void Izin_listesi_bayat_giris_tasimiyor()
    {
        var actual = V1Endpoints().ToArray();
        var keys = actual.Select(static pair => pair.Key).ToHashSet(StringComparer.Ordinal);

        var vanished = Pending.Keys.Where(key => !keys.Contains(key)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            vanished.Length == 0,
            "İzin listesinde artık var olmayan uç(lar): " + string.Join(", ", vanished));

        var covered = actual
            .Where(pair => Pending.ContainsKey(pair.Key) && DeclaresResponseType(pair.Endpoint))
            .Select(static pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            covered.Length == 0,
            "Yanıt tipi kazanmış uç(lar) hâlâ izin listesinde: " + string.Join(", ", covered) +
            " — ProducesContractTests.Pending'den silin.");
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
        var changes = V1Endpoints()
            .Where(static pair => pair.Key.Contains("/v1/changes", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(changes);

        foreach (var (key, endpoint) in changes)
        {
            if (Pending.ContainsKey(key))
            {
                continue;
            }

            Assert.True(DeclaresResponseType(endpoint), $"{key} yanıt tipi bildirmiyor.");
        }

        Assert.Contains("POST /v1/changes/webhooks/{endpointId}", Pending.Keys);
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
    /// T15/T16'nın tükettiği uçlar listeden <b>çıkmış</b> olmalı. Bu test,
    /// listenin gerçekten küçüldüğünün ölçüsü; olmadan "bir gün ekleriz"
    /// sessizce kalıcı olabilir.
    /// </summary>
    [Fact]
    public void Olay_uclari_yanit_tipi_tasiyor()
    {
        var events = V1Endpoints()
            .Where(static pair => pair.Key.Contains("/v1/events", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(3, events.Length);

        foreach (var (key, endpoint) in events)
        {
            Assert.True(DeclaresResponseType(endpoint), $"{key} yanıt tipi bildirmiyor.");
            Assert.DoesNotContain(key, Pending.Keys);
        }
    }
}
