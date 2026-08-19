namespace Bizigo.Contracts.Security;

/// <summary>
/// Ürünün <b>tek</b> gizli bilgi anahtarı (T22 kurdu, T25 paylaşıma çıkardı).
///
/// <para>
/// <b>Neden tek anahtar:</b> T22 bildirim kanalı gizli bilgilerini
/// <c>Alerting:SecretKey</c> altında şifreliyordu. T25 aynı ihtiyacı connector
/// kimlik bilgileri için getirdi ve ikinci bir anahtar tanımlamak ikinci bir
/// rotasyon hikâyesi demekti: iki ayrı ortam değişkeni, iki ayrı "değiştirirsen
/// neyi yeniden şifrelemen gerekir" cevabı, ve altı ay sonra birinin
/// döndürülüp diğerinin unutulması. Anahtar tek, kullanan modül çok.
/// </para>
///
/// <para>
/// <b>Anahtar nerede duruyor — bu karar geri alınamaz.</b> Ortam değişkeni
/// (<c>Security__SecretKey</c>) seçildi; dosya ve dış KMS <b>seçilmedi</b>.
/// Gerekçe: ürün compose ve konteyner ortamında koşuyor, ortam değişkeni her
/// ikisinde de birinci sınıf ve <c>appsettings.json</c>'a yanlışlıkla
/// commit'lenemiyor. Dosya, dizin izinlerini işletim sistemine bırakırdı; dış
/// KMS (Vault, AWS KMS) 50 kişilik bir kurumda kurulum yükünü şifrelemenin
/// kendisinden büyük yapardı ve F2'de böyle bir bağımlılık yok.
/// </para>
///
/// <para>
/// Değiştirmek isteyen için yol açık: <see cref="SecretProtector"/> anahtarı
/// dizge olarak alıyor, nereden geldiğini bilmiyor. KMS'e geçiş bu sınıfın
/// yerine bir çözücü koymak — saklanmış kayıtları yeniden şifrelemek yine de
/// gerekir, o yüzden karar bugün veriliyor.
/// </para>
/// </summary>
public sealed class SecretProtectionOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// base64, 32 bayt (AES-256). Boşsa gizli bilgi <b>kaydedilemiyor</b> —
    /// düz metne düşmek, "şifreli saklanıyor" iddiasını sessizce yalanlardı.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;
}
