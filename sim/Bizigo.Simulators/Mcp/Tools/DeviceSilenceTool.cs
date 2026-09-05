using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Mcp;

namespace Bizigo.Simulators.Mcp.Tools;

/// <summary>
/// <c>sim.device.silence</c> — bir cihazı susturur: <b>basmayı keser</b>,
/// container'ı durdurmaz.
///
/// <para>
/// <b>Ayrım aracın sınavını belirliyor.</b> Bu aracın sınadığı iddia
/// <i>sessizlik korelasyonu</i> — ürün <i>"bu cihaz susmuş"</i> sinyalini
/// gerçekten üretiyor mu. Onu ölçmek için ürünün görmesi gereken şey
/// <b>log akışının durması</b>. Container'ın ölmesi <b>farklı bir olay</b>
/// (ulaşılamama) ve başka bir senaryonun konusu; ikisini aynı araca bağlamak,
/// yeşil bir korelasyon testinin hangi sinyali kanıtladığını belirsiz yapardı.
/// </para>
///
/// <para>
/// İkinci gerekçe kolun sınırı: container'ı susturmak Docker'a bağlanırdı ve
/// bu ticket Docker'a hiç dokunmuyor.
/// </para>
///
/// <para>
/// <b>Susturma bir SENARYO değil</b> ve bilerek ayrı bir alanda duruyor
/// (<see cref="SimulatorDeviceState.Silenced"/>). Bir senaryo adı olsaydı
/// <c>Scenarios.Known</c>'a girmesi gerekirdi — oysa o liste <i>ürünün
/// iddialarını</i> tutuyor ve her satırı bir <c>Claim</c> taşımak zorunda.
/// Susturma bir iddia sınamıyor, bir <b>durum</b> kuruyor; ayrıca bir cihaz
/// hem susturulmuş hem bir senaryoda olabilir ve tek alan bunu ifade edemezdi.
/// </para>
/// </summary>
/// <param name="context">Filo ve durum zemini.</param>
public sealed class DeviceSilenceTool(SimulatorMcpContext context) : SimulatorTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "sim.device.silence";

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "Cihazı sustur";

    /// <inheritdoc/>
    public override string ToolDescription =>
        "Bir cihazın syslog basımını durdurur ya da yeniden açar. Container'ı durdurmaz — "
        + "ürünün göreceği şey log akışının kesilmesi, ulaşılamama değil.";

    /// <inheritdoc/>
    public override bool IsReadOnly => false;

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "device":   { "type": "string", "description": "Profil kimliği; `sim.fleet.list` veriyor." },
            "silenced": { "type": "boolean", "description": "Susturmak için `true` (varsayılan), açmak için `false`." }
          },
          "required": ["device"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "device":       { "type": "string" },
            "silenced":     { "type": "boolean" },
            "silenced_at":  { "type": ["string", "null"], "format": "date-time" },
            "note":         { "type": "string" }
          },
          "required": ["device", "silenced", "silenced_at", "note"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected override ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var device = invocation.Required<string>("device");

        if (context.FindProfile(device) is not { } profile)
        {
            return ValueTask.FromResult(McpToolResult.Failure(SimulatorErrors.UnknownDevice(device, context)));
        }

        // Syslog yüzeyi olmayan bir cihazı susturmak anlamsız — ve sessizce
        // "sustuldu" demek, sessizlik korelasyonunu HİÇ basmamış bir cihazla
        // ölçmeye çalışmak olurdu: sinyal zaten yok, test yeşil, iddia
        // kanıtlanmamış.
        if (profile.Syslog is null)
        {
            return ValueTask.FromResult(McpToolResult.Failure(new McpToolError(
                McpToolError.WrongSurface,
                $"'{device}' syslog yüzeyi taklit etmiyor, dolayısıyla kesilecek bir basımı yok. "
                + "Susturma syslog basımını durduruyor; config yüzeyiyle ilgisi yok.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["device"] = device,
                    ["device_surfaces"] = string.Join(
                        ",", SimulatorSurfaces.Of(profile).Select(SimulatorSurfaces.WireName)),
                })));
        }

        var silenced = invocation.Optional("silenced", true);
        var now = context.State.Now;

        var updated = context.State.Mutate(state =>
        {
            var current = state.For(profile.Id);

            return state.With(profile.Id, current with
            {
                Silenced = silenced,

                // Açılırken damga TEMİZLENİYOR: eski bir "susturuldu" damgası
                // açık bir cihazın yanında dururken `sim.state` okunduğunda
                // hangisinin geçerli olduğu belirsiz olurdu.
                SilencedAt = silenced ? now : null,
            });
        });

        var result = updated.For(profile.Id);

        return ValueTask.FromResult(McpToolResult.Structured(new Payload(
            profile.Id,
            result.Silenced,
            result.SilencedAt,
            result.Silenced ? Silenced : Resumed)));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(McpToolResult.Structured(new Payload(
            SimulatorSamples.Profile.Id,
            true,
            SimulatorSamples.Moment,
            Silenced)));
    }

    /// <summary>
    /// Susturmanın <b>ne zaman yürürlüğe girdiğini</b> söyleyen cümle.
    ///
    /// <para>
    /// Aynı gerekçe <c>sim.scenario.set</c>'inkiyle aynı: bu çağrı hiçbir şeyi
    /// durdurmuyor, <b>bir sonraki basımın</b> ne yapacağını kaydediyor. Şu an
    /// koşan bir <c>sim.syslog.burst</c> varsa o bitene kadar basmaya devam
    /// ediyor — ve bunu yazmazsak sessizlik ölçümü <i>"susturma çalışmadı"</i>
    /// diye okunurdu.
    /// </para>
    /// </summary>
    internal const string Silenced =
        "Cihaz susturuldu: bundan sonraki `sim.syslog.burst` çağrıları bu cihaz için basmayı "
        + "reddedecek. Hâlihazırda koşan bir basım kesilmiyor ve container durdurulmuyor.";

    private const string Resumed =
        "Susturma kaldırıldı: cihaz yeniden basabilir.";

    /// <summary>Yanıt tipi — anonim nesne değil (§8).</summary>
    private sealed record Payload(
        [property: JsonPropertyName("device")] string Device,
        [property: JsonPropertyName("silenced")] bool IsSilenced,
        [property: JsonPropertyName("silenced_at")] DateTimeOffset? SilencedAt,
        [property: JsonPropertyName("note")] string Note);
}
