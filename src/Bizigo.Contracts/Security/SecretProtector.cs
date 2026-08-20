using System.Security.Cryptography;
using System.Text;

namespace Bizigo.Contracts.Security;

/// <summary>
/// Saklanan gizli bilgilerin şifrelenmesi — bildirim kanalları (T22) ve
/// connector kimlik bilgileri (T25) için <b>ortak</b>.
///
/// <para>
/// <b>AES-256-GCM</b>: şifreleme ve bütünlük tek işlemde. Sadece şifreleseydik
/// (ör. AES-CBC) veritabanına erişebilen biri şifreli metni <b>değiştirebilir</b>
/// ve webhook'un gideceği adresi sessizce başka bir yere çevirebilirdi —
/// alarmların bir saldırganın sunucusuna akması, okunamayan bir paroladan çok
/// daha kötü. Aynı gerekçe connector tarafında daha da sert: kurcalanmış bir
/// kimlik bilgisi, ürünün bir cihaza <b>saldırganın seçtiği</b> adresle
/// bağlanması demek.
/// </para>
///
/// <para>
/// Biçim: <c>base64(nonce ‖ tag ‖ ciphertext)</c>. Nonce her şifrelemede yeniden
/// üretiliyor; GCM'de nonce tekrarı anahtarı fiilen kırıyor.
/// </para>
///
/// <para>
/// <b>T25'te <c>Bizigo.Alerting</c>'den buraya taşındı.</b> Connector'ların
/// alarm motoruna bağımlı olması yanlış yön olurdu, ve ikinci bir şifreleme
/// şeması yazmak iki anahtar, iki rotasyon hikâyesi demekti. Anahtarın nereden
/// geldiği <see cref="SecretProtectionOptions"/>'ta karara bağlı.
/// </para>
/// </summary>
public sealed class SecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[]? _key;

    public SecretProtector(SecretProtectionOptions options)
        : this(options?.SecretKey)
    {
    }

    /// <param name="base64Key">
    /// base64, 32 bayt. Boş/<see langword="null"/> ise koruyucu
    /// <b>yapılandırılmamış</b> sayılıyor ve şifreleme reddediliyor.
    /// </param>
    public SecretProtector(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            _key = null;
            return;
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException ex)
        {
            // Mesaj anahtarın DEĞERİNİ değil, biçimini söylüyor.
            throw new InvalidOperationException(
                $"{SecretProtectionOptions.SectionName}:SecretKey base64 değil. 32 baytlık bir anahtar bekleniyor.", ex);
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException(
                $"{SecretProtectionOptions.SectionName}:SecretKey {key.Length} bayt; AES-256 için 32 bayt gerekiyor.");
        }

        _key = key;
    }

    /// <summary>
    /// Anahtar yapılandırılmış mı. Değilse gizli bilgi <b>kaydedilemiyor</b>:
    /// düz metne düşmek, "şifreli saklanıyor" iddiasını sessizce yalanlardı.
    /// </summary>
    public bool IsConfigured => _key is not null;

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var key = _key ?? throw new InvalidOperationException(
            "Security:SecretKey tanımlı değil; gizli bilgi şifrelenemiyor.");

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[bytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, bytes, cipher, tag);
        }

        var packed = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceSize);
        cipher.CopyTo(packed, NonceSize + TagSize);

        return Convert.ToBase64String(packed);
    }

    /// <summary>
    /// Çözer. Bozuk ya da kurcalanmış veri <b>istisna</b> üretiyor.
    ///
    /// <para>
    /// İstisna mesajında ne şifreli metin ne de anahtar geçiyor — hata
    /// mesajlarının gizli bilgi taşıması, bu ticket'ın kapatmak istediği tam
    /// olarak o yol.
    /// </para>
    /// </summary>
    public string Unprotect(string cipherText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cipherText);

        var key = _key ?? throw new InvalidOperationException(
            "Security:SecretKey tanımlı değil; gizli bilgi çözülemiyor.");

        byte[] packed;
        try
        {
            packed = Convert.FromBase64String(cipherText);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Saklanan gizli bilgi okunamadı.", ex);
        }

        if (packed.Length < NonceSize + TagSize)
        {
            throw new InvalidOperationException("Saklanan gizli bilgi eksik.");
        }

        var nonce = packed.AsSpan(0, NonceSize);
        var tag = packed.AsSpan(NonceSize, TagSize);
        var cipher = packed.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "Saklanan gizli bilgi doğrulanamadı; anahtar değişmiş ya da kayıt kurcalanmış olabilir.", ex);
        }

        return Encoding.UTF8.GetString(plain);
    }
}
