using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Alerting;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>alerts.maintenance</c> — bakım pencereleri. Ucu
/// <c>GET /v1/alerts/maintenance</c>.
///
/// <h3>KARAR: bu araç OKUMA tarafında — ve sınır M14'te DARALTILDI</h3>
///
/// <para>
/// M04 bu aracı kapsamdan çıkarmıştı ve açık bıraktığı soru şuydu: <i>bakım
/// penceresi yazma işi mi okuma mı — bu ürün MCP üzerinden yazma yapıyor mu?</i>
/// M10'un cevabı <i>"bugün hayır"</i> oldu ve <b>o cümle fazla geniş yazılmıştı</b>.
/// </para>
///
/// <para>
/// <b>M14 sınırı ölçerek daralttı.</b> Kararı taşıyan şey yazmanın kendisi
/// değildi, aşağıdaki dördüncü maddeydi: <i>hatası SESSİZ olan yazma</i>. Ürün
/// bugün MCP üzerinden <b>bir</b> yazma yapıyor (<c>rca.trigger</c>) ve o dört
/// şartı birden karşılıyor — <b>idempotans · her sonucun bir kaydı · maliyet
/// görünürlüğü · aktörün kimliği</b>. Ölçüt ve gerekçesi
/// <see cref="ProductWriteTool"/>'da.
/// </para>
///
/// <para>
/// <b>Bakım penceresi o ölçütün DÖRDÜNÜ DE karşılamıyor</b>, ve bu yüzden karar
/// değişmedi:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>İdempotans yok.</b> Aynı pencereyi iki kez açmak iki satır ve iki
/// bastırma demek.
/// </item>
/// <item>
/// <b>Aktör kaydı yok.</b> REST'in <c>CreateWindowAsync</c>'i
/// <c>CreatedBy = scope.Subject</c> yazıyor; MCP'de o özne modeli çağıran
/// kimlik ve kayıt <i>"insan karar verdi"</i> ile <i>"model karar verdi"</i>
/// arasında ayrım taşımıyor. T54'ün aynı sınıfı.
/// </item>
/// <item>
/// <b>Maliyet görünmüyor</b> ve silme geri alınamıyor:
/// <c>DELETE /v1/alerts/maintenance/{id}</c> satırı gerçekten siliyor.
/// </item>
/// <item>
/// <b>VE ASIL SEBEP — hatası SESSİZ.</b> Bakım penceresinin işi alarmları
/// <b>bastırmak</b> (<see cref="SuppressionReason.MaintenanceWindow"/>). Yanlış
/// açılmış bir pencerenin belirtisi bir hata değil, <b>sessizlik</b>: alarm hiç
/// tetiklenmiyor, ekran sağlıklı görünüyor ve kimse bir şey aramıyor. Bu
/// deponun §7'de adını koyduğu sınıfın en pahalı hâli.
/// <c>rca.trigger</c>'ın tam tersi: onun her sonucu bir <c>rca_runs</c> satırı.
/// </item>
/// </list>
///
/// <para>
/// <b>Yazan bir araç ayrıca bu tabandan TÜREYEMİYOR:</b>
/// <see cref="ProductReadTool.IsReadOnly"/> <c>sealed</c>. Karar mekanik olarak
/// da tutuluyor — <c>McpReadToolTests.Hicbir_urun_araci_yazma_cagirmiyor</c>
/// yazma çağrılarını IL'den tarıyor ve muafiyet listesi <b>sayılı</b>: ikinci
/// bir yazma aracı iki bilinçli hareket gerektiriyor (§8).
/// </para>
///
/// <para>
/// <b>Ve okuma tarafı modelin gerçekten ihtiyaç duyduğu şey.</b> RCA'nın
/// sorusu <i>"bu alarm neden hiç çıkmadı"</i> ya da <i>"bu sessizlik gerçek
/// mi"</i>. İkisi de bir <b>soru</b>, ve bu araç onu cevaplıyor:
/// <see cref="Row.State"/> pencerenin şu an yürürlükte olup olmadığını,
/// <see cref="Row.RuleId"/> ise tek bir kuralı mı yoksa grubun tamamını mı
/// susturduğunu söylüyor.
/// </para>
///
/// <para>
/// <b>Yürürlük kararı KOPYALANMIYOR.</b> <see cref="Row.State"/>
/// <see cref="AlertSuppression.IsOpen"/>'dan geliyor — bastırma motorunun
/// kullandığı <b>aynı</b> yarı-açık aralık kuralı. Buraya
/// <c>now &gt;= starts &amp;&amp; now &lt; ends</c> yazmak ikinci bir kopya
/// olurdu ve ayrıştığı gün gösterge ile bastırma zıt cevap verirdi: araç
/// <i>"pencere kapandı"</i> derken motor hâlâ bastırıyor olurdu, ve o hâlin
/// hiçbir belirtisi olmazdı.
/// </para>
///
/// <para>
/// <c>reason</c> alanı yükte <b>var</b> ve serbest metin — ama insanın
/// yazdığı bir bakım gerekçesi (<i>"çekirdek anahtar firmware yükseltmesi"</i>),
/// log verisi değil. Kapıdan geçirmek onu maskelemezdi (sır deseni yok) ve
/// <see cref="Contracts.Security.RedactedPrompt"/> yazmak burada
/// <b>tiyatro</b> olurdu: kapının kullanıldığı izlenimini veren, hiçbir şey
/// yapmayan bir alan. <c>logs.get</c>'te kapı gerçek yük taşıyor; burada
/// taşımıyor ve taşımadığı yazılı.
/// </para>
/// </summary>
/// <param name="factory">
/// Kontrol düzlemi bağlamı. <b>Tekil</b> (<c>IDbContextFactory</c> singleton
/// kayıtlı), yani yapıcıda durabiliyor — <c>IScopedQuery</c>'nin aksine
/// esir bağımlılık üretmiyor, çünkü fabrika her çağrıda yeni bir bağlam
/// açıyor.
/// </param>
/// <param name="timeProvider">
/// <see cref="Row.State"/> için "şimdi". Testte sabitlenebiliyor: yürürlük
/// iddiasını duvar saatine bağlamak, gece yarısı kırmızı yanan bir bekçi
/// demek olurdu.
/// </param>
public sealed class AlertsMaintenanceTool(
    IDbContextFactory<ControlPlaneDbContext> factory,
    TimeProvider? timeProvider = null)
    : ProductReadTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "alerts.maintenance";

    /// <summary>Tek çağrıda dönebilecek en fazla pencere.</summary>
    private const int MaxLimit = 200;

    /// <summary>Varsayılan pencere sayısı.</summary>
    private const int DefaultLimit = 50;

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "Bakım pencereleri";

    /// <summary>
    /// <inheritdoc/>
    ///
    /// <para>
    /// <b>"Yalnızca okur" cümlesi kısaltılamaz.</b> Bir modelin bakım penceresi
    /// açmayı deneyip <i>"araç yok"</i> cevabı almasıyla, deneyemeyeceğini
    /// baştan bilmesi arasındaki fark bir tur. Ve <c>rule_id</c> cümlesi de
    /// öyle: <c>null</c>'ı <i>"kural bilinmiyor"</i> diye okuyan bir model,
    /// grubun tamamının susturulduğunu göremez.
    /// </para>
    /// </summary>
    public override string ToolDescription =>
        "Kapsam içindeki bakım pencerelerini listeler — YALNIZCA OKUR, pencere açmaz/kapatmaz. "
        + "`state` pencerenin şu an yürürlükte olup olmadığını söyler. `rule_id` `null` ise pencere "
        + "o gruptaki TÜM kuralları susturuyor, tek bir kuralı değil.";

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "limit": { "type": "integer", "minimum": 1, "maximum": {{MaxLimit}} },
            "open_only": { "type": "boolean" }
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
            "now":       { "type": "string", "format": "date-time" },
            "total":     { "type": "integer", "minimum": 0 },
            "truncated": { "type": "boolean" },
            "windows": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "window_id":   { "type": "string", "format": "uuid" },
                  "rule_id":     { "type": ["string", "null"], "format": "uuid" },
                  "owner_group": { "type": "string" },
                  "starts_at":   { "type": "string", "format": "date-time" },
                  "ends_at":     { "type": "string", "format": "date-time" },
                  "state":       { "type": "string", "enum": ["scheduled", "open", "ended"] },
                  "reason":      { "type": "string" },
                  "created_by":  { "type": "string" }
                },
                "required": [
                  "window_id", "rule_id", "owner_group", "starts_at", "ends_at",
                  "state", "reason", "created_by"
                ],
                "additionalProperties": false
              }
            }
          },
          "required": ["now", "total", "truncated", "windows"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected internal override async ValueTask<McpToolResult> ExecuteScopedAsync(
        McpToolInvocation invocation,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        var limit = invocation.Optional("limit", DefaultLimit);

        if (limit is < 1 or > MaxLimit)
        {
            throw new McpToolArgumentException(
                "limit",
                string.Create(CultureInfo.InvariantCulture, $"1 ile {MaxLimit} arasında olmalı"));
        }

        var openOnly = invocation.Optional("open_only", false);
        var now = time.GetUtcNow();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // ÇEKME SINIRI REST İLE AYNI (200) ve kapsam filtresi de aynı yerde:
        // `scope.Allows` bellekte, çünkü `MaintenanceWindows` tek bir
        // `owner_group` kolonu taşıyor ve kapsam bir küme. `AlertEndpoints`
        // birebir bunu yapıyor; ikinci bir sorgu şekli yazmak, bir gün birinde
        // olup diğerinde olmayan bir filtre demek olurdu (§9).
        var all = await db.MaintenanceWindows
            .AsNoTracking()
            .OrderByDescending(w => w.StartsAt)
            .Take(MaxLimit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return McpToolResult.Structured(Shape(all, scope, now, limit, openOnly));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = new DateTimeOffset(2026, 9, 5, 15, 0, 0, TimeSpan.Zero);

        MaintenanceWindowEntity[] sample =
        [
            // BİRİNCİSİ AÇIK ve `rule_id` YOK: grubun tamamını susturan hâl.
            new()
            {
                Id = Guid.Parse("cccccccc-1111-2222-3333-444444444444"),
                OwnerGroup = "network/core",
                StartsAt = now.AddMinutes(-30),
                EndsAt = now.AddHours(2),
                Reason = "çekirdek anahtar firmware yükseltmesi",
                CreatedBy = "analyst.core",
            },

            // İKİNCİSİ BİTMİŞ ve tek bir kurala bağlı: `rule_id`'nin dize dalı
            // ve `state`'in ikinci değeri de şemaya karşı doğrulansın. Tek
            // satırlık bir örnek `null` ile dizeyi ayırt edemezdi.
            new()
            {
                Id = Guid.Parse("dddddddd-1111-2222-3333-444444444444"),
                RuleId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"),
                OwnerGroup = "network/core",
                StartsAt = now.AddDays(-1),
                EndsAt = now.AddDays(-1).AddHours(1),
                Reason = "deny sağanağı kuralı için bakım",
                CreatedBy = "analyst.core",
            },
        ];

        // Gerçek yolun ŞEKİLLENDİRMESİ — ve kapsam da gerçek: örnek kapsam
        // filtresinden GEÇİYOR, yani filtre bir gün her şeyi elerse örnek boş
        // kalır ve uyum kapısı bunu görür.
        var payload = Shape(
            sample,
            AccessScope.ForGroups("uyum-kapisi", ["network/core"]),
            now,
            limit: 2,
            openOnly: false);

        return ValueTask.FromResult(McpToolResult.Structured(payload));
    }

    private static Payload Shape(
        IReadOnlyList<MaintenanceWindowEntity> all,
        AccessScope scope,
        DateTimeOffset now,
        int limit,
        bool openOnly)
    {
        var visible = all
            .Where(w => scope.Allows(w.OwnerGroup))
            .Where(w => !openOnly || AlertSuppression.IsOpen(w, now))
            .ToArray();

        return new Payload(
            now,
            visible.Length,
            visible.Length > limit,
            [.. visible.Take(limit).Select(w => Row.From(w, now))]);
    }

    /// <summary>Yükün tek satırı — <c>MaintenanceWindowEntity</c> değil (§8).</summary>
    /// <param name="RuleId">
    /// <see langword="null"/> ise pencere gruptaki <b>tüm</b> kuralları
    /// susturuyor. Alan boş dizeye çevrilmiyor: <c>""</c> bir kural kimliği gibi
    /// okunur ve <i>"kural bilinmiyor"</i> ile <i>"kural yok, hepsi"</i> aynı
    /// şey olmaz.
    /// </param>
    /// <param name="State">
    /// <c>scheduled</c> | <c>open</c> | <c>ended</c>. Karar
    /// <see cref="AlertSuppression.IsOpen"/>'dan; <c>open</c> ile
    /// <c>scheduled</c>/<c>ended</c> arasındaki sınır bastırma motorunun
    /// kullandığı sınırın <b>ta kendisi</b>.
    /// </param>
    private sealed record Row(
        [property: JsonPropertyName("window_id")] Guid WindowId,
        [property: JsonPropertyName("rule_id")] Guid? RuleId,
        [property: JsonPropertyName("owner_group")] string OwnerGroup,
        [property: JsonPropertyName("starts_at")] DateTimeOffset StartsAt,
        [property: JsonPropertyName("ends_at")] DateTimeOffset EndsAt,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("created_by")] string CreatedBy)
    {
        internal static Row From(MaintenanceWindowEntity window, DateTimeOffset now) => new(
            window.Id,
            window.RuleId,
            window.OwnerGroup,
            window.StartsAt,
            window.EndsAt,

            // `open` KARARI TEK YERDEN. `scheduled`/`ended` ayrımı ondan
            // türüyor: pencere açık değilse ya henüz başlamamış ya bitmiş, ve
            // ikisini `StartsAt` karşılaştırması ayırıyor. Bu ikinci
            // karşılaştırma bir kopya DEĞİL — bastırma motorunun hiç sormadığı
            // bir soruyu cevaplıyor (motor için ikisi de "bastırma yok").
            AlertSuppression.IsOpen(window, now)
                ? "open"
                : now < window.StartsAt ? "scheduled" : "ended",
            window.Reason,
            window.CreatedBy);
    }

    private sealed record Payload(
        [property: JsonPropertyName("now")] DateTimeOffset Now,
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("truncated")] bool Truncated,
        [property: JsonPropertyName("windows")] IReadOnlyList<Row> Windows);
}
