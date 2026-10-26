using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using LogAnalyzer.Lite.Models;
using LogAnalyzer.Lite.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var dataDir = Environment.GetEnvironmentVariable("LITE_DATA_DIR")
              ?? builder.Configuration["LITE_DATA_DIR"]
              ?? Path.Combine(AppContext.BaseDirectory, "data");

var maxEvents = int.TryParse(Environment.GetEnvironmentVariable("LITE_MAX_EVENTS") ?? builder.Configuration["LITE_MAX_EVENTS"], out var me) && me >= 100
    ? me
    : 10000;

var retentionHours = int.TryParse(Environment.GetEnvironmentVariable("LITE_RETENTION_HOURS") ?? builder.Configuration["LITE_RETENTION_HOURS"], out var rh) && rh >= 1
    ? rh
    : 168;

builder.Services.AddSingleton(new AlertWebhookNotifier(storagePath: dataDir));
builder.Services.AddSingleton(sp => new IncidentFlightRecorder(
    dataDir,
    sp.GetRequiredService<AlertWebhookNotifier>(),
    maxEvents,
    retentionHours));

var app = builder.Build();

static bool ConstantTimeEquals(string? provided, string expected)
{
    if (string.IsNullOrEmpty(provided)) return false;
    var a = Encoding.UTF8.GetBytes(provided);
    var b = Encoding.UTF8.GetBytes(expected);
    return CryptographicOperations.FixedTimeEquals(a, b);
}

// Sub-path reverse proxy support (/loganalyzer/* -> /*) & token guards
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/loganalyzer", out var remaining))
    {
        context.Request.Path = remaining.HasValue && remaining.Value!.Length > 0 ? remaining : new PathString("/index.html");
    }

    var path = context.Request.Path.Value ?? "/";
    var config = context.RequestServices.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
    var ingestToken = config["LITE_INGEST_TOKEN"] ?? config["LOGANALYZER_INGEST_TOKEN"] ?? Environment.GetEnvironmentVariable("LITE_INGEST_TOKEN");
    var adminToken = config["LITE_ADMIN_TOKEN"] ?? config["LOGANALYZER_ADMIN_TOKEN"] ?? Environment.GetEnvironmentVariable("LITE_ADMIN_TOKEN");

    if (path.Equals("/api/v1/ingest", StringComparison.OrdinalIgnoreCase))
    {
        if (!string.IsNullOrWhiteSpace(ingestToken))
        {
            var header = context.Request.Headers["X-LogAnalyzer-Ingest-Token"].ToString();
            if (!ConstantTimeEquals(header, ingestToken.Trim()))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsJsonAsync(new { error = "unauthorized_ingest", message = "Valid X-LogAnalyzer-Ingest-Token required." });
                return;
            }
        }
    }
    else if (path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase))
    {
        if (!string.IsNullOrWhiteSpace(adminToken))
        {
            var header = context.Request.Headers["X-LogAnalyzer-Admin-Token"].ToString();
            if (!ConstantTimeEquals(header, adminToken.Trim()))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsJsonAsync(new { error = "unauthorized_admin", message = "Valid X-LogAnalyzer-Admin-Token required." });
                return;
            }
        }

        if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
        {
            var secFetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
            if (string.Equals(secFetchSite, "cross-site", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "csrf_rejected", message = "Cross-site state mutation rejected." });
                return;
            }
        }
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

// Root and empty path fallback for reverse-proxy strip-prefix
app.MapGet("", () => Results.Redirect("/index.html"));
app.MapGet("/loganalyzer", () => Results.Redirect("/loganalyzer/index.html"));

// --- Health ---
app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    product = "LogAnalyzer Lite",
    component = "Full Traffic & Incident Flight Recorder",
    timestamp = DateTime.UtcNow
}));

// --- Ingestion ---
app.MapPost("/api/v1/ingest", (IngestRequest request, IncidentFlightRecorder recorder) =>
{
    var response = recorder.Ingest(request);
    return Results.Ok(response);
});

// --- Events & Incidents ---
app.MapGet("/api/v1/incidents", (
    int? limit,
    string? search,
    string? level,
    string? source,
    string? direction,
    string? tenantId,
    bool? incidentsOnly,
    IncidentFlightRecorder recorder) =>
{
    var items = recorder.GetIncidents(limit ?? 100, search, level, source, direction, tenantId, incidentsOnly);
    return Results.Ok(items);
});

app.MapGet("/api/v1/events", (
    int? limit,
    string? search,
    string? level,
    string? source,
    string? direction,
    string? tenantId,
    bool? incidentsOnly,
    IncidentFlightRecorder recorder) =>
{
    var items = recorder.GetIncidents(limit ?? 100, search, level, source, direction, tenantId, incidentsOnly);
    return Results.Ok(items);
});

app.MapGet("/api/v1/incidents/{id}", (string id, IncidentFlightRecorder recorder) =>
{
    var incident = recorder.GetIncident(id);
    return incident is not null
        ? Results.Ok(incident)
        : Results.NotFound(new { error = "NotFound", message = $"Event '{id}' not found." });
});

app.MapGet("/api/v1/events/{id}", (string id, IncidentFlightRecorder recorder) =>
{
    var incident = recorder.GetIncident(id);
    return incident is not null
        ? Results.Ok(incident)
        : Results.NotFound(new { error = "NotFound", message = $"Event '{id}' not found." });
});

// --- Stats ---
app.MapGet("/api/v1/stats", (IncidentFlightRecorder recorder) => Results.Ok(recorder.GetStats()));

// --- Maintenance / Purge ---
app.MapPost("/api/v1/logs/purge", (PurgeRequest? request, IncidentFlightRecorder recorder) =>
{
    var response = recorder.Purge(request ?? new PurgeRequest());
    return Results.Ok(response);
});

// --- Alerts & Webhooks ---
app.MapGet("/api/v1/alerts/config", (AlertWebhookNotifier notifier) => Results.Ok(notifier.GetConfig()));

app.MapPost("/api/v1/alerts/config", (AlertWebhookConfig config, AlertWebhookNotifier notifier) =>
{
    notifier.UpdateConfig(config);
    return Results.Ok(new { success = true, message = "Alert webhook configuration updated." });
});

app.MapPost("/api/v1/alerts/test", async (AlertTestRequest request, AlertWebhookNotifier notifier) =>
{
    var result = await notifier.TestAlertAsync(request);
    return Results.Ok(result);
});

// Seed sample incidents trigger (for testing / demo)
app.MapPost("/api/v1/demo/simulate-incident", (IncidentFlightRecorder recorder) =>
{
    var r = new Random();
    var sampleErrors = new[]
    {
        ("EDM Integrator Timeout", 504, "Gateway.EdmAdapter", "/api/v1/invoices/send-edm", "Outbound", "edm", "SendInvoice", "Connection timeout after 30000ms connecting to EDM service endpoint."),
        ("Financial Seal Signature Failure", 500, "Gateway.Security", "/api/v1/security/sign-envelope", "Inbound", "gateway", "SignEnvelope", "SmartCard PKCS#11 token returned PIN_LOCKED."),
        ("UBL-TR Schematron Validation Error", 422, "Gateway.Validation", "/api/v1/invoices/validate", "Inbound", "gateway", "ValidateUbl", "Element 'cbc:TaxAmount' expected decimal with 2 digits, received 'NULL'."),
        ("Sovos FitBulut SOAP Fault", 200, "Gateway.SovosAdapter", "https://efaturawstest.fitbulut.com/ClientEInvoiceServices/ClientEInvoiceServicesPort.asmx", "Outbound", "sovos", "sendUBL", "SOAP Fault: 2005 Gonderici VKN paketteki VKN ile eslesmiyor.")
    };
    var selected = sampleErrors[r.Next(sampleErrors.Length)];
    var trackingId = "ERR-SIM-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    recorder.Ingest(new IngestRequest
    {
        TrackingId = trackingId,
        CorrelationId = Guid.NewGuid().ToString("N"),
        Direction = selected.Item5,
        Provider = selected.Item6,
        Operation = selected.Item7,
        Level = selected.Item2 >= 500 ? "Fatal" : "Error",
        IsBusinessFailure = selected.Item2 == 200,
        Source = selected.Item3,
        Endpoint = selected.Item4,
        HttpMethod = "POST",
        HttpStatus = selected.Item2,
        DurationMs = r.Next(150, 4500),
        ErrorMessage = selected.Item8,
        StackTrace = $"at {selected.Item3}.ExecuteAsync() in /src/Gateway/Pipeline.cs:line {r.Next(40, 200)}",
        RequestPayload = $"{{\"invoiceNumber\":\"GIB2026{r.Next(10000, 99999)}\", \"receiverVkn\":\"1234567890\", \"amount\": {r.Next(100, 50000)}.50}}",
        ResponsePayload = $"{{\"error\":\"{selected.Item1}\", \"code\":{selected.Item2}, \"trackingId\":\"{trackingId}\"}}",
        Metadata = new Dictionary<string, string> { { "simulation", "true" }, { "tenantId", "default" }, { "direction", selected.Item5 } }
    });

    return Results.Ok(new { message = "Simulated incident recorded", trackingId });
});

app.Run();
