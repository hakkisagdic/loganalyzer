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

/// <summary>
/// Bir çekimin neden başarısız olduğu (S06).
///
/// <para>
/// <b>Neden bir enum, neden hata metninin içinde değil:</b> S06'nın kabul
/// kriteri <i>"komut yanlış" ile "cihaz cevap vermedi" tek değere inmesin</i>
/// diyor. İkisi de <see cref="DeviceCommandResult.Ok"/>'i <see langword="false"/>
/// yapıyor ve ayırt etme yolu yalnızca <see cref="DeviceCommandResult.Error"/>
/// metninde arama yapmak olsaydı, teşhis bir cümlenin kelimelerine bağlanırdı —
/// cümle Türkçe yazıldığı ve bir gün düzeltileceği için de sessizce kaybolurdu.
/// </para>
///
/// <para>
/// Operatör için fark somut: <see cref="Unreachable"/> ağ ekibine,
/// <see cref="Authentication"/> kimlik yönetimine, <see cref="CommandRejected"/>
/// <b>bize</b> ait — toplayıcının gönderdiği komutu o cihaz tanımıyor.
/// </para>
/// </summary>
public enum DeviceFailureKind
{
    /// <summary>Başarısızlık yok.</summary>
    None = 0,

    /// <summary>Soket açılamadı, ağ üzerinden ulaşılamadı, oturum koptu.</summary>
    Unreachable = 1,

    /// <summary>Kullanıcı adı ya da kimlik bilgisi reddedildi.</summary>
    Authentication = 2,

    /// <summary>Cihaz süresinde cevap vermedi.</summary>
    Timeout = 3,

    /// <summary>
    /// Cihaz bağlandı, komutu <b>anladı ve reddetti</b>. Vendor'ın kendi hata
    /// metni <see cref="DeviceCommandResult.Error"/> içinde taşınıyor.
    /// </summary>
    CommandRejected = 4,

    /// <summary>
    /// Cihaza <b>hiç bağlanılmadı</b>: bu vendor için toplayıcı yok.
    ///
    /// <para>
    /// Diğer dördünden farklı bir aile — arıza cihazda değil <b>bizim
    /// yapılandırmamızda</b>. Ayrı bir değer olmasının sebebi operatörün
    /// yapacağı işin farklı olması: ağ ekibini aramak yerine desteklenen vendor
    /// listesine bakmak gerekiyor.
    /// </para>
    /// </summary>
    Unsupported = 5,
}

/// <param name="Output">Komutların birleştirilmiş çıktısı.</param>
/// <param name="Failure">
/// <see cref="DeviceFailureKind.None"/> ancak ve ancak <paramref name="Ok"/>
/// doğruyken. Varsayılan değeri <b>yok</b>: varsayılan olsaydı yeni bir
/// başarısızlık yolu yazan kişi türü belirtmeyi unutabilir ve sonuç
/// <c>Ok=false, Failure=None</c> olurdu — yani "başarısız ama sebebi yok".
/// Bu deponun en pahalı hata sınıfı (§7) tam olarak böyle görünüyor.
/// </param>
public sealed record DeviceCommandResult(bool Ok, string Output, string Error, DeviceFailureKind Failure)
{
    public static DeviceCommandResult Succeeded(string output) =>
        new(true, output, string.Empty, DeviceFailureKind.None);

    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="DeviceFailureKind.None"/> verilirse. Başarısızlığın türsüz
    /// olması mümkün olmamalı.
    /// </exception>
    public static DeviceCommandResult Failed(DeviceFailureKind kind, string error)
    {
        if (kind == DeviceFailureKind.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind), "Başarısız sonuç için bir başarısızlık türü zorunlu.");
        }

        return new DeviceCommandResult(false, string.Empty, error, kind);
    }
}

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
