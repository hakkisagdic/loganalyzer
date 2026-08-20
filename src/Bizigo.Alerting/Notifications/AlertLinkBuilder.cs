using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
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
/// <b>Filtreler bağlantıya GÖMÜLÜYOR</b> — ve bu, ilk hâlin tersi. İlk tasarım
/// yalnızca kural kimliğini taşıyıp ekranın kuralı kimliğinden okumasını
/// öngörüyordu; hiç bağlanmadı ve sonuç sessizce yanlıştı: kullanıcı doğru
/// ekrana, doğru aralığa, ama kuralın alan filtreleri olmadan gidiyordu. "5
/// dakikada <c>action=deny</c> &gt; 100" alarmı o beş dakikanın <b>bütün</b>
/// olaylarını gösteren bir ekran açıyordu.
/// </para>
///
/// <para>
/// Kimliği çözdürme fikri ayrıca <b>yanlış zamanı</b> gösterirdi: bağlantı bir
/// kez üretilip bildirime gömülüyor ve kullanıcı günler sonra tıklıyor. O arada
/// kural düzenlenmişse kimliği çözen ekran <i>bugünkü</i> kuralı gösterir,
/// tetiklenme anındakini değil. Filtreleri bağlantının kendisi taşıyınca
/// bağlantı <b>o anın fotoğrafı</b> oluyor — tıpkı zaman aralığı gibi.
/// </para>
///
/// <para>
/// Çeviri yeni bir tasarım değil: <c>ui/src/lib/alerts/criteria-bridge.ts</c>
/// tablosunun <b>ters yönü</b>. <c>kural=&lt;guid&gt;</c> kalıyor ama artık
/// filtrenin taşıyıcısı değil, yalnızca <b>kaynak göstergesi</b>: "bu aramayı
/// hangi kural açtı".
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
    /// <summary>
    /// Ekranın tanımadığı filtreleri bildiren parametre.
    ///
    /// <para>
    /// Sessizce düşmemeleri şart: bir kural <c>src_ip</c> üzerinden filtreliyorsa
    /// ve arama ekranında o alanın karşılığı yoksa, bağlantı kuralın izlediğinden
    /// <b>daha geniş</b> bir küme açar. Ekran bu parametreyi görünce kullanıcıya
    /// "bu alarmın şu filtreleri burada gösterilemiyor" diyor — yani kullanıcı
    /// gördüğü kümenin alarmın kümesi olmadığını biliyor.
    /// </para>
    /// </summary>
    public const string UnsupportedParam = "eksik";

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

        // Kapsam: kuralın kendi grupları. `criteria-bridge` bunu ileri yönde
        // "filtre değil kapsam" diye işaretliyor; ters yönde ekranın
        // `owner_group` parametresine birebir denk geliyor.
        var groups = rule.OwnerGroups
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var group in groups)
        {
            query.Add("owner_group=" + Uri.EscapeDataString(group));
        }

        var search = Search(rule);

        if (!string.IsNullOrWhiteSpace(search.FullText))
        {
            query.Add("q=" + Uri.EscapeDataString(search.FullText));
        }

        foreach (var status in search.ParseStatuses)
        {
            query.Add("parse_status=" + Uri.EscapeDataString(status.ToString().ToLowerInvariant()));
        }

        // Kaynak: açıkça verilen kazanıyor (sessizlik alarmı tek kaynağı
        // işaret ediyor), yoksa kuralın kaydedilmiş araması.
        var source = !string.IsNullOrWhiteSpace(sourceId)
            ? sourceId
            : search.SourceIds.FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(source))
        {
            // Kaynak filtresi yalnızca kolaylık değil ölçülmüş bir kısıt: keyset
            // sayfalama ancak sıralama anahtarının tam öneki (owner_group +
            // source_id) verildiğinde sabit süreli (F1 §ölçümler).
            query.Add("source_id=" + Uri.EscapeDataString(source));
        }

        var unsupported = new List<string>();

        foreach (var filter in search.Filters)
        {
            if (Translate(filter) is { } translated)
            {
                query.Add(translated);
            }
            else
            {
                // Sessizce DÜŞMÜYOR: ekran bunu kullanıcıya söyleyecek.
                unsupported.Add(filter.Field);
            }
        }

        if (unsupported.Count > 0)
        {
            query.Add(UnsupportedParam + "=" + Uri.EscapeDataString(
                string.Join(',', unsupported.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))));
        }

        return root + "?" + string.Join('&', query);
    }

    /// <summary>
    /// Tek bir alan filtresinin URL karşılığı; yoksa <see langword="null"/>.
    ///
    /// <para>
    /// Eşleme <c>criteria-bridge.ts</c>'in aynadaki hâli. Orada olmayan bir
    /// kolon burada da yok — ve <b>uydurulmuyor</b>: ekranın tanımadığı bir
    /// parametre üretmek, filtreyi sessizce düşürmekle aynı sonucu verirdi.
    /// </para>
    /// </summary>
    private static string? Translate(FieldFilter filter)
    {
        var value = filter.Values.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return (filter.Field, filter.Operator) switch
        {
            ("vendor", FilterOperator.Equals) => "vendor=" + Uri.EscapeDataString(value),
            ("proto", FilterOperator.Equals) => "proto=" + Uri.EscapeDataString(value),
            ("action", FilterOperator.Equals) => "action=" + Uri.EscapeDataString(value),

            // İleri yönde ekranın "n ve üzeri"si `gt n-1`'e çevriliyordu; geri
            // yönde n-1'e 1 eklenip özgün eşiğe dönülüyor. Çevirinin iki yönü
            // birbirini götürmezse alarm bir kademe kayık bir ekran açar.
            ("severity_num", FilterOperator.GreaterThan) when int.TryParse(
                value, NumberStyles.None, CultureInfo.InvariantCulture, out var floor) =>
                "severity_min=" + (floor + 1).ToString(CultureInfo.InvariantCulture),

            _ => null,
        };
    }

    private static AlertSearch Search(AlertRuleEntity rule)
    {
        try
        {
            return AlertSearchCodec.Deserialize(rule.SearchJson);
        }
        catch (JsonException)
        {
            // Okunamayan arama, filtresiz bir bağlantı demek — ama sessiz
            // değil: `eksik` işaretini koyamıyoruz çünkü hangi alan olduğunu
            // bilmiyoruz, o yüzden bağlantı hiç üretilmiyor.
            return new AlertSearch();
        }
    }
}
