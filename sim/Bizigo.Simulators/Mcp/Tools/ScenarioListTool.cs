using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Mcp;

namespace Bizigo.Simulators.Mcp.Tools;

/// <summary>
/// <c>sim.scenario.list</c> — tanımlı senaryolar ve <b>hangi yüzeye</b> ait
/// oldukları.
///
/// <para>
/// <b>Yüzey alanı bu aracın asıl yükü.</b> Bitti tanımının 8. maddesi
/// <c>sim.scenario.set</c>'in <i>reddini</i> istiyor; ama bir modelin o reddi
/// hiç almaması daha iyi, ve almaması için yüzeyi <b>önceden</b> görmesi
/// gerekiyor. Yalnızca adları listeleyen bir araç, modeli <c>kural-eklendi</c>'yi
/// bir syslog cihazına uygulamaya davet ederdi.
/// </para>
///
/// <para>
/// Liste <c>Scenarios.Known</c>'dan geliyor — <b>ikinci kopya yok</b> (§9). O
/// liste elle tutulan tarafta ve öyle kalmalı: denetlenen küme ürünün
/// iddiaları, keşfedilebilir bir kod yüzeyi değil.
/// </para>
/// </summary>
public sealed class ScenarioListTool : SimulatorTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "sim.scenario.list";

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "Simülatör senaryoları";

    /// <inheritdoc/>
    public override string ToolDescription =>
        "Tanımlı senaryoları, değiştirdikleri yüzeyi (config/syslog/infrastructure) ve sınadıkları "
        + "ürün iddiasını listeler. Yüzey önemli: bir senaryo yalnızca kendi yüzeyine uygulanabilir.";

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "surface": {
              "type": "string",
              "enum": ["config", "syslog", "infrastructure"],
              "description": "Verilirse yalnızca bu yüzeyin senaryoları."
            }
          },
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "scenarios": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name":    { "type": "string" },
                  "surface": { "type": "string", "enum": ["config", "syslog", "infrastructure"] },
                  "claim":   { "type": "string" }
                },
                "required": ["name", "surface", "claim"],
                "additionalProperties": false
              }
            },
            "count":    { "type": "integer", "minimum": 0 },
            "baseline": { "type": "string" }
          },
          "required": ["scenarios", "count", "baseline"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected override ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var requested = invocation.Optional<string>("surface");

        var scenarios = requested is null
            ? Scenarios.Known
            : [.. Scenarios.Known.Where(s =>
                string.Equals(SimulatorSurfaces.WireName(s.Surface), requested, StringComparison.Ordinal))];

        return ValueTask.FromResult(McpToolResult.Structured(Shape(scenarios)));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Gerçek liste, gerçek şekillendirme. Bu araçta örnek ile asıl çağrı
        // ZATEN aynı şey — dış bir bağımlılığı yok — ve o hâlde sabit bir
        // taklit yazmak yalnızca ayrışabilecek ikinci bir şekil üretirdi.
        return ValueTask.FromResult(McpToolResult.Structured(Shape(Scenarios.Known)));
    }

    private static Payload Shape(IReadOnlyList<ScenarioDefinition> scenarios) => new(
        [.. scenarios
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => new ScenarioPayload(s.Name, SimulatorSurfaces.WireName(s.Surface), s.Claim))],
        scenarios.Count,
        Scenarios.Baseline);

    /// <summary>Yanıt tipi — anonim nesne değil (§8).</summary>
    private sealed record Payload(
        [property: JsonPropertyName("scenarios")] IReadOnlyList<ScenarioPayload> Scenarios,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("baseline")] string Baseline);

    private sealed record ScenarioPayload(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("surface")] string Surface,
        [property: JsonPropertyName("claim")] string Claim);
}
