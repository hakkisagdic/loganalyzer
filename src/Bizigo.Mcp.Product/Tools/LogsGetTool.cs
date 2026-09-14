using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.Contracts.Security;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>logs.get</c> — <b>tek bir olayın satırı</b>, redaksiyon kapısından geçmiş
/// hâliyle. Ucu <c>GET /v1/events/{id}</c>.
///
/// <para>
/// <b>Bu araç M06'nın redaksiyon kapısının ilk gerçek müşterisi</b> ve o yüzden
/// buradaki üç karar, aracın kendisinden daha önemli.
/// </para>
///
/// <h3>1 · Log metni yükte <see cref="RedactedPrompt"/> olarak duruyor</h3>
///
/// <para>
/// <see cref="Payload.Body"/> ve <see cref="Payload.Attrs"/>'ın değerleri
/// <c>string</c> DEĞİL <see cref="RedactedPrompt"/>. Tel üzerinde ikisi de
/// maskelenmiş dize olarak iniyor (<c>RedactedPromptJsonConverter</c>), yani
/// kapı yüke kadar geliyor — M06'nın o dönüştürücüyü yazma sebebi tam olarak
/// bu.
/// </para>
///
/// <h3>2 · <c>WithLogText</c> KULLANILMIYOR, ve bu ölçülmüş bir karar</h3>
///
/// <para>
/// M04 <c>McpReadToolTests.Hicbir_okuma_araci_log_metni_tasimiyor</c> içinde
/// <i>"kapının ilk müşterisi <c>logs.get</c> olacak"</i> diye yazmıştı ve
/// beklenen şey <see cref="McpToolResult.WithLogText(RedactedPrompt[])"/> idi.
/// Kullanılmadı, üç sebeple:
/// </para>
/// <list type="number">
/// <item>
/// <b>Şema dışında kalıyor.</b> <c>outputSchema</c> yalnızca
/// <c>structuredContent</c>'i tarif ediyor; <c>WithLogText</c> ile giden metin
/// hiçbir şemanın tarif etmediği bir <c>content</c> bloğu olurdu — yani modelin
/// aldığı en pahalı bilgi, ilan edilmemiş bir kanaldan gelirdi. Bu deponun
/// <c>outputSchema</c>'yı zorunlu kılma gerekçesinin tersi.
/// </item>
/// <item>
/// <b>Model onu ALAN olarak adresleyemiyor.</b> Yapısal alan
/// <c>payload.body</c>; metin bloğu sırasız bir dize listesinin bir elemanı.
/// </item>
/// <item>
/// <b>Bağlam bütçesi.</b> <see cref="BizigoMcpTool.ToProtocol"/> yükü
/// <b>zaten iki kez</b> gönderiyor (<c>structuredContent</c> + metin kopyası).
/// <c>WithLogText</c> aynı satırı <b>üçüncü</b> kez taşırdı.
/// </item>
/// </list>
///
/// <para>
/// ⚠️ <b>BULGU — kapı bu kanalda derleyicide DEĞİL, VE BU ÖLÇÜLDÜ.</b>
/// <c>WithLogText</c>'in imzası <see cref="RedactedPrompt"/> istiyor, yani o
/// kanalda kapıyı atlayan çağrı derlenmiyor. Yapısal kanalda böyle bir şart
/// <b>yok</b>: <see cref="Payload.Body"/> alanı <c>string</c> yazılıp
/// <c>source.Body</c> olduğu gibi verildiğinde çözüm <b>0 hata 0 uyarı
/// derlendi</b> (<c>tools/m10-kirmizi-olcumu.py</c>, ilk kusur). Yani kapıyı
/// atlamak bu kanalda bir derleme hatası değil, yalnızca iki bekçinin
/// yakaladığı bir davranış. M06 bunu kendi belgesinde beyan etmişti
/// (<c>McpToolResult</c>: <i>"o alanın öyle yazılması bugün MEKANİK OLARAK
/// TUTULMUYOR"</i>) ve bekçiyi <b>bilerek</b> yazmamıştı — o gün tüketici
/// yoktu ve görülmeyen bir şeye bekçi yazmak §8'in yasağı. Bugün tüketici
/// <b>bu araç</b>, dolayısıyla bekçi yazılabilir hâle geldi ve yazıldı:
/// <c>McpReadToolTests.Logs_get_govdesi_RedactedPrompt_olarak_yazili</c> alanın
/// tipini yansımayla ölçüyor, <c>Logs_get_sirri_maskelenmis_dondurüyor</c> ise
/// davranışı.
/// </para>
///
/// <h3>3 · Ham BAYT dönmüyor — ne base64 ne onaltılık</h3>
///
/// <para>
/// REST'in <c>GET /v1/events/{id}/raw</c>'ı <c>raw_b64</c> ile orijinal baytları
/// veriyor ve orada doğru: ekranın işi kodlama tespitinin doğru olup olmadığını
/// göstermek (K4) ve karşısındaki bir insan. Modele base64 vermek <b>kapıyı
/// tümden atlamak</b> olurdu: base64 kayıpsız bir kodlama, redaksiyon onun
/// içinde hiçbir şey göremiyor ve model tek adımda çözüyor. Yani
/// <c>raw_b64</c> taşıyan bir MCP aracı, kapının kıyafetini giymiş bir
/// atlatma yolu olurdu. Bekçisi
/// <c>Logs_get_ham_baytlari_base64_olarak_dondurmuyor</c>.
/// </para>
///
/// <para>
/// <b>Ve arşive hiç inilmiyor</b> — <c>RawEventLocator</c> bu araçta yok.
/// Sebep bir tercih değil, bu derlemede <b>zaten verilmiş</b> bir karar:
/// <c>CatalogParsersTool</c> kapsam oranını taşımıyor çünkü
/// <c>CatalogCoverageCache.Measure(...)</c> bütün kataloğu yeniden ölçüyor ve
/// <i>"modelin göremediği bir maliyeti modelin tetikleyebilmesi istenmedi"</i>.
/// <c>RawEventLocator.FindAsync</c> aynı sınıf: manifest'ten aday nesneleri
/// buluyor ve <b>sekiz nesneye kadar açıp satır satır tarıyor</b> — object
/// storage üzerinde, indekssiz. İnsan tetikli ve seyrek bir işlem olarak
/// tasarlandı; bir ajanın döngü içinde çağırabileceği bir araç olarak değil.
/// Modelin eline verilen şey <see cref="Payload.RawRef"/>: baytların
/// <b>nerede</b> olduğu, ve oraya inmek REST'in (yani bir insanın) işi.
/// </para>
///
/// <h3>Yükte NE YOK ve neden</h3>
///
/// <para>
/// <c>logs.search</c>'ün satırında zaten olan alanlar <b>tekrarlanmıyor</b>:
/// <c>vendor</c>, <c>product</c>, <c>severity_num</c>, <c>signature_hash</c>,
/// <c>ingested_at</c>, <c>time_source</c>. Bir <c>event_id</c>'yi elinde tutan
/// model onu <c>logs.search</c>'ten aldı, yani o alanları da aldı; ikinci kez
/// göndermek her çağrıda ödenen bir bedel olurdu. Bu aracın cevapladığı soru
/// dar ve tek: <b>bu satır ne diyordu.</b>
/// </para>
///
/// <para>
/// <c>ocsf</c> ve <c>otel</c> görünümleri de yok. REST'te tek istekte
/// geliyorlar ve gerekçesi <i>ekran sekmesi</i> — sekme değiştirmek yeni bir
/// istek gerektirseydi kullanıcı boş ekran görürdü. Modelde sekme yok; iki
/// görünüm türetilmiş alan adlarından ibaret ve <c>attrs</c> zaten
/// <c>ocsf.</c>/<c>otel.</c> önekli hâllerini taşıyor.
/// </para>
/// </summary>
/// <param name="scopes">
/// Çağrı başına kapsam açmak için. <c>IScopedQuery</c> <b>scoped</b> ve araç
/// tekil — gerekçe <see cref="ProductReadTool"/> belgesinde.
/// </param>
public sealed class LogsGetTool(IServiceScopeFactory scopes) : ProductReadTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "logs.get";

    /// <summary>
    /// Yükte taşınacak en fazla <c>attrs</c> anahtarı.
    ///
    /// <para>
    /// Sınır <b>gerekli</b>: <c>attrs</c> parser'ın çıkardığı her alanı ve
    /// türetilmiş <c>ocsf.</c>/<c>otel.</c> hâllerini taşıyor, yani sayısı
    /// parser YAML'ına bağlı ve üstten sınırsız. Şema bütçesi araç başına
    /// ölçülüyor ama <b>yük</b> çağrı başına ödeniyor: sınırsız bir sözlük tek
    /// bir çağrıda modelin penceresini yiyebilir.
    /// </para>
    ///
    /// <para>
    /// Kesilme <see cref="Payload.AttrsTruncated"/> ile <b>söyleniyor</b>.
    /// Sessiz kesme, modelin eksik bir alan kümesini tam sanması demek olurdu —
    /// ve bu araçta o yanlış doğrudan bir kök neden iddiasına dönüşür.
    /// </para>
    /// </summary>
    private const int MaxAttributes = 60;

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "Olay satırı";

    /// <summary>
    /// <inheritdoc/>
    ///
    /// <para>
    /// <b>İki cümle kısaltılamaz.</b> Birincisi maskelemeyi söylüyor: bir model
    /// <c>[gizli:…]</c> gördüğünde onu <i>verinin kendisi</i> sanmamalı, yoksa
    /// kök neden olarak bir maske dizesini gösterir. İkincisi ham baytların
    /// dönmediğini söylüyor; söylenmezse model onları <b>ister</b> ve
    /// isteyemeyeceğini ancak bir hata alarak öğrenir.
    /// </para>
    /// </summary>
    public override string ToolDescription =>
        "Tek bir olayın log satırını ve ayrıştırılmış alanlarını döndürür. Metin REDAKSİYON "
        + "kapısından geçiyor: sır olarak tanınan değerler `[gizli:…]` ile maskeli gelir, yani "
        + "gördüğünüz maske verinin kendisi DEĞİL. Ham baytlar (base64) dönmez; `raw_ref` "
        + "yalnızca arşivdeki yerini söyler.";

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "event_id": { "type": "string", "format": "uuid" }
          },
          "required": ["event_id"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "event_id":          { "type": "string", "format": "uuid" },
            "ts":                { "type": "string", "format": "date-time" },
            "owner_group":       { "type": "string" },
            "source_id":         { "type": "string" },
            "parse_status":      { "type": "string", "enum": ["ok", "partial", "failed"] },
            "parser_id":         { "type": "string" },
            "encoding_detected": { "type": "string" },
            "raw_ref":           { "type": "string" },
            "body":              { "type": "string" },
            "masked_values":     { "type": "integer", "minimum": 0 },
            "attrs":             { "type": "object", "additionalProperties": { "type": "string" } },
            "attrs_truncated":   { "type": "boolean" }
          },
          "required": [
            "event_id", "ts", "owner_group", "source_id", "parse_status", "parser_id",
            "encoding_detected", "raw_ref", "body", "masked_values", "attrs", "attrs_truncated"
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
        var eventId = invocation.Required<Guid>("event_id");

        // Kapsam ÇAĞRI BAŞINA açılıyor; gerekçe `ProductReadTool` belgesinde.
        await using var services = scopes.CreateAsyncScope();

        var found = await services.ServiceProvider
            .GetRequiredService<IScopedQuery>()
            .GetEventAsync(eventId, scope, cancellationToken)
            .ConfigureAwait(false);

        if (found is null)
        {
            // KAPSAM DIŞI OLAY DA `not_found`. REST'in kendi gerekçesi:
            // "403 dönmek 'böyle bir olay var ama göremezsin' bilgisini
            // sızdırırdı". MCP'de bedeli daha da yüksek — o cümle modelin
            // bağlamına giren bir varlık iddiası olurdu.
            return McpToolResult.Failure(new McpToolError(
                McpToolError.NotFound,
                "Bu `event_id` kapsamda bulunamadı.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["event_id"] = eventId.ToString(),
                }));
        }

        return McpToolResult.Structured(Shape(found));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sample = new LogEvent
        {
            EventId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Timestamp = new DateTimeOffset(2026, 9, 5, 14, 48, 2, TimeSpan.Zero),
            OwnerGroup = "network/core",
            SourceId = "fw-edge-01",
            ParserId = "fortinet-fortigate-kv",
            ParseStatus = ParseStatus.Ok,
            EncodingDetected = "utf-8",
            RawRef = "raw/network-core/2026/09/05/14/firewall/",

            // Örnek BİLEREK bir sır taşıyor: kapının örnek yolunda da
            // koştuğunu uyum kapısı görebilsin. Maskelenmemiş bir örnek,
            // "kapı bağlı" iddiasını ölçülmemiş bırakırdı.
            Body = "date=2026-09-05 devname=FG100 action=deny set psksecret Xk7Qm2Rv9Tz4Lw8Yb3Nc",
            Attrs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["action"] = "deny",
                ["devname"] = "FG100",
            },
        };

        // Gerçek yolun ŞEKİLLENDİRMESİ — elle yazılmış JSON değil.
        return ValueTask.FromResult(McpToolResult.Structured(Shape(sample)));
    }

    /// <summary>
    /// Olay → yük. <b>Kapının çağrıldığı tek yer</b>, ve tek olması bilinçli:
    /// gerçek yol ile <see cref="SampleAsync"/> aynı fonksiyondan geçiyor, yani
    /// örnek maskeliyken gerçek yolun maskelememesi <b>ifade edilemiyor</b>.
    /// </summary>
    private static Payload Shape(LogEvent source)
    {
        var body = RedactedPrompt.Redact(source.Body);

        var attrs = new Dictionary<string, RedactedPrompt>(StringComparer.Ordinal);

        // Anahtarlar kapıdan GEÇMİYOR ve geçmemeli: alan adları parser
        // kataloğundan geliyor, yani yapılandırma — değerler ise logdan, yani
        // veri. Anahtarı maskelemek modele alanın adını da gizlerdi ve
        // maskelenecek bir şey de yoktu.
        foreach (var (key, value) in source.Attrs.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (attrs.Count == MaxAttributes)
            {
                break;
            }

            attrs[key] = RedactedPrompt.Redact(value);
        }

        return new Payload(
            source.EventId,
            source.Timestamp,
            source.OwnerGroup,
            source.SourceId,

            // Sayı değil ad — `logs.search`'teki gerekçenin aynısı: `2`
            // gövdesine bakan hiç kimse "partial" demiyor.
            source.ParseStatus.ToString().ToLowerInvariant(),
            source.ParserId,
            source.EncodingDetected,
            source.RawRef,
            body,

            // KAPININ KOŞTUĞUNUN KANITI, ve sıfırken de yazılıyor.
            // `RedactedPrompt.EvidenceFields`'in kendi gerekçesi: "gizlenen bir
            // sıfır 'henüz ölçülmedi' ile 'ölçüldü, sıfır' farkını siler".
            // Modelin tarafında da aynı: alan hiç yoksa maskeleme yapıldığı
            // iddiası yükten okunamaz.
            body.MaskedValues + attrs.Values.Sum(static value => value.MaskedValues),
            attrs,
            source.Attrs.Count > attrs.Count);
    }

    /// <summary>
    /// Yükün şekli — <c>LogEvent</c> değil (§8: depolama tipi tel sözleşmesi
    /// değildir).
    /// </summary>
    /// <param name="Body">
    /// Log satırı. <b>Tipi <see cref="RedactedPrompt"/> ve bu alanın tipi
    /// kapının kendisi</b>: <see cref="RedactedPrompt"/> yalnızca
    /// <see cref="RedactedPrompt.Redact"/>'ten çıkabildiği için, buraya
    /// atanabilen her metin redaksiyondan geçmiş oluyor. <c>string</c> yazmak
    /// derlenirdi — o yüzden yanında bir yansıma bekçisi duruyor.
    /// </param>
    /// <param name="Attrs">
    /// Parser'ın çıkardığı alanlar. <b>Değerler de kapıdan geçiyor:</b> bir
    /// parola <c>attrs["psksecret"]</c> içinde de durabilir ve orada
    /// maskelenmezse <see cref="Body"/>'nin maskelenmesi hiçbir şey ifade
    /// etmezdi.
    /// </param>
    /// <param name="MaskedValues">
    /// Kapının maskelediği <b>ayrı</b> değer sayısı (gövde + alanlar).
    /// </param>
    /// <param name="AttrsTruncated"><see cref="MaxAttributes"/> sınırına takıldı mı.</param>
    private sealed record Payload(
        [property: JsonPropertyName("event_id")] Guid EventId,
        [property: JsonPropertyName("ts")] DateTimeOffset Timestamp,
        [property: JsonPropertyName("owner_group")] string OwnerGroup,
        [property: JsonPropertyName("source_id")] string SourceId,
        [property: JsonPropertyName("parse_status")] string ParseStatus,
        [property: JsonPropertyName("parser_id")] string ParserId,
        [property: JsonPropertyName("encoding_detected")] string EncodingDetected,
        [property: JsonPropertyName("raw_ref")] string RawRef,
        [property: JsonPropertyName("body")] RedactedPrompt Body,
        [property: JsonPropertyName("masked_values")] int MaskedValues,
        [property: JsonPropertyName("attrs")] IReadOnlyDictionary<string, RedactedPrompt> Attrs,
        [property: JsonPropertyName("attrs_truncated")] bool AttrsTruncated);
}
