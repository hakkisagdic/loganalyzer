using System.Net;
using System.Net.Mail;
using Bizigo.ControlPlane;

namespace Bizigo.Alerting.Notifications;

/// <summary>
/// SMTP taşıması — <b>arayüz olmasının tek sebebi test</b>.
///
/// <para>
/// E-posta kanalının doğrulanması gereken davranışı "SMTP protokolü doğru mu"
/// değil: parolanın hata mesajına düşmemesi, geçici arızanın yeniden
/// denenmesi, kalıcı olanın denenmemesi. Bunların hiçbiri gerçek bir SMTP
/// sunucusu gerektirmiyor ve gerektirseydi bu davranışlar ancak konteyner
/// kaldırabilen bir makinede sınanabilirdi.
/// </para>
/// </summary>
public interface ISmtpTransport
{
    Task SendAsync(SmtpEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <param name="Password">SMTP parolası. Bu tipin dışına çıkmıyor.</param>
public sealed record SmtpEnvelope(
    string Host,
    int Port,
    bool UseStartTls,
    string User,
    string Password,
    string From,
    IReadOnlyList<string> To,
    string Subject,
    string Body);

/// <summary>Üretimdeki taşıma.</summary>
public sealed class SystemSmtpTransport(AlertingOptions options) : ISmtpTransport
{
    public async Task SendAsync(SmtpEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var client = new SmtpClient(envelope.Host, envelope.Port)
        {
            EnableSsl = envelope.UseStartTls,
            Timeout = (int)options.ChannelTimeout.TotalMilliseconds,
        };

        if (!string.IsNullOrWhiteSpace(envelope.User))
        {
            client.Credentials = new NetworkCredential(envelope.User, envelope.Password);
        }

        using var mail = new MailMessage
        {
            From = new MailAddress(envelope.From),
            Subject = envelope.Subject,
            Body = envelope.Body,
            IsBodyHtml = false,
        };

        foreach (var recipient in envelope.To)
        {
            mail.To.Add(recipient);
        }

        await client.SendMailAsync(mail, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// E-posta kanalı (T22).
///
/// <para>
/// Gizli bilgi burada URL değil <b>SMTP parolası</b>. Sunucu adı, port ve
/// alıcılar gizli değil — yapılandırma tarafında duruyorlar ve kanal listesinde
/// görünüyorlar; parola tek başına şifreli kolonda.
/// </para>
/// </summary>
public sealed class EmailChannel(ISmtpTransport transport) : INotificationChannel
{
    public NotificationChannelType Type => NotificationChannelType.Email;

    public async Task<ChannelResult> SendAsync(
        NotificationMessage message,
        ResolvedChannel channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(channel);

        var settings = channel.Settings;

        if (string.IsNullOrWhiteSpace(settings.Host)
            || string.IsNullOrWhiteSpace(settings.From)
            || settings.To.Count == 0)
        {
            // Eksik yapılandırma kalıcı: yeniden denemek aynı eksikle karşılaşır.
            return ChannelResult.Permanent(
                "E-posta kanalı eksik yapılandırılmış: 'host', 'from' ve en az bir 'to' gerekiyor.");
        }

        var envelope = new SmtpEnvelope(
            settings.Host,
            settings.Port,
            settings.UseStartTls,
            settings.User,
            channel.Secret,
            settings.From,
            settings.To,
            message.Title,
            message.ToPlainText());

        try
        {
            await transport.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
            return ChannelResult.Delivered();
        }
        catch (SmtpException ex)
        {
            var error = SecretRedactor.Redact(
                $"SMTP hatası ({ex.StatusCode}).", channel.Secret, settings.User);

            // Kimlik doğrulama ve alıcı hataları kalıcı; gerisi (bağlantı,
            // geçici ret) yeniden denenmeye değer.
            return ex.StatusCode is SmtpStatusCode.MailboxNameNotAllowed
                or SmtpStatusCode.MustIssueStartTlsFirst
                or SmtpStatusCode.ClientNotPermitted
                ? ChannelResult.Permanent(error)
                : ChannelResult.Transient(error);
        }
        catch (FormatException ex)
        {
            // Bozuk adres — düzeltilmeden yeniden denemek anlamsız.
            return ChannelResult.Permanent(
                SecretRedactor.Redact($"E-posta adresi geçersiz: {ex.Message}", channel.Secret));
        }
        catch (InvalidOperationException ex)
        {
            return ChannelResult.Transient(
                SecretRedactor.Redact($"E-posta gönderilemedi: {ex.Message}", channel.Secret));
        }
    }
}
