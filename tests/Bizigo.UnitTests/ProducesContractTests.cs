using Bizigo.Api;
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
        ["GET /v1/changes"] = "T24 — değişiklik akışı formu",
        ["POST /v1/changes"] = "T24 — değişiklik akışı formu",
        ["GET /v1/health/pipeline"] = "T20 — boru hattı sağlık ekranı",
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
        })
        {
            var captured = type;
            builder.Services.AddSingleton(captured, _ =>
                throw new InvalidOperationException(
                    $"{captured.Name} bu testte çözülmemeli — yalnızca kayıt sınanıyor."));
        }

        var app = builder.Build();

        app.MapOtlpLogs();
        app.MapEvents();
        app.MapSources();
        app.MapChanges();
        app.MapPipelineHealth();
        app.MapReplay();
        app.MapParsers();
        app.MapParserAuthoring();

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
