using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Rca;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>rca.trigger</c> — ajan tetikli kök neden analizi. <b>Ürünün MCP üzerinden
/// yaptığı TEK yazma</b> (M14).
///
/// <h3>Neden bu yazma yapılıyor, <c>alerts.maintenance</c>'ınki yapılmıyor</h3>
///
/// <para>
/// M10 bakım penceresi yazmayı reddetti ve kararı taşıyan gerekçe yazmanın
/// kendisi değildi: <i>"yanlış açılmış bir pencerenin belirtisi bir hata değil
/// SESSİZLİK."</i> M14 o cümleyi <see cref="ProductWriteTool"/>'da dört şarta
/// daralttı, ve bu araç dördünü de karşılıyor — <b>karşıladığı ölçülerek</b>:
/// </para>
///
/// <list type="table">
///   <item>
///     <term>İdempotans</term>
///     <description>
///       Anahtar <see cref="McpIdempotency.KeyFor"/>'dan, yani <b>sunucudan</b>.
///       Aşağıdaki şemada bir <c>idempotency_key</c> argümanı <b>YOK</b> ve
///       olmaması kararın kendisi.
///     </description>
///   </item>
///   <item>
///     <term>Her sonucun kaydı</term>
///     <description>
///       Kabul, ret, kota, derinlik, soyağacı tekrarı — hepsi <c>rca_runs</c>'ta
///       bir satır (T46). Yükte dönen <c>state</c> + <c>reason</c> o satırın
///       kendisi.
///     </description>
///   </item>
///   <item>
///     <term>Maliyet görünürlüğü</term>
///     <description><c>counts_against_quota</c> yükte.</description>
///   </item>
///   <item>
///     <term>Aktörün kimliği</term>
///     <description>
///       <c>Source = Agent</c> ve <c>RequestedBy = scope.Subject</c>. Kaynak
///       <b><c>External</c> DEĞİL</b>: o <c>POST /v1/rca</c> + istemcinin
///       kendi <c>Idempotency-Key</c>'i, yani bir insanın API'si. Ayrımı
///       silmek <i>"kim nihayetinde sebep oldu"</i> sorusunu cevapsız
///       bırakırdı.
///     </description>
///   </item>
/// </list>
///
/// <h3>Anahtar modelden gelmiyor — ve bu şemada GÖRÜLÜYOR</h3>
///
/// <para>
/// M05 <see cref="McpIdempotency"/>'yi tam bu araç için yazdı ve gerekçesini
/// ölçtü: her denemede yeni anahtar üreten model kotayı defalarca yer, aynı
/// anahtarı ısrarla üreten model farklı bir tetiklemeyi bastırır — ikisi de
/// sessiz. Anahtar burada <b>kimlik taşıyan alanlardan</b> türüyor: çağıran,
/// kapsam, pencere.
/// </para>
///
/// <para>
/// Model <b>girdileri</b> seçiyor (farklı pencere = farklı anahtar) ve bu bir
/// kaçak değil: o zaman <b>farklı bir RCA</b> istemiş oluyor, aynısını iki kez
/// değil. Engellenmek istenen şey buydu — kısıtın harfi değil ruhu.
/// </para>
///
/// <h3>⚠️ Kota İNSANLA PAYLAŞILIYOR — ölçüldü, yazılı</h3>
///
/// <para>
/// Kota <b>grup başına</b> (<c>RcaQuotaOptions.DailyPerGroup</c>) ve sayım
/// <c>owner_group</c> üzerinden, yani stdio'dan gelen bir tetikleme HTTP ile
/// <b>aynı havuzu</b> yiyor. Sonucu açık: <b>bir modelin gürültülü koşumu, aynı
/// gruptaki insanı RCA'sız bırakabilir.</b>
/// </para>
///
/// <para>
/// Rezervasyon mekanizması var (<c>EventReservePercent</c>) ama <b>bu vakayı
/// kapsamıyor</b>: <c>RcaQuota.EffectiveLimit</c> rezervi yalnızca
/// <see cref="RcaTriggerSource.Schedule"/> için uyguluyor, yani
/// <c>Agent</c> ile <c>Manual</c> tam limiti paylaşıyor. Varsayılanı da
/// <c>0</c>, yani bugün hiçbir kaynak için açık değil.
/// </para>
///
/// <para>
/// <b>Sayı önerilmiyor</b> ve önerilemez: tüketimin kaynak başına dağılımı
/// <c>rca_runs</c>'tan okunabiliyor (<c>Source</c> + <c>CountsAgainstQuota</c>
/// kolonları, her koşumda yazılıyor), yani operatör rezervasyonun gerekip
/// gerekmediğini <b>veriden</b> görecek. Mekanizma gözlem için hazır; korumanın
/// <c>Agent</c>'ı kapsaması ayrı bir karar ve bu ticket'ta verilmedi.
/// </para>
/// </summary>
/// <param name="scopes">
/// Çağrı başına kapsam açmak için: <c>RcaAdmission</c> <b>scoped</b>. Yapıcıda
/// istemek esir bağımlılık olurdu — gerekçe <see cref="ProductReadTool"/>
/// belgesinde.
/// </param>
/// <param name="timeProvider">Varsayılan pencere için; testte sabitlenebiliyor.</param>
public sealed class RcaTriggerTool(IServiceScopeFactory scopes, TimeProvider? timeProvider = null)
    : ProductWriteTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "rca.trigger";

    /// <summary>
    /// Zaman aralığı verilmezse bakılan pencere. <c>logs.search</c> ile aynı
    /// (24 saat) — iki aracın aynı soruya farklı pencere vermesi, modelin
    /// karşılaştırdığı iki cevabı sessizce uyumsuz yapardı.
    /// </summary>
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "RCA tetikle";

    /// <summary>
    /// <inheritdoc/>
    ///
    /// <para>
    /// <b>Üç cümle kısaltılamaz.</b> Kota tükettiğini söylemeyen bir açıklama
    /// modeli döngü içinde çağırmaya bırakır; idempotansın sunucuda olduğunu
    /// söylemeyen açıklama modelin bir anahtar argümanı aramasına yol açar; ve
    /// <c>state=rejected</c>'ın <b>bakılmadı</b> demek olduğu söylenmezse model
    /// onu <i>"sebep bulunamadı"</i> diye okur.
    /// </para>
    /// </summary>
    public override string ToolDescription =>
        "Bir pencere için kök neden analizi tetikler. KOTA TÜKETİR (grup başına, günlük) ve "
        + "aynı kotayı bu gruptaki insanlarla paylaşır. Tekrar denemek güvenli: idempotency "
        + "anahtarı SUNUCUDA türetiliyor, argüman olarak verilmiyor — aynı kapsam ve aynı pencere "
        + "aynı koşumu döndürür. `state` `rejected` ise RCA HİÇ ÇALIŞMADI (sebebi `reason`'da), "
        + "`existing` true ise bu çağrı yeni bir koşum başlatmadı.";

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "owner_group": { "type": "string", "minLength": 1 },
            "from":        { "type": "string", "format": "date-time" },
            "to":          { "type": "string", "format": "date-time" },
            "identity":    { "type": "string", "minLength": 1 }
          },
          "required": ["owner_group"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "run_id":               { "type": "string", "format": "uuid" },
            "state":                { "type": "string" },
            "reason":               { "type": "string" },
            "accepted":             { "type": "boolean" },
            "existing":             { "type": "boolean" },
            "counts_against_quota": { "type": "boolean" },
            "owner_group":          { "type": "string" },
            "from":                 { "type": "string", "format": "date-time" },
            "to":                   { "type": "string", "format": "date-time" }
          },
          "required": [
            "run_id", "state", "reason", "accepted", "existing",
            "counts_against_quota", "owner_group", "from", "to"
          ],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected internal override async ValueTask<McpToolResult> ExecuteScopedAsync(
        McpToolInvocation invocation,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        var ownerGroup = invocation.Required<string>("owner_group");

        // KAPSAM GENİŞLETİLEMİYOR. Okuma araçlarında filtre sorguya iniyor;
        // burada iş bir grup ADINA yapılıyor, dolayısıyla grubun kapsamda
        // olduğu AÇIKÇA sorulmak zorunda. `not_found` dönüyor, `forbidden`
        // değil: "böyle bir grup var ama yazamazsın" bilgisini sızdırmak,
        // okuma tarafındaki 404 kararının tersini yapmak olurdu.
        if (!scope.Allows(ownerGroup))
        {
            return McpToolResult.Failure(new McpToolError(
                McpToolError.NotFound,
                "Bu `owner_group` kapsamda değil; RCA tetiklenmedi.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["owner_group"] = ownerGroup,
                }));
        }

        var to = invocation.Optional<DateTimeOffset?>("to") ?? time.GetUtcNow();
        var from = invocation.Optional<DateTimeOffset?>("from") ?? to - DefaultWindow;

        if (from >= to)
        {
            throw new McpToolArgumentException("from", "`to` değerinden küçük olmalı");
        }

        // Tetikleyici kimliği: çağıranın verdiği ad ya da öznesi. Bu alan
        // `rca_runs`'ta gruplama anahtarı ve `rca.runs`'ın `trigger` filtresinin
        // öznesi, yani modelin kendi tetiklemesini sonradan bulabilmesi buna
        // bağlı.
        var identity = invocation.Optional<string>("identity") is { Length: > 0 } given
            ? given
            : $"mcp:{scope.Subject}";

        await using var services = scopes.CreateAsyncScope();

        var admission = services.ServiceProvider.GetRequiredService<RcaAdmission>();

        var result = await admission.AdmitAsync(
            new RcaTriggerRequest
            {
                // KAYNAK `Agent` — `External` DEĞİL. Gerekçe sınıf belgesinde.
                Source = RcaTriggerSource.Agent,
                Identity = identity,
                OwnerGroup = ownerGroup,
                WindowFrom = from,
                WindowTo = to,

                // ANAHTAR SUNUCUDAN. Argüman torbasında karşılığı yok ve
                // olmaması M05'in ölçtüğü kararın kendisi.
                IdempotencyKey = McpIdempotency.KeyFor(scope.Subject, [ownerGroup], from, to),
                RequestedBy = scope.Subject,
            },
            cancellationToken).ConfigureAwait(false);

        return McpToolResult.Structured(Shape(result, ownerGroup, from, to));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var to = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var from = to - DefaultWindow;

        // ÖRNEK REDDEDİLMİŞ BİR KOŞUM ve bilerek: kabul edilmiş bir örnek,
        // `reason`'ın gerçekten bir cümle taşıdığını göstermezdi (`None` retinde
        // `Describe` başka bir dal döndürüyor). Ret hâli aynı zamanda aracın en
        // kolay yanlış okunan çıktısı.
        var run = new RcaRunEntity
        {
            Id = Guid.Parse("44444444-aaaa-2222-3333-444444444444"),
            Source = RcaTriggerSource.Agent,
            TriggerIdentity = "mcp:analyst.core",
            OwnerGroup = "network/core",
            State = RcaRunState.Rejected,
            Rejection = RcaRejectionReason.Debounced,
            CountsAgainstQuota = false,
            RequestedAt = to,
        };

        // Gerçek yolun ŞEKİLLENDİRMESİ — elle yazılmış JSON değil.
        return ValueTask.FromResult(McpToolResult.Structured(
            Shape(new RcaAdmissionResult(run, Existing: true), "network/core", from, to)));
    }

    private static Payload Shape(
        RcaAdmissionResult result,
        string ownerGroup,
        DateTimeOffset from,
        DateTimeOffset to) => new(
        result.Run.Id,

        // Durum ve cümle T46'nın KENDİ kaynaklarından — `rca.runs` ile aynı iki
        // yer. İkinci bir eşleme, aynı koşumun iki araçta farklı görünmesi
        // demek olurdu (§9).
        result.Run.State.ToString().ToLowerInvariant(),
        RcaRunLifecycle.Describe(result.Run.State, result.Run.Rejection),
        result.Accepted,

        // `existing`: bu çağrı yeni bir koşum başlattı mı. İdempotans sunucuda
        // olduğu için model bunu ANCAK buradan öğrenebiliyor — yoksa aynı
        // cevabı iki kez alıp iki koşum sanardı.
        result.Existing,
        result.Run.CountsAgainstQuota,
        ownerGroup,
        from,
        to);

    /// <summary>Yükün şekli — <c>RcaRunEntity</c> değil (§8).</summary>
    private sealed record Payload(
        [property: JsonPropertyName("run_id")] Guid RunId,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("accepted")] bool Accepted,
        [property: JsonPropertyName("existing")] bool Existing,
        [property: JsonPropertyName("counts_against_quota")] bool CountsAgainstQuota,
        [property: JsonPropertyName("owner_group")] string OwnerGroup,
        [property: JsonPropertyName("from")] DateTimeOffset From,
        [property: JsonPropertyName("to")] DateTimeOffset To);
}
