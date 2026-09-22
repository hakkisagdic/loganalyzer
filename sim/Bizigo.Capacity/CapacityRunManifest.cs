using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bizigo.Capacity;

/// <summary>
/// <b>Bir yük koşumunun manifesti</b> — üretecin bastığını <i>iddia ettiği</i>
/// şey (B01).
///
/// <para>
/// <b>Şekli seçilmedi, TÜKETİCİSİNDEN okundu.</b> <see cref="ArrivalLedger"/>
/// bugün ağaçta ve testli; manifest ise yoktu. §8'in yönü: bir üreticinin şekli
/// <b>var olan tüketicisine</b> göre kurulur, tersi değil. Yeni bir şekil
/// seçilseydi iki temsil doğar ve biri diğerine uymaya çalışırdı — §9'un
/// yasakladığı hâl.
/// </para>
///
/// <para>
/// Defterin beklediği üç şey ve karşılıkları:
/// </para>
///
/// <list type="table">
/// <item>
/// <term><c>ArrivalLedger.RunId</c></term>
/// <description><see cref="RunId"/> — deneme başına ayrı, çünkü bir kademenin
/// gecikmiş olayları diğerinin sayısını kirletiyor.</description>
/// </item>
/// <item>
/// <term><c>ArrivalLedger.Expected</c></term>
/// <description><see cref="Expected"/> — basılan satır sayısı.</description>
/// </item>
/// <item>
/// <term><c>ArrivalLedger.ProductArchived</c> bir <b>sayım değil eşleşme</b>
/// ve eşleşme <c>sha256</c> üzerinden kuruluyor</term>
/// <description><see cref="Digests"/> — satır başına özet. Toplam sayı
/// yetmezdi: düz bir sayım, bir satırın kaybolup başka birinin iki kez
/// yazılmasını <b>görmez</b>.</description>
/// </item>
/// </list>
///
/// <para>
/// <b>Ve manifest hükmü de taşıyor</b> (<see cref="Attainment"/>) — bu, tel
/// sözleşmesine eklenen tek şey ve gerekçesi bugünün kuralı:
/// <i>"yanlış katmanı suçlamak hiç suçlamamaktan kötüdür, arama yanlış yerde
/// başlar."</i> <c>Expected</c> tek başına dolaşırsa defter onu bir <b>olgu</b>
/// gibi okur; oysa üreteç hedefine ulaşamadıysa o sayı bir <b>iddia</b> ve
/// eksik olan satırlar <b>hiç basılmamıştır</b>. Hükmü sayının yanına
/// çivilemek, <c>Expected</c>'in yorumlanabilir olup olmadığını okuyanın
/// hatırlamasına bırakmıyor.
/// </para>
///
/// <para>
/// <b>Ürün sayaçları burada YOK ve olmayacak.</b> Manifest yalnızca üretecin
/// yaptığını biliyor; ham arşiv ve <c>events</c> sayıları deftere ürün
/// tarafından veriliyor. İkisini tek yere yazmak <c>GENERATOR-LIMITED</c> ile
/// <c>LEDGER-LIMITED</c> ayrımını yok ederdi — B02'nin
/// <c>ProcessedRecords</c>/<c>AcceptedRecords</c>'ı bilerek dışarıda
/// bırakmasının aynı gerekçesi: defterin var olma sebebi ürünün <b>dışından</b>
/// saymak.
/// </para>
/// </summary>
/// <param name="RunId">Koşum kimliği; deneme başına ayrı.</param>
/// <param name="Profile">Hangi profil koştu — rapora giriyor.</param>
/// <param name="Attainment">
/// Üreteç tarafındaki hüküm. <see cref="Expected"/> ancak
/// <see cref="GeneratorAttainment.LossIsInterpretable"/> iken bir kayıp
/// hesabının paydası olabilir.
/// </param>
/// <param name="Digests">
/// Basılan satırların <c>sha256</c> özetleri, <b>basılma sırasında</b>. Sıra
/// korunuyor çünkü boşluk deseni sınıflandırması (B04) *"baştan mı, sondan mı,
/// blok hâlinde mi"* diye soruyor ve o soru sırasız bir kümede cevaplanamaz.
/// </param>
/// <param name="GeneratorOnSameHost">
/// Üreteç ile hedef <b>aynı makinede mi</b>. <see langword="null"/> =
/// <b>söylenmedi</b>, ve söylenmemişse rapor bunu yazmak zorunda: aynı
/// makinede koşan bir üreteç ölçtüğü sistemin CPU'sunu yiyor, yani sayı iki
/// yükün toplamının tavanı olur. Kapasite belgesi §6'nın üç açık sorusundan
/// biri; <b>varsayılmıyor</b>.
/// </param>
public sealed record CapacityRunManifest(
    string RunId,
    string Profile,
    GeneratorAttainment Attainment,
    IReadOnlyList<string> Digests,
    bool? GeneratorOnSameHost)
{
    /// <summary>
    /// Üretecin bastığını <b>iddia ettiği</b> satır sayısı —
    /// <c>ArrivalLedger.Expected</c>'in kaynağı.
    /// </summary>
    public long Expected => Digests.Count;

    /// <summary>
    /// <b>Bu manifest bir kayıp hesabının paydası olabilir mi.</b>
    ///
    /// <para>
    /// İki şart <b>birlikte</b>: hüküm kaybı yorumlanabilir kılıyor
    /// <b>ve</b> üretecin nerede koştuğu söylenmiş. İkincisi olmadan sayı
    /// yorumlanabilir görünür ama neyin tavanı olduğu bilinmez — ve o hâl
    /// *"ölçtüm"* diye raporlanırsa arama yanlış yerde başlar.
    /// </para>
    /// </summary>
    public bool CanAnchorLossAccounting =>
        Attainment.LossIsInterpretable && GeneratorOnSameHost is not null;

    /// <summary>
    /// Satırın manifeste giren hâlinin özeti — <b>tel üzerindeki baytların</b>
    /// özeti.
    ///
    /// <para>
    /// <c>UTF-8</c> değil <see cref="Encoding.Latin1"/>: bu depoda tel
    /// kodlaması bayt ↔ kod noktası eşlemesini birebir ve tersinir tutmak için
    /// <c>iso-8859-1</c> üzerinden taşınıyor (collector <c>encoding:
    /// iso-8859-1</c>, ürün tarafında <c>Latin1.GetBytes</c>). Özeti UTF-8 ile
    /// almak, ham arşivdeki baytlarla <b>eşleşmeyen</b> bir özet üretirdi ve
    /// <c>ProductArchived</c> sessizce sıfır çıkardı.
    /// </para>
    /// </summary>
    public static string Digest(string wireLine)
    {
        ArgumentNullException.ThrowIfNull(wireLine);

        return Digest(Encoding.Latin1.GetBytes(wireLine));
    }

    /// <summary>
    /// Gerçek basıcının kullandığı kanonik yol: özet, kodlama uygulanmış tel
    /// baytlarından alınır. Dizgi overload'u yalnızca Latin-1 ile taşınmış ham
    /// arşiv metnini geri çevirmek içindir.
    /// </summary>
    public static string Digest(ReadOnlySpan<byte> wireBytes) =>
        Convert.ToHexStringLower(SHA256.HashData(wireBytes));

    /// <summary>
    /// Rapor satırı — sayıyı <b>hükmüyle birlikte</b> basıyor.
    ///
    /// <para>
    /// Sayının tek başına basıldığı bir biçim yok, ve bu bilinçli: bir
    /// <c>Expected</c> değerini hükümsüz gören okuyucu onu bir olgu sanar.
    /// </para>
    /// </summary>
    public string Describe()
    {
        var host = GeneratorOnSameHost switch
        {
            true => "üreteç hedefle AYNI makinede (CPU paylaşılıyor)",
            false => "üreteç ayrı makinede",
            null => "üretecin nerede koştuğu SÖYLENMEDİ",
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"koşum={RunId} profil={Profile} basılan={Expected} · {host} · {Attainment.Describe()}");
    }
}
