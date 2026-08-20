using System.Text.Json.Serialization;
using Bizigo.ControlPlane;
using Bizigo.Evidence;

namespace Bizigo.Api;

/// <summary>
/// RCA koşusu isteği — <b>elle tetikleme</b> (RCA §5'in "kullanıcı" yolu).
///
/// <para>
/// Kuyruk, kota ve debounce <b>yok</b>: onlar dört tetikleyicinin tamamıyla
/// birlikte F4'te geliyor. Burada olan tek şey, kullanıcının bir pencere seçip
/// kanıtı toplatması. Eşzamanlılık koruması zaten var — hız sınırı kullanıcı
/// başına (<c>Program.cs</c>).
/// </para>
/// </summary>
public sealed record RcaRequest
{
    [JsonPropertyName("from")]
    public required DateTimeOffset From { get; init; }

    [JsonPropertyName("to")]
    public required DateTimeOffset To { get; init; }

    /// <summary>
    /// Taban penceresinin başı. <b>Zorunlu ve sunucu varsayılanı yok</b> — bu
    /// bilinçli.
    ///
    /// <para>
    /// T35 taban uzunluğunu ölçmeyi bilerek açık bıraktı ve ölçüm aracını
    /// (<c>BaselineWindowMeasurement</c>) yazdı: çok kısa seçilirse her yeni şey
    /// "ilk-görülen" olur, çok uzun seçilirse gerçek yenilik gürültüde kaybolur.
    /// Sunucuya bir varsayılan koymak, T35'in uydurmayı reddettiği sayıyı
    /// uydurmak ve <b>ölçülmüş gibi göstermek</b> olurdu. Ölçüm koşulduğunda
    /// varsayılan tek yerden gelecek.
    /// </para>
    /// </summary>
    [JsonPropertyName("baseline_from")]
    public required DateTimeOffset BaselineFrom { get; init; }

    [JsonPropertyName("baseline_to")]
    public required DateTimeOffset BaselineTo { get; init; }

    /// <summary>Kapsam <b>daraltması</b> — kullanıcının kapsamını genişletemez.</summary>
    [JsonPropertyName("owner_groups")]
    public IReadOnlyList<string> OwnerGroups { get; init; } = [];

    [JsonPropertyName("source_ids")]
    public IReadOnlyList<string> SourceIds { get; init; } = [];
}

/// <summary>
/// İnceleme isteği (RCA §7). <b>İnceleyen gövdeden gelmiyor</b>, token'dan:
/// aksi halde herkes başkasının adına oy yazabilirdi ve altın küme kimin ne
/// dediğini kaybederdi.
/// </summary>
public sealed record RcaReviewRequest
{
    /// <summary>
    /// <c>correct</c> · <c>wrong</c> · <c>incomplete</c> · <c>unknown</c>.
    ///
    /// <para>
    /// <b>Dördüncü değer bir kaçış kapısı değil, bir ölçüm.</b> "Bilmiyorum"
    /// seçeneği olmasaydı, gerçekten bilmeyen kişi rastgele birini seçerdi ve
    /// altın küme sessizce gürültüyle dolardı — ölçülemez olmaktan kötü,
    /// çünkü ölçülüyormuş gibi görünürdü. <c>unknown</c> doğruluk oranının
    /// <b>paydasına girmiyor</b> ve kendi oranı ayrı bir gösterge: yüksekse
    /// ya kanıt paketi yetersiz ya soru yanlış soruluyor.
    /// </para>
    /// </summary>
    [JsonPropertyName("verdict")]
    public required string Verdict { get; init; }

    /// <summary>
    /// Çelişen kanıt bölümü hakkında ayrı karar:
    /// <c>not_present</c> · <c>sound</c> · <c>trivial</c> · <c>unknown</c>.
    ///
    /// <para>
    /// Ayrı bir soru, çünkü model bu bölümü doldurmak için önemsiz bir şey
    /// uydurabilir ve rapor <b>bütün olarak</b> hâlâ doğru görünebilir.
    /// Varsayılan <c>unknown</c> — değerlendirmeyeni "bölüm yoktu" diyen
    /// biriyle karıştırmamak için.
    /// </para>
    /// </summary>
    [JsonPropertyName("contradicting_evidence")]
    public string ContradictingEvidence { get; init; } = "unknown";

    /// <summary>
    /// Altın kümenin asıl değerli yarısı: "yanlış" demek modeli düzeltmiyor,
    /// <b>doğrusunun ne olduğu</b> düzeltiyor. Zorunlu değil — zorunlu yapmak,
    /// acelesi olanın düğmeye hiç basmamasına yol açardı.
    /// </summary>
    [JsonPropertyName("actual_root_cause")]
    public string ActualRootCause { get; init; } = string.Empty;

    [JsonPropertyName("note")]
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// RCA kanıt paketi ve deterministik rapor uçları (T37).
///
/// <para>
/// <b>Yol adı <c>/v1/rca</c>:</b> ürünün her yerinde kullanılan terim bu.
/// </para>
///
/// <para>
/// <b>Kapsam kapısı okuma yolunda ayrıca uygulanıyor</b> ve bu ucun en kolay
/// gözden kaçacak yanı: paket <b>saklanıyor</b> ve toplandığı kapsamı taşıyor,
/// yani kimlikle okunan bir belge. Sorgu yolundaki filtre burada işe yaramıyor —
/// okuma bir sorgu değil, bir belge getirme. A grubunun kapsamıyla toplanmış bir
/// paketi B grubundan biri isteyebilseydi, içindeki her kanıt satırını görürdü
/// (K17). Kural <c>BundleScope.IsReadableBy</c>'da ve okuyamayan <b>404</b>
/// alıyor: 403 paketin varlığını doğrulardı ve bu tek başına bir sızıntı.
/// </para>
/// </summary>
public static class EvidenceEndpoints
{
    public static IEndpointRouteBuilder MapRca(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/rca")
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithTags("rca");

        // Elle tetikleme. `Read` yetkisi yeterli: koşu yalnızca kullanıcının
        // zaten görebildiği veriyi okuyor ve kapsam kapısından geçiyor.
        group.MapPost("/", GatherAsync)
            .WithName("GatherRca")
            .Produces<RcaReportResponse>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/", ListAsync)
            .WithName("ListRcaBundles")
            .Produces<RcaBundleListResponse>();

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetRcaReport")
            .Produces<RcaReportResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status410Gone);

        // Export **sunucudan** ve ekranla **aynı metinden**. Tarayıcıda ikinci
        // bir biçimlendirici yazmak, ekran ile export'un sessizce ayrışması
        // demek olurdu — ve ayrıştıklarında kimse fark etmezdi, çünkü ikisini
        // yan yana koyan bir şey yok. Aynı `ToMarkdown()`'dan gelirlerse
        // ayrışamazlar.
        group.MapGet("/{id:guid}/export", ExportAsync)
            .WithName("ExportRcaReport")
            .Produces<string>(StatusCodes.Status200OK, "text/markdown")
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status410Gone);

        group.MapPost("/{id:guid}/review", ReviewAsync)
            .WithName("ReviewRcaReport")
            .Produces<RcaReviewResponse>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        return routes;
    }

    private static async Task<IResult> GatherAsync(
        RcaRequest request,
        EvidenceBundleFactory factory,
        EvidenceBundleStore store,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var window = new RcaWindow
        {
            From = request.From,
            To = request.To,
            BaselineFrom = request.BaselineFrom,
            BaselineTo = request.BaselineTo,
            OwnerGroups = request.OwnerGroups,
            SourceIds = request.SourceIds,
        };

        try
        {
            // Pencere doğrulaması sağlayıcılara bırakılmıyor: örtüşen bir taban
            // "ilk-görülen"i tanım gereği boşaltır ve sinyal SESSİZCE hiçbir şey
            // döndürür. `Validate` bunu istisnaya çeviriyor.
            window.Validate();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }

        var bundle = await factory.BuildAsync(window, user.Scope, cancellationToken: cancellationToken);
        await store.SaveAsync(bundle, cancellationToken);

        var report = DeterministicReport.From(bundle);

        return Results.Created(
            $"/v1/rca/{bundle.Id}",
            RcaReportResponse.Of(report, review: null));
    }

    private static async Task<IResult> ListAsync(
        EvidenceBundleStore store,
        CancellationToken cancellationToken,
        int limit = 50)
    {
        var summaries = await store.ListRecentAsync(limit, cancellationToken);

        return Results.Ok(new RcaBundleListResponse(
            [.. summaries.Select(RcaBundleSummaryResponse.Of)]));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        EvidenceBundleStore store,
        GoldenReviewStore reviews,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var (bundle, failure) = await LoadAsync(id, store, user, cancellationToken);

        if (failure is not null)
        {
            return failure;
        }

        // Son inceleme — `ForBundleAsync` en yeniden eskiye sıralı döndürüyor.
        // Tekil DEĞİL ve olmamalı: aynı paketi iki kişi ayrı ayrı
        // inceleyebilir ve ikisinin de kaydı duruyor (F4 "insanlar birbiriyle
        // ne kadar anlaşıyor" sorusunu bunlardan soracak). Ekranın açılışta
        // sorduğu soru "son söz ne" olduğu için burada ilki alınıyor.
        var review = (await reviews.ForBundleAsync(id, user.Scope, cancellationToken))
            .FirstOrDefault();

        return Results.Ok(RcaReportResponse.Of(DeterministicReport.From(bundle!), review));
    }

    private static async Task<IResult> ExportAsync(
        Guid id,
        EvidenceBundleStore store,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var (bundle, failure) = await LoadAsync(id, store, user, cancellationToken);

        if (failure is not null)
        {
            return failure;
        }

        var markdown = DeterministicReport.From(bundle!).ToMarkdown();

        // İndirilebilir dosya: olay sonrası paylaşılan şey rapor, ekran değil.
        return Results.File(
            System.Text.Encoding.UTF8.GetBytes(markdown),
            "text/markdown; charset=utf-8",
            $"rca-{id}.md");
    }

    private static async Task<IResult> ReviewAsync(
        Guid id,
        RcaReviewRequest request,
        EvidenceBundleStore store,
        GoldenReviewStore reviews,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var (_, failure) = await LoadAsync(id, store, user, cancellationToken);

        if (failure is not null)
        {
            return failure;
        }

        // Tel adlarını **tek yer** çözüyor (T38'in `ReviewWire`'ı). Buradaki
        // önceki hâli `Enum.TryParse(ignoreCase: true)` + `Replace("_", "")`
        // idi ve iki şeyi birden yanlış yapıyordu.
        //
        // Sözleşmeden geniş kabul ediyordu: `"Correct"`, `"CORRECT"`,
        // `"notpresent"` geçiyordu. Kimse bunu istemedi; kolay olduğu için
        // oradaydı.
        //
        // Daha sinsisi: `Enum.TryParse` enum'a eklenen bir değeri **telde
        // kendiliğinden kabul eder**. Yarın `ReviewVerdict`'e bir üye
        // eklendiğinde sözleşme kimse karar vermeden büyürdü — `Pending` ve
        // `ExpectedExemptCount`'un var olma sebebinin tam tersi. Açık bir
        // eşleme tablosunda aynı ekleme derlenir ama **tanınmaz**, yani telde
        // görünmesi ayrı ve bilinçli bir hareket olur.
        if (!ReviewWire.TryParseVerdict(request.Verdict, out var verdict))
        {
            return Results.BadRequest(new ErrorResponse(
                $"Tanınmayan karar: '{request.Verdict}'. Beklenen: correct, wrong, incomplete, unknown."));
        }

        if (!ReviewWire.TryParseContradicting(request.ContradictingEvidence, out var contradicting))
        {
            return Results.BadRequest(new ErrorResponse(
                $"Tanınmayan çelişen-kanıt kararı: '{request.ContradictingEvidence}'. "
                + "Beklenen: not_present, sound, trivial, unknown."));
        }

        GoldenReviewEntity review;
        try
        {
            review = await reviews.AddAsync(
                new ReviewInput(
                    BundleId: id,
                    // Kullanıcı tetikli inceleme; alarm kapatma yolu `TriggerId`
                    // taşıyor ve o T38'in ucundan geliyor.
                    TriggerId: null,
                    Verdict: verdict,
                    ContradictingEvidence: contradicting,
                    Note: request.Note,
                    ActualRootCause: request.ActualRootCause),
                // İnceleyen token'dan; gövdeden gelseydi herkes başkasının adına
                // oy yazabilirdi.
                user.Scope,
                cancellationToken);
        }
        catch (ReviewRejectedException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }

        return Results.Created($"/v1/rca/{id}", RcaReviewResponse.Of(review));
    }

    /// <summary>
    /// Paketi kapsam kapısından geçirerek okur.
    ///
    /// <para>
    /// Üç uç da bunu çağırıyor — kapsam kontrolünü her uçta tekrar yazmak, bir
    /// gün birinde unutulmasının en kolay yolu olurdu ve unutulan uç sessizce
    /// başka grubun kanıtını verirdi.
    /// </para>
    ///
    /// <para>
    /// Okunamayan <b>sürüm</b> ayrı bir durum ve <c>410 Gone</c> alıyor: "paket
    /// yok" ile "paket var ama bugünkü kod okuyamıyor" farklı şeyler ve
    /// ikincisini 404 yapmak, veri kaybını bir arama hatası gibi gösterirdi.
    /// </para>
    /// </summary>
    private static async Task<(EvidenceBundle? Bundle, IResult? Failure)> LoadAsync(
        Guid id,
        EvidenceBundleStore store,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        EvidenceBundle? bundle;

        try
        {
            bundle = await store.GetAsync(id, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return (null, Results.Json(
                new ErrorResponse(ex.Message, "Paket okunamayan bir şema sürümüyle yazılmış."),
                statusCode: StatusCodes.Status410Gone));
        }

        if (bundle is null || !bundle.Scope.IsReadableBy(user.Scope))
        {
            // Kapsam dışı paket için de 404: 403 paketin var olduğunu doğrular
            // ve "şu pencerede RCA koşulmuş" bilgisi tek başına bir sızıntı.
            return (null, Results.NotFound(new ErrorResponse($"Kanıt paketi bulunamadı: {id}.")));
        }

        return (bundle, null);
    }
}
