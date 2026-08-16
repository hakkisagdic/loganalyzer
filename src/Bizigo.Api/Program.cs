using Bizigo.Api;
using Bizigo.ControlPlane;
using Bizigo.Ingest;
using Bizigo.Ingest.Discovery;
using Bizigo.Ingest.Pipeline;
using Bizigo.Parsing;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Grok;
using Bizigo.Ingest.Wal;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Bizigo.Storage.Raw;

var builder = WebApplication.CreateBuilder(args);

var postgres = builder.Configuration.GetConnectionString("ControlPlane")
    ?? throw new InvalidOperationException("ConnectionStrings:ControlPlane tanımlı değil.");

var clickHouseOptions = new ClickHouseOptions();
builder.Configuration.GetSection(ClickHouseOptions.SectionName).Bind(clickHouseOptions);
clickHouseOptions.ConnectionString = builder.Configuration.GetConnectionString("ClickHouse")
    ?? clickHouseOptions.ConnectionString;

builder.Services.AddControlPlane(postgres);

// Veri düzleminin tamamı tek satırda. API katmanı somut okuyucuları hiç görmüyor —
// yalnızca IScopedQuery (K17). Mimari test bunu zorluyor.
builder.Services.AddBizigoDataPlane(clickHouseOptions);

// Parser motoru, katalog ve dispatcher (T05, T06).
builder.Services.AddBizigoParsing(builder.Configuration);

// Ingest: OTLP çözücü + WAL + boru hattı (T03).
builder.Services.AddBizigoIngest(builder.Configuration);

// Ham arşiv: yükleyici, manifest, scrub (T04).
builder.Services.AddBizigoRawArchive(builder.Configuration);

// Kimlik ve yetkilendirme (T09).
builder.Services.AddBizigoAuthentication(builder.Configuration);

builder.Services.AddHealthChecks();

var app = builder.Build();

// Ayağa kalkarken iki şema da hazır olmalı.
// Not: üretimde göç ayrı bir adım olacak; şimdilik açılışta.
await app.Services.MigrateControlPlaneAsync();

var (applied, existing) = await app.Services.MigrateDataPlaneAsync();
app.Logger.LogInformation(
    "ClickHouse göçleri: {Applied} uygulandı, {Existing} zaten vardı.", applied, existing);

app.UseAuthentication();
app.UseAuthorization();

// Grup → owner_group eşlemesini belleğe al. Boş kalırsa kimse veri göremez;
// bu, eşleme tablosu boşken "her şeyi gör"e düşmekten iyidir (K17).
await app.Services.GetRequiredService<AccessScopeResolver>().RefreshAsync();

app.MapHealthChecks("/healthz");
app.MapAuth();
app.MapOtlpLogs();

// Ingest sayaçları: "boru hattı akıyor mu" sorusunun tek bakışta cevabı.
// `declared_encoding_mismatches` sıfırdan büyükse envanterdeki `encoding` yanlış.
app.MapGet("/internal/ingest/stats", (IngestStats stats, WriteAheadLog wal) => Results.Ok(new
{
    accepted_batches = stats.AcceptedBatches,
    accepted_records = stats.AcceptedRecords,
    processed_records = stats.ProcessedRecords,
    rejected_full = stats.RejectedFull,
    rejected_invalid = stats.RejectedInvalid,
    non_utf8_records = stats.NonUtf8Records,
    declared_encoding_mismatches = stats.DeclaredEncodingMismatches,
    wal = new
    {
        total_bytes = wal.TotalBytes,
        is_full = wal.IsFull,
        recovery = wal.Recovery,
    },
}));

// Keşif yolu (T12). Devre kesici **görünür olmak zorunda** (F1 §9): sidecar
// sıcak yolda olmadığı için arızası hiçbir alarmı tetiklemez; tek belirti
// `template_id`'nin sessizce boş kalmasıdır.
app.MapGet("/internal/discovery/stats", (
    SidecarOptions options,
    DiscoveryStats stats,
    IServiceProvider services) =>
{
    var breaker = services.GetService<SidecarCircuitBreaker>();
    var cache = services.GetService<TemplateCache>();
    var masks = services.GetService<MaskCatalog>();

    return Results.Ok(new
    {
        enabled = options.Enabled,
        base_url = options.BaseUrl,
        api_version = options.ApiVersion,
        timeout_seconds = options.Timeout.TotalSeconds,
        queue_capacity = options.QueueCapacity,
        sample_rate = options.SampleRate,
        circuit = new
        {
            state = breaker?.State.ToString() ?? "Disabled",
            opened_count = breaker?.OpenedCount ?? 0,
            break_minutes = options.BreakDuration.TotalMinutes,
            failure_threshold = options.FailureThreshold,
            last_error = breaker?.LastError,
        },
        masks = new
        {
            version = masks?.Version ?? 0,
            count = masks?.Masks.Count ?? 0,
            source = masks?.SourcePath,
        },
        queue = new
        {
            enqueued = stats.Enqueued,
            dropped_queue_full = stats.DroppedQueueFull,
            dropped_circuit_open = stats.DroppedCircuitOpen,
        },
        sidecar = new
        {
            requests = stats.Requests,
            failures = stats.RequestFailures,
            timeouts = stats.Timeouts,
            mined_messages = stats.MinedMessages,
            new_templates = stats.NewTemplates,
        },
        templates = new
        {
            cache_size = cache?.Count ?? 0,
            cache_hits = stats.CacheHits,
            cache_misses = stats.CacheMisses,
            // Sıfırdan büyükse .NET ile Python maskeleri ayrışmış demektir.
            signature_drift = stats.SignatureDrift,
        },
    });
});

app.MapGet("/", () => Results.Ok(new
{
    service = "bizigo-loganalyzer",
    phase = "F1",
    status = "T08 · T12",
}));

await app.RunAsync();

/// <summary>Entegrasyon testlerinin <c>WebApplicationFactory</c> ile erişebilmesi için.</summary>
public partial class Program;
