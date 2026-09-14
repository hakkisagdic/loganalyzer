using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.Evidence;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>rca.quality</c> — altın kümenin kalite göstergesi. Ucu
/// <c>GET /v1/rca/quality</c>.
///
/// <para>
/// <b>Bu araç ürünün kendi ölçümlerini taşıyor, log verisi taşımıyor.</b>
/// Sayılar T38'in incelemelerinden (doğruluk, <c>accuracy@k</c>, "çelişen kanıt
/// tiyatrosu") ve T47'nin atılan cümle ölçümünden geliyor. Yükte hiçbir serbest
/// metin yok — ne inceleyenin notu, ne <c>actual_root_cause</c>, ne rapor
/// cümlesi. Üçü de insanın ya da modelin yazdığı metin ve kurumun verisi
/// hakkında bilgi taşıyabiliyor; bir <i>gösterge</i> aracının onları taşıması
/// için hiçbir sebep yok.
/// </para>
///
/// <h3>Neden bir MCP aracı hak ediyor</h3>
///
/// <para>
/// Model kendi kalitesini okuyabildiğinde <i>"bu raporun doğruluk geçmişi
/// nedir"</i> sorusunun cevabı bağlamda duruyor. T47'nin ölçtüğü şey tam olarak
/// bunun için var: <b>atılan cümle oranı</b> modelin ürettiği cümlelerin kaçta
/// kaçının kanıtla desteklenmediğini söylüyor.
/// </para>
///
/// <h3>Yük DÜZ, alan adları REST'le birebir, ve ÜÇ ALAN DÜŞTÜ — ölçüm bu şekli
/// seçtirdi</h3>
///
/// <para>
/// İlk hâl üç ayrı nesneydi (<c>verdicts</c> / <c>contradicting</c> /
/// <c>reasoning</c>), gerekçesi <i>"üç ayrı ölçüm ekseni üç ayrı nesnede"</i>.
/// <b>Ölçüldü ve şema bütçesini aştı: 792 belirteç, araç başına tavan 700.</b>
/// Üç hamle sırayla ölçüldü:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Düzleştirme: 792 → 773.</b> Beklenenden az, ve bu <b>maliyetin nerede
/// olduğunu</b> söylüyor: iç içe zarflar değil, <b>alan adlarının kendisi</b>.
/// Her ad <c>properties</c> ve <c>required</c>'da iki kez geçiyor ve yanında bir
/// tip sözcüğü taşıyor. Kazanç yine de tutuldu, çünkü ikinci bir faydası var:
/// bu araç artık <c>GET /v1/rca/quality</c> ile <b>aynı sözlüğü</b> konuşuyor.
/// Karışma riskine karşı zarf da gerekmiyordu — REST aynı sorunu <b>önek</b>
/// ile çözmüş (<c>unknown</c> ↔ <c>contradicting_unknown</c>).
/// </item>
/// <item>
/// <b>Türetilebilir üç alan düştü: <c>decided</c>,
/// <c>contradicting_evaluated</c>, <c>reports_measured</c>.</b> Ölçüt bir
/// belirteç avı değil bir <b>kural</b>: yükte kalan alanlar üzerinde <i>tek bir
/// aritmetik işlemle</i> elde edilebilen ve <see langword="null"/> kuralı
/// olmayan alan düşüyor. Üçü de öyle —
/// <c>total - unknown</c>, <c>sound + trivial</c>,
/// <c>reviewed_bundles - reasoning_absent</c>.
/// </item>
/// <item>
/// <b>Açıklama kırpıldı.</b> Kapının kendi mesajının söylediği ilk yer
/// (<i>"İlk bakılacak yer <c>description</c>"</i>). Kırpılırken iki cümle
/// <b>korundu</b>: eksenlerin ne olduğu ve oranların <see langword="null"/>
/// olabildiği.
/// </item>
/// </list>
///
/// <para>
/// ⚠️ <b>Kuralın DIŞINDA bırakılan bir alan var ve bilerek:</b>
/// <c>measured_coverage</c> de türetilebilir (<c>reports_measured /
/// reviewed_bundles</c>) ama <b>duruyor</b>. Kaynağın kendi gerekçesi:
/// <i>"Düşükse yukarıdaki oranlar altın kümenin küçük bir diliminden geliyor
/// demektir. Bu sayı olmadan <c>DroppedSentenceRatio</c> temsil ettiğinden daha
/// geniş okunur."</i> Yani bu alan bir sayı değil bir <b>uyarı</b>; modelin onu
/// kendisi hesaplaması gerekirse hesaplamaz ve oranı olduğundan geniş okur.
/// Bütçe için düşürülecek son şey uyarı olurdu.
/// </para>
///
/// <para>
/// <b>Tavan yükseltilmedi</b> ve yükseltilmemesi kararın kendisi: sabit
/// <c>700</c>, bugünün en pahalı aracının (<c>logs.search</c>, 578) üstüne ~%20
/// pay bırakacak şekilde ölçülerek konmuş. Bu araç için yükseltmek, kapının
/// <i>"bir aracın bütçesi şunu aşamaz"</i> cümlesini o aracın kendisine göre
/// yeniden yazmak olurdu. <b>Düşen üç alanı geri getirmek isteyen karar,
/// tavanı da beraberinde tartışmak zorunda</b> — ve ikisi birlikte tartışılsın
/// diye buraya yazılı.
/// </para>
///
/// <h3><see langword="null"/> ile sıfırın farkı KORUNUYOR</h3>
///
/// <para>
/// Her oran <c>["number", "null"]</c>. Kaynağın kendi gerekçesi
/// (<c>GoldenSetQuality.ContradictingTrivialRatio</c>): <i>"%0 tiyatro en iyi
/// sonuç, 'hiç değerlendirilmemiş' ise HİÇBİR sonuç. İkisi tek sayıya inerse
/// ekran, ölçülmemiş bir boyutu mükemmel diye gösterir."</i> Modelde bedeli
/// daha yüksek: ekran yanlış bir rozet gösterir, model yanlış bir <b>cümle</b>
/// kurar — <i>"kalite mükemmel"</i>. Oranı sıfıra düşürmek ya da alanı hiç
/// göndermemek bu aracı zararlı yapardı.
/// </para>
/// </summary>
/// <param name="scopes">
/// Çağrı başına kapsam açmak için. <c>GoldenReviewStore</c> <b>scoped</b>
/// (<c>EvidenceServiceCollectionExtensions</c>: <c>AddScoped</c>), araç ise
/// tekil — yapıcıda istemek esir bağımlılık olurdu, gerekçe
/// <see cref="ProductReadTool"/> belgesinde.
/// </param>
public sealed class RcaQualityTool(IServiceScopeFactory scopes) : ProductReadTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "rca.quality";

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "RCA kalite göstergesi";

    /// <summary>
    /// <inheritdoc/>
    ///
    /// <para>
    /// <b><see langword="null"/> cümlesi kısaltılamaz.</b> Oranların
    /// <c>null</c> olabildiğini söylemeyen bir açıklama, modeli <c>null</c>'ı
    /// sıfır gibi okumaya bırakır — ve o okuma bu göstergenin engellemek için
    /// var olduğu hatanın kendisi.
    /// </para>
    /// </summary>
    public override string ToolDescription =>
        "Altın kümenin kalitesi: rapor doğruluğu, `accuracy@k`, çelişen kanıt ve atılan cümle "
        + "oranı — kapsam içindeki incelemelerden. Oranlar `null` OLABİLİR ve sıfırdan farklıdır: "
        + "`null` ölçülecek kayıt yok, `0` ölçüldü ve sıfır çıktı.";

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "total":                       { "type": "integer", "minimum": 0 },
            "correct":                     { "type": "integer", "minimum": 0 },
            "unknown":                     { "type": "integer", "minimum": 0 },
            "accuracy":                    { "type": ["number", "null"] },
            "unknown_ratio":               { "type": ["number", "null"] },
            "rank_asked":                  { "type": "integer", "minimum": 0 },
            "accuracy_at_one":             { "type": ["number", "null"] },
            "accuracy_at_three":           { "type": ["number", "null"] },
            "contradicting_sound":         { "type": "integer", "minimum": 0 },
            "contradicting_trivial":       { "type": "integer", "minimum": 0 },
            "contradicting_unknown":       { "type": "integer", "minimum": 0 },
            "contradicting_unspecified":   { "type": "integer", "minimum": 0 },
            "contradicting_trivial_ratio": { "type": ["number", "null"] },
            "reviewed_bundles":            { "type": "integer", "minimum": 0 },
            "reasoning_absent":            { "type": "integer", "minimum": 0 },
            "produced_nothing":            { "type": "integer", "minimum": 0 },
            "all_dropped":                 { "type": "integer", "minimum": 0 },
            "produced_sentences":          { "type": "integer", "minimum": 0 },
            "dropped_sentences":           { "type": "integer", "minimum": 0 },
            "fabricated_sentences":        { "type": "integer", "minimum": 0 },
            "dropped_sentence_ratio":      { "type": ["number", "null"] },
            "fabricated_citation_ratio":   { "type": ["number", "null"] },
            "measured_coverage":           { "type": ["number", "null"] }
          },
          "required": [
            "total", "correct", "unknown", "accuracy", "unknown_ratio",
            "rank_asked", "accuracy_at_one", "accuracy_at_three",
            "contradicting_sound", "contradicting_trivial", "contradicting_unknown",
            "contradicting_unspecified", "contradicting_trivial_ratio",
            "reviewed_bundles", "reasoning_absent", "produced_nothing",
            "all_dropped", "produced_sentences", "dropped_sentences", "fabricated_sentences",
            "dropped_sentence_ratio", "fabricated_citation_ratio", "measured_coverage"
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
        await using var services = scopes.CreateAsyncScope();

        var reviews = services.ServiceProvider.GetRequiredService<GoldenReviewStore>();

        // İKİ SORGU, TEK YÜK — REST'in kendi şekli. Ayrı iki araç yapmak
        // modelin ikisini birden istemesini gerektirirdi ve bir tanesini
        // isteyip diğerini atlaması, oranı paydasız okumak olurdu:
        // `dropped_sentence_ratio` ancak `measured_coverage` ile birlikte
        // anlamlı ve ikisi ayrı çağrılarda olsa model ikincisini atlar.
        var quality = await reviews.QualityAsync(scope, cancellationToken).ConfigureAwait(false);
        var reasoning = await reviews.ReasoningQualityAsync(scope, cancellationToken).ConfigureAwait(false);

        return McpToolResult.Structured(Shape(quality, reasoning));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Örnek BİLEREK hem sayı hem `null` üretiyor: `accuracy` dolu
        // (karar verilmiş inceleme var) ama `contradicting.trivial_ratio`
        // `null` (hiç değerlendirilmemiş). Yalnızca dolu bir örnek, şemanın
        // `null` dalını hiç sınamazdı — ve o dal bu aracın en pahalı ayrımı.
        var quality = new GoldenSetQuality(
            Total: 12,
            Correct: 7,
            Unknown: 2,
            ContradictingSound: 0,
            ContradictingTrivial: 0,
            ContradictingUnknown: 3,
            ContradictingUnspecified: 9,
            RankAsked: 5,
            RankFirst: 3,
            RankTopThree: 4);

        var reasoning = new GoldenSetReasoningQuality(
            ReviewedBundles: 12,
            ReasoningAbsent: 4,
            ReportsMeasured: 8,
            ProducedNothing: 1,
            AllDropped: 1,
            ProducedSentences: 96,
            DroppedSentences: 11,
            FabricatedSentences: 3);

        // Gerçek yolun ŞEKİLLENDİRMESİ — elle yazılmış JSON değil.
        return ValueTask.FromResult(McpToolResult.Structured(Shape(quality, reasoning)));
    }

    /// <summary>
    /// Kayıtlar → yük. <b>Türetilmiş oranlar burada HESAPLANMIYOR</b>, kayıtların
    /// kendi özelliklerinden okunuyor: <c>Accuracy</c>, <c>AccuracyAtOne</c>,
    /// <c>TrivialRatio</c> ve üçünün <see langword="null"/> kuralı
    /// <c>Bizigo.Evidence</c>'ta yazılı. İkinci bir hesap, bir gün payda
    /// değiştiğinde REST ile MCP'nin farklı sayılar göstermesi demek olurdu
    /// (§9).
    /// </summary>
    private static Payload Shape(GoldenSetQuality quality, GoldenSetReasoningQuality reasoning) => new(
        quality.Total,
        quality.Correct,
        quality.Unknown,
        quality.Accuracy,
        quality.UnknownRatio,
        quality.RankAsked,
        quality.AccuracyAtOne,
        quality.AccuracyAtThree,
        quality.ContradictingSound,
        quality.ContradictingTrivial,
        quality.ContradictingUnknown,
        quality.ContradictingUnspecified,
        quality.ContradictingTrivialRatio,
        reasoning.ReviewedBundles,
        reasoning.ReasoningAbsent,
        reasoning.ProducedNothing,
        reasoning.AllDropped,
        reasoning.ProducedSentences,
        reasoning.DroppedSentences,
        reasoning.FabricatedSentences,
        reasoning.DroppedSentenceRatio,
        reasoning.FabricatedCitationRatio,
        reasoning.MeasuredCoverage);

    /// <summary>
    /// Yükün şekli — <c>GoldenSetQuality</c>/<c>GoldenSetReasoningQuality</c>
    /// değil (§8: bir sonraki turda o kayıtlara eklenen alan buraya kimse karar
    /// vermeden sızardı).
    /// </summary>
    /// <param name="MeasuredCoverage">
    /// Ölçülebilen incelenmiş paketlerin oranı. <b>Türetilebilir olduğu hâlde
    /// duruyor</b> — gerekçesi sınıf belgesinde: bu bir sayı değil bir uyarı.
    /// </param>
    /// <param name="ContradictingUnspecified">
    /// Alan hiç doldurulmadı — <i>"kimse söylemedi"</i>. <c>NotPresent</c>
    /// (<i>"bölüm yoktu"</i>) yükte <b>yok</b> ve olmaması bir eksik değil:
    /// ikisi de paydaya girmiyor ve modelin ayırt etmesi gereken tek şey
    /// <i>ölçülmüş</i> ile <i>ölçülmemiş</i>.
    /// </param>
    /// <param name="ProducedNothing">
    /// Koştu, hiç cümle üretmedi. <paramref name="AllDropped"/> ile <b>ayrı</b>
    /// ve ikisi saf sayımda aynı görünüyor (ikisi de boş bulgu listesi) ama zıt
    /// şeyler söylüyor — kaynağın kendi gerekçesi.
    /// </param>
    private sealed record Payload(
        [property: JsonPropertyName("total")] long Total,
        [property: JsonPropertyName("correct")] long Correct,
        [property: JsonPropertyName("unknown")] long Unknown,
        [property: JsonPropertyName("accuracy")] double? Accuracy,
        [property: JsonPropertyName("unknown_ratio")] double? UnknownRatio,
        [property: JsonPropertyName("rank_asked")] long RankAsked,
        [property: JsonPropertyName("accuracy_at_one")] double? AccuracyAtOne,
        [property: JsonPropertyName("accuracy_at_three")] double? AccuracyAtThree,
        [property: JsonPropertyName("contradicting_sound")] long ContradictingSound,
        [property: JsonPropertyName("contradicting_trivial")] long ContradictingTrivial,
        [property: JsonPropertyName("contradicting_unknown")] long ContradictingUnknown,
        [property: JsonPropertyName("contradicting_unspecified")] long ContradictingUnspecified,
        [property: JsonPropertyName("contradicting_trivial_ratio")] double? ContradictingTrivialRatio,
        [property: JsonPropertyName("reviewed_bundles")] long ReviewedBundles,
        [property: JsonPropertyName("reasoning_absent")] long ReasoningAbsent,
        [property: JsonPropertyName("produced_nothing")] long ProducedNothing,
        [property: JsonPropertyName("all_dropped")] long AllDropped,
        [property: JsonPropertyName("produced_sentences")] long ProducedSentences,
        [property: JsonPropertyName("dropped_sentences")] long DroppedSentences,
        [property: JsonPropertyName("fabricated_sentences")] long FabricatedSentences,
        [property: JsonPropertyName("dropped_sentence_ratio")] double? DroppedSentenceRatio,
        [property: JsonPropertyName("fabricated_citation_ratio")] double? FabricatedCitationRatio,
        [property: JsonPropertyName("measured_coverage")] double? MeasuredCoverage);
}
