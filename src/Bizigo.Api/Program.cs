using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;

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

builder.Services.AddHealthChecks();

var app = builder.Build();

// Ayağa kalkarken iki şema da hazır olmalı.
// Not: üretimde göç ayrı bir adım olacak; şimdilik açılışta.
await app.Services.MigrateControlPlaneAsync();

var (applied, existing) = await app.Services.MigrateDataPlaneAsync();
app.Logger.LogInformation(
    "ClickHouse göçleri: {Applied} uygulandı, {Existing} zaten vardı.", applied, existing);

app.MapHealthChecks("/healthz");
app.MapGet("/", () => Results.Ok(new
{
    service = "bizigo-loganalyzer",
    phase = "F1",
    status = "T02",
}));

await app.RunAsync();

/// <summary>Entegrasyon testlerinin <c>WebApplicationFactory</c> ile erişebilmesi için.</summary>
public partial class Program;
