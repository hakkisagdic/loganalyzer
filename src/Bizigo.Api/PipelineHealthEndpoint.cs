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
            .WithTags("health");

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

        return Results.Ok(new
        {
            // 1) Dispatcher: envanter bakımsız kalırsa bu oran düşer ve sistem
            //    hâlâ çalışıyor görünür.
            dispatch = new
            {
                total = dispatch.Total,
                bound_ratio = Math.Round(boundRatio, 4),
                bound_ratio_target = BoundRatioTarget,
                bound_ratio_healthy = dispatch.Total == 0 || boundRatio >= BoundRatioTarget,
                bound_misses = dispatch.BoundMisses,
                unmatched_ratio = Math.Round(dispatch.UnmatchedRatio, 4),
                unassigned_source_events = dispatch.UnassignedSources,
            },

            // 2) parse_status dağılımı.
            parse = new
            {
                ok = dispatch.Bound + dispatch.Candidate,
                unmatched = dispatch.Unmatched,
                processed_records = ingest.ProcessedRecords,
            },

            // 3) WAL derinliği: dayanıklılık sınırı burada. Dolarsa ingest 503 döner.
            wal = new
            {
                total_bytes = wal.TotalBytes,
                is_full = wal.IsFull,
                recovery = wal.Recovery,
            },

            // 4) Ingest kabul/ret sayaçları.
            ingest = new
            {
                accepted_records = ingest.AcceptedRecords,
                rejected_full = ingest.RejectedFull,
                rejected_invalid = ingest.RejectedInvalid,
                non_utf8_records = ingest.NonUtf8Records,
                declared_encoding_mismatches = ingest.DeclaredEncodingMismatches,
            },

            // 5) Arşiv + scrub: kayıp ya da bozuk nesne replay gününde değil
            //    bugün görünmeli.
            archive = new
            {
                by_state = manifest.ToDictionary(x => x.State.ToString(), x => x.Count, StringComparer.Ordinal),
                healthy = manifest.All(x => x.State is RawObjectState.Uploaded or RawObjectState.Verified),
            },

            // 6) Sidecar devre kesici: sıcak yolda olmadığı için arızası hiçbir
            //    alarmı tetiklemez, tek belirtisi template_id'nin boş kalmasıdır.
            sidecar = new
            {
                enabled = sidecarOptions.Enabled,
                circuit = breaker?.State.ToString() ?? "Disabled",
                opened_count = breaker?.OpenedCount ?? 0,
                dropped_queue_full = discovery.DroppedQueueFull,
                dropped_circuit_open = discovery.DroppedCircuitOpen,
                signature_drift = discovery.SignatureDrift,
            },

            inventory = new
            {
                unassigned_sources = unassigned,
            },
        });
    }
}
