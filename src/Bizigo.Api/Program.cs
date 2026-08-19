using Bizigo.Api;
using Bizigo.Alerting;
using Bizigo.Authoring;
using Bizigo.ControlPlane;
using Bizigo.Ingest;
using Bizigo.Ingest.Discovery;
using Bizigo.Ingest.Pipeline;
using Bizigo.Parsing;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Grok;
using Bizigo.Ingest.Wal;
using Bizigo.Query;
using Bizigo.Replay;
using Bizigo.Storage.ClickHouse;
using Bizigo.Storage.Raw;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

// OpenAPI belge üretimi (T14) bu giriş noktasını gerçekten çalıştırıyor;
// gerekçesi ve üç ayarı `DocumentGeneration` içinde.
var builder = DocumentGeneration.CreateBuilder(args);

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

// Replay: gölge tablo + bölüm değiştirme (T11).
builder.Services.AddBizigoReplay();

// Parser yazarlığı: taslak deposu ve yayın kapıları (T18).
builder.Services.AddBizigoAuthoring();

// Alarm motoru ve bildirim kanalları (T21, T22).
builder.Services.AddBizigoAlerting(builder.Configuration);

// Kimlik ve yetkilendirme (T09).
builder.Services.AddBizigoAuthentication(builder.Configuration);

builder.Services.AddHealthChecks();

// OpenAPI: F2'nin istemci kodu bu şemadan doğacak (T10).
builder.Services.AddOpenApi();

// Hız sınırı (risk #6, gürültülü komşu). Tek bir kullanıcının ağır sorgusu
// ClickHouse'u ve dolayısıyla herkesi yavaşlatabiliyor; sınır KULLANICI BAŞINA,
// çünkü küresel bir sınır kalabalık bir ekibi tek kişilik bir ekiple aynı
// kefeye koyardı.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var subject = context.User.FindFirst(BizigoClaims.Subject)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";

        return RateLimitPartition.GetConcurrencyLimiter(subject, _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = 4,
            QueueLimit = 8,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    });
});

var app = builder.Build();

// Belge üretimi sırasında ortada Postgres de ClickHouse da yok; göç adımı
// bağlantı hatasıyla düşer ve belge hiç üretilemez.
if (!DocumentGeneration.IsActive)
{
    // Ayağa kalkarken iki şema da hazır olmalı.
    // Not: üretimde göç ayrı bir adım olacak; şimdilik açılışta.
    await app.Services.MigrateControlPlaneAsync();

    var (applied, existing) = await app.Services.MigrateDataPlaneAsync();
    app.Logger.LogInformation(
        "ClickHouse göçleri: {Applied} uygulandı, {Existing} zaten vardı.", applied, existing);
}

app.UseAuthentication();
app.UseAuthorization();

if (!DocumentGeneration.IsActive)
{
    // Grup → owner_group eşlemesini belleğe al. Boş kalırsa kimse veri göremez;
    // bu, eşleme tablosu boşken "her şeyi gör"e düşmekten iyidir (K17).
    await app.Services.GetRequiredService<AccessScopeResolver>().RefreshAsync();
}

app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/healthz");
app.MapAuth();
app.MapOtlpLogs();

// Sorgu ve yazma yüzeyi (T10). Hepsi IScopedQuery'den geçiyor; mimari test
// API'nin somut okuyuculara erişmesini zaten yasaklıyor.
app.MapEvents();
app.MapSources();
app.MapChanges();
app.MapPipelineHealth();
app.MapReplay();
app.MapParsers();
app.MapParserAuthoring();
app.MapAlerts();
app.MapNotificationChannels();

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
    phase = "F2",
    status = "F1 tamamlandı; F2 sürüyor",
}));

await app.RunAsync();

/// <summary>Entegrasyon testlerinin <c>WebApplicationFactory</c> ile erişebilmesi için.</summary>
public partial class Program;
