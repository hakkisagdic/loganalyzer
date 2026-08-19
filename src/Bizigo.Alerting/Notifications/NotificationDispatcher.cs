using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bizigo.Alerting.Notifications;

/// <summary>Bir <see cref="NotificationDispatcher.RunTurnAsync"/> turunun sonucu.</summary>
public enum DispatchTurn
{
    Disabled,

    /// <summary>Vadesi gelmiş teslim yoktu.</summary>
    Idle,

    /// <summary>En az bir mesaj gönderilmeye çalışıldı.</summary>
    Dispatched,
}

/// <summary>
/// Bildirim gönderici (T22).
///
/// <para>
/// <b>Gruplama burada, kanalın kendisinde değil.</b> Bekleyen teslimler
/// <c>(kural, kanal)</c> ikilisine göre toplanıp <b>tek</b> mesaja çevriliyor.
/// On cihazın sustuğu bir sessizlik kuralı on tetiklenme üretiyor ama Slack tek
/// mesaj görüyor. Kabul kriteri "aynı kural arka arkaya tetiklendiğinde kanal
/// boğulmuyor" ancak böyle karşılanıyor — kural düzeyindeki tekrar aralığı bunun
/// ilk kademesi, bu ikincisi.
/// </para>
///
/// <para>
/// <b><see cref="RunTurnAsync"/> public, aynı gerekçeyle.</b> Testin geri adım
/// davranışını sınaması gerekiyor ve bunu arka plan görevini başlatıp beklemekle
/// yapmak, F1'de test paketini 6,5 dakikaya çıkaran hatanın aynısı olurdu.
/// </para>
/// </summary>
public sealed class NotificationDispatcher(
    AlertingOptions options,
    IDbContextFactory<ControlPlaneDbContext> factory,
    IEnumerable<INotificationChannel> channels,
    SecretProtector secrets,
    AlertingStats stats,
    ILogger<NotificationDispatcher> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private readonly Dictionary<NotificationChannelType, INotificationChannel> _channels =
        channels.ToDictionary(c => c.Type);

    /// <summary>
    /// Geri adım ilerlemesi — <b>saf fonksiyon</b>, <c>DiscoveryWorker.NextBackoff</c>
    /// ile aynı kalıp.
    ///
    /// <para>
    /// İlk düşüşte 30 sn, sonra ikiye katlanarak 15 dakikada duruyor. Üst sınır
    /// var, çünkü sınırsız katlanma tek bir geçici arızada teslimi saatlerce
    /// öteler ve alarm geç gelirse gelmemişle aynı şeydir.
    /// </para>
    /// </summary>
    public static TimeSpan NextBackoff(int attempts)
    {
        var seconds = 30d * Math.Pow(2, Math.Max(attempts - 1, 0));
        return TimeSpan.FromSeconds(Math.Min(seconds, 900));
    }

    /// <summary>Döngünün <b>tek turu</b>: bekleyenleri topla, grupla, gönder, sonucu yaz.</summary>
    public async Task<DispatchTurn> RunTurnAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return DispatchTurn.Disabled;
        }

        var now = _time.GetUtcNow();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var pending = await db.NotificationDeliveries
            .Where(d => d.State == DeliveryState.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(options.MaxDeliveriesPerTurn)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {
            return DispatchTurn.Idle;
        }

        var triggerIds = pending.Select(d => d.TriggerId).Distinct().ToArray();
        var ruleIds = pending.Select(d => d.RuleId).Distinct().ToArray();
        var channelIds = pending.Select(d => d.ChannelId).Distinct().ToArray();

        var triggers = await db.AlertTriggers.AsNoTracking()
            .Where(t => triggerIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, cancellationToken)
            .ConfigureAwait(false);

        var rules = await db.AlertRules.AsNoTracking()
            .Where(r => ruleIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, cancellationToken)
            .ConfigureAwait(false);

        var configured = await db.NotificationChannels.AsNoTracking()
            .Where(c => channelIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken)
            .ConfigureAwait(false);

        foreach (var group in pending.GroupBy(d => (d.RuleId, d.ChannelId)))
        {
            await SendGroupAsync(group.ToArray(), rules, triggers, configured, now, cancellationToken)
                .ConfigureAwait(false);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DispatchTurn.Dispatched;
    }

    private async Task SendGroupAsync(
        IReadOnlyList<NotificationDeliveryEntity> deliveries,
        IReadOnlyDictionary<Guid, AlertRuleEntity> rules,
        IReadOnlyDictionary<Guid, AlertTriggerEntity> triggers,
        IReadOnlyDictionary<Guid, NotificationChannelEntity> configured,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var first = deliveries[0];

        if (!rules.TryGetValue(first.RuleId, out var rule)
            || !configured.TryGetValue(first.ChannelId, out var entity))
        {
            // Kural ya da kanal silinmiş. Teslimi sonsuza kadar bekletmek
            // kuyruğu kirletirdi; kaydı kapatıp sebebini yazıyoruz.
            Close(deliveries, DeliveryState.Failed, "Kural ya da kanal artık yok.", now);
            return;
        }

        if (!_channels.TryGetValue(entity.ChannelType, out var channel))
        {
            Close(deliveries, DeliveryState.Failed, $"Kanal tipi desteklenmiyor: {entity.ChannelType}.", now);
            return;
        }

        var resolved = deliveries
            .Select(d => triggers.TryGetValue(d.TriggerId, out var t) ? t : null)
            .Where(t => t is not null)
            .Select(t => t!)
            .OrderBy(t => t.FiredAt)
            .ToArray();

        if (resolved.Length == 0)
        {
            Close(deliveries, DeliveryState.Failed, "Tetiklenme kaydı bulunamadı.", now);
            return;
        }

        string secret;
        try
        {
            secret = string.IsNullOrEmpty(entity.SecretCipher)
                ? string.Empty
                : secrets.Unprotect(entity.SecretCipher);
        }
        catch (InvalidOperationException ex)
        {
            // Mesaj bilerek istisnanınki: SecretProtector şifreli metni ya da
            // anahtarı mesajına koymuyor.
            Close(deliveries, DeliveryState.Failed, ex.Message, now);
            return;
        }

        var message = Compose(rule, resolved);
        var settings = ChannelSettings.Parse(entity.ConfigJson);

        var result = await channel
            .SendAsync(message, new ResolvedChannel(entity, settings, secret), cancellationToken)
            .ConfigureAwait(false);

        // Son bir redaksiyon: kanal göndericisi zaten geçirdi, ama bu satır
        // "gizli bilgi hiçbir yerde görünmüyor" iddiasının son savunması ve
        // yeni bir kanal eklendiğinde unutulacak yer tam olarak orası.
        var error = SecretRedactor.Redact(result.Error, secret, settings.User);

        if (result.Ok)
        {
            stats.DeliverNotification();
            Close(deliveries, DeliveryState.Delivered, string.Empty, now);
            return;
        }

        foreach (var delivery in deliveries)
        {
            delivery.Attempts++;
            delivery.LastError = Truncate(error, 1024);

            var exhausted = !result.Retryable || delivery.Attempts >= options.MaxDeliveryAttempts;

            if (exhausted)
            {
                stats.AbandonNotification();
                delivery.State = DeliveryState.Failed;
                continue;
            }

            stats.RetryNotification();
            delivery.NextAttemptAt = now + NextBackoff(delivery.Attempts);
        }

        logger.LogWarning(
            "Bildirim gönderilemedi: kural {Rule}, kanal {Channel} ({Type}) — {Error}",
            rule.Name,
            entity.Name,
            entity.ChannelType,
            error);
    }

    /// <summary>
    /// Gruplanmış tetiklenmelerden tek mesaj.
    ///
    /// <para>
    /// Pencere, gruptaki <b>en erken başlangıç</b> ile <b>en geç bitiş</b>
    /// arasında: bağlantı tıklandığında mesajdaki her satırın olayı ekranda
    /// olmalı. Yalnızca son tetiklenmenin penceresini kullanmak, gruplanan
    /// diğerlerini kapsam dışında bırakırdı.
    /// </para>
    /// </summary>
    private NotificationMessage Compose(AlertRuleEntity rule, IReadOnlyList<AlertTriggerEntity> triggers)
    {
        var from = triggers.Min(t => t.WindowFrom);
        var to = triggers.Max(t => t.WindowTo);

        // Kaynak filtresi yalnızca hepsi aynı kaynağa aitse bağlantıya giriyor;
        // aksi hâlde bağlantı mesajın bir kısmını gizlerdi.
        var sources = triggers.Select(t => t.SourceId).Distinct(StringComparer.Ordinal).ToArray();
        var single = sources.Length == 1 && !string.IsNullOrEmpty(sources[0]) ? sources[0] : null;

        return new NotificationMessage(
            rule.Name,
            rule.RuleType,
            rule.OwnerGroups,
            from,
            to,
            [.. triggers.Select(t => new NotificationLine(t.SourceId, t.Value, t.Threshold, t.Summary))],
            AlertLinkBuilder.Build(options, rule, from, to, single));
    }

    private static void Close(
        IReadOnlyList<NotificationDeliveryEntity> deliveries,
        DeliveryState state,
        string error,
        DateTimeOffset now)
    {
        foreach (var delivery in deliveries)
        {
            delivery.State = state;
            delivery.Attempts++;
            delivery.LastError = Truncate(error, 1024);

            if (state == DeliveryState.Delivered)
            {
                delivery.DeliveredAt = now;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

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
#pragma warning disable CA1031 // Göndericiyi öldürmek, kuyruğu sessizce durdurmak olurdu.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger.LogError(ex, "Bildirim turunda beklenmedik hata; döngü sürüyor.");
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
