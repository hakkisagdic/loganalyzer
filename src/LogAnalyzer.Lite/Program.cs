using System;
using System.IO;
using LogAnalyzer.Lite.Models;
using LogAnalyzer.Lite.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddSingleton<AlertWebhookNotifier>();
builder.Services.AddSingleton<IncidentFlightRecorder>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();

// Allow iframe embedding from Gateway and all origins
app.Use(async (context, next) =>
{
    context.Response.Headers.Remove("X-Frame-Options");
    context.Response.Headers.Append("Content-Security-Policy", "frame-ancestors *;");
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
    component = "Incident Flight Recorder",
    timestamp = DateTime.UtcNow
}));

// --- Ingestion ---
app.MapPost("/api/v1/ingest", (IngestRequest request, IncidentFlightRecorder recorder) =>
{
    var response = recorder.Ingest(request);
    return Results.Ok(response);
});

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
});

app.Run();
