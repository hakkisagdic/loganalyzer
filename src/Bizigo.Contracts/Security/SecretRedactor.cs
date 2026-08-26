namespace Bizigo.Contracts.Security;

/// <summary>
/// Gizli bilgiyi metinden söker (T22 kabul kriteri; T25'te paylaşıma çıktı).
///
/// <para>
/// <b>Neden şifrelemenin üstüne ayrıca bu var:</b> şifreleme gizli bilgiyi
/// <i>durduğu yerde</i> koruyor, ama alarm yolunda gizli bilgi bir de
/// <b>kullanılırken</b> sızıyor — hata mesajında, log satırında, API yanıtında.
/// F1'in dersi tam olarak buydu: doğrulanmamış her katman kırıktı ve hiçbiri
/// kendini belli etmedi. Şifreli bir webhook URL'i, ilk 500 hatasında düz metin
/// olarak loga düşerse şifreleme hiçbir işe yaramamış olur.
/// </para>
///
/// <para>
/// <b>Parça parça maskeliyor, sadece tam eşleşmeyi değil.</b> Bir istisna mesajı
/// URL'in tamamını değil çoğu zaman bir parçasını taşıyor — host adını, yolu ya
/// da sorgu değerini. Yalnızca tam dizgeyi arasaydık koruma ilk gerçek hatada
/// boşa çıkardı.
/// </para>
/// </summary>
public static class SecretRedactor
{
    public const string Mask = "[gizli]";

    /// <summary>
    /// Maskelenecek en kısa <b>türetilmiş</b> parça. Altı karakterin altı
    /// maskelenmiyor: <c>https</c>, <c>api</c>, <c>v1</c> gibi parçalar her
    /// mesajda geçiyor ve hepsini maskelemek hata mesajını okunamaz hâle
    /// getirir — okunamayan bir hata mesajı da kendi başına bir arıza.
    ///
    /// <para>
    /// <b>T41'de gözden geçirildi ve korundu — ama yalnızca bu yolda.</b>
    /// Eşiğin gerekçesi <see cref="Fragments(IEnumerable{string?})"/>'ın
    /// <i>türettiği</i> parçalarla ilgili: bir webhook URL'inden çıkan
    /// <c>api</c> parçası sır değil, gürültü. <see cref="RedactExact"/> yolunda
    /// böyle bir türetme yok — oradaki değer, bir sır atamasının sağ tarafı
    /// olarak <b>bulunmuş</b> bir değer. ASA'nın dört karakterlik bir SNMP
    /// community'si gerçek bir sır, ve altı karakterlik bir taban onu sessizce
    /// atlardı: hata yok, sayaç yok, belirti yok.
    /// </para>
    /// </summary>
    private const int MinFragment = 6;

    public static string Redact(string? text, params string?[] secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        return Redact(text, (IEnumerable<string?>)secrets);
    }

    public static string Redact(string? text, IEnumerable<string?> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        return Apply(text, Fragments(secrets));
    }

    /// <summary>
    /// <b>Keşifle bulunmuş</b> değerleri maskeler: türev çıkarılmaz, uzunluk
    /// tabanı uygulanmaz (T41).
    ///
    /// <para>
    /// <see cref="Redact(string?, IEnumerable{string?})"/> "şu sırrı biliyorum,
    /// metinde parçası geçiyor olabilir" sorusunu cevaplıyor ve bu yüzden
    /// türetiyor. Burada soru başka: değer <b>bu metnin içinde</b>, aynen,
    /// bulundu. Türetecek bir şey yok — ve türetilseydi <c>password</c> alanına
    /// yazılmış bir URL'in host'u bütün prompt boyunca maskelenirdi.
    /// </para>
    ///
    /// <para>
    /// İkame tarafı ortak: aynı <see cref="Apply"/>, aynı uzundan kısaya sıra.
    /// İkinci bir ikame uygulaması yok (CLAUDE.md §9).
    /// </para>
    /// </summary>
    public static string RedactExact(string? text, IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                set.Add(value.Trim());
            }
        }

        return Apply(text, [.. set.OrderByDescending(static x => x.Length)]);
    }

    /// <summary>
    /// İkamenin tek uygulaması. Parçalar buraya <b>uzundan kısaya</b> sıralı
    /// geliyor ve sıralamak çağıranın işi; bozulursa sonuç sessiz: kısa parça
    /// önce maskelenirse uzun parça artık metinde bulunamaz ve geri kalanı
    /// açıkta kalır.
    /// </summary>
    private static string Apply(string? text, IReadOnlyList<string> ordered)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;

        foreach (var fragment in ordered)
        {
            result = result.Replace(fragment, Mask, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// Bir gizli bilgiden türeyen tüm maskelenebilir parçalar, <b>uzundan kısaya</b>.
    ///
    /// <para>
    /// Sıra önemli: önce kısa parçayı maskelersek uzun parça artık metinde
    /// bulunamaz ve geri kalanı açıkta kalır. Uzundan başlamak bunu engelliyor.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Fragments(IEnumerable<string?> secrets)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var secret in secrets)
        {
            if (string.IsNullOrWhiteSpace(secret))
            {
                continue;
            }

            var trimmed = secret.Trim();
            Add(set, trimmed);

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                continue;
            }

            // URL'in her anlamlı parçası ayrı ayrı: istisna mesajları çoğunlukla
            // tamamını değil birini taşıyor.
            Add(set, uri.Host);
            Add(set, uri.Authority);
            Add(set, uri.AbsolutePath);
            Add(set, uri.PathAndQuery);
            Add(set, uri.GetLeftPart(UriPartial.Authority));

            foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                Add(set, segment);
            }

            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var index = pair.IndexOf('=', StringComparison.Ordinal);
                Add(set, index >= 0 ? pair[(index + 1)..] : pair);
            }
        }

        return [.. set.OrderByDescending(static x => x.Length)];
    }

    private static void Add(HashSet<string> set, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Length >= MinFragment)
        {
            set.Add(value);
        }
    }
}
