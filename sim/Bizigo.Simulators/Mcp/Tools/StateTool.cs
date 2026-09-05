using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Mcp;

namespace Bizigo.Simulators.Mcp.Tools;

/// <summary>
/// <c>sim.state</c> — <b>bu katmandan ne istendiği</b>. Filonun gerçeği değil.
///
/// <para>
/// <b>Bu ayrım aracın en önemli özelliği ve yanıtın içinde YAZIYLA duruyor.</b>
/// Plan §4 bu aracı <i>"simülatörün o anki hâli: hangi cihaz hangi senaryoda,
/// ne kadardır"</i> diye tarif ediyor. O cümle, uzun ömürlü bir simülatör
/// prosesi varsayıyor — <b>yok</b>. Simülatör tek atımlık bir CLI; ortada
/// sorulabilecek bir "o an" yok, yalnızca bu katmanın kaydettiği niyet var.
/// </para>
///
/// <para>
/// Vaadi daraltmak yerine cümleyi olduğu gibi taşımak, aracın <b>ölçemediği</b>
/// bir şeyi ölçüyormuş gibi göstermesi olurdu — ve bu, simülatörün var olma
/// sebebi olan hata sınıfının simülatörün kendisinde doğması demek.
/// </para>
///
/// <para>
/// <b>Ayrışma sessiz kalmıyor.</b> Niyet ile etkinin ayrıştığı iki hâl var ve
/// ikisi de <see cref="Payload.Divergences"/> içinde adıyla raporlanıyor:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>İstendi, hiç uygulanmadı</b> — <c>scenario_set_at</c> dolu,
/// <c>last_applied_at</c> boş. Cihaz hâlâ eski davranışta.
/// </item>
/// <item>
/// <b>İstenen senaryo katalogda yok</b> — durum dosyasına başka bir yoldan
/// (elle düzenleme) tanınmayan bir ad girmiş. <c>sim.scenario.set</c> bunu
/// yazmıyor, ama dosya bu katmanın tekelinde değil.
/// </item>
/// </list>
///
/// <para>
/// <b>Kaydın kapsamadığı ayrışma da yazılı</b>, çünkü asıl risk okuyanın
/// listeyi <i>tam</i> sanması: MCP'yi atlayan doğrudan bir
/// <c>dotnet run --project sim/Bizigo.Simulators -- --profile … </c> koşumu bu
/// katmana <b>hiç uğramıyor</b>. O basımı burası göremez ve görmediğini
/// <see cref="Payload.Note"/> söylüyor.
/// </para>
///
/// <para>
/// <b>Örnek satır DÖNDÜRMÜYOR</b> — ticket'ın 3. kriterine bu koldaki cevap.
/// Gerekçenin tamamı <c>SyslogBurstTool</c> belgesinde: bu yüzeydeki hiçbir
/// araç cihaz metni taşımıyor, dolayısıyla redaksiyon kapısının bugün bu
/// yüzeyde bir öznesi yok.
/// </para>
/// </summary>
/// <param name="context">Filo ve durum zemini.</param>
public sealed class StateTool(SimulatorMcpContext context) : SimulatorTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "sim.state";

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "Simülatör durumu";

    /// <inheritdoc/>
    public override string ToolDescription =>
        "Bu katmandan hangi cihaza ne istendiğini döndürür: senaryo, ne zaman istendiği, en son ne "
        + "zaman tele yazıldığı, susturma. Filonun anlık gerçeği değil — istenen ile uygulanan ayrı.";

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "device": { "type": "string", "description": "Verilirse yalnızca bu cihaz." }
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
            "schema_version": { "type": "integer", "minimum": 1 },
            "state_path":     { "type": "string" },
            "devices": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "device":          { "type": "string" },
                  "scenario":        { "type": ["string", "null"] },
                  "scenario_set_at": { "type": ["string", "null"], "format": "date-time" },
                  "last_applied_at": { "type": ["string", "null"], "format": "date-time" },
                  "applied":         { "type": "boolean" },
                  "silenced":        { "type": "boolean" },
                  "silenced_at":     { "type": ["string", "null"], "format": "date-time" },
                  "in_fleet":        { "type": "boolean" }
                },
                "required": ["device", "scenario", "scenario_set_at", "last_applied_at", "applied", "silenced", "silenced_at", "in_fleet"],
                "additionalProperties": false
              }
            },
            "untouched_devices": { "type": "array", "items": { "type": "string" } },
            "divergences":       { "type": "array", "items": { "type": "string" } },
            "note":              { "type": "string" }
          },
          "required": ["schema_version", "state_path", "devices", "untouched_devices", "divergences", "note"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected override ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var requested = invocation.Optional<string>("device")?.Trim();
        var fleet = context.LoadFleet();
        var state = context.State.Read();

        return ValueTask.FromResult(McpToolResult.Structured(
            Shape(state, fleet.Profiles.Select(p => p.Id).ToList(), context.State.Path, requested)));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var profile = SimulatorSamples.Profile;

        // Örnek BİLEREK ayrışmış bir durumu gösteriyor: istendi, uygulanmadı.
        // Şemanın en bilgi taşıyan alanı `divergences` ve boş bir örnek onu
        // hiç doldurmazdı — yani kapı o alanın şeklini hiç görmezdi.
        var state = SimulatorState.Empty.With(
            profile.Id,
            new SimulatorDeviceState("saat-kaymasi", SimulatorSamples.Moment, LastAppliedAt: null));

        return ValueTask.FromResult(McpToolResult.Structured(
            Shape(state, [profile.Id], context.State.Path, device: null)));
    }

    private static Payload Shape(
        SimulatorState state,
        IReadOnlyList<string> fleetDevices,
        string statePath,
        string? device)
    {
        var entries = state.DeviceStates
            .Where(pair => device is null || string.Equals(pair.Key, device, StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

        var divergences = new List<string>();

        foreach (var (id, entry) in entries)
        {
            if (entry.Scenario is not null && entry.LastAppliedAt is null)
            {
                divergences.Add(
                    $"'{id}': '{entry.Scenario}' istendi ({entry.ScenarioSetAt:O}) ama hiç uygulanmadı. "
                    + "Cihaz hâlâ önceki davranışta; `sim.syslog.burst` çağrılana kadar öyle kalacak.");
            }

            if (entry.Scenario is not null && Scenarios.Find(entry.Scenario) is null)
            {
                divergences.Add(
                    $"'{id}': '{entry.Scenario}' katalogda YOK. `sim.scenario.set` bilinmeyen bir adı "
                    + "yazmıyor, yani durum dosyası bu katmanı atlayan bir yoldan düzenlenmiş.");
            }

            if (!fleetDevices.Contains(id, StringComparer.Ordinal))
            {
                divergences.Add(
                    $"'{id}' durum dosyasında var ama FİLODA yok. Profil silinmiş ya da filodan "
                    + "çıkarılmış olabilir; bu kayıt hiçbir cihazı etkilemiyor.");
            }
        }

        return new Payload(
            state.SchemaVersion,
            statePath,
            [.. entries.Select(pair => Describe(pair.Key, pair.Value, fleetDevices))],
            [.. fleetDevices.Where(id => !state.DeviceStates.ContainsKey(id)).Order(StringComparer.Ordinal)],
            divergences,
            IntentNotTruth);
    }

    private static DevicePayload Describe(
        string id,
        SimulatorDeviceState entry,
        IReadOnlyList<string> fleetDevices) => new(
        id,
        entry.Scenario,
        entry.ScenarioSetAt,
        entry.LastAppliedAt,
        entry.LastAppliedAt is not null,
        entry.Silenced,
        entry.SilencedAt,
        fleetDevices.Contains(id, StringComparer.Ordinal));

    /// <summary>
    /// <b>Aracın vaadinin kendisi</b>, ve yanıtın içinde taşınmasının sebebi:
    /// bu cümleyi yalnızca kod yorumuna yazmak, onu okuyanın modele
    /// ulaşmaması demekti — oysa yanlış okuyacak olan taraf model.
    /// </summary>
    public const string IntentNotTruth =
        "Bu yanıt MCP katmanının NİYETİNİ gösteriyor, filonun anlık gerçeğini değil. "
        + "Simülatörün uzun ömürlü bir prosesi yok: `scenario_set_at` ne istendiğini, "
        + "`last_applied_at` en son ne zaman tele yazıldığını söylüyor. MCP'yi atlayan doğrudan "
        + "bir `dotnet run --project sim/Bizigo.Simulators` koşumu bu katmana hiç uğramaz ve "
        + "burada görünmez.";

    /// <summary>Yanıt tipi — anonim nesne değil (§8).</summary>
    private sealed record Payload(
        [property: JsonPropertyName("schema_version")] int SchemaVersion,
        [property: JsonPropertyName("state_path")] string StatePath,
        [property: JsonPropertyName("devices")] IReadOnlyList<DevicePayload> Devices,
        [property: JsonPropertyName("untouched_devices")] IReadOnlyList<string> UntouchedDevices,
        [property: JsonPropertyName("divergences")] IReadOnlyList<string> Divergences,
        [property: JsonPropertyName("note")] string Note);

    private sealed record DevicePayload(
        [property: JsonPropertyName("device")] string Device,
        [property: JsonPropertyName("scenario")] string? Scenario,
        [property: JsonPropertyName("scenario_set_at")] DateTimeOffset? ScenarioSetAt,
        [property: JsonPropertyName("last_applied_at")] DateTimeOffset? LastAppliedAt,
        [property: JsonPropertyName("applied")] bool Applied,
        [property: JsonPropertyName("silenced")] bool Silenced,
        [property: JsonPropertyName("silenced_at")] DateTimeOffset? SilencedAt,
        [property: JsonPropertyName("in_fleet")] bool InFleet);
}
