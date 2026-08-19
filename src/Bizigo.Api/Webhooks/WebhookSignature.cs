using System.Security.Cryptography;
using System.Text;

namespace Bizigo.Api.Webhooks;

public enum SignatureVerdict
{
    Valid = 0,

    /// <summary>İmza başlığı hiç gelmedi.</summary>
    Missing = 1,

    /// <summary>Başlık var ama eşleşmiyor ya da biçimi bozuk.</summary>
    Invalid = 2,

    /// <summary>Uçta gizli anahtar tanımlı değil. İstek <b>kabul edilmiyor</b>.</summary>
    NotConfigured = 3,
}

/// <summary>
/// Webhook imza doğrulaması (T24 kabul kriteri: doğrulanmamış istek kayıt
/// oluşturmaz).
///
/// <para>
/// <b>Gizli anahtar hiçbir çıktıya girmiyor.</b> Bu tipin döndürdüğü tek şey bir
/// <see cref="SignatureVerdict"/>; ne beklenen imzayı, ne gelen imzayı, ne de
/// anahtarı bir mesaja koyuyor. Sızıntının en olası yolu "beklenen X, gelen Y"
/// diye yardımcı olmaya çalışan bir hata mesajıdır — o mesaj burada yok ve bir
/// birim testi yokluğunu sabitliyor.
/// </para>
///
/// <para>
/// Karşılaştırma <see cref="CryptographicOperations.FixedTimeEquals"/> ile:
/// sıradan bir <c>==</c> ilk farklı bayta kadar geçen süreyi sızdırır ve imza
/// bayt bayt tahmin edilebilir hâle gelir.
/// </para>
/// </summary>
public static class WebhookSignature
{
    /// <summary>Kendi başlığımız: Jenkins'in ve genel sağlayıcıların standardı yok.</summary>
    public const string DefaultHeader = "X-Bizigo-Signature";

    /// <summary>GitHub: <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c>.</summary>
    public const string GitHubHeader = "X-Hub-Signature-256";

    /// <summary>
    /// GitLab HMAC yapmıyor: paylaşılan jetonu düz metin başlıkta yolluyor.
    /// Zayıf ama sağlayıcının verdiği tek şey bu; TLS zorunluluğu bunun için var.
    /// </summary>
    public const string GitLabHeader = "X-Gitlab-Token";

    public static string HeaderFor(ChangeWebhookEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!string.IsNullOrWhiteSpace(endpoint.SignatureHeader))
        {
            return endpoint.SignatureHeader;
        }

        return endpoint.Provider switch
        {
            ChangeWebhookProviders.GitHub => GitHubHeader,
            ChangeWebhookProviders.GitLab => GitLabHeader,
            _ => DefaultHeader,
        };
    }

    /// <param name="header">Başlık okuyucu. Bulunmayan başlık için <see langword="null"/>.</param>
    public static SignatureVerdict Verify(
        ChangeWebhookEndpoint endpoint,
        Func<string, string?> header,
        ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(header);

        // Anahtarsız uç açık kapıdır. "Gizli anahtar yoksa doğrulamayı atla"
        // varsayılanı, yapılandırmadaki tek eksik satırı sessiz bir güvenlik
        // açığına çevirirdi.
        if (string.IsNullOrEmpty(endpoint.Secret))
        {
            return SignatureVerdict.NotConfigured;
        }

        var presented = header(HeaderFor(endpoint));

        if (string.IsNullOrWhiteSpace(presented))
        {
            return SignatureVerdict.Missing;
        }

        return endpoint.Provider == ChangeWebhookProviders.GitLab
            ? VerifyToken(endpoint.Secret, presented)
            : VerifyHmac(endpoint.Secret, presented, body);
    }

    private static SignatureVerdict VerifyToken(string secret, string presented)
    {
        var expected = Encoding.UTF8.GetBytes(secret);
        var actual = Encoding.UTF8.GetBytes(presented.Trim());

        return CryptographicOperations.FixedTimeEquals(expected, actual)
            ? SignatureVerdict.Valid
            : SignatureVerdict.Invalid;
    }

    private static SignatureVerdict VerifyHmac(
        string secret,
        string presented,
        ReadOnlySpan<byte> body)
    {
        // `sha256=` öneki GitHub'ın biçimi; kendi başlığımızda da öneksiz hâli
        // kabul ediyoruz ki elle `openssl dgst` çıktısı yapıştırılabilsin.
        var value = presented.Trim();
        const string Prefix = "sha256=";

        if (value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[Prefix.Length..];
        }

        byte[] actual;

        try
        {
            actual = Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            // Bozuk hex "geçersiz imza"dır — ayrı bir hata sınıfı değil.
            return SignatureVerdict.Invalid;
        }

        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, expected);

        return CryptographicOperations.FixedTimeEquals(expected, actual)
            ? SignatureVerdict.Valid
            : SignatureVerdict.Invalid;
    }

    /// <summary>
    /// Test ve belge amaçlı imza üretimi — üretim yolunda çağrılmıyor.
    /// Aynı algoritmayı iki yerde yazmamak için burada duruyor.
    /// </summary>
    public static string Compute(string secret, ReadOnlySpan<byte> body)
    {
        Span<byte> hash = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, hash);

        return "sha256=" + Convert.ToHexStringLower(hash);
    }
}
