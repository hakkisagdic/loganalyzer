using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>logs.search</c> — kapsam içindeki olay <b>üst verisini</b> arar.
///
/// <para>
/// <b>Ucu <c>/v1/events/search</c>, <c>/v1/logs</c> DEĞİL.</b> İkincisi OTLP
/// <i>ingest</i> ucu (<c>LogsEndpoint.MapOtlpLogs</c>); adının "logs" olması
/// okuma ucu olduğu anlamına gelmiyor ve plandan kopyalanan eşleme yanlıştı.
/// Ölçüldü.
/// </para>
///
/// <para>
/// <b>Bu araç log İÇERİĞİ döndürmüyor — ve bu bir eksik değil, K6'nın kendisi.</b>
/// REST'in <c>EventResponse</c>'u <c>body</c> (satırın kendisi), <c>attrs</c>
/// (ayrıştırılmış alanlar), <c>user_name</c>, <c>src_ip</c>, <c>host</c> ve
/// <c>raw_ref</c> taşıyor. Bunları buraya koymak, log içeriğini
/// <c>McpLogText</c> menteşesini <b>atlayarak</b> doğrudan modelin bağlamına
/// sokmak olurdu — M01 o menteşenin fabrikasını bilerek <c>internal</c>
/// bıraktı, yani bu derlemeden ulaşılamıyor. Yükte yalnızca <b>ürünün kendi
/// atadığı</b> alanlar var:
/// </para>
/// <list type="bullet">
/// <item><c>event_id</c>, <c>owner_group</c>, <c>source_id</c> — bizim kimliklerimiz.</item>
/// <item><c>ts</c> + <c>time_source</c> — ikincisi olmadan birincisi cümle kurmuyor.</item>
/// <item><c>ingested_at</c> — bizim aldığımız an.</item>
/// <item><c>vendor</c>, <c>product</c> — envanterden, logdan değil.</item>
/// <item><c>parse_status</c>, <c>severity_num</c> — boru hattının kendi kararı.</item>
/// <item>
/// <c>signature_hash</c> — maskelenmiş metnin XXH64'ü. Metnin kendisi değil
/// <b>kimliği</b>: aynı imzalı satırları saymaya yetiyor, satırı geri kurmaya
/// yetmiyor. <c>template_id</c> yerine bu seçildi çünkü o örneklemeye tabi ve
/// bir imzanın <b>ilk görülüşünde tanım gereği boş</b> — yani modele "yeni bir
/// şey yok" diye okunacak bir boşluk.
/// </item>
/// </list>
///
/// <para>
/// <b>Şemada <c>filters</c> yok ve sebebi bir kopyalama yasağı.</b> Alan/operatör
/// üçlüsünü kabul etmek için operatör beyaz listesi gerekiyor
/// (<c>eq|ne|in|gt|lt|contains|startswith</c>) ve o liste bugün
/// <c>Bizigo.Api.EventsEndpoints.ToFilter</c> içinde <c>internal</c>. Buradan
/// ulaşılamıyor; kopyalamak §9'un yasakladığı ikinci kopya olurdu — bir gün
/// birinde olup diğerinde olmayan bir operatör. Listeyi ortak bir yere taşımak
/// koordinasyon isteyen bir hareket, bu ticket'ta yapılmadı.
/// </para>
/// </summary>
/// <param name="scopes">
/// Çağrı başına kapsam açmak için. <c>IScopedQuery</c> <b>scoped</b> ve araç
/// tekil — gerekçe <see cref="ProductReadTool"/> belgesinde.
/// </param>
/// <param name="timeProvider">Varsayılan pencere için; testte sabitlenebiliyor.</param>
public sealed class LogsSearchTool(IServiceScopeFactory scopes, TimeProvider? timeProvider = null)
    : ProductReadTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "logs.search";

    /// <summary>
    /// Tek çağrıda dönebilecek en fazla satır.
    ///
    /// <para>
    /// REST'in tavanı <c>1000</c>; burada <c>200</c>. Sebep ClickHouse değil
    /// <b>bağlam</b>: bin satırlık bir yük modelin penceresinin önemli bir
    /// kısmını yer ve model sonraki turda onu <i>zaten okumuş</i> sanar. Daha
    /// fazlasını isteyen <c>cursor</c> ile ilerliyor — sayfalama zaten bunun
    /// için var.
    /// </para>
    /// </summary>
    private const int MaxLimit = 200;

    /// <summary>Varsayılan satır sayısı.</summary>
    private const int DefaultLimit = 50;

    /// <summary>Zaman aralığı verilmezse bakılan pencere — REST ile aynı.</summary>
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "Olay arama";

    /// <summary>
    /// <inheritdoc/>
    ///
    /// <para>
    /// <b>Bu metin bir bütçe kalemi.</b> Her <c>tools/list</c> yanıtında
    /// taşınıyor ve ölçüldüğünde araç başına en pahalı kalem şema değil bu.
    /// Buna rağmen iki cümle <b>kısaltılamaz</b>: log içeriğinin dönmediğini
    /// söylemeyen bir açıklama modeli <c>body</c> aramaya gönderir, ve imlecin
    /// aynı adla geri verildiğini söylemeyen bir açıklama F1'in sessiz
    /// tekrarını modele yaptırır.
    /// </para>
    /// </summary>
    public override string ToolDescription =>
        "Kapsam içindeki olayların ÜST VERİSİNİ arar; log satırının kendisi, ayrıştırılmış "
        + "alanları ve ham baytları bu araçtan DÖNMEZ. Sayfalama: yanıttaki `cursor` değerini "
        + "sonraki çağrıda aynı adla geri verin; `null` ise sayfa bitti.";

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "from":           { "type": "string", "format": "date-time" },
            "to":             { "type": "string", "format": "date-time" },
            "full_text":      { "type": "string", "minLength": 1 },
            "source_ids":     { "type": "array", "items": { "type": "string" } },
            "owner_groups":   { "type": "array", "items": { "type": "string" } },
            "parse_statuses": {
              "type": "array",
              "items": { "type": "string", "enum": ["ok", "partial", "failed"] }
            },
            "limit":          { "type": "integer", "minimum": 1, "maximum": {{MaxLimit}} },
            "{{McpCursor.FieldName}}": { "type": "string", "minLength": 1 }
          },
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "from":     { "type": "string", "format": "date-time" },
            "to":       { "type": "string", "format": "date-time" },
            "count":    { "type": "integer", "minimum": 0 },
            "has_more": { "type": "boolean" },
            "{{McpCursor.FieldName}}": { "type": ["string", "null"] },
            "events": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "event_id":       { "type": "string", "format": "uuid" },
                  "ts":             { "type": "string", "format": "date-time" },
                  "time_source":    { "type": "string" },
                  "ingested_at":    { "type": "string", "format": "date-time" },
                  "owner_group":    { "type": "string" },
                  "source_id":      { "type": "string" },
                  "vendor":         { "type": "string" },
                  "product":        { "type": "string" },
                  "parse_status":   { "type": "string", "enum": ["ok", "partial", "failed"] },
                  "severity_num":   { "type": "integer", "minimum": 0, "maximum": 255 },
                  "signature_hash": { "type": "string" }
                },
                "required": [
                  "event_id", "ts", "time_source", "ingested_at", "owner_group",
                  "source_id", "vendor", "product", "parse_status", "severity_num",
                  "signature_hash"
                ],
                "additionalProperties": false
              }
            }
          },
          "required": ["from", "to", "count", "has_more", "{{McpCursor.FieldName}}", "events"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected internal override async ValueTask<McpToolResult> ExecuteScopedAsync(
        McpToolInvocation invocation,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        var request = Read(invocation);

        // Kapsam ÇAĞRI BAŞINA açılıyor; gerekçe `ProductReadTool` belgesinde.
        await using var services = scopes.CreateAsyncScope();

        var page = await services.ServiceProvider
            .GetRequiredService<IScopedQuery>()
            .SearchEventsAsync(request.Query, scope, cancellationToken)
            .ConfigureAwait(false);

        return McpToolResult.Structured(Shape(request, page));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var from = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddHours(1);

        var sample = new LogEvent
        {
            EventId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Timestamp = from.AddMinutes(3),
            TimeSource = TimeSources.Parsed,
            IngestedAt = from.AddMinutes(3).AddSeconds(2),
            OwnerGroup = "network/core",
            SourceId = "fw-edge-01",
            Vendor = "cisco",
            Product = "asa",
            ParseStatus = ParseStatus.Ok,
            SeverityNum = 4,
            SignatureHash = 0xDEADBEEFCAFEF00D,

            // Örnek BİLEREK dolu bir `Body` taşıyor: şekillendirmenin onu
            // yükten DIŞARIDA bıraktığını kapının kendisi görebilsin. Boş
            // bıraksaydık "sızmıyor" iddiası ölçülmemiş kalırdı.
            Body = "%ASA-4-106023: Deny tcp src outside:203.0.113.9/44321",
        };

        var request = new Request(
            new EventQuery { From = from, To = to, Limit = DefaultLimit },
            from,
            to);

        // İmleç ALANI DOLU bir örnek: kapı `cursor`'ın dize hâlini de
        // doğrulasın. `null` bir örnek, şemanın dize dalını hiç sınamazdı.
        var page = new EventPage(
            [sample],
            new EventCursor(sample.Timestamp, sample.EventId),
            HasMore: true);

        // Gerçek yolun ŞEKİLLENDİRMESİ — elle yazılmış JSON değil.
        return ValueTask.FromResult(McpToolResult.Structured(Shape(request, page)));
    }

    private Request Read(McpToolInvocation invocation)
    {
        var to = invocation.Optional<DateTimeOffset?>("to") ?? time.GetUtcNow();
        var from = invocation.Optional<DateTimeOffset?>("from") ?? to - DefaultWindow;

        if (from >= to)
        {
            throw new McpToolArgumentException("from", "`to` değerinden küçük olmalı");
        }

        var limit = invocation.Optional("limit", DefaultLimit);

        if (limit is < 1 or > MaxLimit)
        {
            throw new McpToolArgumentException(
                "limit",
                string.Create(CultureInfo.InvariantCulture, $"1 ile {MaxLimit} arasında olmalı"));
        }

        EventCursor? after = null;

        if (invocation.Has(McpCursor.FieldName))
        {
            var encoded = invocation.Required<string>(McpCursor.FieldName);

            // ÇÖZÜLEMEYEN İMLEÇ REDDEDİLİYOR — yok sayılmıyor. Yok saymak
            // sessizce ilk sayfayı tekrarlamak olurdu ve model bunu fark
            // etmezdi (§7'nin ilk örneği).
            if (!McpCursor.TryDecode(encoded, out var decoded, out var reason))
            {
                throw new McpToolArgumentException(McpCursor.FieldName, reason);
            }

            after = decoded;
        }

        var statuses = new List<ParseStatus>();

        foreach (var name in invocation.Optional<IReadOnlyList<string>>("parse_statuses") ?? [])
        {
            if (!Enum.TryParse<ParseStatus>(name, ignoreCase: true, out var parsed))
            {
                throw new McpToolArgumentException("parse_statuses", $"tanınmayan değer: `{name}`");
            }

            statuses.Add(parsed);
        }

        return new Request(
            new EventQuery
            {
                From = from,
                To = to,
                FullText = invocation.Optional<string>("full_text"),
                OwnerGroups = invocation.Optional<IReadOnlyList<string>>("owner_groups") ?? [],
                SourceIds = invocation.Optional<IReadOnlyList<string>>("source_ids") ?? [],
                ParseStatuses = statuses,
                After = after,
                Limit = limit,
            },
            from,
            to);
    }

    private static Payload Shape(Request request, EventPage page) => new(
        request.From,
        request.To,
        page.Events.Count,
        page.HasMore,

        // Çıktıdaki ad girdideki adla AYNI (`McpCursor.FieldName`). Bu iki
        // şemanın gerçekten aynı adı taşıdığı `McpCursorContractTests`
        // tarafından şemalardan okunarak ölçülüyor.
        McpCursor.Encode(page.Next),
        [.. page.Events.Select(Row.From)]);

    /// <summary>Okunmuş istek — sorgu ve pencere birlikte, ikisi de yükte.</summary>
    private sealed record Request(EventQuery Query, DateTimeOffset From, DateTimeOffset To);

    /// <summary>
    /// Yükün tek satırı. Anonim nesne değil ve <c>LogEvent</c> de değil: §8'in
    /// iki ayrı maddesi. Depolama tipi tel sözleşmesi değildir, ve domain
    /// tipine eklenen her alan buraya <b>kimse karar vermeden</b> sızardı —
    /// MCP'de o sızıntı doğrudan modelin bağlamına giriyor.
    /// </summary>
    private sealed record Row(
        [property: JsonPropertyName("event_id")] Guid EventId,
        [property: JsonPropertyName("ts")] DateTimeOffset Timestamp,
        [property: JsonPropertyName("time_source")] string TimeSource,
        [property: JsonPropertyName("ingested_at")] DateTimeOffset IngestedAt,
        [property: JsonPropertyName("owner_group")] string OwnerGroup,
        [property: JsonPropertyName("source_id")] string SourceId,
        [property: JsonPropertyName("vendor")] string Vendor,
        [property: JsonPropertyName("product")] string Product,
        [property: JsonPropertyName("parse_status")] string ParseStatus,
        [property: JsonPropertyName("severity_num")] byte SeverityNum,
        [property: JsonPropertyName("signature_hash")] string SignatureHash)
    {
        internal static Row From(LogEvent source) => new(
            source.EventId,
            source.Timestamp,
            source.TimeSource,
            source.IngestedAt,
            source.OwnerGroup,
            source.SourceId,
            source.Vendor,
            source.Product,

            // Sayı değil ad — REST tarafındaki gerekçenin aynısı: `2` gövdesine
            // bakan hiç kimse "partial" demiyor ve enum sırası değiştiğinde
            // sessizce başka bir durum gösterirdi.
            source.ParseStatus.ToString().ToLowerInvariant(),
            source.SeverityNum,

            // Onaltılık dize, sayı değil: `ulong` JSON'da 2^53'ü aşınca
            // JavaScript istemcilerinde SESSİZCE yuvarlanıyor ve iki farklı
            // imza aynı görünüyor.
            source.SignatureHash.ToString("x16", CultureInfo.InvariantCulture));
    }

    private sealed record Payload(
        [property: JsonPropertyName("from")] DateTimeOffset From,
        [property: JsonPropertyName("to")] DateTimeOffset To,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("has_more")] bool HasMore,
        [property: JsonPropertyName(McpCursor.FieldName)] string? Cursor,
        [property: JsonPropertyName("events")] IReadOnlyList<Row> Events);
}
