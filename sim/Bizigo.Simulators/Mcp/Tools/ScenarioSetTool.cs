using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Mcp;

namespace Bizigo.Simulators.Mcp.Tools;

/// <summary>
/// <c>sim.scenario.set</c> — bir cihazın senaryosunu değiştirir.
///
/// <para>
/// <b>Bitti tanımının 8. maddesinin taşıyıcısı:</b> <i>"Simülatör senaryosu
/// yanlış yüzeye uygulandığında hata YÜZEYİ söylüyor, profili değil."</i>
/// </para>
///
/// <para>
/// <b>Mesaj burada YAZILMIYOR, <c>Scenarios.Reject</c>'ten taşınıyor</b> (§9).
/// MCP katmanında yeni bir dizge yazmak ikinci bir kopya olurdu ve ayrışması
/// <b>sessiz</b>: S04'ün düzelttiği yanlış cümle (<i>"profilde böyle bir
/// senaryo yok"</i>) yalnızca MCP yüzeyinde geri dönerdi ve SSH yolundan bakan
/// kimse fark etmezdi. Bu araç yalnızca metni bir <see cref="McpToolError"/>
/// gövdesine <b>koyuyor</b> ve doğru <b>kodu</b> seçiyor.
/// </para>
///
/// <para>
/// <b>Kod seçimi iki yönlü ve her iki yön de ölçülüyor</b>
/// (<c>SimulatorMcpToolTests</c>):
/// </para>
/// <list type="bullet">
/// <item>Yanlış yüzeye uygulanmış <b>var olan</b> senaryo → <c>wrong_surface</c>.
/// <c>not_found</c> dönerse kırmızı.</item>
/// <item><b>Var olmayan</b> bir ad → <c>not_found</c>. <c>wrong_surface</c>
/// dönerse kırmızı.</item>
/// </list>
///
/// <para>
/// İkinci yön olmadan ölçüm işe yaramaz: <i>"her şeye <c>wrong_surface</c> de"</i>
/// diyen bir uygulama tek yönlü testi <b>geçerdi</b>. Ayrım
/// <c>Scenarios.Find</c>'dan türetiliyor — yani ret metnini üreten predicate'in
/// ta kendisinden, ikinci bir kararla değil.
/// </para>
///
/// <para>
/// <b>DÖRDÜNCÜ HÂL — <c>Scenarios.Reject</c>'in bilmediği ret.</b> Config
/// yüzeyi <c>ssh-sim</c> container'ında yaşıyor ve senaryosu açılışta
/// <c>SIM_SCENARIO</c> ile sabitleniyor; çalışan bir container'ın config
/// yüzeyini bu katman <b>değiştiremiyor</b>. Cevap <c>unavailable</c> ve mesaj
/// <b>ne yapılacağını</b> söylüyor. Alternatif — niyeti kaydedip
/// <c>sim.state</c>'te <i>"beklemede"</i> göstermek — elendi: yürürlükte
/// olmayan bir senaryoyu <i>"set edildi"</i> diye raporlamak, sessiz-yanlış
/// sınıfının <b>simülatörün kendisinde</b> doğması olurdu, üstelik simülatörün
/// var olma sebebi o sınıfı yakalamak.
/// </para>
/// </summary>
/// <param name="context">Filo ve durum zemini.</param>
public sealed class ScenarioSetTool(SimulatorMcpContext context) : SimulatorTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "sim.scenario.set";

    /// <summary>
    /// Config yüzeyinin neden değiştirilemediğini ve <b>ne yapılacağını</b>
    /// söyleyen mesajın gövdesi.
    ///
    /// <para>
    /// Sabit, çünkü bekçi mesajın <b>yönlendirdiğini</b> ölçüyor. Bu deponun
    /// dersi <i>"bir hata mesajı yanlış yüzeyi işaret ederse insan yanlış yerde
    /// arar"</i>; doğru yüzeyi işaret etmek yetmiyor, <b>kapıyı nerede
    /// açacağını</b> da söylemeli.
    /// </para>
    /// </summary>
    public static string ConfigSurfaceUnavailable(string device, string scenario) =>
        $"'{device}' cihazının config yüzeyi `ssh-sim` container'ında yaşıyor ve senaryosu "
        + $"açılışta sabitleniyor; çalışırken değiştirilemiyor. `SIM_SCENARIO={scenario}` ile "
        + "container yeniden yaratılmalı.";

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "Senaryo ayarla";

    /// <inheritdoc/>
    public override string ToolDescription =>
        "Bir cihazın senaryosunu ayarlar. Senaryo yalnızca kendi yüzeyine uygulanabilir; yanlış "
        + "yüzeyde hata hangi yüzeye ait olduğunu söyler. Config yüzeyi çalışırken değiştirilemez.";

    /// <inheritdoc/>
    public override bool IsReadOnly => false;

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "device":   { "type": "string", "description": "Profil kimliği; `sim.fleet.list` veriyor." },
            "scenario": { "type": "string", "description": "Senaryo adı, ya da değişimi kaldırmak için `baseline`." }
          },
          "required": ["device", "scenario"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "device":          { "type": "string" },
            "scenario":        { "type": ["string", "null"] },
            "surface":         { "type": ["string", "null"], "enum": ["config", "syslog", "infrastructure", null] },
            "scenario_set_at": { "type": ["string", "null"], "format": "date-time" },
            "last_applied_at": { "type": ["string", "null"], "format": "date-time" },
            "note":            { "type": "string" }
          },
          "required": ["device", "scenario", "surface", "scenario_set_at", "last_applied_at", "note"],
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
        var scenario = invocation.Required<string>("scenario");

        if (context.FindProfile(device) is not { } profile)
        {
            return ValueTask.FromResult(McpToolResult.Failure(SimulatorErrors.UnknownDevice(device, context)));
        }

        var surfaces = SimulatorSurfaces.Of(profile);

        if (surfaces.Count == 0)
        {
            // Ne config ne syslog: bu cihaz hiçbir senaryo yüzeyi taklit
            // etmiyor. `Scenarios.Reject`'e verilecek bir yüzey yok, o yüzden
            // cümle burada — ve bu, kuralın istisnası değil kapsamı dışı:
            // taşınacak bir mesaj yok, çünkü motorun bu hâl için bir cümlesi yok.
            return ValueTask.FromResult(McpToolResult.Failure(new McpToolError(
                McpToolError.WrongSurface,
                $"'{device}' hiçbir senaryo yüzeyi taklit etmiyor (profilinde ne `config` ne `syslog` var), "
                + "dolayısıyla ona uygulanabilecek bir senaryo yok.",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["device"] = device })));
        }

        if (Scenarios.IsBaseline(scenario))
        {
            return ValueTask.FromResult(Apply(profile, scenario: null, ScenarioSurface.Syslog, baseline: true));
        }

        var definition = Scenarios.Find(scenario);

        // Hedef yüzey: senaryonun KENDİ yüzeyi cihazda varsa o, yoksa cihazın
        // ilk yüzeyi. İkinci hâl reddi üretiyor ve `Reject` o zaman cihazın
        // gerçekten taşıdığı bir yüzeyin adını yazıyor — cihazda olmayan bir
        // yüzeyi "senin yüzeyin" diye göstermek okuyanı yanlış yere yollardı.
        var target = definition is not null && surfaces.Contains(definition.Surface)
            ? definition.Surface
            : surfaces[0];

        if (Scenarios.Reject(scenario, target) is { } rejection)
        {
            // KOD, METNİ ÜRETEN PREDICATE'İN KENDİSİNDEN türetiliyor. İkinci bir
            // "bu senaryo var mı" kararı yazsaydık, o karar `Reject`'inkiyle
            // ayrışabilirdi ve ayrışma tam olarak şu hâli üretirdi: metin
            // "böyle bir senaryo yok" derken kod `wrong_surface` olurdu.
            var code = definition is null ? McpToolError.NotFound : McpToolError.WrongSurface;

            return ValueTask.FromResult(McpToolResult.Failure(new McpToolError(
                code,
                rejection,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["device"] = device,
                    ["scenario"] = scenario,
                    ["scenario_surface"] = definition is null
                        ? "unknown"
                        : SimulatorSurfaces.WireName(definition.Surface),
                    ["device_surfaces"] = string.Join(",", surfaces.Select(SimulatorSurfaces.WireName)),
                })));
        }

        if (definition!.Surface is ScenarioSurface.Config)
        {
            return ValueTask.FromResult(McpToolResult.Failure(new McpToolError(
                McpToolError.Unavailable,
                ConfigSurfaceUnavailable(device, scenario),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["device"] = device,
                    ["scenario"] = scenario,
                    ["surface"] = SimulatorSurfaces.WireName(ScenarioSurface.Config),
                    ["remedy"] = $"SIM_SCENARIO={scenario}",
                })));
        }

        return ValueTask.FromResult(Apply(profile, definition.Name, definition.Surface, baseline: false));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // GERÇEK şekillendirme, GERÇEK yazma değil. Örnek çağrının diske
        // dokunması, uyum kapısını koşturmanın simülatör durumunu değiştirmesi
        // demek olurdu — bir bekçinin ölçtüğü şeyi bozması.
        return ValueTask.FromResult(McpToolResult.Structured(new Payload(
            SimulatorSamples.Profile.Id,
            "saat-kaymasi",
            SimulatorSurfaces.WireName(ScenarioSurface.Syslog),
            SimulatorSamples.Moment,
            LastAppliedAt: null,
            NotAppliedYet)));
    }

    /// <summary>
    /// <b>Niyet kaydedildi, etki henüz yok</b> — ve bunun yazıyla söylenmesi
    /// aracın sözleşmesinin parçası.
    ///
    /// <para>
    /// Simülatörün uzun ömürlü bir prosesi yok: bu çağrı hiçbir şeyi tele
    /// yazmıyor, <b>bir sonraki basımın</b> ne yapacağını kaydediyor. Cümle
    /// olmasaydı <c>sim.scenario.set</c>'in başarılı yanıtı <i>"cihaz artık
    /// saat kaydırıyor"</i> diye okunurdu ve bu, bu ticket'ın kaçındığı tam
    /// olarak o sessiz yanlış.
    /// </para>
    /// </summary>
    internal const string NotAppliedYet =
        "Niyet kaydedildi; henüz tele yazılmadı. Simülatörün uzun ömürlü bir prosesi yok — "
        + "senaryo bir sonraki `sim.syslog.burst` çağrısında uygulanacak ve `last_applied_at` "
        + "o zaman dolacak.";

    private const string BaselineCleared =
        "Senaryo kaldırıldı; bir sonraki basım baseline davranışını kullanacak.";

    private McpToolResult Apply(
        SimulatorProfile profile,
        string? scenario,
        ScenarioSurface surface,
        bool baseline)
    {
        var now = context.State.Now;

        var updated = context.State.Mutate(state =>
        {
            var current = state.For(profile.Id);

            return state.With(profile.Id, current with
            {
                Scenario = scenario,
                ScenarioSetAt = now,

                // ETKİ SIFIRLANIYOR: yeni bir niyet, eski bir etkinin damgasını
                // taşıyamaz. Taşısaydı `sim.state` "istendi ve uygulandı" derdi
                // ve uygulanan şey ÖNCEKİ senaryo olurdu — iki gösterimin tek
                // kutuya düştüğü hâlin ta kendisi.
                LastAppliedAt = null,
            });
        });

        var state = updated.For(profile.Id);

        return McpToolResult.Structured(new Payload(
            profile.Id,
            state.Scenario,
            baseline ? null : SimulatorSurfaces.WireName(surface),
            state.ScenarioSetAt,
            state.LastAppliedAt,
            baseline ? BaselineCleared : NotAppliedYet));
    }

    /// <summary>Yanıt tipi — anonim nesne değil (§8).</summary>
    private sealed record Payload(
        [property: JsonPropertyName("device")] string Device,
        [property: JsonPropertyName("scenario")] string? Scenario,
        [property: JsonPropertyName("surface")] string? Surface,
        [property: JsonPropertyName("scenario_set_at")] DateTimeOffset? ScenarioSetAt,
        [property: JsonPropertyName("last_applied_at")] DateTimeOffset? LastAppliedAt,
        [property: JsonPropertyName("note")] string Note);
}
