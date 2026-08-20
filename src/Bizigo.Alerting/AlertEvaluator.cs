using System.Globalization;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.Extensions.Logging;

namespace Bizigo.Alerting;

/// <param name="SourceId">Sessizlik tipinde dolu; eşik ve oranda boş (sonuç toplu).</param>
/// <param name="Value">Tetikleyen değer: sayı, katsayı ya da susma saniyesi.</param>
public sealed record AlertHit(
    string OwnerGroup,
    string SourceId,
    double Value,
    double Threshold,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    string Summary);

/// <param name="Hits">Tetiklenmeler. Boş liste "tetiklenmedi" demek — hata demek değil.</param>
public sealed record AlertOutcome(AlertRunState State, IReadOnlyList<AlertHit> Hits, string Error)
{
    public static AlertOutcome Quiet() => new(AlertRunState.Quiet, [], string.Empty);

    public static AlertOutcome Fired(IReadOnlyList<AlertHit> hits) =>
        hits.Count == 0 ? Quiet() : new AlertOutcome(AlertRunState.Fired, hits, string.Empty);
}

/// <summary>
/// Üç kural tipi, <b>tek</b> değerlendirici (K32, T21).
///
/// <para>
/// Tek olmasının sebebi estetik değil: kapsam zorlaması, zaman aşımı semantiği
/// ve pencere hesabı üç tipte de aynı ve üç ayrı sınıfta ayrışırlardı. Tiplerin
/// ayrıştığı yer yalnızca "hangi soruyu soruyorum" — o da aşağıda üç metot.
/// </para>
///
/// <para>
/// <b>Zaman aşımı burada bir sonuç değil, bir durum.</b> F1'in en pahalı dersi
/// duvar saati bütçesinin ölçmek istediğin şeyi ölçmemesiydi ve üç ayrı yerde
/// aynı belirtiyi verdi: bütçe dolduğunda kod "cevap yok" ile "cevap hayır"ı
/// birbirine karıştırıyordu. Burada zaman aşımı <see cref="AlertRunState.TimedOut"/>
/// üretiyor, <see cref="AlertRunState.Quiet"/> değil — yani "alarm yok" cevabı
/// asla bir zaman aşımından türemiyor.
/// </para>
/// </summary>
public sealed class AlertEvaluator(
    AlertingOptions options,
    AlertingStats stats,
    ILogger<AlertEvaluator> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>
    /// Zaman aşımı sayacı da <b>saatten</b> geliyor, <c>CancelAfter</c>'ın
    /// gizli zamanlayıcısından değil.
    ///
    /// <para>
    /// Fark testte ortaya çıkıyor: "zaman aşımı Quiet değil TimedOut üretiyor"
    /// bekçisi, aksi hâlde gerçekten bir saniye <b>beklemek</b> zorunda kalırdı
    /// ve F1'in beş kez ödediği hatanın altıncısı olurdu. Saat enjekte edilince
    /// bekleme bir sinyale dönüşüyor.
    /// </para>
    /// </summary>
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Kuralın kaydedilmiş kapsamını <see cref="AccessScope"/>'a çeviriyor.
    ///
    /// <para>
    /// <b>Tek dönüşüm noktası ve sınırsız kapsam üretemiyor.</b> Kural yazılırken
    /// gruplar açıkça saklandığı için burada "sahibinin bugünkü kapsamı" diye bir
    /// arama yok; olsaydı sahibi ekip değiştirdiğinde kural sessizce başka
    /// ekibin verisini saymaya başlardı.
    /// </para>
    /// </summary>
    public static AccessScope ScopeOf(AlertRuleEntity rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var groups = rule.OwnerGroups
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return AccessScope.ForGroups($"alert-rule:{rule.Id}", groups);
    }

    public async Task<AlertOutcome> EvaluateAsync(
        AlertRuleEntity rule,
        AlertEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(context);

        stats.Evaluate();

        var scope = ScopeOf(rule);

        // Kapsamsız kural hiçbir şey sayamaz. Sorguya çıkmadan durmak sadece
        // maliyet değil, doğruluk meselesi: boş kapsam "her şey"e düşmemeli (K17).
        if (scope.IsEmpty)
        {
            return new AlertOutcome(AlertRunState.Failed, [],
                "Kuralın kapsamı boş; hiçbir grup tanımlı değil.");
        }

        using var budget = new CancellationTokenSource(
            TimeSpan.FromSeconds(options.EvaluationTimeoutSeconds), _time);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);

        try
        {
            return rule.RuleType switch
            {
                AlertRuleType.Threshold => await EvaluateThresholdAsync(rule, scope, context, timeout.Token)
                    .ConfigureAwait(false),
                AlertRuleType.Ratio => await EvaluateRatioAsync(rule, scope, context, timeout.Token)
                    .ConfigureAwait(false),
                AlertRuleType.Silence => await EvaluateSilenceAsync(rule, scope, context)
                    .ConfigureAwait(false),
                _ => new AlertOutcome(AlertRunState.Failed, [], $"Bilinmeyen kural tipi: {rule.RuleType}."),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Dışarıdaki jeton iptal edilmediyse süreyi dolduran BİZİM
            // bütçemizdi. Bu bir cevap değil, cevabın alınamaması.
            stats.TimeOut();
            logger.LogWarning(
                "Alarm kuralı zaman aşımına uğradı: {Rule} ({Seconds} sn). Sonuç bilinmiyor.",
                rule.Name,
                options.EvaluationTimeoutSeconds);

            return new AlertOutcome(AlertRunState.TimedOut, [],
                $"Değerlendirme {options.EvaluationTimeoutSeconds} sn içinde bitmedi.");
        }
#pragma warning disable CA1031 // Tek bir bozuk kural motoru durduramaz; durum kayda geçiyor.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            stats.Fail();
            logger.LogError(ex, "Alarm kuralı değerlendirilemedi: {Rule}.", rule.Name);
            return new AlertOutcome(AlertRunState.Failed, [], $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Sayı bir sınırı aştı mı.</summary>
    private static async Task<AlertOutcome> EvaluateThresholdAsync(
        AlertRuleEntity rule,
        AccessScope scope,
        AlertEvaluationContext context,
        CancellationToken cancellationToken)
    {
        var to = context.Now;
        var from = to - TimeSpan.FromSeconds(rule.WindowSeconds);

        var count = await context
            .CountAsync(BuildQuery(rule, from, to), scope, cancellationToken)
            .ConfigureAwait(false);

        if (!Matches(count, rule.Threshold, rule.Comparison))
        {
            return AlertOutcome.Quiet();
        }

        return AlertOutcome.Fired([new AlertHit(
            rule.OwnerGroups,
            string.Empty,
            count,
            rule.Threshold,
            from,
            to,
            string.Create(CultureInfo.InvariantCulture,
                $"{Describe(rule.WindowSeconds)} içinde {count} olay — sınır {Symbol(rule.Comparison)} {rule.Threshold:0.##}."))]);
    }

    /// <summary>
    /// Değişim hızlandı mı: pencere, kendinden bir önceki eşit uzunluktaki
    /// pencereye göre.
    ///
    /// <para>
    /// <b>Taban bire yuvarlanıyor.</b> Sıfır tabanla oran tanımsız; "3× arttı"
    /// hiçlikten bir şeye geçiş için doğru cümle değil. Bölmeyi reddetmek ise
    /// 0 → 10.000 sıçramasını görünmez yapardı, ki bu tam da görülmesi gereken
    /// şey. Tabanı bire çekmek ikisinin arasını buluyor ve süreksizlik
    /// üretmiyor: 0 → 1 katsayı 1 veriyor, 0 → 10.000 katsayı 10.000.
    /// </para>
    /// </summary>
    private static async Task<AlertOutcome> EvaluateRatioAsync(
        AlertRuleEntity rule,
        AccessScope scope,
        AlertEvaluationContext context,
        CancellationToken cancellationToken)
    {
        var window = TimeSpan.FromSeconds(rule.WindowSeconds);
        var to = context.Now;
        var from = to - window;
        var baselineFrom = from - window;

        var current = await context
            .CountAsync(BuildQuery(rule, from, to), scope, cancellationToken)
            .ConfigureAwait(false);

        var baseline = await context
            .CountAsync(BuildQuery(rule, baselineFrom, from), scope, cancellationToken)
            .ConfigureAwait(false);

        var ratio = current / (double)Math.Max(baseline, 1);

        if (!Matches(ratio, rule.Threshold, rule.Comparison))
        {
            return AlertOutcome.Quiet();
        }

        return AlertOutcome.Fired([new AlertHit(
            rule.OwnerGroups,
            string.Empty,
            ratio,
            rule.Threshold,
            from,
            to,
            string.Create(CultureInfo.InvariantCulture,
                $"{Describe(rule.WindowSeconds)} içinde {current} olay, önceki eşit pencerede {baseline} — " +
                $"{ratio:0.##}× (sınır {Symbol(rule.Comparison)} {rule.Threshold:0.##}×)."))]);
    }

    /// <summary>
    /// Beklenen veri gelmedi mi — <b>en zoru ve en değerlisi</b>.
    ///
    /// <para>
    /// Diğer iki tip verinin varlığı üzerinde çalışıyor; bu, yokluğu üzerinde.
    /// Olay tablosu var olmayan bir şeyi listeleyemeyeceği için cevap tek bir
    /// sorgudan çıkmıyor: <b>envanter</b> (ne beklemeliyiz) ile <b>etkinlik</b>
    /// (ne geldi) arasındaki fark alınıyor. İkisi de tur boyunca paylaşılıyor,
    /// yani elli sessizlik kuralı elli sorgu değil.
    /// </para>
    ///
    /// <para>
    /// <b>Yeni kaynağa mühlet var.</b> Hiç veri göndermemiş bir kaynak, envantere
    /// gireli eşik kadar süre geçmediyse susmuş sayılmıyor. Olmasaydı her yeni
    /// cihaz kaydı, eklendiği dakika alarm üretirdi ve motor eklenen ilk gün
    /// güvenilirliğini kaybederdi.
    /// </para>
    /// </summary>
    /// <summary>
    /// Ne kadar zamandır haber alınmadı — <b>saat kaymasını ayırarak</b> (T27).
    ///
    /// <para>
    /// <c>now - seen</c> <b>negatif çıkabiliyor</b>: son <c>ingested_at</c>,
    /// değerlendiricinin şimdisinden ileride olabilir. Sebebi ingest eden makine
    /// ile değerlendiren makine arasındaki saat farkı — bu üründe ikisi ayrı
    /// süreç ve ayrı makine olabiliyor.
    /// </para>
    ///
    /// <para>
    /// <b>Eski davranış:</b> negatif değer eşiğin altında kaldığı için kaynak
    /// sessizce atlanıyordu. Bu bir "hiç tetiklemez" değil, <b>gecikme</b>: saat
    /// farkı kapanana kadar susma süresi eşiğe ulaşmıyor. Yine de sinsi, çünkü
    /// susan bir cihaz o süre boyunca izlenmiyor ve <b>hiçbir yerde belirti
    /// yok</b> — alarmın var olma sebebinin tersi.
    /// </para>
    ///
    /// <para>
    /// <b>Karar: davranış aynı kalıyor, kayma görünür oluyor.</b> Kaynak bu
    /// turda yine susmuş sayılmıyor — <c>ingested_at</c> ileride olduğu sürece
    /// susma iddiasını destekleyen bir kanıt yok. Değişen tek şey, bunun artık
    /// <b>sayılıyor ve günlüğe düşüyor</b> olması.
    /// </para>
    ///
    /// <para>
    /// <b>Gecikme sınırlanmadı ve sınırlanamaz — ölçüldü.</b> İlk tasarımda
    /// "kaymayı sıfıra kırparsak gecikme eşik kadar olur" yazmıştım; <b>yanlış</b>.
    /// Değerlendirici tur başına durumsuz: her turda aynı <c>seen</c> değerinden
    /// yeniden hesaplıyor, dolayısıyla kırpmak alarmın ne zaman tetikleneceğini
    /// değiştirmiyor. Gecikmeyi sınırlamak "kaymayı ilk ne zaman gördük"
    /// bilgisini turlar arasında saklamayı gerektirir; bu ayrı bir tasarım
    /// kararı ve burada verilmedi.
    /// </para>
    ///
    /// <para>
    /// Sıfıra kırpma yine de duruyor ama <b>savunma amaçlı</b>: negatif bir
    /// süre aşağı akışa (<c>AlertHit.Value</c>) hiç sızmasın. Davranışı taşıyan
    /// şey kırpma değil, eşik karşılaştırması.
    /// </para>
    ///
    /// <para>
    /// <b>Neden <c>CreatedAt</c>'e düşülmedi:</b> o, kaynağı "envantere gireli
    /// beri hiç konuşmamış" saymak demek ve saati kayan her kaynak için anında
    /// bir alarm sağanağı üretirdi — bir veri kalitesi sorununu yanlış alarma
    /// çevirmek, izleme boşluğundan iyi değil.
    /// </para>
    /// </summary>
    private TimeSpan Since(DateTimeOffset now, DateTimeOffset seen, string sourceId)
    {
        var since = now - seen;

        if (since >= TimeSpan.Zero)
        {
            return since;
        }

        stats.ClockSkewedSource();

        logger.LogWarning(
            "'{SourceId}' kaynağının son ingest zamanı şimdiden {Skew} ileride: " +
            "ingest eden ile değerlendiren makinenin saatleri ayrışmış. " +
            "Sessizlik alarmı bu kaynak için eşik kadar gecikiyor.",
            sourceId,
            -since);

        return TimeSpan.Zero;
    }

    private async Task<AlertOutcome> EvaluateSilenceAsync(
        AlertRuleEntity rule,
        AccessScope scope,
        AlertEvaluationContext context)
    {
        var search = AlertSearchCodec.Deserialize(rule.SearchJson);
        var threshold = TimeSpan.FromSeconds(rule.SilenceSeconds);
        var now = context.Now;

        var inventory = await context.InventoryAsync(scope).ConfigureAwait(false);
        var activity = await context.ActivityAsync(scope).ConfigureAwait(false);

        var lastSeen = activity.ToDictionary(
            static a => a.SourceId,
            // `LastIngestedAt`: soru "cihazın saatine göre en son ne zaman"
            // değil, "ondan en son ne zaman haber aldık". Saati kayan bir
            // kaynak aksi halde susmuş görünürdü.
            static a => a.LastIngestedAt,
            StringComparer.Ordinal);

        var watched = search.SourceIds.Count == 0
            ? (IReadOnlySet<string>?)null
            : search.SourceIds.ToHashSet(StringComparer.Ordinal);

        var hits = new List<AlertHit>();

        foreach (var source in inventory)
        {
            if (!source.Enabled || (watched is not null && !watched.Contains(source.SourceId)))
            {
                continue;
            }

            var since = lastSeen.TryGetValue(source.SourceId, out var seen)
                ? Since(now, seen, source.SourceId)
                : now - source.CreatedAt;

            if (since < threshold)
            {
                continue;
            }

            var never = !lastSeen.ContainsKey(source.SourceId);

            // Bildirimdeki bağlantının açacağı aralık: <b>son görülmeden şimdiye</b>.
            // Sabit bir geriye bakış penceresi kullanmak, kullanıcıyı susmanın
            // başladığı ana değil rastgele bir noktaya götürürdü — "bir şey oldu"
            // ile "şuna bak" arasındaki farkı belirleyen tam olarak bu.
            // Geriye bakışla sınırlanıyor: hiç veri göndermemiş bir kaynakta
            // aralık aksi hâlde envantere giriş anına, yani günlere uzardı.
            var span = since < context.SilenceLookback ? since : context.SilenceLookback;

            hits.Add(new AlertHit(
                source.OwnerGroup,
                source.SourceId,
                since.TotalSeconds,
                threshold.TotalSeconds,
                now - span,
                now,
                never
                    ? $"'{source.SourceId}' envantere gireli {Describe((int)since.TotalSeconds)} oldu ve hiç veri göndermedi."
                    : $"'{source.SourceId}' {Describe((int)since.TotalSeconds)} sessiz (eşik {Describe(rule.SilenceSeconds)})."));
        }

        return AlertOutcome.Fired(hits);
    }

    private static EventQuery BuildQuery(AlertRuleEntity rule, DateTimeOffset from, DateTimeOffset to)
    {
        var search = AlertSearchCodec.Deserialize(rule.SearchJson);

        return new EventQuery
        {
            From = from,
            To = to,
            FullText = search.FullText,
            Filters = search.Filters,
            SourceIds = search.SourceIds,
            ParseStatuses = search.ParseStatuses,

            // `OwnerGroups` BİLEREK boş: kapsam daraltması kuralın kendi
            // kapsamından geliyor ve `AccessScope` zaten AND'leniyor. Burada
            // ayrıca grup yazmak, kapsamın iki yerden gelmesi demek olurdu.
        };
    }

    private static bool Matches(double value, double threshold, AlertComparison comparison) => comparison switch
    {
        AlertComparison.GreaterThan => value > threshold,
        AlertComparison.GreaterThanOrEqual => value >= threshold,
        AlertComparison.LessThan => value < threshold,
        AlertComparison.LessThanOrEqual => value <= threshold,
        _ => false,
    };

    private static string Symbol(AlertComparison comparison) => comparison switch
    {
        AlertComparison.GreaterThan => ">",
        AlertComparison.GreaterThanOrEqual => "≥",
        AlertComparison.LessThan => "<",
        AlertComparison.LessThanOrEqual => "≤",
        _ => "?",
    };

    /// <summary>Saniyeyi insanın okuyacağı hâle getiriyor; mesajın yarısı bu.</summary>
    internal static string Describe(int seconds) => seconds switch
    {
        < 60 => $"{seconds} sn",
        < 3600 => $"{seconds / 60} dk",
        < 86400 => $"{seconds / 3600} sa",
        _ => $"{seconds / 86400} gün",
    };
}
