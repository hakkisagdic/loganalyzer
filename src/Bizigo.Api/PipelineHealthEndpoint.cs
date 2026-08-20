using Bizigo.ControlPlane;
using Bizigo.Ingest.Discovery;
using Bizigo.Ingest.Pipeline;
using Bizigo.Ingest.Wal;
using Bizigo.Parsing.Dispatch;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Api;

/// <summary>
/// Boru hattı sağlığı (T10 kabul kriteri: altı gösterge tek yerde).
///
/// <para>
/// Buradaki göstergelerin ortak yanı, <b>hiçbirinin arıza anında alarm
/// üretmemesi</b>. Sistem çalışmaya devam ediyor: <c>bound_ratio</c> düşse de
/// satırlar ayrıştırılıyor, WAL birikse de veri kaybolmuyor, sidecar ölse de
/// ingest akıyor, scrub bir nesne kaybetse de sorgular çalışıyor. Hepsi sessiz
/// çürüme sınıfından — bu uç olmadan fark edilmezler.
/// </para>
/// </summary>
public static class PipelineHealthEndpoint
{
    /// <summary><c>bound_ratio</c> için hedef (F1 §4.2).</summary>
    private const double BoundRatioTarget = 0.95;

    public static IEndpointRouteBuilder MapPipelineHealth(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/v1/health/pipeline", HandleAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("PipelineHealth")
            .WithTags("health")
            // Tüketicisi T17'nin envanter ekranındaki özet bloğu.
            .Produces<PipelineHealthResponse>();

        return routes;
    }

    private static async Task<IResult> HandleAsync(
        DispatchStats dispatch,
        IngestStats ingest,
        WriteAheadLog wal,
        DiscoveryStats discovery,
        SidecarOptions sidecarOptions,
        ControlPlaneDbContext db,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var breaker = services.GetService<SidecarCircuitBreaker>();

        var manifest = await db.RawManifest
            .AsNoTracking()
            .GroupBy(m => m.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var unassigned = await db.Sources
            .AsNoTracking()
            .CountAsync(s => s.OwnerGroup == Contracts.OwnerGroups.Unassigned, cancellationToken);

        var boundRatio = dispatch.BoundRatio;

        return Results.Ok(new PipelineHealthResponse(
            // 1) Dispatcher: envanter bakımsız kalırsa bu oran düşer ve sistem
            //    hâlâ çalışıyor görünür.
            new PipelineDispatchHealth(
                dispatch.Total,
                Math.Round(boundRatio, 4),
                BoundRatioTarget,
                dispatch.Total == 0 || boundRatio >= BoundRatioTarget,
                dispatch.BoundMisses,
                Math.Round(dispatch.UnmatchedRatio, 4),
                dispatch.UnassignedSources),

            // 2) parse_status dağılımı.
            new PipelineParseHealth(
                dispatch.Bound + dispatch.Candidate,
                dispatch.Unmatched,
                ingest.ProcessedRecords),

            // 3) WAL derinliği: dayanıklılık sınırı burada. Dolarsa ingest 503 döner.
            new PipelineWalHealth(
                wal.TotalBytes,
                wal.IsFull,
                new PipelineWalRecovery(
                    wal.Recovery.SegmentCount, wal.Recovery.FrameCount, wal.Recovery.TruncatedBytes)),

            // 4) Ingest kabul/ret sayaçları.
            new PipelineIngestHealth(
                ingest.AcceptedRecords,
                ingest.RejectedFull,
                ingest.RejectedInvalid,
                ingest.NonUtf8Records,
                ingest.DeclaredEncodingMismatches),

            // 5) Arşiv + scrub: kayıp ya da bozuk nesne replay gününde değil
            //    bugün görünmeli.
            new PipelineArchiveHealth(
                manifest.ToDictionary(x => x.State.ToString(), x => x.Count, StringComparer.Ordinal),
                manifest.All(x => x.State is RawObjectState.Uploaded or RawObjectState.Verified)),

            // 6) Sidecar devre kesici: sıcak yolda olmadığı için arızası hiçbir
            //    alarmı tetiklemez, tek belirtisi template_id'nin boş kalmasıdır.
            new PipelineSidecarHealth(
                sidecarOptions.Enabled,
                breaker?.State.ToString() ?? "Disabled",
                breaker?.OpenedCount ?? 0,
                discovery.DroppedQueueFull,
                discovery.DroppedCircuitOpen,
                discovery.SignatureDrift),

            new PipelineInventoryHealth(unassigned)));
    }
}
