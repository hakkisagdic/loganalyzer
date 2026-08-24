using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bizigo.Alerting;

/// <summary>Bir <see cref="AlertSchedulerWorker.RunTurnAsync"/> turunun sonucu.</summary>
public enum AlertTurn
{
    /// <summary>Motor kapalı.</summary>
    Disabled,

    /// <summary>Vadesi gelmiş kural yoktu.</summary>
    Idle,

    /// <summary>En az bir kural ele alındı.</summary>
    Evaluated,
}

/// <summary>
/// Kural zamanlayıcısı (T21).
///
/// <para>
/// <b><see cref="RunTurnAsync"/> neden public:</b> F1'de aynı sınıftan bir işçi,
/// testlerin arka plan görevini başlatıp sonucu <b>yoklaması</b> yüzünden en
/// pahalı hatayı üretti — aynı commit CI'da 14 saniye, yerelde 6,5 dakika sürdü
/// ve her koşumda başka bir test düştü. Çözüm zamanlamayı ayarlamak değil
/// denklemden çıkarmaktı. Burada da tur doğrudan çağrılabiliyor:
/// <see cref="ExecuteAsync"/> tam olarak bunu çağırıyor, yani test ile üretim
/// aynı kodu koşuyor ve test hiçbir şey beklemiyor.
/// </para>
///
/// <para>
/// <b>Değerlendirme paralel, yazma sıralı.</b> Okuma yolu kapsam başına
/// paylaşılıyor ve eşzamanlılık kapısından geçiyor (K16); sonuçların kontrol
/// düzlemine yazılması ise tek <c>DbContext</c> üzerinde tek tek yapılıyor.
/// Aksi hâlde EF'in eşzamanlı erişim istisnası, belirtisi "alarmlar bazen
/// çalışmıyor" olan bir hataya dönüşürdü.
/// </para>
/// </summary>
public sealed class AlertSchedulerWorker(
    AlertingOptions options,
    IDbContextFactory<ControlPlaneDbContext> factory,
    IAlertQuerySource queries,
    AlertEvaluator evaluator,
    AlertingStats stats,
    ILogger<AlertSchedulerWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Sessizlik değerlendirmesinin geriye bakış penceresi — saf fonksiyon.
    ///
    /// <para>
    /// Kuralın eşiğinin <b>iki katı</b> alınıyor: tam eşik kadar bakmak, sınırın
    /// hemen altındaki bir kaynağı "hiç görülmemiş" gösterirdi ve o kaynak için
    /// susma süresi envantere giriş anından hesaplanırdı — yani günlerce.
    /// </para>
    /// </summary>
    public static TimeSpan SilenceLookbackFor(
        IEnumerable<AlertRuleEntity> rules,
        AlertingOptions options)
    {
        var widest = rules
            .Where(r => r.RuleType == AlertRuleType.Silence)
            .Select(r => TimeSpan.FromSeconds(Math.Max(r.SilenceSeconds, 1) * 2L))
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();

        return widest > options.SilenceLookback ? widest : options.SilenceLookback;
    }

    /// <summary>
    /// Bir sonraki koşum anı — saf fonksiyon.
    ///
    /// <para>
    /// Tekrar aralığı yüzünden bastırılan kural, aralığın sonuna kadar
    /// <b>hiç sorgulanmıyor</b>. K16'nın uyarısı burada işliyor: bastırılacağı
    /// baştan belli olan bir değerlendirme için ClickHouse'a gitmek, tek kötü
    /// kuralın maliyetini tekrar tekrar ödemek demek.
    /// </para>
    /// </summary>
    public static DateTimeOffset NextRunAt(
        AlertRuleEntity rule,
        DateTimeOffset now,
        SuppressionReason reason)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var next = now + TimeSpan.FromSeconds(Math.Max(rule.IntervalSeconds, 1));

        if (reason == SuppressionReason.RepeatInterval && rule.LastFiredAt is { } last)
        {
            var resume = last + TimeSpan.FromSeconds(rule.RepeatIntervalSeconds);
            if (resume > next)
            {
                next = resume;
            }
        }

        return next;
    }

    /// <summary>Döngünün <b>tek turu</b>: vadesi gelenleri bul, değerlendir, sonucu yaz.</summary>
    public async Task<AlertTurn> RunTurnAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return AlertTurn.Disabled;
        }

        var now = _time.GetUtcNow();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var due = await db.AlertRules
            // `Status == Enabled` — yani `Disabled` DE `Gated` DE sorgu üretmiyor.
            //
            // İkisi aynı sonucu veriyor ama aynı şey değil ve kriter ikisini
            // ayrı sınamak zorunda: `Gated` bir kural "kapalı kural sorgu
            // üretmiyor" kriterini TANIM GEREĞİ sağlıyor (zaten SQL'i yok),
            // dolayısıyla ikisini karıştıran bir test `Disabled` yolunu hiç
            // sınamaz ve yeşil kalır.
            .Where(r => r.Status == AlertRuleStatus.Enabled && (r.NextRunAt == null || r.NextRunAt <= now))
            .OrderBy(r => r.NextRunAt)
            .Take(options.MaxRulesPerTurn)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (due.Count == 0)
        {
            return AlertTurn.Idle;
        }

        var windows = await db.MaintenanceWindows
            .AsNoTracking()
            .Where(w => w.StartsAt <= now && w.EndsAt > now)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Birinci geçiş: bastırma. Hiçbir sorgu atmıyor — en ucuz bastırma,
        // ClickHouse'a hiç gitmeyen bastırmadır.
        var pending = new List<AlertRuleEntity>(due.Count);

        foreach (var rule in due)
        {
            var reason = AlertSuppression.Evaluate(rule, windows, now);

            if (reason == SuppressionReason.None)
            {
                pending.Add(rule);
                continue;
            }

            stats.Suppress();
            rule.LastRunAt = now;
            rule.LastRunState = AlertRunState.Suppressed;
            rule.LastError = string.Empty;
            rule.NextRunAt = NextRunAt(rule, now, reason);
        }

        if (pending.Count > 0)
        {
            var context = new AlertEvaluationContext(
                queries,
                stats,
                now,
                SilenceLookbackFor(pending, options),
                TimeSpan.FromSeconds(options.EvaluationTimeoutSeconds),
                cancellationToken,
                _time);

            var outcomes = await EvaluateAllAsync(pending, context, cancellationToken).ConfigureAwait(false);
            await PersistAsync(db, outcomes, now, cancellationToken).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        stats.Turn();
        return AlertTurn.Evaluated;
    }

    /// <summary>Eşzamanlılık kapısı burada: aynı anda en fazla N kural (K16).</summary>
    private async Task<IReadOnlyList<(AlertRuleEntity Rule, AlertOutcome Outcome)>> EvaluateAllAsync(
        IReadOnlyList<AlertRuleEntity> rules,
        AlertEvaluationContext context,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentEvaluations));

        var work = rules.Select(async rule =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return (rule, await evaluator.EvaluateAsync(rule, context, cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                gate.Release();
            }
        });

        return await Task.WhenAll(work).ConfigureAwait(false);
    }

    private async Task PersistAsync(
        ControlPlaneDbContext db,
        IReadOnlyList<(AlertRuleEntity Rule, AlertOutcome Outcome)> outcomes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var firedRuleIds = outcomes
            .Where(o => o.Outcome.State == AlertRunState.Fired)
            .Select(o => o.Rule.Id)
            .ToArray();

        // Kanal bağlantıları tek sorguda: kural başına ayrı sorgu, tetiklenme
        // sağanağında kontrol düzlemine yüklenirdi.
        var bindings = firedRuleIds.Length == 0
            ? []
            : await (from link in db.AlertRuleChannels.AsNoTracking()
                     join channel in db.NotificationChannels.AsNoTracking()
                         on link.ChannelId equals channel.Id
                     where firedRuleIds.Contains(link.RuleId) && channel.Enabled
                     select new { link.RuleId, link.ChannelId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        var byRule = bindings
            .GroupBy(b => b.RuleId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ChannelId).ToArray());

        foreach (var (rule, outcome) in outcomes)
        {
            rule.LastRunAt = now;
            rule.LastRunState = outcome.State;
            rule.LastError = Truncate(outcome.Error, 1024);
            rule.NextRunAt = NextRunAt(rule, now, SuppressionReason.None);

            if (outcome.State != AlertRunState.Fired)
            {
                continue;
            }

            stats.Fire();
            rule.LastFiredAt = now;

            var channels = byRule.TryGetValue(rule.Id, out var ids) ? ids : [];

            foreach (var hit in outcome.Hits)
            {
                var trigger = new AlertTriggerEntity
                {
                    RuleId = rule.Id,
                    FiredAt = now,
                    WindowFrom = hit.WindowFrom,
                    WindowTo = hit.WindowTo,
                    Value = hit.Value,
                    Threshold = hit.Threshold,
                    SourceId = Truncate(hit.SourceId, 128),
                    OwnerGroup = Truncate(hit.OwnerGroup, 64),
                    Summary = Truncate(hit.Summary, 1024),
                };

                db.AlertTriggers.Add(trigger);

                foreach (var channelId in channels)
                {
                    stats.QueueNotification();

                    db.NotificationDeliveries.Add(new NotificationDeliveryEntity
                    {
                        TriggerId = trigger.Id,
                        RuleId = rule.Id,
                        ChannelId = channelId,
                        State = DeliveryState.Pending,
                        NextAttemptAt = now,
                        CreatedAt = now,
                    });
                }
            }

            logger.LogInformation(
                "Alarm tetiklendi: {Rule} — {Hits} bulgu, {Channels} kanal.",
                rule.Name,
                outcome.Hits.Count,
                channels.Length);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Alarm motoru kapalı (Alerting:Enabled=false).");
            return;
        }

        logger.LogInformation(
            "Alarm zamanlayıcısı başladı: tur {Turn}, eşzamanlılık {Concurrency}, zaman aşımı {Timeout} sn.",
            options.TurnInterval,
            options.MaxConcurrentEvaluations,
            options.EvaluationTimeoutSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunTurnAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // İşçiyi öldürmek, motoru sessizce kapatmak olurdu.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger.LogError(ex, "Alarm turunda beklenmedik hata; döngü sürüyor.");
            }

            try
            {
                await Task.Delay(options.TurnInterval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
