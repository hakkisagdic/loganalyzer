using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Mcp;

namespace Bizigo.Simulators.Mcp.Tools;

/// <summary>
/// <c>sim.syslog.burst</c> — sayılı satır basar, ve <b>niyeti etkiye çevirir</b>.
///
/// <para>
/// Bu araç durum katmanının diğer ucu: <c>sim.scenario.set</c> bir senaryoyu
/// <i>istiyor</i>, burası onu <b>tele yazıyor</b> ve
/// <see cref="SimulatorDeviceState.LastAppliedAt"/>'i dolduruyor. İki damganın
/// ayrı olmasının bütün kazancı bu çağrıda görünüyor: basımdan önce
/// <c>scenario_set_at</c> dolu ve <c>last_applied_at</c> boş.
/// </para>
///
/// <para>
/// <b>ÇIKTI SATIR İÇERİĞİ TAŞIMIYOR — ve bu redaksiyon kapısının sınırı.</b>
/// Ticket'ın 3. kabul kriteri kapının neye baktığının <i>yazılı</i> olmasını
/// istiyor. Bu koldaki cevap: <b>hiçbir <c>sim.*</c> aracı cihaz metni
/// döndürmüyor.</b> Bu araç sayaç döndürüyor (satır, bayt, süre);
/// <c>sim.state</c> niyet/etki damgaları döndürüyor, örnek satır <b>değil</b>.
/// Dolayısıyla bugün bu yüzeyde redaksiyon kapısının bir öznesi <b>yok</b>, ve
/// bu bir eksik değil bir <b>tasarım sınırı</b>.
/// </para>
///
/// <para>
/// <b>Sınırın nerede biteceği de yazılı:</b> bir gün bir araç örnek satır
/// döndürürse (örneğin <i>"bu cihaz ne basıyor"</i> diye bir araç), o metin
/// <b>sentetik olduğu için muaf değil</b> — cihaz metni cihaz metnidir ve
/// <c>McpToolResult</c> zaten serbest <c>string</c> kabul etmiyor, yani o gün
/// tek yol <c>McpLogText</c> menteşesi olacak. M06 fabrikanın parametre tipini
/// <c>RedactedPrompt</c> yaptığında o yol <b>derleyicide</b> kapıya bağlanıyor;
/// bugün yazılacak fazladan bir şey yok, çünkü tüketicisi olmayan bir tip
/// tahmindir (§8).
/// </para>
/// </summary>
/// <param name="context">Filo ve durum zemini.</param>
public sealed class SyslogBurstTool(SimulatorMcpContext context) : SimulatorTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "sim.syslog.burst";

    /// <summary>Varsayılan satır sayısı — <c>Program.cs</c>'in CLI varsayılanıyla aynı.</summary>
    public const int DefaultCount = 100;

    /// <summary>
    /// Tek çağrıda basılabilecek en fazla satır.
    ///
    /// <para>
    /// ⚠ <b>İşaretli sabit: seçildi, ölçülmedi.</b> Bir tavan olmasının gerekçesi
    /// ölçülmüş değil ama somut: bu aracı çağıran bir <b>model</b> ve
    /// <c>count</c>'u o yazıyor. Tavansız bir arayüzde bir basamak hatası
    /// (<c>100000</c> yerine <c>1000000</c>) collector'ı dakikalarca meşgul
    /// eder ve iptal bildirimi gelene kadar makine bu deponun §3'te tarif ettiği
    /// hâle girer. Tavanın <b>değeri</b> ölçülmedi; ölçülecek şey bu makinede
    /// hangi satır sayısının boru hattını doyurduğu.
    /// </para>
    /// </summary>
    public const int MaximumCount = 100_000;

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "Syslog bas";

    /// <inheritdoc/>
    public override string ToolDescription =>
        "Bir cihazın örnek satırlarını collector'a basar ve o cihaza ayarlanmış syslog senaryosunu "
        + "uygular. Satır içeriği döndürmez — basılan satır ve bayt sayısını döndürür.";

    /// <inheritdoc/>
    public override bool IsReadOnly => false;

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "device": { "type": "string", "description": "Profil kimliği; `sim.fleet.list` veriyor." },
            "count":  { "type": "integer", "minimum": 1, "maximum": {{MaximumCount}}, "description": "Basılacak satır sayısı (varsayılan {{DefaultCount}})." },
            "host":   { "type": "string", "description": "Collector adresi; verilmezse sunucunun yapılandırdığı adres." }
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
            "device":          { "type": "string" },
            "host":            { "type": "string" },
            "transport":       { "type": "string" },
            "scenario":        { "type": ["string", "null"] },
            "lines":           { "type": "integer", "minimum": 0 },
            "bytes":           { "type": "integer", "minimum": 0 },
            "elapsed_ms":      { "type": "integer", "minimum": 0 },
            "last_applied_at": { "type": ["string", "null"], "format": "date-time" },
            "note":            { "type": "string" }
          },
          "required": ["device", "host", "transport", "scenario", "lines", "bytes", "elapsed_ms", "last_applied_at", "note"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected override async ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var device = invocation.Required<string>("device");

        if (context.FindProfile(device) is not { } profile)
        {
            return McpToolResult.Failure(SimulatorErrors.UnknownDevice(device, context));
        }

        if (profile.Syslog is null || profile.Syslog.Samples.Count == 0)
        {
            return McpToolResult.Failure(new McpToolError(
                McpToolError.WrongSurface,
                $"'{device}' syslog yüzeyi taklit etmiyor: profilinde basılacak örnek tanımlı değil. "
                + "Bu bir eksiklik değil — her profil her yüzeyi taklit etmek zorunda değil.",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["device"] = device }));
        }

        var state = context.State.Read().For(profile.Id);

        if (state.Silenced)
        {
            // SUSTURMA BURADA YÜRÜRLÜĞE GİRİYOR. `sim.device.silence` bir bayrak
            // yazıyor; onu bir DAVRANIŞA çeviren tek yer burası. Bayrağı yazıp
            // burada okumamak, susturmayı "kaydedilmiş ama etkisiz" yapardı —
            // yani `sim.state` "susturuldu" derken cihaz basmaya devam ederdi.
            return McpToolResult.Failure(new McpToolError(
                McpToolError.Unavailable,
                $"'{device}' susturulmuş durumda ve basım reddedildi. Sessizlik korelasyonunu "
                + "ölçen şey tam olarak bu: cihazın log akışı duruyor. Açmak için "
                + "`sim.device.silence` çağrısını `silenced: false` ile yapın.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["device"] = device,
                    ["silenced_at"] = state.SilencedAt?.ToString("O") ?? "?",
                }));
        }

        var count = invocation.Optional("count", DefaultCount);
        var host = invocation.Optional<string>("host") ?? context.CollectorHost;

        if (count < 1 || count > MaximumCount)
        {
            return McpToolResult.Failure(new McpToolError(
                McpToolError.InvalidArgument,
                $"`count` 1 ile {MaximumCount} arasında olmalı; {count} verildi.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["maximum"] = MaximumCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }));
        }

        EmitResult emitted;

        try
        {
            emitted = await SyslogEmitter
                .EmitAsync(profile, context.RepositoryRoot, host, count, cancellationToken, state.Scenario)
                .ConfigureAwait(false);
        }
        catch (SocketException error)
        {
            // AĞ ARIZASI BİR ARAÇ HATASI, protokol hatası değil: collector'ın
            // kapalı olması istemcinin bağlantısıyla ilgisiz. Fırlatıp geçseydik
            // istemci bunu MCP oturumunun düştüğü sanırdı.
            return McpToolResult.Failure(new McpToolError(
                McpToolError.Unavailable,
                $"Collector'a bağlanılamadı ({host}): {error.Message}. Basım yapılmadı.",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = host }));
        }
        catch (FileNotFoundException error)
        {
            return McpToolResult.Failure(new McpToolError(
                McpToolError.NotFound,
                $"'{device}' örnek dosyaya işaret ediyor ama dosya yok: {error.FileName}. "
                + $"Yollar depo köküne göre çözülüyor ({context.RepositoryRoot}).",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["device"] = device }));
        }
        catch (InvalidOperationException error)
        {
            // Basıcının kendi yüzey reddi. Buraya bugün ulaşılamıyor —
            // `sim.scenario.set` config senaryosunu duruma hiç yazmıyor — ama
            // dal yine de doğru KODLA duruyor: bir gün duruma başka bir yoldan
            // (dosyayı elle düzenleyerek) config senaryosu girerse cevap
            // `wrong_surface` olmalı, `unavailable` değil.
            return McpToolResult.Failure(new McpToolError(
                McpToolError.WrongSurface, error.Message,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["device"] = device,
                    ["scenario"] = state.Scenario ?? Scenarios.Baseline,
                }));
        }

        var appliedAt = context.State.Now;

        // ETKİ DAMGASI YALNIZCA BASIM BAŞARILI OLUNCA YAZILIYOR. Yukarıdaki
        // `catch` dallarının hiçbiri buraya düşmüyor: düşseydi
        // `last_applied_at` "tele yazıldı" derken hiçbir satır gitmemiş olurdu
        // — niyet/etki ayrımını kuran şeyin kendisi bozulurdu.
        context.State.Mutate(current =>
            current.With(profile.Id, current.For(profile.Id) with { LastAppliedAt = appliedAt }));

        return McpToolResult.Structured(new Payload(
            profile.Id,
            host,
            profile.Syslog.Transport,
            state.Scenario,
            emitted.Lines,
            emitted.Bytes,
            (long)emitted.Elapsed.TotalMilliseconds,
            appliedAt,
            WireOnly));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Örnek SOKET AÇMIYOR. Şart olan şey şemanın çıktıyı tarif etmesi;
        // collector'ın cevabı entegrasyon testinin işi (§2).
        return ValueTask.FromResult(McpToolResult.Structured(new Payload(
            SimulatorSamples.Profile.Id,
            SimulatorMcpContext.DefaultCollectorHost,
            "tcp",
            "saat-kaymasi",
            DefaultCount,
            66_800,
            1_240,
            SimulatorSamples.Moment,
            WireOnly)));
    }

    /// <summary>
    /// <b>Basmak "ulaştı" demek değil</b> — ve bu cümle CLI'dakiyle aynı
    /// (<c>Program.cs</c>).
    ///
    /// <para>
    /// TCP'ye yazmak yalnızca collector'ın soketi aldığını söylüyor. Satırın
    /// WAL'a, arşive ve ClickHouse'a ulaştığını sorgulayan başka bir adım var
    /// ve bu araç onu yapmıyor. Yazılmasaydı, dönen <c>lines</c> sayısı
    /// <i>"ürüne 100 satır girdi"</i> diye okunurdu — bu depoda ölçülmüş bir
    /// olay: basıcı <i>"5 satır, 3334 bayt yazdım"</i> derken ClickHouse'a
    /// <b>sıfır</b> satır ulaşmıştı.
    /// </para>
    /// </summary>
    internal const string WireOnly =
        "Bu sayı TELE giden satır. Collector'ın soketi aldığını söylüyor; satırların WAL'a, "
        + "arşive ve ClickHouse'a ulaştığını ayrıca doğrulayın.";

    /// <summary>Yanıt tipi — anonim nesne değil (§8).</summary>
    private sealed record Payload(
        [property: JsonPropertyName("device")] string Device,
        [property: JsonPropertyName("host")] string Host,
        [property: JsonPropertyName("transport")] string Transport,
        [property: JsonPropertyName("scenario")] string? Scenario,
        [property: JsonPropertyName("lines")] long Lines,
        [property: JsonPropertyName("bytes")] long Bytes,
        [property: JsonPropertyName("elapsed_ms")] long ElapsedMs,
        [property: JsonPropertyName("last_applied_at")] DateTimeOffset? LastAppliedAt,
        [property: JsonPropertyName("note")] string Note);
}
