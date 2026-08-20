namespace Bizigo.Devices;

/// <summary>Kimlik bilgisinin cihazda nasıl kullanılacağı.</summary>
public enum DeviceAuthMode
{
    /// <summary>Parola. Kimlik bilgisi doğrudan paroladır.</summary>
    Password = 0,

    /// <summary>Özel anahtar (PEM). Kimlik bilgisi anahtarın kendisidir.</summary>
    PrivateKey = 1,
}

/// <summary>
/// Bağlanılacak cihaz (T26).
///
/// <para>
/// <b>Kimlik bilgisi bu tipin içinde <c>Credential</c> olarak duruyor ve bu tip
/// bilerek <c>record</c> DEĞİL:</b> kayıt tipinin üretilmiş <c>ToString()</c>'i
/// bütün özellikleri basar, yani hedefi loglayan ilk satır parolayı da basardı.
/// T24'te <c>ChangeWebhookEndpoint</c> için verilen kararın aynısı.
/// </para>
/// </summary>
public sealed class DeviceTarget
{
    public required string Vendor { get; init; }

    public required string Host { get; init; }

    public int Port { get; init; } = 22;

    public required string Username { get; init; }

    /// <summary>Parola ya da PEM özel anahtar. <b>Hiçbir çıktıya girmiyor.</b></summary>
    public required string Credential { get; init; }

    public DeviceAuthMode AuthMode { get; init; } = DeviceAuthMode.Password;

    /// <summary>
    /// Bağlantı ve komut zaman aşımı.
    ///
    /// <para>
    /// <b>Burada duvar saati doğru ölçü</b> — F1'in dersinin istisnası. Orada
    /// bütçe <i>bizim kodumuzun</i> hızını ölçmeye çalışıyordu ve makineyi
    /// ölçüyordu; burada ölçülen şey uzaktaki cihazın cevap verip vermediği,
    /// yani gerçekten bir süre. Zaman aşımı olmadan erişilemeyen tek bir cihaz
    /// çekim turunu süresiz kilitler.
    /// </para>
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Parolayı asla basmaz.</summary>
    public override string ToString() => $"{Vendor}@{Host}:{Port}";
}

/// <param name="Output">Komutların birleştirilmiş çıktısı.</param>
public sealed record DeviceCommandResult(bool Ok, string Output, string Error);

/// <summary>
/// Cihazdan metin okuyan taşıma katmanı.
///
/// <para>
/// Arayüz olmasının sebebi test değil <b>sınır</b>: bu ürün cihaza yalnızca
/// okumak için bağlanıyor ve arayüzün yüzeyinde yazma diye bir şey yok. Bir gün
/// biri config değiştirmek isterse, yapması gereken yeni bir metot eklemek —
/// yani görünür bir karar (T26 kapsamı: "cihaza yazma, bu ürün config
/// değiştirmiyor").
/// </para>
/// </summary>
public interface IDeviceTransport
{
    Task<DeviceCommandResult> RunAsync(
        DeviceTarget target,
        IReadOnlyList<string> commands,
        CancellationToken cancellationToken);
}
