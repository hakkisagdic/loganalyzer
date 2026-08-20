using System.Text.Json.Serialization;

namespace Bizigo.Api;

/// <summary>
/// <c>GET /v1/health/pipeline</c> gövdesi (T17 özet göstergeleri).
///
/// <para>
/// Anonim nesne yerine adlandırılmış tipler: envanter ekranı bu göstergelerin
/// özetini gösteriyor ve tipin <c>unknown</c> kalması, ekranın altı bloğun
/// şeklini elle yazması demekti.
/// </para>
///
/// <para>
/// Buradaki göstergelerin ortak yanı <b>hiçbirinin arıza anında alarm
/// üretmemesi</b>: sistem çalışmaya devam ediyor ve hepsi sessiz çürüme
/// sınıfından. Ekranın işi, çürümeyi bakılmadan görünür kılmak.
/// </para>
/// </summary>
public sealed record PipelineHealthResponse(
    [property: JsonPropertyName("dispatch")] PipelineDispatchHealth Dispatch,
    [property: JsonPropertyName("parse")] PipelineParseHealth Parse,
    [property: JsonPropertyName("wal")] PipelineWalHealth Wal,
    [property: JsonPropertyName("ingest")] PipelineIngestHealth Ingest,
    [property: JsonPropertyName("archive")] PipelineArchiveHealth Archive,
    [property: JsonPropertyName("sidecar")] PipelineSidecarHealth Sidecar,
    [property: JsonPropertyName("inventory")] PipelineInventoryHealth Inventory);

/// <summary>
/// Dispatcher. <c>bound_ratio</c> düşerse envanter bakımsız kalmış demek — ve
/// sistem bu sırada <b>çalışıyor görünür</b>.
/// </summary>
public sealed record PipelineDispatchHealth(
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("bound_ratio")] double BoundRatio,
    [property: JsonPropertyName("bound_ratio_target")] double BoundRatioTarget,
    [property: JsonPropertyName("bound_ratio_healthy")] bool BoundRatioHealthy,
    [property: JsonPropertyName("bound_misses")] long BoundMisses,
    [property: JsonPropertyName("unmatched_ratio")] double UnmatchedRatio,
    [property: JsonPropertyName("unassigned_source_events")] long UnassignedSourceEvents);

public sealed record PipelineParseHealth(
    [property: JsonPropertyName("ok")] long Ok,
    [property: JsonPropertyName("unmatched")] long Unmatched,
    [property: JsonPropertyName("processed_records")] long ProcessedRecords);

/// <summary>WAL derinliği: dayanıklılık sınırı burada. Dolarsa ingest 503 döner.</summary>
public sealed record PipelineWalHealth(
    [property: JsonPropertyName("total_bytes")] long TotalBytes,
    [property: JsonPropertyName("is_full")] bool IsFull,
    /// <summary>
    /// Açılışta kurtarılan WAL. <c>truncated_bytes</c> sıfırdan büyükse yarım
    /// yazılmış bir çerçeve atılmış demek — süreç sert kapanmış.
    /// </summary>
    [property: JsonPropertyName("recovery")] PipelineWalRecovery Recovery);

public sealed record PipelineWalRecovery(
    [property: JsonPropertyName("segment_count")] int SegmentCount,
    [property: JsonPropertyName("frame_count")] int FrameCount,
    [property: JsonPropertyName("truncated_bytes")] long TruncatedBytes);

public sealed record PipelineIngestHealth(
    [property: JsonPropertyName("accepted_records")] long AcceptedRecords,
    [property: JsonPropertyName("rejected_full")] long RejectedFull,
    [property: JsonPropertyName("rejected_invalid")] long RejectedInvalid,
    [property: JsonPropertyName("non_utf8_records")] long NonUtf8Records,
    /// <summary>Sıfırdan büyükse envanterdeki <c>encoding</c> yanlış.</summary>
    [property: JsonPropertyName("declared_encoding_mismatches")] long DeclaredEncodingMismatches);

/// <summary>Kayıp ya da bozuk nesne replay gününde değil <b>bugün</b> görünmeli.</summary>
public sealed record PipelineArchiveHealth(
    [property: JsonPropertyName("by_state")] IReadOnlyDictionary<string, int> ByState,
    [property: JsonPropertyName("healthy")] bool Healthy);

/// <summary>
/// Sidecar devre kesici. Sıcak yolda olmadığı için arızası hiçbir alarmı
/// tetiklemiyor; tek belirtisi <c>template_id</c>'nin sessizce boş kalması.
/// </summary>
public sealed record PipelineSidecarHealth(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("circuit")] string Circuit,
    [property: JsonPropertyName("opened_count")] long OpenedCount,
    [property: JsonPropertyName("dropped_queue_full")] long DroppedQueueFull,
    [property: JsonPropertyName("dropped_circuit_open")] long DroppedCircuitOpen,
    /// <summary>Sıfırdan büyükse .NET ile Python maskeleri ayrışmış demektir.</summary>
    [property: JsonPropertyName("signature_drift")] long SignatureDrift);

public sealed record PipelineInventoryHealth(
    [property: JsonPropertyName("unassigned_sources")] int UnassignedSources);
