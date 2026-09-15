using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Rca;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>rca.runs</c> — RCA koşumları. Ucu <c>GET /v1/rca/runs</c> (T46).
///
/// <h3>T46'nın üç yönlü ayrımı OLDUĞU GİBİ taşınıyor</h3>
///
/// <para>
/// Bu aracın tek zor kararı <b>hiçbir karar vermemek</b>. T46 dört cevabı
/// birbirinden ayırmak için bütün defteri kurdu:
/// </para>
///
/// <list type="table">
///   <item><term><c>rejected</c> satırı</term><description>Kapı reddetti — <b>bakılmadı</b>.</description></item>
///   <item><term><c>empty</c> satırı</term><description>Bakıldı, ilişkili kanıt <b>bulunamadı</b>.</description></item>
///   <item><term><c>cancelled</c> satırı</term><description>Başladı, bilinen bir sınırda kesildi.</description></item>
///   <item><term>boş liste</term><description><b>Hiç tetiklenmedi.</b></description></item>
/// </list>
///
/// <para>
/// <b>MCP'ye özel bir durum kovası EKLENMEDİ</b> ve eklenmemesi şart: ikinci bir
/// gösterim doğduğu gün ekranın gördüğü durum ile modelin gördüğü durum
/// ayrışır, ve ayrışmayı gösterecek hiçbir şey olmaz (§9). Durum dizgisi ve
/// <c>reason</c> cümlesi <b>REST ile aynı iki kaynaktan</b> geliyor —
/// <c>RcaRunEntity.State</c> ve <see cref="RcaRunLifecycle.Describe"/>.
/// İkinci bir <c>Describe</c> yazmak, motorun bildirimdeki gerekçesiyle modelin
/// okuduğu gerekçeyi ayırmak olurdu.
/// </para>
///
/// <para>
/// <b>Reddedilen satırlar YÜKTE DURUYOR</b>, ve bu bir liste tercihi değil
/// dördüncü cevabın şartı: boş listenin <i>"hiç tetiklenmedi"</i> garantisi,
/// reddin bir <b>satır</b> olmayı sürdürmesine bağlı. Reddedilenleri gizleyen
/// bir yük, <i>"kota reddetti"</i> ile <i>"hiç denenmedi"</i>yi tek cevaba
/// indirirdi — ve modelin bundan çıkaracağı sonuç <i>"burada RCA'ya gerek
/// görülmedi"</i> olurdu.
/// </para>
///
/// <h3><c>counts_against_quota</c> neden yükte</h3>
///
/// <para>
/// Reddedilen satırlar listede duruyor ama <b>kotayı yemiyor</b>, ve ikisi
/// karıştırılabilir: <i>"on koşum görüyorum, kotam on"</i> yanlış bir çıkarım.
/// REST bu alanı ekran için taşıyor; model için gerekçe aynı ve daha keskin,
/// çünkü model <c>rca.trigger</c>'ı çağırıp kotayı <b>kendisi</b> tüketiyor.
/// </para>
/// </summary>
/// <param name="factory">
/// Kontrol düzlemi bağlamı — <b>tekil</b> fabrika, yani yapıcıda durabiliyor.
/// Gerekçe <see cref="ProductReadTool"/> belgesinde.
/// </param>
public sealed class RcaRunsTool(IDbContextFactory<ControlPlaneDbContext> factory) : ProductReadTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "rca.runs";

    /// <summary>
    /// Tek çağrıda dönebilecek en fazla koşum.
    ///
    /// <para>
    /// REST'in tavanıyla <b>aynı</b> (<see cref="RcaRunEndpoints"/> değil,
    /// aşağıdaki sabitten — o tip <c>Bizigo.Api</c>'de ve bu derleme onu
    /// görmüyor). Sayının aynı olması bilinçli; ayrışsaydı iki yüzey aynı
    /// sorguya farklı sayıda satır dönerdi.
    /// </para>
    /// </summary>
    private const int MaxLimit = 200;

    /// <summary>Varsayılan koşum sayısı.</summary>
    private const int DefaultLimit = 50;

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "RCA koşumları";

    /// <summary>
    /// <inheritdoc/>
    ///
    /// <para>
    /// <b>İki cümle kısaltılamaz.</b> Boş listenin anlamını söylemeyen bir
    /// açıklama, modelin <i>"kota reddetti"</i> ile <i>"hiç denenmedi"</i>yi
    /// karıştırmasına açık kapı bırakır — T46'nın bütün defteri o ayrım için
    /// var. Ve <c>counts_against_quota</c> söylenmezse model listedeki satır
    /// sayısını tüketim sanar.
    /// </para>
    /// </summary>
    public override string ToolDescription =>
        "Kapsam içindeki RCA koşumları, en yeniden eskiye. `state` ile `reason` birlikte "
        + "okunmalı: `rejected` BAKILMADI, `empty` bakıldı ve bulunamadı, `cancelled` başladı ve "
        + "kesildi. BOŞ LİSTE bunlardan farklı — o tetikleyici için RCA hiç denenmedi. "
        + "`counts_against_quota` false olan satırlar kotayı yemiyor.";

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "owner_group": { "type": "string" },
            "trigger":     { "type": "string" },
            "limit":       { "type": "integer", "minimum": 1, "maximum": {{MaxLimit}} }
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
            "count": { "type": "integer", "minimum": 0 },
            "runs": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "run_id":               { "type": "string", "format": "uuid" },
                  "source":               { "type": "string" },
                  "trigger_identity":     { "type": "string" },
                  "owner_group":          { "type": "string" },
                  "state":                { "type": "string" },
                  "reason":               { "type": "string" },
                  "counts_against_quota": { "type": "boolean" },
                  "requested_at":         { "type": "string", "format": "date-time" },
                  "finished_at":          { "type": ["string", "null"], "format": "date-time" }
                },
                "required": [
                  "run_id", "source", "trigger_identity", "owner_group", "state", "reason",
                  "counts_against_quota", "requested_at", "finished_at"
                ],
                "additionalProperties": false
              }
            }
          },
          "required": ["count", "runs"],
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

        var ownerGroup = invocation.Optional<string>("owner_group");
        var trigger = invocation.Optional<string>("trigger");

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var query = db.RcaRuns.AsNoTracking();

        // KAPSAM FİLTRESİ `Take`'TEN ÖNCE — REST'in ölçülmüş kararı: bellekte
        // yapılsaydı en yeni elli satırın hepsi kapsam dışı olduğunda cevap boş
        // dönerdi ve boş listenin garantisi ("hiç tetiklenmedi") sessizce yalan
        // olurdu.
        if (!scope.IsUnrestricted)
        {
            var groups = scope.OwnerGroups.ToArray();
            query = query.Where(r => groups.Contains(r.OwnerGroup));
        }

        // Çağıranın verdiği grup kapsamı GENİŞLETMİYOR, yalnızca daraltıyor.
        if (!string.IsNullOrWhiteSpace(ownerGroup))
        {
            query = query.Where(r => r.OwnerGroup == ownerGroup);
        }

        if (!string.IsNullOrWhiteSpace(trigger))
        {
            query = query.Where(r => r.TriggerIdentity == trigger);
        }

        var rows = await query
            .OrderByDescending(r => r.RequestedAt)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return McpToolResult.Structured(Shape(rows));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

        // ÜÇ SATIR, ÜÇ AYRI CEVAP — ve üçü bilerek: tek satırlık bir örnek
        // ayrımın kendisini hiç göstermezdi, oysa bu aracın tamamı o ayrım.
        // `finished_at`'in hem dolu hem `null` dalı da böylece şemaya karşı
        // doğrulanıyor.
        RcaRunEntity[] sample =
        [
            new()
            {
                Id = Guid.Parse("11111111-aaaa-2222-3333-444444444444"),
                Source = RcaTriggerSource.Agent,
                TriggerIdentity = "alert:deny-sağanağı",
                OwnerGroup = "network/core",
                State = RcaRunState.Complete,
                CountsAgainstQuota = true,
                RequestedAt = now.AddMinutes(-30),
                FinishedAt = now.AddMinutes(-28),
            },

            // BAKILMADI: kota reddetti, kotadan DÜŞÜLMEDİ.
            new()
            {
                Id = Guid.Parse("22222222-aaaa-2222-3333-444444444444"),
                Source = RcaTriggerSource.Agent,
                TriggerIdentity = "alert:deny-sağanağı",
                OwnerGroup = "network/core",
                State = RcaRunState.Rejected,
                Rejection = RcaRejectionReason.QuotaExceeded,
                CountsAgainstQuota = false,
                RequestedAt = now.AddMinutes(-20),
            },

            // BAKILDI, BULUNAMADI: kotadan düşüldü — bakma maliyeti ödendi.
            new()
            {
                Id = Guid.Parse("33333333-aaaa-2222-3333-444444444444"),
                Source = RcaTriggerSource.Manual,
                TriggerIdentity = "manual:analyst.core",
                OwnerGroup = "network/core",
                State = RcaRunState.Empty,
                CountsAgainstQuota = true,
                RequestedAt = now.AddMinutes(-10),
                FinishedAt = now.AddMinutes(-9),
            },
        ];

        // Gerçek yolun ŞEKİLLENDİRMESİ — elle yazılmış JSON değil.
        return ValueTask.FromResult(McpToolResult.Structured(Shape(sample)));
    }

    private static Payload Shape(IReadOnlyList<RcaRunEntity> rows) =>
        new(rows.Count, [.. rows.Select(Row.From)]);

    /// <summary>Yükün tek satırı — <c>RcaRunEntity</c> değil (§8).</summary>
    /// <param name="State">
    /// Kapalı kümenin değeri, küçük harfli. Sayı değil ad: <c>4</c> gövdesine
    /// bakan hiç kimse <i>"empty"</i> demiyor ve enum sırası değiştiğinde
    /// sessizce başka bir durum gösterirdi.
    /// </param>
    /// <param name="Reason">
    /// <b><see cref="RcaRunLifecycle.Describe"/>'dan</b> — ikinci bir cümle
    /// üretici yazılmadı. İstemci <c>state</c> ile ret sebebini birleştirip
    /// anlam üretmek zorunda değil; kalsaydı o birleştirme her tüketicide
    /// yeniden yazılırdı ve hepsi kendi içinde tutarlı görünürdü.
    /// </param>
    private sealed record Row(
        [property: JsonPropertyName("run_id")] Guid RunId,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("trigger_identity")] string TriggerIdentity,
        [property: JsonPropertyName("owner_group")] string OwnerGroup,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("counts_against_quota")] bool CountsAgainstQuota,
        [property: JsonPropertyName("requested_at")] DateTimeOffset RequestedAt,
        [property: JsonPropertyName("finished_at")] DateTimeOffset? FinishedAt)
    {
        internal static Row From(RcaRunEntity run) => new(
            run.Id,
            run.Source.ToString().ToLowerInvariant(),
            run.TriggerIdentity,
            run.OwnerGroup,
            run.State.ToString().ToLowerInvariant(),
            RcaRunLifecycle.Describe(run.State, run.Rejection),
            run.CountsAgainstQuota,
            run.RequestedAt,
            run.FinishedAt);
    }

    private sealed record Payload(
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("runs")] IReadOnlyList<Row> Runs);
}
