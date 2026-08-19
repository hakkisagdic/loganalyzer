using Bizigo.Alerting;
using Bizigo.Alerting.Notifications;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// Gönderici bekçileri (T22): yeniden deneme, geri adım, gruplama ve bağlantı.
///
/// <para>
/// Turu doğrudan çağırıyorlar; hiçbiri arka plan görevi başlatmıyor ve hiçbiri
/// duvar saati beklemiyor.
/// </para>
/// </summary>
public sealed class NotificationDispatcherTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryControlPlaneFactory _factory = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly AlertingStats _stats = new();
    private readonly RecordingChannel _channel = new(NotificationChannelType.Slack);

    private readonly AlertingOptions _options = new()
    {
        SecretKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
        ProductBaseUrl = "https://bizigo.example",
        MaxDeliveryAttempts = 3,
    };

    public void Dispose() => _factory.Dispose();

    private NotificationDispatcher Dispatcher() => new(
        _options,
        _factory,
        [_channel],
        new SecretProtector(_options),
        _stats,
        NullLogger<NotificationDispatcher>.Instance,
        _time);

    /// <summary>Bir kural, bir kanal ve <paramref name="triggerCount"/> tetiklenme.</summary>
    private async Task<AlertRuleEntity> SeedAsync(int triggerCount, AlertRuleType type = AlertRuleType.Silence)
    {
        var rule = new AlertRuleEntity
        {
            Name = "sessiz cihazlar",
            OwnerSubject = "tester",
            OwnerGroups = "network/core",
            RuleType = type,
        };

        var channel = new NotificationChannelEntity
        {
            Name = "noc-slack",
            OwnerGroup = "network/core",
            ChannelType = NotificationChannelType.Slack,
            SecretCipher = new SecretProtector(_options).Protect("https://hooks.slack.com/services/A/B/C"),
        };

        await using var db = _factory.CreateDbContext();
        db.AlertRules.Add(rule);
        db.NotificationChannels.Add(channel);

        for (var i = 0; i < triggerCount; i++)
        {
            var trigger = new AlertTriggerEntity
            {
                RuleId = rule.Id,
                FiredAt = Start,
                WindowFrom = Start.AddMinutes(-15 - i),
                WindowTo = Start.AddMinutes(-i),
                SourceId = $"fw-{i:00}",
                OwnerGroup = "network/core",
                Value = 900 + i,
                Threshold = 900,
                Summary = $"fw-{i:00} sessiz",
            };

            db.AlertTriggers.Add(trigger);
            db.NotificationDeliveries.Add(new NotificationDeliveryEntity
            {
                TriggerId = trigger.Id,
                RuleId = rule.Id,
                ChannelId = channel.Id,
                NextAttemptAt = Start,
            });
        }

        await db.SaveChangesAsync(Token);
        return rule;
    }

    [Fact]
    public async Task Bekleyen_teslim_yoksa_tur_bos_donuyor()
    {
        Assert.Equal(DispatchTurn.Idle, await Dispatcher().RunTurnAsync(Token));
    }

    /// <summary>
    /// T22 kabul kriteri: <b>aynı kural arka arkaya tetiklendiğinde kanal
    /// boğulmuyor.</b> On tetiklenme, tek mesaj.
    /// </summary>
    [Fact]
    public async Task On_tetiklenme_tek_mesaja_gruplaniyor()
    {
        await SeedAsync(triggerCount: 10);

        Assert.Equal(DispatchTurn.Dispatched, await Dispatcher().RunTurnAsync(Token));

        var message = Assert.Single(_channel.Sent);
        Assert.Equal(10, message.TriggerCount);
        Assert.Contains("10 tetiklenme", message.Title, StringComparison.Ordinal);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(10, await db.NotificationDeliveries.CountAsync(d => d.State == DeliveryState.Delivered, Token));
    }

    /// <summary>
    /// Gruplanan mesajın penceresi <b>en erken başlangıç – en geç bitiş</b>:
    /// bağlantıya tıklandığında mesajdaki her satırın olayı ekranda olmalı.
    /// </summary>
    [Fact]
    public async Task Gruplanan_mesajin_penceresi_tum_tetiklenmeleri_kapsiyor()
    {
        await SeedAsync(triggerCount: 3);

        await Dispatcher().RunTurnAsync(Token);

        var message = Assert.Single(_channel.Sent);
        Assert.Equal(Start.AddMinutes(-17), message.WindowFrom);
        Assert.Equal(Start, message.WindowTo);
    }

    [Fact]
    public async Task Tek_kaynakli_grup_baglantiya_source_id_koyuyor()
    {
        await SeedAsync(triggerCount: 1);

        await Dispatcher().RunTurnAsync(Token);

        var message = Assert.Single(_channel.Sent);
        Assert.NotNull(message.Link);
        Assert.Contains("source_id=fw-00", message.Link, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cok_kaynakli_grup_baglantiya_source_id_koymuyor()
    {
        // Tek bir kaynağa daraltmak, mesajdaki diğer satırları ekranda gizlerdi.
        await SeedAsync(triggerCount: 3);

        await Dispatcher().RunTurnAsync(Token);

        var message = Assert.Single(_channel.Sent);
        Assert.NotNull(message.Link);
        Assert.DoesNotContain("source_id=", message.Link, StringComparison.Ordinal);
    }

    /// <summary>T22 kabul kriteri: kanal 500 döndüğünde yeniden deneniyor.</summary>
    [Fact]
    public async Task Gecici_hatada_yeniden_deneme_planlaniyor()
    {
        await SeedAsync(triggerCount: 1);
        _channel.Results.Enqueue(ChannelResult.Transient("Kanal 500 döndü."));

        await Dispatcher().RunTurnAsync(Token);

        await using var db = _factory.CreateDbContext();
        var delivery = await db.NotificationDeliveries.SingleAsync(Token);

        Assert.Equal(DeliveryState.Pending, delivery.State);
        Assert.Equal(1, delivery.Attempts);
        Assert.Equal(Start + NotificationDispatcher.NextBackoff(1), delivery.NextAttemptAt);
        Assert.Equal(1, _stats.NotificationsRetried);
    }

    [Fact]
    public async Task Geri_adim_dolmadan_yeniden_denenmiyor()
    {
        await SeedAsync(triggerCount: 1);
        _channel.Results.Enqueue(ChannelResult.Transient("Kanal 500 döndü."));

        await Dispatcher().RunTurnAsync(Token);
        Assert.Single(_channel.Sent);

        // Geri adım süresi dolmadan yeni tur: hiçbir şey gönderilmiyor.
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(DispatchTurn.Idle, await Dispatcher().RunTurnAsync(Token));
        Assert.Single(_channel.Sent);

        // Dolduktan sonra ikinci deneme.
        _time.Advance(NotificationDispatcher.NextBackoff(1));
        Assert.Equal(DispatchTurn.Dispatched, await Dispatcher().RunTurnAsync(Token));
        Assert.Equal(2, _channel.Sent.Count);
    }

    /// <summary>T22 kabul kriteri: <b>deneme sayısı sınırlı.</b></summary>
    [Fact]
    public async Task Deneme_hakki_bitince_teslim_kapaniyor()
    {
        await SeedAsync(triggerCount: 1);

        for (var attempt = 1; attempt <= _options.MaxDeliveryAttempts; attempt++)
        {
            _channel.Results.Enqueue(ChannelResult.Transient("Kanal 500 döndü."));
            await Dispatcher().RunTurnAsync(Token);
            _time.Advance(NotificationDispatcher.NextBackoff(attempt));
        }

        await using var db = _factory.CreateDbContext();
        var delivery = await db.NotificationDeliveries.SingleAsync(Token);

        Assert.Equal(DeliveryState.Failed, delivery.State);
        Assert.Equal(_options.MaxDeliveryAttempts, delivery.Attempts);
        Assert.Equal(1, _stats.NotificationsAbandoned);

        // Kayıt SİLİNMİYOR: kalıcı olarak kırık bir kanalın izi kalmalı.
        Assert.NotEqual(string.Empty, delivery.LastError);
    }

    [Fact]
    public async Task Kalici_hatada_hic_yeniden_denenmiyor()
    {
        await SeedAsync(triggerCount: 1);
        _channel.Results.Enqueue(ChannelResult.Permanent("Kanal 404 döndü."));

        await Dispatcher().RunTurnAsync(Token);

        await using var db = _factory.CreateDbContext();
        var delivery = await db.NotificationDeliveries.SingleAsync(Token);

        Assert.Equal(DeliveryState.Failed, delivery.State);
        Assert.Equal(1, delivery.Attempts);
    }

    [Fact]
    public void Geri_adim_ikiye_katlanip_on_bes_dakikada_duruyor()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), NotificationDispatcher.NextBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(60), NotificationDispatcher.NextBackoff(2));
        Assert.Equal(TimeSpan.FromSeconds(120), NotificationDispatcher.NextBackoff(3));

        // Üst sınır: sınırsız katlanma, alarmın saatlerce ötelenmesi demekti.
        Assert.Equal(TimeSpan.FromMinutes(15), NotificationDispatcher.NextBackoff(20));
    }

    [Fact]
    public async Task Kanali_silinmis_teslim_kuyrukta_sonsuza_kadar_beklemiyor()
    {
        await SeedAsync(triggerCount: 1);

        await using (var db = _factory.CreateDbContext())
        {
            db.NotificationChannels.RemoveRange(db.NotificationChannels);
            await db.SaveChangesAsync(Token);
        }

        await Dispatcher().RunTurnAsync(Token);

        await using (var db = _factory.CreateDbContext())
        {
            Assert.Equal(DeliveryState.Failed, (await db.NotificationDeliveries.SingleAsync(Token)).State);
        }
    }

    [Fact]
    public async Task Mesaj_kural_adini_degeri_ve_araligi_tasiyor()
    {
        await SeedAsync(triggerCount: 1);

        await Dispatcher().RunTurnAsync(Token);

        var text = Assert.Single(_channel.Sent).ToPlainText();

        Assert.Contains("sessiz cihazlar", text, StringComparison.Ordinal);
        Assert.Contains("fw-00 sessiz", text, StringComparison.Ordinal);
        Assert.Contains("sessizlik", text, StringComparison.Ordinal);
        Assert.Contains("network/core", text, StringComparison.Ordinal);
        Assert.Contains("Aramayı aç:", text, StringComparison.Ordinal);
    }
}
