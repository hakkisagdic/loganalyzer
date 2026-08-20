using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Bizigo.ControlPlane;

/// <summary>
/// Saklanan kanıt paketi (T36, RCA §4.1).
///
/// <para>
/// <b>Neden saklanıyor:</b> F4 aynı kanıt üzerinde farklı model/prompt
/// koşturup karşılaştıracak. Saklanmasaydı her karşılaştırma kanıtı yeniden
/// toplamak zorunda kalırdı ve o sırada veri değişmiş olurdu — yani
/// karşılaştırma modeli değil, veriyi ölçerdi. Aynı sebep regresyon testini de
/// mümkün kılıyor: model değiştiğinde eski paketler üzerinde yeniden koşulur.
/// </para>
///
/// <para>
/// <b>Neden ClickHouse değil Postgres:</b> paket bir <b>kontrol düzlemi</b>
/// kaydı — sayısı olay tablosuyla kıyaslanamayacak kadar az (koşu başına bir
/// satır), tekil kimlikle okunuyor ve RCA raporuyla yabancı anahtar ilişkisi
/// kuracak (T37, F4). ClickHouse'un güçlü olduğu hiçbir şeye ihtiyaç yok.
/// </para>
///
/// <para>
/// <b>Neden gövde tek bir belge:</b> kanıt satırları bir <b>anlık görüntü</b>,
/// sorgulanan bir çalışma kümesi değil. Kimse "paketler arasında ağırlığı 3'ten
/// büyük satırlar" diye sormuyor; paket bütün olarak okunuyor. İlişkisel bir
/// alt tablo, her şema göçünde geçmiş satırlara <c>NULL</c> kolonlar ekleyip
/// eski paketleri sessizce farklı bir şekle sokardı. Belge + açık
/// <see cref="SchemaVersion"/> o donmayı görünür ve sınanabilir yapıyor.
/// </para>
/// </summary>
[Table("evidence_bundles")]
public sealed class EvidenceBundleEntity
{
    [Key]
    public Guid Id { get; set; }

    public DateTimeOffset GatheredAt { get; set; }

    /// <summary>
    /// Paket biçiminin sürümü. Kolon olarak duruyor (belgenin içinde de var):
    /// "bugünkü kod hangi sürümleri okuyabiliyor" sorusu <b>belgeyi açmadan</b>
    /// cevaplanabilmeli, yoksa okunamayan bir paketi bulmanın tek yolu onu
    /// okumaya çalışmak olur.
    /// </summary>
    public int SchemaVersion { get; set; }

    /// <summary>
    /// Aynı pencere + kapsam + kanıt için aynı değer. Duvar saati taşıyan
    /// alanlar dışarıda (bkz. <c>BundleSerializer.HashOf</c>).
    ///
    /// <para>
    /// Tekil <b>değil</b>, bilerek: aynı pencerenin iki kez toplanması meşru bir
    /// iş (F4 karşılaştırması) ve ikisi de saklanmalı. İndeks yalnızca "bu kanıt
    /// daha önce toplanmış mı" sorusunu ucuzlatıyor.
    /// </para>
    /// </summary>
    [MaxLength(64)]
    public string ContentHash { get; set; } = string.Empty;

    // --- Sorgulanabilir üst veri -------------------------------------------
    // Belgenin içinde de duruyorlar. Kopya olmaları bilinçli: paket listesi
    // ekranı ("son RCA'lar", "şu pencereye ait paketler") her satır için JSON
    // açmak zorunda kalmamalı. Tek yazan taraf var — depo — ve bir test ikisinin
    // ayrışmadığını tutuyor.

    public DateTimeOffset WindowFrom { get; set; }

    public DateTimeOffset WindowTo { get; set; }

    public DateTimeOffset BaselineFrom { get; set; }

    public DateTimeOffset BaselineTo { get; set; }

    /// <summary>Kapsam dışı toplam (RCA §3.2) — yalnızca sayı, içerik değil.</summary>
    public long OutOfScopeCount { get; set; }

    /// <summary>Kanıt eksik mi toplandı — liste ekranında rozet.</summary>
    public bool IsPartial { get; set; }

    /// <summary>
    /// Paketin tamamı, <c>BundleSerializer</c> biçiminde.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string Payload { get; set; } = string.Empty;
}
