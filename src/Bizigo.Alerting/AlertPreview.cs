using Bizigo.Contracts;
using Bizigo.ControlPlane;

namespace Bizigo.Alerting;

/// <param name="At">Kovanın başlangıcı.</param>
/// <param name="Count">Kovadaki olay sayısı.</param>
/// <param name="Value">
/// Kural tipinin baktığı değer: eşikte sayının kendisi, oranda bir önceki kovaya
/// göre katsayı. Eşik <b>uygulanmamış</b> hâli.
/// </param>
public sealed record PreviewPoint(DateTimeOffset At, long Count, double Value);

/// <param name="Gaps">
/// Bu kaynağın penceredeki sessizlik süreleri (saniye). Eşik uygulanmamış:
/// kullanıcı eşiği değiştirdiğinde kaç tanesinin aşıldığı aynı listeden
/// yeniden sayılıyor, yeni bir sorgu atılmıyor.
/// </param>
public sealed record PreviewSource(
    string SourceId,
    string OwnerGroup,
    DateTimeOffset? LastSeen,
    IReadOnlyList<double> Gaps);

/// <param name="FiringCount">
/// Gönderilen eşikle hesaplanmış tetiklenme sayısı — kolaylık için. Asıl cevap
/// <see cref="Points"/> ve <see cref="Sources"/>; ekran eşiği değiştirdiğinde
/// bu sayıyı kendisi yeniden hesaplıyor.
/// </param>
/// <param name="Note">Önizlemenin sınırladığı yerler; ekran bunu kullanıcıya gösteriyor.</param>
public sealed record AlertPreviewResult(
    AlertRuleType RuleType,
    DateTimeOffset From,
    DateTimeOffset To,
    int BucketSeconds,
    double Threshold,
    int FiringCount,
    IReadOnlyList<PreviewPoint> Points,
    IReadOnlyList<PreviewSource> Sources,
    string Note);

/// <summary>
/// Kural önizlemesi (T23): "bu kural son 24 saatte kaç kez tetiklenirdi".
///
/// <para>
/// <b>Ticket'ın taşıyıcı maddesi ve sebebi K16.</b> Elli kişilik bir kurumda
/// eşiğini görmeden yazılan kural ya hiç tetiklenmiyor ya herkesi boğuyor;
/// önizleme, gürültüyü kural üretime girmeden kesen tek mekanizma.
/// </para>
///
/// <para>
/// <b>Tasarımın taşıyıcı kararı: eşik burada uygulanmıyor.</b> Önizleme
/// ClickHouse'a <b>bir kez</b> gidiyor ve eşikten bağımsız bir histogram alıyor;
/// kullanıcı eşiği değiştirdiğinde ekran aynı veriyi yeniden yorumluyor. Aksi
/// tasarımda kaydırıcıyı sürükleyen tek bir kullanıcı saniyede onlarca ağır
/// sorgu üretirdi — yani önizleme, önlemeye çalıştığı sorunun kendisi olurdu.
/// </para>
/// </summary>
public sealed class AlertPreview(IAlertQuerySource queries)
{
    /// <summary>Önizlemenin bakabileceği en uzun geçmiş.</summary>
    public static readonly TimeSpan MaxLookback = TimeSpan.FromDays(7);

    /// <summary>Varsayılan geçmiş — ticket'ın sorduğu "son 24 saat".</summary>
    public static readonly TimeSpan DefaultLookback = TimeSpan.FromHours(24);

    /// <summary>
    /// Sessizlik önizlemesinde en fazla kaç kaynak gösterilir.
    ///
    /// <para>
    /// Sınır yanıtın boyutu için: binlerce kaynaklı bir kapsamda kova başına
    /// kaynak serisi döndürmek, tarayıcıyı önizleme ekranında kilitler. Kesilen
    /// kaynak sayısı <see cref="AlertPreviewResult.Note"/> ile bildiriliyor —
    /// sessizce kırpmak, kullanıcıya eksik bir tabloyu tam sanmak olurdu.
    /// </para>
    /// </summary>
    public const int MaxSources = 200;

    public async Task<AlertPreviewResult> RunAsync(
        AlertRuleInput input,
        AccessScope scope,
        TimeSpan? lookback,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(scope);

        var window = Clamp(lookback ?? DefaultLookback);
        var from = now - window;

        // Kuralın kapsamı, kullanıcının kapsamıyla KESİŞTİRİLİYOR. Kural henüz
        // kaydedilmediği için doğrulamadan geçmemiş olabilir; önizlemenin
        // kaydetmeden daha geniş veri göstermesi, kapsam ayrımını en kolay
        // atlatılacak yerden delerdi.
        var effective = Intersect(input.OwnerGroups, scope);

        if (effective.IsEmpty)
        {
            return new AlertPreviewResult(
                input.RuleType, from, now, input.WindowSeconds, input.Threshold, 0, [], [],
                "Kuralın kapsamı sizin kapsamınızla kesişmiyor; önizlenecek veri yok.");
        }

        return input.RuleType switch
        {
            AlertRuleType.Silence => await PreviewSilenceAsync(input, effective, from, now, cancellationToken)
                .ConfigureAwait(false),
            _ => await PreviewCountingAsync(input, effective, from, now, cancellationToken)
                .ConfigureAwait(false),
        };
    }

    /// <summary>Eşik ve oran: kova başına sayım, sonra eşik karşılaştırması.</summary>
    private async Task<AlertPreviewResult> PreviewCountingAsync(
        AlertRuleInput input,
        AccessScope scope,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var bucket = Math.Max(input.WindowSeconds, 1);

        using var lease = queries.Lease();

        var buckets = await lease.Query.GetEventHistogramAsync(
            new EventHistogramQuery
            {
                From = from,
                To = to,
                BucketSeconds = bucket,
                FullText = input.Search.FullText,
                Filters = input.Search.Filters,
                SourceIds = input.Search.SourceIds,
            },
            scope,
            cancellationToken).ConfigureAwait(false);

        // Boş kovalar SQL'den dönmüyor — "hiç olay yok" da bir cevap ve oran
        // hesabında taban olarak lazım. Seriyi burada tamamlıyoruz.
        var series = Densify(buckets, from, to, bucket);
        var points = ToPoints(series, input.RuleType);

        var note = buckets.Count == 0
            ? "Seçilen pencerede hiç olay yok; eşik ne olursa olsun kural tetiklenmezdi."
            : string.Empty;

        return new AlertPreviewResult(
            input.RuleType,
            from,
            to,
            bucket,
            input.Threshold,
            CountFirings(points, input.Threshold, input.Comparison),
            points,
            [],
            note);
    }

    /// <summary>
    /// Sessizlik: kaynak başına <b>boşluklar</b>.
    ///
    /// <para>
    /// Diğer iki tip gibi tek bir sayı üretmiyor, çünkü sorulan şey de tek bir
    /// sayı değil: "hangi kaynak ne kadar sustu". Boşluk listesi eşikten bağımsız
    /// olduğu için eşik değiştiğinde yeniden sayılabiliyor — histogram kararının
    /// aynısı, kaynak ekseninde.
    /// </para>
    /// </summary>
    private async Task<AlertPreviewResult> PreviewSilenceAsync(
        AlertRuleInput input,
        AccessScope scope,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        // Kova, eşiğin onda biri: daha kaba bakmak sessizlik süresini eşiğin
        // kendisi kadar yanlış ölçerdi, daha ince bakmak kova sınırına takılır.
        var bucket = Math.Clamp(input.SilenceSeconds / 10, 60, 3600);

        using var lease = queries.Lease();

        var inventory = await lease.Query.SearchSourcesAsync(scope, cancellationToken).ConfigureAwait(false);

        var buckets = await lease.Query.GetEventHistogramAsync(
            new EventHistogramQuery
            {
                From = from,
                To = to,
                BucketSeconds = bucket,
                SourceIds = input.Search.SourceIds,
                GroupBySource = true,
            },
            scope,
            cancellationToken).ConfigureAwait(false);

        var seen = buckets
            .GroupBy(b => b.SourceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(b => b.Start).Order().ToArray(), StringComparer.Ordinal);

        var watched = input.Search.SourceIds.Count == 0
            ? (IReadOnlySet<string>?)null
            : input.Search.SourceIds.ToHashSet(StringComparer.Ordinal);

        var candidates = inventory
            .Where(s => s.Enabled && (watched is null || watched.Contains(s.SourceId)))
            .OrderBy(s => s.SourceId, StringComparer.Ordinal)
            .ToArray();

        var sources = new List<PreviewSource>(Math.Min(candidates.Length, MaxSources));

        foreach (var source in candidates.Take(MaxSources))
        {
            var stamps = seen.TryGetValue(source.SourceId, out var found) ? found : [];

            // Kaynak envantere pencereden SONRA girdiyse, girmeden önceki
            // sessizliği ona yazmak yanlış olurdu.
            var start = source.CreatedAt > from ? source.CreatedAt : from;

            sources.Add(new PreviewSource(
                source.SourceId,
                source.OwnerGroup,
                stamps.Length > 0 ? stamps[^1] : null,
                Gaps(stamps, start, to, bucket)));
        }

        var truncated = candidates.Length - sources.Count;
        var note = truncated > 0
            ? $"{candidates.Length} kaynaktan ilk {MaxSources} tanesi gösteriliyor; {truncated} kaynak listede yok."
            : string.Empty;

        var threshold = input.SilenceSeconds;

        return new AlertPreviewResult(
            AlertRuleType.Silence,
            from,
            to,
            bucket,
            threshold,
            sources.Sum(s => s.Gaps.Count(g => g >= threshold)),
            [],
            sources,
            note);
    }

    /// <summary>
    /// Boş kovaları doldurur — saf fonksiyon.
    ///
    /// <para>
    /// SQL yalnızca olay olan kovaları döndürüyor. "Hiç olay yok" da bir cevap ve
    /// özellikle oran tipinde <b>taban</b> olarak gerekiyor: eksik bir kovayı
    /// atlamak, iki gerçek kovanın yan yana sayılması yani uydurma bir oran demek.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<(DateTimeOffset At, long Count)> Densify(
        IReadOnlyList<HistogramBucket> buckets,
        DateTimeOffset from,
        DateTimeOffset to,
        int bucketSeconds)
    {
        var width = TimeSpan.FromSeconds(bucketSeconds);
        var counts = buckets
            .GroupBy(b => b.Start)
            .ToDictionary(g => g.Key, g => g.Sum(b => b.Count));

        // Sunucu kovaları epoch'a hizalıyor (`toStartOfInterval`); seriyi de aynı
        // hizada üretmezsek anahtarlar hiç tutmaz ve her kova boş görünürdü.
        var first = Align(from, width);
        var series = new List<(DateTimeOffset, long)>();

        for (var at = first; at < to; at += width)
        {
            series.Add((at, counts.TryGetValue(at, out var count) ? count : 0));
        }

        return series;
    }

    private static DateTimeOffset Align(DateTimeOffset value, TimeSpan width)
    {
        var seconds = value.ToUnixTimeSeconds();
        var size = (long)width.TotalSeconds;
        return DateTimeOffset.FromUnixTimeSeconds(seconds - (seconds % size));
    }

    private static IReadOnlyList<PreviewPoint> ToPoints(
        IReadOnlyList<(DateTimeOffset At, long Count)> series,
        AlertRuleType type)
    {
        var points = new List<PreviewPoint>(series.Count);

        for (var i = 0; i < series.Count; i++)
        {
            var (at, count) = series[i];

            // Oran, değerlendiricinin kullandığı hesabın aynısı: taban bire
            // yuvarlanıyor. İki yerde farklı hesaplasaydık önizleme, motorun
            // yapacağından başka bir şey vaat ederdi.
            var value = type == AlertRuleType.Ratio
                ? count / (double)Math.Max(i > 0 ? series[i - 1].Count : 0, 1)
                : count;

            points.Add(new PreviewPoint(at, count, value));
        }

        // Oran tipinde ilk kovanın tabanı yok; onu saymak "hiçten bir şeye"
        // geçişi tetiklenme sayardı.
        return type == AlertRuleType.Ratio && points.Count > 0 ? points[1..] : points;
    }

    /// <summary>Eşik karşılaştırması — motorunkiyle aynı kapalı küme.</summary>
    internal static int CountFirings(
        IReadOnlyList<PreviewPoint> points,
        double threshold,
        AlertComparison comparison) =>
        points.Count(p => comparison switch
        {
            AlertComparison.GreaterThan => p.Value > threshold,
            AlertComparison.GreaterThanOrEqual => p.Value >= threshold,
            AlertComparison.LessThan => p.Value < threshold,
            AlertComparison.LessThanOrEqual => p.Value <= threshold,
            _ => false,
        });

    /// <summary>
    /// Bir kaynağın penceredeki sessizlik süreleri — saf fonksiyon.
    ///
    /// <para>
    /// Kova çözünürlüğü bir alt sınır getiriyor: kova genişliğinden kısa bir
    /// boşluk görünmüyor. Bu, önizlemenin bilinen ve <b>kabul edilen</b>
    /// hassasiyeti; kova zaten eşiğin onda biri seçiliyor, yani hata payı
    /// eşiğin %10'u.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<double> Gaps(
        IReadOnlyList<DateTimeOffset> stamps,
        DateTimeOffset from,
        DateTimeOffset to,
        int bucketSeconds)
    {
        var width = TimeSpan.FromSeconds(bucketSeconds);
        var gaps = new List<double>();
        var cursor = from;

        foreach (var stamp in stamps)
        {
            if (stamp > cursor)
            {
                gaps.Add((stamp - cursor).TotalSeconds);
            }

            // Kovanın SONU: kova içindeki son olayın tam anını bilmiyoruz ve
            // kovanın başını almak her boşluğu bir kova kadar uzun gösterirdi.
            var end = stamp + width;
            if (end > cursor)
            {
                cursor = end;
            }
        }

        // Penceresinin sonuna kadar süren sessizlik de bir boşluk — üstelik
        // alarm açısından en önemlisi, çünkü hâlâ sürüyor.
        if (to > cursor)
        {
            gaps.Add((to - cursor).TotalSeconds);
        }

        return gaps;
    }

    private TimeSpan Clamp(TimeSpan requested)
    {
        if (requested <= TimeSpan.Zero)
        {
            return DefaultLookback;
        }

        return requested > MaxLookback ? MaxLookback : requested;
    }

    /// <summary>
    /// Kuralın istediği gruplarla kullanıcının kapsamının kesişimi.
    ///
    /// <para>
    /// Kesişim, birleşim değil: önizleme henüz kaydedilmemiş bir kuralı
    /// çalıştırıyor, yani <see cref="AlertRuleService"/>'in doğrulamasından
    /// geçmemiş olabilir. Kesişim almazsak kullanıcı, kaydedemeyeceği bir kuralı
    /// önizleyerek kapsamı dışındaki veriyi sayabilirdi.
    /// </para>
    /// </summary>
    internal static AccessScope Intersect(IReadOnlyList<string> requested, AccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(scope);

        var groups = requested
            .Select(static g => g.Trim())
            .Where(static g => g.Length > 0)
            .ToArray();

        if (groups.Length == 0)
        {
            // Grup seçilmemişse kullanıcının kendi kapsamı — form daha
            // doldurulmadan da önizleme anlamlı bir şey göstersin diye.
            return scope;
        }

        return scope.IsUnrestricted
            ? AccessScope.ForGroups(scope.Subject, groups)
            : AccessScope.ForGroups(scope.Subject, groups.Where(scope.OwnerGroups.Contains));
    }
}
