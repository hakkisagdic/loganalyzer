using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Mcp;

namespace Bizigo.Simulators.Mcp.Tools;

/// <summary>
/// <c>sim.fleet.list</c> — filodaki cihazlar, grupları ve <b>bu katmandan
/// istenmiş</b> senaryoları.
///
/// <para>
/// <b>Filo doğrulama hataları yanıtta taşınıyor</b>, yutulmuyor. Yutulsaydı
/// bozuk bir filo boş bir cihaz listesi verirdi ve model bunu <i>"filoda cihaz
/// yok"</i> diye okurdu — bu depoda adı konmuş sınıf: hata yok, sayaç yok,
/// cevap yanlış.
/// </para>
/// </summary>
/// <param name="context">Filo ve durum zemini.</param>
public sealed class FleetListTool(SimulatorMcpContext context) : SimulatorTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "sim.fleet.list";

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "Simülatör filosu";

    /// <inheritdoc/>
    public override string ToolDescription =>
        "Simüle edilen cihazları listeler: kimlik, üretici, kapsam grubu, taklit ettiği yüzeyler "
        + "ve bu katmandan istenmiş senaryo. Ürün verisi değil, simülatör durumu döndürür.";

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "devices": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id":              { "type": "string" },
                  "vendor":          { "type": "string" },
                  "product":         { "type": "string" },
                  "hostname":        { "type": "string" },
                  "owner_group":     { "type": "string" },
                  "surfaces":        { "type": "array", "items": { "type": "string", "enum": ["config", "syslog"] } },
                  "scenario":        { "type": ["string", "null"] },
                  "scenario_set_at": { "type": ["string", "null"], "format": "date-time" },
                  "last_applied_at": { "type": ["string", "null"], "format": "date-time" },
                  "silenced":        { "type": "boolean" }
                },
                "required": ["id", "vendor", "product", "hostname", "owner_group", "surfaces", "scenario", "scenario_set_at", "last_applied_at", "silenced"],
                "additionalProperties": false
              }
            },
            "device_count": { "type": "integer", "minimum": 0 },
            "fleet_errors": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["devices", "device_count", "fleet_errors"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected override ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fleet = context.LoadFleet();
        var state = context.State.Read();

        return ValueTask.FromResult(McpToolResult.Structured(Shape(fleet, state)));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Sabit bir domain nesnesiyle GERÇEK şekillendirme. Diske ve filoya
        // dokunmuyor (§2: örneğin şartı altyapıya bağlanmak değil, şemanın
        // gerçekten üretilen çıktıyı tarif ettiğini göstermek).
        var profile = SimulatorSamples.Profile;

        var state = SimulatorState.Empty.With(
            profile.Id,
            new SimulatorDeviceState(
                "saat-kaymasi",
                SimulatorSamples.Moment,
                SimulatorSamples.Moment,
                Silenced: false));

        return ValueTask.FromResult(
            McpToolResult.Structured(Shape(new FleetSnapshot([profile], []), state)));
    }

    private static Payload Shape(FleetSnapshot fleet, SimulatorState state) => new(
        [.. fleet.Profiles.Select(profile => Describe(profile, state.For(profile.Id)))],
        fleet.Profiles.Count,
        fleet.Errors);

    private static DevicePayload Describe(SimulatorProfile profile, SimulatorDeviceState state) => new(
        profile.Id,
        profile.Vendor,
        profile.Product,
        profile.Hostname,
        profile.OwnerGroup,
        [.. SimulatorSurfaces.Of(profile).Select(SimulatorSurfaces.WireName)],
        state.Scenario,
        state.ScenarioSetAt,
        state.LastAppliedAt,
        state.Silenced);

    /// <summary>Yanıt tipi — anonim nesne değil (§8).</summary>
    private sealed record Payload(
        [property: JsonPropertyName("devices")] IReadOnlyList<DevicePayload> Devices,
        [property: JsonPropertyName("device_count")] int DeviceCount,
        [property: JsonPropertyName("fleet_errors")] IReadOnlyList<string> FleetErrors);

    private sealed record DevicePayload(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("vendor")] string Vendor,
        [property: JsonPropertyName("product")] string Product,
        [property: JsonPropertyName("hostname")] string Hostname,
        [property: JsonPropertyName("owner_group")] string OwnerGroup,
        [property: JsonPropertyName("surfaces")] IReadOnlyList<string> Surfaces,
        [property: JsonPropertyName("scenario")] string? Scenario,
        [property: JsonPropertyName("scenario_set_at")] DateTimeOffset? ScenarioSetAt,
        [property: JsonPropertyName("last_applied_at")] DateTimeOffset? LastAppliedAt,
        [property: JsonPropertyName("silenced")] bool Silenced);
}
