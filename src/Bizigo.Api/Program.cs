using Bizigo.Api;
using Bizigo.ControlPlane;
using Bizigo.Ingest;
using Bizigo.Ingest.Pipeline;
using Bizigo.Parsing;
using Bizigo.Parsing.Dispatch;
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

builder.Services.AddHealthChecks();

var app = builder.Build();

// Ayağa kalkarken iki şema da hazır olmalı.
// Not: üretimde göç ayrı bir adım olacak; şimdilik açılışta.
await app.Services.MigrateControlPlaneAsync();

var (applied, existing) = await app.Services.MigrateDataPlaneAsync();
app.Logger.LogInformation(
    "ClickHouse göçleri: {Applied} uygulandı, {Existing} zaten vardı.", applied, existing);

app.MapHealthChecks("/healthz");
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

app.MapGet("/", () => Results.Ok(new
{
    service = "bizigo-loganalyzer",
    phase = "F1",
    status = "T03",
}));

await app.RunAsync();

/// <summary>Entegrasyon testlerinin <c>WebApplicationFactory</c> ile erişebilmesi için.</summary>
public partial class Program;
