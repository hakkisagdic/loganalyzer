using LogAnalyzer.Lite.Configuration;
using LogAnalyzer.Lite.Models;
using LogAnalyzer.Lite.Security;
using LogAnalyzer.Lite.Services;
using Microsoft.AspNetCore.Cors.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Ayarlar DI'dan tembel çözülüyor: Build() sonrası yapılandırmanın tamamını görsün.
builder.Services.AddSingleton(sp => LiteSettings.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<LiteStorage>();
builder.Services.AddSingleton<AlertWebhookNotifier>();
builder.Services.AddSingleton<IncidentFlightRecorder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IncidentFlightRecorder>());

// Bildirim istemcisi: zaman aşımı, yönlendirme yok, ve HttpClientFactory'nin
// varsayılan istek logları KAPALI — o loglar tam URL'yi yazıyor ve webhook
// adresinin yolu sırrın kendisi.
builder.Services
    .AddHttpClient(AlertWebhookNotifier.HttpClientName, client => client.Timeout = AlertWebhookNotifier.RequestTimeout)
    .ConfigurePrimaryHttpMessageHandler(AlertWebhookNotifier.CreatePrimaryHandler)
    .RemoveAllLoggers();

builder.Services.AddCors();
builder.Services.AddOptions<CorsOptions>().Configure<LiteSettings>(BrowserPolicies.ConfigureCors);

var app = builder.Build();

var settings = app.Services.GetRequiredService<LiteSettings>();

if (settings.IngestToken is null)
{
    app.Logger.LogWarning(
        "{Key} is not set: /api/v1/ingest accepts unauthenticated requests. This is a rollout-compatibility mode; " +
        "set the token here and send it from the Gateway sink as {Header}.",
        LiteSettings.IngestTokenKey,
        LiteRequestGuardMiddleware.IngestTokenHeader);
}

foreach (var warning in settings.Warnings)
{
    app.Logger.LogWarning("{Warning}", warning);
}

app.UseLiteSecurityHeaders(settings);

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseCors();
app.UseMiddleware<LiteRequestGuardMiddleware>();

// Root and empty path fallback for reverse-proxy strip-prefix
app.MapGet("", () => Results.Redirect("/index.html"));
app.MapGet("/loganalyzer", () => Results.Redirect("/loganalyzer/index.html"));

// --- Health ---
app.MapGet("/health", () => Results.Ok(new HealthResponse
{
    Version = BuildInfo.Version,
    Commit = BuildInfo.Commit(settings),
    Timestamp = DateTime.UtcNow,
}));

// --- Ingestion ---
app.MapPost("/api/v1/ingest", (IngestRequest request, IncidentFlightRecorder recorder) =>
{
    var response = recorder.Ingest(request);
    return Results.Ok(response);
}).WithMetadata(IngestTokenRequirement.Instance);

// --- Incidents ---
app.MapGet("/api/v1/incidents", (int? limit, string? search, string? level, string? source, IncidentFlightRecorder recorder) =>
{
    var incidents = recorder.GetIncidents(limit ?? 50, search, level, source);
    return Results.Ok(incidents);
});

app.MapGet("/api/v1/incidents/{id}", (string id, IncidentFlightRecorder recorder) =>
{
    var incident = recorder.GetIncident(id);
    return incident is not null ? Results.Ok(incident) : Results.NotFound(new { error = "Incident not found" });
});

// --- Stats & Purge ---
app.MapGet("/api/v1/stats", (IncidentFlightRecorder recorder) => Results.Ok(recorder.GetStats()));

app.MapPost("/api/v1/logs/purge", (PurgeRequest? request, IncidentFlightRecorder recorder) =>
{
    var response = recorder.Purge(request ?? new PurgeRequest());
    return Results.Ok(response);
}).WithMetadata(AdminActionRequirement.Instance);

// --- Alerts & Webhooks ---
app.MapGet("/api/v1/alerts/config", (AlertWebhookNotifier notifier) => Results.Ok(notifier.GetConfigView()));

app.MapPost("/api/v1/alerts/config", (AlertConfigUpdateRequest request, AlertWebhookNotifier notifier, ILogger<AlertWebhookNotifier> logger) =>
{
    AlertConfigUpdateResult result;

    try
    {
        result = notifier.TryUpdate(request);
    }
    catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException)
    {
        logger.LogError(storageError, "Alert configuration could not be persisted.");
        return Results.Json(
            new ApiError { Error = "storage_unavailable", Message = "Yapılandırma diske yazılamadı; değişiklik uygulanmadı." },
            statusCode: StatusCodes.Status500InternalServerError);
    }

    return result.Error is { } invalid
        ? Results.BadRequest(invalid)
        : Results.Ok(new AlertConfigUpdateResponse
        {
            Message = "Alert webhook configuration updated.",
            Config = result.View,
        });
}).WithMetadata(AdminActionRequirement.Instance);

app.MapPost("/api/v1/alerts/test", async (AlertTestRequest request, AlertWebhookNotifier notifier, CancellationToken cancellationToken) =>
{
    if (AlertWebhookNotifier.ValidateTestRequest(request) is { } error)
    {
        return Results.BadRequest(error);
    }

    var result = await notifier.TestAlertAsync(request, cancellationToken);
    return Results.Ok(result);
}).WithMetadata(AdminActionRequirement.Instance);

// Seed sample incidents trigger (for testing / demo). Üretim deposuna sahte
// olay yazdığı için YALNIZCA LITE_ENABLE_DEMO=true iken haritalanıyor; aksi
// hâlde uç yok ve 404.
if (settings.EnableDemo)
{
    app.MapPost("/api/v1/demo/simulate-incident", (IncidentFlightRecorder recorder) =>
    {
        var r = Random.Shared;
        var sampleErrors = new[]
        {
            ("EDM Integrator Timeout", 504, "Gateway.EdmAdapter", "/api/v1/invoices/send-edm", "Connection timeout after 30000ms connecting to EDM service endpoint."),
            ("Financial Seal Signature Failure", 500, "Gateway.Security", "/api/v1/security/sign-envelope", "SmartCard PKCS#11 token returned PIN_LOCKED."),
            ("UBL-TR Schematron Validation Error", 422, "Gateway.Validation", "/api/v1/invoices/validate", "Element 'cbc:TaxAmount' expected decimal with 2 digits, received 'NULL'."),
            ("Iyzico Payment Gateway 401 Unauthorized", 401, "Gateway.IyzicoAdapter", "/api/v1/payments/iyzico/init", "Invalid API Key or Signature mismatch for merchant 4840847211.")
        };
        var selected = sampleErrors[r.Next(sampleErrors.Length)];
        var trackingId = "TRK-SIM-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        recorder.Ingest(new IngestRequest
        {
            TrackingId = trackingId,
            Level = selected.Item2 >= 500 ? "Fatal" : "Error",
            Source = selected.Item3,
            Endpoint = selected.Item4,
            HttpMethod = "POST",
            HttpStatus = selected.Item2,
            DurationMs = r.Next(150, 4500),
            ErrorMessage = selected.Item5,
            StackTrace = $"at {selected.Item3}.ExecuteAsync() in /src/Gateway/Pipeline.cs:line {r.Next(40, 200)}",
            RequestPayload = $"{{\"invoiceNumber\":\"GIB2026{r.Next(10000, 99999)}\", \"receiverVkn\":\"1234567890\", \"amount\": {r.Next(100, 50000)}.50}}",
            ResponsePayload = $"{{\"error\":\"{selected.Item1}\", \"code\":{selected.Item2}, \"trackingId\":\"{trackingId}\"}}",
            Metadata = new Dictionary<string, string> { { "simulation", "true" }, { "tenant", "EDM-Default" } }
        });

        return Results.Ok(new { message = "Simulated incident recorded", trackingId });
    }).WithMetadata(AdminActionRequirement.Instance);
}

app.Run();
