using System.Globalization;

namespace Bizigo.Capacity;

/// <summary>
/// Varış defterinin katmanları — <b>kaybın yerleştirilebileceği yerler</b>.
///
/// <para>
/// Bugün *"hiçbir satır kaybolmuyor"* iddiasının tamamı ürünün kendi
/// sayaçlarına dayanıyor (WAL, <c>raw_manifest</c>, <c>parse_status</c>,
/// <c>bound_ratio</c>) ve bu yüzden <b>üç ayrı arıza aynı görünüyor</b>:
/// çekirdek düşürdü, collector düşürdü, boru hattı düşürdü. Defterin tek işi
/// bu üçünü ayırmak.
/// </para>
/// </summary>
public enum LedgerLayer
{
    /// <summary>
    /// Tel — OS soket sayaçları. Linux <c>/proc/net/udp</c> <c>drops</c>,
    /// Windows <c>netstat -s</c> UDP receive errors.
    /// <b>Bu katman varışı değil DÜŞÜRMEYİ sayıyor</b>: kanıtı negatif.
    /// </summary>
    Wire,

    /// <summary>
    /// Collector — OTel Collector'ın <b>kendi</b> metrikleri
    /// (<c>otelcol_receiver_accepted_log_records</c>, <c>..._refused_...</c>).
    /// </summary>
    Collector,

    /// <summary>
    /// Ürün — ham arşive <b>dayanıklı yazılan</b> ve <c>events</c>'te
    /// <b>aranabilir olan</b> satır sayısı. İkisi ayrı sorular ve ayrı
    /// okunuyor: dayanıklı yazılmış ama aranamayan bir satır gerçek bir arıza
    /// sınıfı.
    /// </summary>
    Product,
}

/// <summary>
/// Bir katmandan alınan <b>tek</b> okuma — ve okumanın <b>güvenilir olup
/// olmadığı</b>.
///
/// <para>
/// <b><see cref="Value"/> neden nullable:</b> bu tipin var olma sebebi
/// <c>LEDGER-LIMITED</c>. Bir sayaç okunamadığında sıfır dönmek, *"kayıp yok"*
/// demek olurdu — ve bu deponun en pahalı hata sınıfı tam olarak o (§7: sessiz
/// yanlış davranış). <c>null</c> *"ölçemedim"* demek, ve <see cref="LimitReason"/>
/// nedenini <b>yazıyor</b>.
/// </para>
///
/// <para>
/// <b>Gerekçe ölçülmüş bir olaydan geliyor.</b> <c>tools/machine-resources.sh</c>
/// bu depoya girdiğinde Linux'ta bellek okuyucusu <c>100</c>, disk okuyucusu
/// <c>0</c> basıyordu — ikisi de sessiz, ikisinin yönü zıt, ve ikisi de bir
/// ölçüm değil. Defter aynı tuzağa düşemez: <b>platform okuyucusu yoksa uydurma
/// sayı değil, <c>Limited</c>.</b>
/// </para>
/// </summary>
/// <param name="Layer">Hangi katman.</param>
/// <param name="Source">
/// Sayının <b>nereden</b> geldiği (<c>/proc/net/udp</c>, <c>netstat -s</c>,
/// <c>otelcol_receiver_accepted_log_records</c>, <c>raw_manifest</c>…).
/// Rapora yazılıyor: kaynağı yazılmayan bir sayı, bir sonraki turda yeniden
/// üretilemez.
/// </param>
/// <param name="Value">Okunan sayı; <c>null</c> ise ölçülemedi.</param>
/// <param name="LimitReason">
/// Ölçülemediyse <b>neden</b>. <c>null</c> olması ile boş dize olması aynı şey
/// değil: <see cref="Measured"/> fabrikası nedeni hiç almıyor, çünkü ölçülmüş
/// bir okumanın kısıt gerekçesi olamaz.
/// </param>
public sealed record LedgerReading(
    LedgerLayer Layer,
    string Source,
    long? Value,
    string? LimitReason)
{
    /// <summary>Okuma güvenilir mi.</summary>
    public bool IsMeasured => Value.HasValue;

    public static LedgerReading Measured(LedgerLayer layer, string source, long value) =>
        new(layer, source, value, null);

    /// <summary>
    /// <b>Ölçemedim.</b> Neden <b>zorunlu</b> — gerekçesiz bir kısıt, kısıtın
    /// kendisinden kötü: rapor *"ölçemedim"* der ve okuyan *neyi*
    /// düzelteceğini bilemez.
    /// </summary>
    public static LedgerReading Limited(LedgerLayer layer, string source, string reason) =>
        new(
            layer,
            source,
            null,
            string.IsNullOrWhiteSpace(reason)
                ? throw new ArgumentException("Kısıt gerekçesi boş olamaz.", nameof(reason))
                : reason);

    public string Describe() => IsMeasured
        ? string.Create(CultureInfo.InvariantCulture, $"{Layer}/{Source} = {Value}")
        : string.Create(CultureInfo.InvariantCulture, $"{Layer}/{Source} = ÖLÇÜLEMEDİ ({LimitReason})");
}
