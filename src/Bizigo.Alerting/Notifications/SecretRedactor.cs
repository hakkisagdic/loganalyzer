namespace Bizigo.Alerting.Notifications;

/// <summary>
/// Gizli bilgiyi metinden söker (T22 kabul kriteri).
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
    /// Maskelenecek en kısa parça. Altı karakterin altı maskelenmiyor: <c>https</c>,
    /// <c>api</c>, <c>v1</c> gibi parçalar her mesajda geçiyor ve hepsini
    /// maskelemek hata mesajını okunamaz hâle getirir — okunamayan bir hata
    /// mesajı da kendi başına bir arıza.
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

        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;

        foreach (var fragment in Fragments(secrets))
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
