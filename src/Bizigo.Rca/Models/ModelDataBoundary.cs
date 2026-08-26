namespace Bizigo.Rca.Models;

/// <summary>
/// Bir model ucunun <b>hangi tarafta</b> durduğu — K6'nın çizdiği tek eksen.
///
/// <para>
/// <b>Eksen "yerel / uzak" DEĞİL.</b> K6 kararının kendi metni uzak bir GPU
/// kümesini açıkça kapsıyor: <i>"Yerel (Ollama/vLLM) + uzak GPU cluster …
/// Log verisi kurum dışına çıkmaz."</i> Yani uzaklık yasak değil; yasak olan
/// <b>kurum dışına çıkmak</b>. Kurum içindeki bir GPU kümesi ile aynı makinedeki
/// Ollama, bu eksende <b>aynı</b> yerde duruyor.
/// </para>
///
/// <para>
/// RCA §2.1 aynı ayrımı açılış cümlesinde yapıyor: K6 <b>ağ sınırını</b>
/// çiziyor, prompt'un <b>içeriği</b> ayrı bir soru. Bu enum ağ sınırının
/// tarafı; içerik düzeyi <see cref="PromptContentLevel"/>.
/// </para>
/// </summary>
public enum ModelDataBoundary
{
    /// <summary>
    /// Beyan edilmedi. <b>Varsayılan bu ve kabul edilmiyor</b> —
    /// <see cref="ModelBoundaryGate"/> bu değerde ucu reddediyor.
    ///
    /// <para>
    /// Sıfırın "iç ağ" sayılması bu deponun en pahalı hata sınıfı olurdu:
    /// yapılandırmayı yazan kişi alanı hiç görmemiş olur, ürün çalışır, ve
    /// kurumun en büyük sözü <b>hiç kimse karar vermeden</b> boşa çıkar.
    /// Ölçülmemiş bir sınır çalışıyor sayılmaz.
    /// </para>
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Uç kurum sınırının içinde. Beyan <b>doğrulanıyor</b>: çözülen her adres
    /// yönlendirilemeyen (loopback / özel / benzersiz-yerel / bağlantı-yerel)
    /// olmak zorunda.
    /// </summary>
    Internal = 1,

    /// <summary>
    /// Uç kurum sınırının dışında. <b>Hiçbir içerik düzeyi geçmiyor</b> —
    /// <c>summary</c> dahil.
    ///
    /// <para>
    /// Gerekçe: özet de müşteri verisi. <i>"edge-rtr-07 14:02'de sustu"</i>
    /// cümlesi host adı, sahiplik grubu ve topoloji taşıyor. K6 "ham log
    /// çıkmaz" demiyor, <b>"log verisi çıkmaz"</b> diyor — ve özet o verinin
    /// türevi. Kapıyı düzey eksenine kurmak, bu cümleyi düzeylerden birine
    /// istisna yazmak olurdu.
    /// </para>
    /// </summary>
    External = 2,
}
