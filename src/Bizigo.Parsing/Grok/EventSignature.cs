using System.IO.Hashing;
using System.Text;

namespace Bizigo.Parsing.Grok;

/// <summary>
/// Bir olayın maskelenmiş imzası ve onun sabit genişlikli kimliği (K35).
///
/// <para>
/// İkisi <b>birlikte</b> dönüyor çünkü ayrı hesaplanırlarsa bir gün ayrışırlar
/// ve ayrıştıkları hiçbir yerde görünmez: yanlış hesaplanmış bir
/// <c>signature_hash</c> istisna atmaz, sorgu düşürmez, yalnızca RCA'nın
/// "ilk-görülen imza" ve "hacim sapması" sinyallerini sessizce bozar.
/// </para>
///
/// <para>
/// <see cref="Text"/> keşif yolunun (T12) kullandığı insan-okunur imza;
/// <see cref="Hash"/> ClickHouse'a inen ve saf SQL korelasyonların üzerine
/// kurulduğu değer.
/// </para>
/// </summary>
/// <param name="Text">Maskelenmiş satır — boş ise imza üretilemedi.</param>
/// <param name="Hash"><see cref="SignatureHash.Of"/> sonucu; <c>0</c> = imza yok.</param>
public readonly record struct EventSignature(string Text, ulong Hash)
{
    /// <summary>İmza üretilemedi: boş gövde ya da uzunluk sınırını aşan satır.</summary>
    public static EventSignature None => new(string.Empty, SignatureHash.None);

    public bool IsEmpty => Hash == SignatureHash.None;
}

/// <summary>
/// <c>events.signature_hash</c>'in tanımı. <b>Tek yerde</b> duruyor: ingest,
/// replay ve testler aynı fonksiyonu çağırmak zorunda, yoksa replay her satırı
/// "değişti" diye raporlar ve kimse sebebini bulamaz.
/// </summary>
public static class SignatureHash
{
    /// <summary>
    /// "İmza yok" değeri. Uzunluk sınırını aşan satır
    /// (<see cref="MaskCatalog.MaxInputLength"/>) ve boş gövde buraya düşüyor.
    ///
    /// <para>
    /// Ayrı bir <c>Nullable(UInt64)</c> kolonu yerine ayrılmış bir değer
    /// seçildi: null, her satıra bir bayt ve her korelasyon sorgusuna bir
    /// <c>IS NOT NULL</c> ekliyordu, oysa "imzası yok" durumu zaten
    /// <c>signature_hash = 0</c> ile ifade edilebiliyor.
    /// </para>
    /// </summary>
    public const ulong None = 0;

    /// <summary>
    /// <b>Neyin hash'i alınıyor:</b> yalnızca <b>maskelenmiş metin</b>. Ham satır
    /// değil, vendor/kaynak/host <b>değil</b>.
    ///
    /// <para>
    /// Bu karar geriye dönük değiştirilemez: değişirse bugünden sonraki satırlar
    /// geçmiştekilerle eşleşmez ve "ilk-görülen imza" bir gün boyunca <b>her</b>
    /// imzayı yeni sanır. O yüzden hem burada yazılı hem de teste sabitlenmiş
    /// (<c>SignatureHashTests</c>).
    /// </para>
    ///
    /// <para>
    /// <b>Vendor neden dahil değil:</b> aynı imza iki kaynaktan geldiğinde aynı
    /// hash'i vermek zorunda — RCA'nın "yayılma sırası" sinyali tam olarak bunu
    /// soruyor ("aynı şey kaç cihazda, hangi sırayla belirdi"). Kaynak bazlı
    /// ayrım gerektiğinde SQL zaten <c>owner_group</c>/<c>source_id</c>/
    /// <c>vendor</c> kolonlarına <c>GROUP BY</c> yapıyor; hash'e gömmek o seçimi
    /// sorgudan alıp yazma anına hapsederdi.
    /// </para>
    ///
    /// <para>
    /// <b>Neden XXH64:</b> ClickHouse'un <c>xxHash64()</c> fonksiyonuyla birebir
    /// aynı — yani hash'in doğruluğu üretim veritabanının kendisine karşı
    /// sınanabiliyor (<c>SignatureHashClickHouseTests</c>). Kriptografik hash
    /// gerekmiyor: burada saldırgan modeli yok, tek gereken deterministik ve
    /// ucuz bir kimlik. 64 bit çakışma payı ~10⁷ ayrı imzada 10⁻⁶ mertebesinde
    /// ve bedeli tek bir imzanın "yeni değil" sanılması.
    /// </para>
    ///
    /// <para>
    /// <b>Maske sözlüğü sürümü hash'e girmiyor</b> — bilinçli.
    /// <see cref="MaskCatalog.Version"/>'ı girdiye eklemek, sözlük her
    /// güncellendiğinde <b>bütün</b> geçmiş hash'leri geçersiz kılardı ve
    /// "ilk-görülen" o gün her satır için ateşlerdi. Sürüm dışarıda bırakıldığında
    /// yalnızca <b>gerçekten etkilenen</b> maskelerin imzaları kayıyor; bu da
    /// doğru davranış, çünkü o satırların maskelenmiş metni fiilen değişmiş
    /// oluyor. Yani sözleşme şu: <c>signature_hash</c> maskelenmiş metnin
    /// kimliğidir, sözlüğün değil. Sözlük değişimi bilinen bir kayma üretir ve
    /// bir bekçi testi sürüm numarasını sabitleyerek bu değişimin kazara
    /// yapılmasını engelliyor.
    /// </para>
    /// </summary>
    public static ulong Of(string maskedText)
    {
        ArgumentNullException.ThrowIfNull(maskedText);

        if (maskedText.Length == 0)
        {
            return None;
        }

        var bytes = Encoding.UTF8.GetBytes(maskedText);
        var hash = XxHash64.HashToUInt64(bytes);

        // 0 "imza yok" için ayrıldı. Gerçek bir imzanın 0'a düşme olasılığı 2⁻⁶⁴
        // ama düşerse o satır sessizce "imzasız" görünürdü — tam da bu ticket'ın
        // önlemeye çalıştığı hata sınıfı. Bir sonraki değere kaydırmak, o tek
        // satırı 1'e düşen imzalarla birleştirmekten başka bir bedel taşımıyor.
        return hash == None ? 1UL : hash;
    }
}
