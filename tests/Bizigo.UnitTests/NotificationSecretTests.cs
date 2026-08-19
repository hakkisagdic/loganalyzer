using System.Net;
using Bizigo.Alerting;
using Bizigo.Alerting.Notifications;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.Extensions.Logging;

namespace Bizigo.UnitTests;

/// <summary>
/// T22'nin en sert kabul kriteri: <b>gizli bilgiler hiçbir log, hata mesajı veya
/// API yanıtında görünmüyor.</b>
///
/// <para>
/// Bu bekçilerin şekli bilinçli olarak "beyaz liste" değil <b>kara liste</b>:
/// çıktının ne içermesi gerektiğini değil, ne içermemesi gerektiğini sınıyorlar.
/// Sebep F1'in dersi — doğrulanmamış katman kırıktı ve hiçbiri kendini belli
/// etmedi. Gizli bilgi sızıntısı da öyle: çıktı doğru <i>görünür</i>, içinde
/// jeton vardır.
/// </para>
/// </summary>
public sealed class NotificationSecretTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Gerçekçi bir Slack webhook'u: host, yol parçaları ve bir jeton.</summary>
    private const string SlackHook =
        "https://hooks.slack.com/services/T00000000/B11111111/XyZgizliJETONdegeri9876";

    private const string SmtpPassword = "sMtP-p4rol4-cok-gizli";

    private static AlertingOptions Options() => new()
    {
        // Base64, 32 bayt.
        SecretKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
        ProductBaseUrl = "https://bizigo.example",
        ChannelTimeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public void Sifreleme_gidip_geliyor()
    {
        var protector = new SecretProtector(Options());

        var cipher = protector.Protect(SlackHook);

        Assert.NotEqual(SlackHook, cipher);
        Assert.DoesNotContain("hooks.slack.com", cipher, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SlackHook, protector.Unprotect(cipher));
    }

    [Fact]
    public void Ayni_gizli_bilgi_her_seferinde_farkli_sifreli_metin_uretiyor()
    {
        var protector = new SecretProtector(Options());

        // Nonce her şifrelemede yeniden üretiliyor. Aynı çıktı, iki kanalın aynı
        // hedefe yazdığını veritabanına bakarak anlamak demek olurdu.
        Assert.NotEqual(protector.Protect(SlackHook), protector.Protect(SlackHook));
    }

    [Fact]
    public void Kurcalanmis_sifreli_metin_reddediliyor()
    {
        var protector = new SecretProtector(Options());
        var cipher = protector.Protect(SlackHook);

        var bytes = Convert.FromBase64String(cipher);
        bytes[^1] ^= 0xFF;

        var ex = Assert.Throws<InvalidOperationException>(
            () => protector.Unprotect(Convert.ToBase64String(bytes)));

        // İstisna mesajının kendisi de temiz olmalı.
        Assert.DoesNotContain(cipher, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Anahtar_yoksa_sifreleme_reddediliyor_duz_metne_dusulmuyor()
    {
        var protector = new SecretProtector(new AlertingOptions());

        Assert.False(protector.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => protector.Protect(SlackHook));
    }

    /// <summary>URL'in tamamı değil, <b>parçaları</b> da maskeleniyor.</summary>
    [Theory]
    [InlineData("POST https://hooks.slack.com/services/T00000000/B11111111/XyZgizliJETONdegeri9876 başarısız")]
    [InlineData("Bağlantı kurulamadı: hooks.slack.com")]
    [InlineData("Yanıt gövdesi: {\"error\":\"invalid_token\",\"url\":\"/services/T00000000/B11111111/XyZgizliJETONdegeri9876\"}")]
    [InlineData("jeton XyZgizliJETONdegeri9876 reddedildi")]
    public void Redaksiyon_url_parcalarini_da_maskeliyor(string message)
    {
        var redacted = SecretRedactor.Redact(message, SlackHook);

        Assert.DoesNotContain("XyZgizliJETONdegeri9876", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hooks.slack.com", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SecretRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redaksiyon_uzun_parcayi_once_maskeliyor()
    {
        // Kısa parça önce maskelenseydi uzun parça metinde bulunamaz ve
        // geri kalanı açıkta kalırdı.
        var fragments = SecretRedactor.Fragments([SlackHook]);

        Assert.Equal(fragments.OrderByDescending(f => f.Length), fragments);
    }

    [Fact]
    public void Redaksiyon_cok_kisa_parcalari_maskelemiyor()
    {
        // "https" her hata mesajında geçiyor; maskelenseydi mesaj okunamaz
        // hâle gelirdi ve okunamayan hata mesajı da kendi başına bir arıza.
        var redacted = SecretRedactor.Redact("https şeması bekleniyordu", "https://a.io/x");

        Assert.Contains("https", redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kanal 500 döndüğünde hata metni üretiliyor ve <b>içinde adres yok</b>.
    /// Aynı test yeniden denenebilirliği de sabitliyor.
    /// </summary>
    [Fact]
    public async Task Slack_500_donunce_hata_adres_sizdirmiyor_ve_yeniden_denenebilir()
    {
        var channel = new SlackChannel(
            new StubHttpClientFactory(HttpStatusCode.InternalServerError), Options());

        var result = await channel.SendAsync(Message(), Channel(SlackHook), Token);

        Assert.False(result.Ok);
        Assert.True(result.Retryable);
        Assert.DoesNotContain("XyZgizliJETONdegeri9876", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("hooks.slack.com", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Kanal_404_donunce_yeniden_denenmiyor()
    {
        var channel = new WebhookChannel(
            new StubHttpClientFactory(HttpStatusCode.NotFound), Options());

        var result = await channel.SendAsync(Message(), Channel(SlackHook), Token);

        Assert.False(result.Ok);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task Ag_hatasinda_istisna_mesaji_oldugu_gibi_tasinmiyor()
    {
        var channel = new TeamsChannel(
            new StubHttpClientFactory(new HttpRequestException(
                $"No such host is known. ({new Uri(SlackHook).Host}:443)")),
            Options());

        var result = await channel.SendAsync(Message(), Channel(SlackHook), Token);

        Assert.True(result.Retryable);
        Assert.DoesNotContain("hooks.slack.com", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gecersiz_adres_hatasi_adresi_yazmiyor()
    {
        var channel = new SlackChannel(new StubHttpClientFactory(HttpStatusCode.OK), Options());

        var result = await channel.SendAsync(
            Message(), Channel("bu-bir-url-degil-XyZgizliJETONdegeri9876"), Token);

        Assert.False(result.Ok);
        Assert.False(result.Retryable);
        Assert.DoesNotContain("XyZgizliJETONdegeri9876", result.Error, StringComparison.Ordinal);
    }

    /// <summary>SMTP parolası hata metnine de loga da düşmemeli.</summary>
    [Fact]
    public async Task Smtp_parolasi_hata_metnine_dusmuyor()
    {
        var transport = new ThrowingSmtpTransport();
        var channel = new EmailChannel(transport);

        var settings = new ChannelSettings
        {
            Host = "smtp.local",
            Port = 587,
            From = "alarm@bizigo.example",
            To = ["noc@bizigo.example"],
            User = "alarm",
        };

        var entity = new NotificationChannelEntity
        {
            Name = "eposta",
            OwnerGroup = "network/core",
            ChannelType = NotificationChannelType.Email,
        };

        var result = await channel.SendAsync(
            Message(), new ResolvedChannel(entity, settings, SmtpPassword), Token);

        Assert.False(result.Ok);
        Assert.DoesNotContain(SmtpPassword, result.Error, StringComparison.Ordinal);

        // Parola taşımada gerçekten kullanıldı — yani test, kullanılmadığı için
        // sızmayan bir değeri sınamıyor.
        Assert.Equal(SmtpPassword, transport.SeenPassword);
    }

    /// <summary>
    /// Gönderici turunun tamamı: kanal hata döndüğünde veritabanına yazılan
    /// <c>last_error</c> ve loga düşen satır <b>ikisi de</b> temiz olmalı.
    /// </summary>
    [Fact]
    public async Task Gonderici_turu_ne_veritabanina_ne_loga_gizli_bilgi_yaziyor()
    {
        using var factory = new InMemoryControlPlaneFactory();
        var options = Options();
        var protector = new SecretProtector(options);
        var logs = new CapturingLoggerProvider();

        var channel = new SecretEchoingChannel(NotificationChannelType.Slack);

        Guid deliveryId;

        await using (var db = factory.CreateDbContext())
        {
            var rule = new AlertRuleEntity
            {
                Name = "sessiz cihaz", OwnerSubject = "t", OwnerGroups = "network/core",
                RuleType = AlertRuleType.Silence,
            };

            var trigger = new AlertTriggerEntity
            {
                RuleId = rule.Id,
                WindowFrom = DateTimeOffset.UnixEpoch,
                WindowTo = DateTimeOffset.UnixEpoch.AddMinutes(5),
                Summary = "fw-core-01 30 dk sessiz",
                SourceId = "fw-core-01",
            };

            var configured = new NotificationChannelEntity
            {
                Name = "noc-slack",
                OwnerGroup = "network/core",
                ChannelType = NotificationChannelType.Slack,
                SecretCipher = protector.Protect(SlackHook),
            };

            var delivery = new NotificationDeliveryEntity
            {
                TriggerId = trigger.Id,
                RuleId = rule.Id,
                ChannelId = configured.Id,
                NextAttemptAt = DateTimeOffset.UnixEpoch,
            };

            deliveryId = delivery.Id;

            db.AlertRules.Add(rule);
            db.AlertTriggers.Add(trigger);
            db.NotificationChannels.Add(configured);
            db.NotificationDeliveries.Add(delivery);
            await db.SaveChangesAsync(Token);
        }

        var dispatcher = new NotificationDispatcher(
            options, factory, [channel], protector, new AlertingStats(),
            logs.CreateLogger<NotificationDispatcher>());

        Assert.Equal(DispatchTurn.Dispatched, await dispatcher.RunTurnAsync(Token));

        // Kanal gizli bilgiyi gerçekten aldı: yol canlı.
        Assert.Equal(SlackHook, channel.SeenSecret);

        await using (var db = factory.CreateDbContext())
        {
            var stored = await db.NotificationDeliveries.FindAsync([deliveryId], Token);

            Assert.NotNull(stored);
            Assert.DoesNotContain("XyZgizliJETONdegeri9876", stored.LastError, StringComparison.Ordinal);
            Assert.DoesNotContain("hooks.slack.com", stored.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(SecretRedactor.Mask, stored.LastError, StringComparison.Ordinal);
        }

        var written = string.Join('\n', logs.Lines);
        Assert.DoesNotContain("XyZgizliJETONdegeri9876", written, StringComparison.Ordinal);
        Assert.DoesNotContain("hooks.slack.com", written, StringComparison.OrdinalIgnoreCase);
    }

    private static NotificationMessage Message() => new(
        "sınama kuralı",
        AlertRuleType.Threshold,
        "network/core",
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch.AddMinutes(5),
        [new NotificationLine(string.Empty, 5, 1, "5 dk içinde 120 olay")],
        null);

    private static ResolvedChannel Channel(string secret) => new(
        new NotificationChannelEntity
        {
            Name = "sınama",
            OwnerGroup = "network/core",
            ChannelType = NotificationChannelType.Slack,
        },
        new ChannelSettings(),
        secret);

    /// <summary>
    /// Gizli bilgiyi hata metnine <b>bilerek</b> koyan kanal.
    ///
    /// <para>
    /// Göndericinin son redaksiyon katmanını sınamanın tek dürüst yolu bu:
    /// gerçek kanallar zaten temizliyor, dolayısıyla onlarla sınamak
    /// göndericinin kendi savunmasının çalıştığını göstermez. Yeni bir kanal
    /// eklendiğinde unutulacak yer tam olarak burası.
    /// </para>
    /// </summary>
    private sealed class SecretEchoingChannel(NotificationChannelType type) : INotificationChannel
    {
        public NotificationChannelType Type { get; } = type;

        public string? SeenSecret { get; private set; }

        public Task<ChannelResult> SendAsync(
            NotificationMessage message,
            ResolvedChannel channel,
            CancellationToken cancellationToken = default)
        {
            SeenSecret = channel.Secret;
            return Task.FromResult(ChannelResult.Transient($"POST {channel.Secret} → 500"));
        }
    }

    private sealed class ThrowingSmtpTransport : ISmtpTransport
    {
        public string? SeenPassword { get; private set; }

        public Task SendAsync(SmtpEnvelope envelope, CancellationToken cancellationToken = default)
        {
            SeenPassword = envelope.Password;
            throw new System.Net.Mail.SmtpException(
                System.Net.Mail.SmtpStatusCode.ServiceNotAvailable,
                $"Kimlik doğrulama reddedildi (kullanıcı alarm, parola {envelope.Password}).");
        }
    }
}

/// <summary>Sabit yanıt ya da sabit istisna üreten <c>HttpClient</c> fabrikası.</summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpStatusCode? _status;
    private readonly Exception? _throw;

    public StubHttpClientFactory(HttpStatusCode status) => _status = status;

    public StubHttpClientFactory(Exception toThrow) => _throw = toThrow;

    public HttpClient CreateClient(string name) => new(new StubHandler(_status, _throw));

    private sealed class StubHandler(HttpStatusCode? status, Exception? toThrow) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            toThrow is not null
                ? Task.FromException<HttpResponseMessage>(toThrow)
                : Task.FromResult(new HttpResponseMessage(status!.Value));
    }
}

/// <summary>Yazılan log satırlarını biriktiren sağlayıcı.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Lines { get; } = [];

    public ILogger<T> CreateLogger<T>() => new Captured<T>(Lines);

    public ILogger CreateLogger(string categoryName) => new Captured<object>(Lines);

    public void Dispose() => GC.SuppressFinalize(this);

    private sealed class Captured<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            lock (lines)
            {
                lines.Add(formatter(state, exception));

                if (exception is not null)
                {
                    lines.Add(exception.ToString());
                }
            }
        }
    }
}
