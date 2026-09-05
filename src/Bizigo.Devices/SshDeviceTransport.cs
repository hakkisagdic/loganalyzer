using System.Text;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Bizigo.Devices;

/// <summary>
/// SSH üzerinden komut çalıştıran taşıma (T26).
///
/// <para>
/// <b>Ürünün cihaza açtığı tek soket burada.</b> Üç vendor da (FortiGate,
/// Cisco ASA, MikroTik) SSH konuşuyor; REST ve SNMP <b>yazılmadı</b> çünkü
/// ticket'ın kendisi uyarıyor: üç yöntemi tek soyutlamaya sıkıştırmak erken
/// genelleme olur. <see cref="IDeviceTransport"/> yüzeyi iki somut vendor
/// yazıldıktan sonra çıkarıldı ve yalnızca "komut çalıştır, metin al" diyor —
/// REST bir gün gerektiğinde aynı yüzeyi uygulayabilir.
/// </para>
///
/// <para>
/// <b>Hata mesajları kimlik bilgisi taşımıyor.</b> SSH.NET istisnaları
/// kullanıcı adını ve host'u taşıyabiliyor ama parolayı taşımıyor; yine de
/// dışarı verilen metin burada <b>yeniden yazılıyor</b>, kütüphanenin ne
/// bastığına güvenilmiyor. İkinci savunma katmanı T25'in servis kapısında
/// (<c>SecretRedactor</c>) duruyor.
/// </para>
/// </summary>
public sealed class SshDeviceTransport(ILogger<SshDeviceTransport> log) : IDeviceTransport
{
    public async Task<DeviceCommandResult> RunAsync(
        DeviceTarget target,
        IReadOnlyList<string> commands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(commands);

        try
        {
            // Bağlantı kurma senkron; iptal jetonuna saygı duyması için ayrı bir
            // görevde koşuyor. Aksi hâlde kapanan bir uygulama, cevap vermeyen
            // bir cihazı zaman aşımı dolana kadar beklerdi.
            return await Task.Run(
                () => Execute(target, commands, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SshAuthenticationException)
        {
            // Mesaj kütüphaneden DEĞİL bizden: kimlik doğrulama hatası, kimlik
            // bilgisinin kendisini taşımaya en yatkın istisna sınıfı.
            return Failed(target, DeviceFailureKind.Authentication, "Kimlik doğrulama reddedildi.");
        }
        catch (SshOperationTimeoutException)
        {
            return Failed(
                target,
                DeviceFailureKind.Timeout,
                $"Cihaz {target.Timeout.TotalSeconds:0} saniyede cevap vermedi.");
        }
        catch (SshConnectionException)
        {
            return Failed(target, DeviceFailureKind.Unreachable, "SSH bağlantısı kurulamadı.");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            return Failed(target, DeviceFailureKind.Unreachable, "Cihaza ağ üzerinden ulaşılamadı.");
        }
    }

    private DeviceCommandResult Failed(DeviceTarget target, DeviceFailureKind kind, string reason)
    {
        // Log satırı hedefi taşıyor, kimlik bilgisini DEĞİL — `ToString()`
        // yalnızca vendor/host/port basıyor.
        log.LogWarning("Cihaz çekimi başarısız: {Target} — {Kind}: {Reason}", target, kind, reason);

        return DeviceCommandResult.Failed(kind, reason);
    }

    private static DeviceCommandResult Execute(
        DeviceTarget target,
        IReadOnlyList<string> commands,
        CancellationToken cancellationToken)
    {
        using var client = new SshClient(Connection(target));
        client.ConnectionInfo.Timeout = target.Timeout;

        client.Connect();

        var output = new StringBuilder();

        foreach (var command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var run = client.CreateCommand(command);
            run.CommandTimeout = target.Timeout;

            output.AppendLine(run.Execute());

            // Komut hata çıktısı VERİYOR ama sıfır dönüyorsa bu bir arıza
            // değil: cihazlar sayfalama uyarılarını stderr'e basıyor. Yalnızca
            // çıkış kodu bakılıyor.
            if (run.ExitStatus is not (0 or null))
            {
                return DeviceCommandResult.Failed(
                    DeviceFailureKind.CommandRejected,
                    Rejection(command, run.ExitStatus.Value, run.Error));
            }
        }

        return DeviceCommandResult.Succeeded(output.ToString());
    }

    /// <summary>
    /// Reddedilen komutun cümlesi — <b>cihazın kendi metniyle birlikte</b>
    /// (S06).
    ///
    /// <para>
    /// Önceden yalnızca çıkış kodu taşınıyordu (<c>"… 127 koduyla döndü"</c>) ve
    /// vendor'ın kendi cümlesi — <c>% Invalid input detected at '^' marker.</c>,
    /// <c>command parse error before …</c> — okunmadan atılıyordu. Teşhis için
    /// gereken tek şey oydu: çıkış kodu komutun <i>hangi</i> sebeple
    /// reddedildiğini söylemiyor.
    /// </para>
    ///
    /// <para>
    /// <b>Sınıf yorumundaki "kütüphanenin ne bastığına güvenilmiyor" kararıyla
    /// çelişmiyor.</b> Oradaki kaygı kimlik bilgisiydi ve parola bu yola hiç
    /// girmiyor: <see cref="Connection"/> dışında hiçbir yerde dolaşmıyor, ve
    /// buraya gelen metin cihazın <i>bizim gönderdiğimiz komuta</i> verdiği
    /// cevap. Yine de metin <b>kırpılıyor</b>: stderr'e sayfalarca basan bir
    /// cihaz, hata mesajını log satırına sığmaz hâle getirebilir.
    /// </para>
    /// </summary>
    private static string Rejection(string command, int exitStatus, string? deviceText)
    {
        var text = (deviceText ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return $"'{command}' komutu {exitStatus} koduyla döndü.";
        }

        const int Limit = 500;

        if (text.Length > Limit)
        {
            text = text[..Limit] + "…";
        }

        return $"'{command}' komutu {exitStatus} koduyla döndü: {text}";
    }

    /// <summary>
    /// Bağlantı bilgisi. Parola ve özel anahtar yalnızca burada, tek ifadede
    /// dolaşıyor.
    /// </summary>
    private static ConnectionInfo Connection(DeviceTarget target)
    {
        AuthenticationMethod method;

        if (target.AuthMode == DeviceAuthMode.PrivateKey)
        {
            using var pem = new MemoryStream(Encoding.UTF8.GetBytes(target.Credential));
            method = new PrivateKeyAuthenticationMethod(target.Username, new PrivateKeyFile(pem));
        }
        else
        {
            method = new PasswordAuthenticationMethod(target.Username, target.Credential);
        }

        return new ConnectionInfo(target.Host, target.Port, target.Username, method);
    }
}
