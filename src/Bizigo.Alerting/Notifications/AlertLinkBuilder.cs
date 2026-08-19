using System.Globalization;
using Bizigo.ControlPlane;

namespace Bizigo.Alerting.Notifications;

/// <summary>
/// Bildirimdeki "şuna bak" bağlantısı (T22).
///
/// <para>
/// <b>Küçük görünen, işe yararlığı belirleyen parça.</b> "Bir şey oldu" ile
/// "şuna bak" arasındaki fark bu bağlantı: yanlış zaman aralığını açan bir alarm,
/// kullanıcıyı olayın olmadığı bir ekrana götürüp güveni bir kerede bitiriyor.
/// Aralık bu yüzden tetiklenmenin <b>kendi</b> penceresinden geliyor, "son 15
/// dakika" gibi bir varsayılandan değil.
/// </para>
///
/// <para>
/// <b>Filtreler bağlantıya gömülmüyor, kural kimliği taşınıyor.</b> Kaydedilmiş
/// aramanın alan filtrelerini URL'e kodlamak, aynı sorgu tanımının iki yerde
/// (kuralda ve bağlantıda) durması demek olurdu; kural düzenlendiğinde eski
/// bildirimlerdeki bağlantılar sessizce yanlış sorguyu açardı. Ekran kuralı
/// kimliğinden okuyor, aralığı bağlantıdan alıyor.
/// </para>
/// </summary>
public static class AlertLinkBuilder
{
    /// <summary>
    /// Bağlantı üretiyor; kök yapılandırılmamışsa <see langword="null"/>.
    ///
    /// <para>
    /// Yapılandırılmamış kökle tahmini bir adres uydurmak, kullanıcıyı hiçbir
    /// yere götürmeyen bir bağlantı vermek olurdu — bağlantısız bir mesaj bundan
    /// dürüst.
    /// </para>
    /// </summary>
    public static string? Build(
        AlertingOptions options,
        AlertRuleEntity rule,
        DateTimeOffset windowFrom,
        DateTimeOffset windowTo,
        string? sourceId = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rule);

        if (string.IsNullOrWhiteSpace(options.ProductBaseUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(options.ProductBaseUrl.TrimEnd('/') + options.SearchPath, UriKind.Absolute, out var root))
        {
            return null;
        }

        // Tur boyunca UTC kullanılıyor; bağlantı da UTC taşımalı. Yerel saate
        // çevirmek, alıcının hangi zaman diliminde olduğuna göre farklı aralık
        // açan bir bağlantı üretirdi.
        var query = new List<string>
        {
            "from=" + Uri.EscapeDataString(windowFrom.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            "to=" + Uri.EscapeDataString(windowTo.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            "kural=" + Uri.EscapeDataString(rule.Id.ToString()),
        };

        if (!string.IsNullOrWhiteSpace(sourceId))
        {
            // Kaynak filtresi yalnızca kolaylık değil ölçülmüş bir kısıt: keyset
            // sayfalama ancak sıralama anahtarının tam öneki (owner_group +
            // source_id) verildiğinde sabit süreli (F1 §ölçümler).
            query.Add("source_id=" + Uri.EscapeDataString(sourceId));
        }

        return root + "?" + string.Join('&', query);
    }
}
