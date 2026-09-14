namespace Bizigo.Contracts.Security;

/// <summary>
/// Gölge katmanın (T41 · katman A) bir adayının <b>mekanik</b> sınıfı — T60.
///
/// <para>
/// <b>Neden var.</b> T41 gölge sayacını ölçtü ve sayı okunamaz çıktı: altın
/// korpusta <c>aday=59 payda=96 oran=0.6146</c>. Tek bir oran *"kaç belirteç
/// daha maskelenirdi"* sorusunu cevaplıyor ama *"ne maskelenirdi"* sorusuna
/// hiç dokunmuyor — ve terfi kararını belirleyen ikincisi. 59'un içinde
/// adresler, port numaraları, zaman damgaları, ürün sürümleri ve FortiGate
/// imza adları vardı; yani oran yükseldiğinde okunacak şey *"sır riski arttı"*
/// değil *"prompt'ta daha çok adres geçti"* oluyordu.
/// </para>
///
/// <para>
/// <b>Sınıflandırma yalnızca RAPORLUYOR, hiçbir maskeleme kararını
/// KAPATMIYOR.</b> Bu cümle bu tipin en önemli kısıtı ve T41 §4'ün
/// asimetrisinden doğuyor: fazla maskeleme ölçülebilir, kaçırma ölçülemez.
/// Bir sınıfı *"sır olamaz"* diye maskelemenin dışında bırakmak, ölçülemeyen
/// yöne yeni bir kapı açmak demek. Sınıf yalnızca sayacın yanına yazılıyor;
/// katman A maskelemediği için sınıflandırmanın kör noktaları <b>bugün
/// hiçbir şeye mal olmuyor</b>. Bu, sınıflandırmayı güvenli kılan tek şey —
/// A terfi ederse aynı sınıflar birden kapı olur ve o hareket ayrı bir karar.
/// </para>
///
/// <para>
/// <b>Sözcük listesi yok.</b> Ayrım karakter sınıflarına ve ayraç yapısına
/// bakıyor: hangi sözcüklerin geçtiğine değil. Bu depo elle tutulan listelerin
/// bekçiyi körleştirdiğini beş kez ölçtü; ayrıca bir imza adı listesi
/// tanımıyla eskiyen bir liste olurdu (yeni imza her gün doğuyor).
/// </para>
/// </summary>
public enum ShadowTokenClass
{
    /// <summary>
    /// Entropisi <b>birleştirmenin</b> ürünü: ayraçla bölünen kısa parçalar.
    /// <c>outside:192.168.80.32/53</c>, <c>25/Oct/2016:14:49:33</c>,
    /// <c>Chrome/81.0.4044.138</c>, <c>Adobe.Flash.newfunction.Handling.Code.Execution</c>.
    /// Bu sınıfın tamamı <b>RCA'nın raporlamak istediği şey</b>.
    /// </summary>
    Composite,

    /// <summary>
    /// Ayraçlar çıkarıldığında tamamı onaltılık ve en az bir rakam taşıyan
    /// belirteç: özet (sha256), UUID, oturum kimliği — <b>ve onaltılık
    /// kodlanmış bir anahtar</b>.
    ///
    /// <para>
    /// <b>Bu sınıfın ayrı adı olmasının sebebi tam olarak bu ikilik.</b>
    /// 64 karakterlik bir sha256 ile 64 karakterlik bir WPA PSK aynı
    /// alfabeden, aynı uzunlukta ve aynı entropiyle geliyor — <b>ayırt
    /// edilemezler</b>, çünkü fark kodlamada değil bağlamda. Sınıfı
    /// <see cref="Opaque"/> içine yutmak bu ayırt edilemezliği görünmez
    /// yapardı.
    /// </para>
    /// </summary>
    Hexadecimal,

    /// <summary>
    /// Ayraçsız, tamamı harf tek bir dizi: <c>someotherrouteridagain</c>.
    /// Uzun bir sözcük Shannon eşiğini geçebiliyor — entropi bir <b>alfabe
    /// çeşitliliği</b> ölçüsü, rastgelelik ölçüsü değil.
    /// </summary>
    Word,

    /// <summary>
    /// Ayraçsız <b>en az 12 karakterlik</b> bir dizi taşıyan, o dizide en az
    /// iki karakter sınıfı (küçük · büyük · rakam · diğer) bulunan ve
    /// onaltılık olmayan belirteç.
    ///
    /// <para>
    /// <b>Bilinmeyen bir sırrın saklanabildiği tek sınıf.</b> T41'in sahte sır
    /// fixture'larındaki değerlerin hepsi buraya düşüyor (ölçüldü, T60).
    /// Tersi doğru değil: bu sınıfa düşen her şey sır değil — bir Debian sürüm
    /// dizgesi de (<c>0.8.16~exp12ubuntu10.21</c>) buraya düşüyor.
    /// </para>
    /// </summary>
    Opaque,
}

/// <summary>
/// <see cref="ShadowTokenClass"/> kararını veren tek yer.
///
/// <para>
/// <b>Sıra önemli ve gerekçeli:</b> onaltılık kontrolü <see cref="ShadowTokenClass.Opaque"/>'tan
/// ÖNCE geliyor, çünkü bir UUID'nin son grubu (<c>d1d2ce663f4b</c>) opak dizi
/// ölçütünü de karşılıyor. Onaltılık daha <b>dar</b> bir iddia — alfabesi
/// yazılı — ve dar iddia geniş olanı yutmamalı.
/// </para>
/// </summary>
public static class ShadowTokenClassifier
{
    /// <summary>
    /// Belirteci parçalara bölen yapısal ayraçlar. Gölge sayacının belirteç
    /// ayırıcıları (boşluk, tırnak, <c>=</c>, <c>,</c>, parantezler) bu listede
    /// <b>yok</b>: onlar belirteci hiç oluşturmuyor, buraya gelen şey zaten tek
    /// bir belirteç.
    /// </summary>
    private static readonly char[] StructureSeparators =
        ['.', ':', '/', '-', '_', '%', '~', '@', '+', '?', '&', '#', '\\', '*', '!', '$', '^', '='];

    /// <summary>
    /// Bir dizinin <see cref="ShadowTokenClass.Opaque"/> sayılması için gereken
    /// en kısa uzunluk.
    ///
    /// <para>
    /// <b>Bu bir eşik ve TAŞIYICI — ölçüldü</b> (T60,
    /// <c>Sinif_ekseninde_temiz_bir_ayrim_noktasi_yok</c>). Altın korpusun opak
    /// sayısı 4→17, 8→9, 12→3, 15→0 diye iniyor. İlk hâlinde bu yorumda
    /// *"taşıyıcı değil"* yazıyordu ve ölçüm onu <b>yanlışladı</b>.
    /// </para>
    ///
    /// <para>
    /// <b>Değerin 12 olmasının gerekçesi bu eğrinin şekli:</b> 15'te altın
    /// korpusta hiç opak kalmıyor ama <b>aynı noktada sahte sırların biri de
    /// opak olmaktan çıkıyor</b> — yani temiz bir ayrım noktası yok. 12,
    /// sırların hepsinin opak kaldığı bandın (≤14) içinde ve altın gürültünün
    /// üçe indiği ilk nokta.
    /// </para>
    ///
    /// <para>
    /// <b>Eşiğin taşıyıcı olması bugün bir sorun değil ve yarın olurdu:</b>
    /// sınıf yalnızca raporun etiketi olduğu sürece kayan bir eşik yalnızca
    /// etiketi kaydırıyor. Bir maskeleme kapısına bağlanırsa aynı eşik
    /// ölçülmemiş bir muafiyet hâline gelir — T41 §4'ün yasakladığı yön.
    /// </para>
    /// </summary>
    public const int OpaqueRunLength = 12;

    /// <summary>
    /// Belirtecin mekanik sınıfı. Boş girdi <see cref="ShadowTokenClass.Composite"/>
    /// dönüyor — sınıflandırmanın "hiçbir şey" hâli en zararsız hâl olmalı,
    /// çünkü <see cref="ShadowTokenClass.Opaque"/> raporun dikkat çeken sınıfı.
    ///
    /// <para>
    /// <paramref name="opaqueRunLength"/> <b>parametre</b>, çünkü eşiğin
    /// taşıyıcı olup olmadığı ölçülüyor (T60): aynı sınıflandırıcı 8–20
    /// bandında süpürülüyor. Sabit olsaydı ölçüm ikinci bir sınıflandırıcı
    /// yazmak zorunda kalırdı — §9'un yasakladığı şey, ve ölçümün ölçtüğü
    /// şeyin ürünün kullandığı şey olmadığı hâl.
    /// </para>
    /// </summary>
    public static ShadowTokenClass Classify(string? token, int opaqueRunLength = OpaqueRunLength)
    {
        if (string.IsNullOrEmpty(token))
        {
            return ShadowTokenClass.Composite;
        }

        var segments = token.Split(StructureSeparators, StringSplitOptions.RemoveEmptyEntries);

        if (Hexadecimal(segments))
        {
            return ShadowTokenClass.Hexadecimal;
        }

        foreach (var segment in segments)
        {
            if (segment.Length >= opaqueRunLength && CharacterClasses(segment) >= 2)
            {
                return ShadowTokenClass.Opaque;
            }
        }

        return segments.Length == 1 && segments[0].All(char.IsLetter)
            ? ShadowTokenClass.Word
            : ShadowTokenClass.Composite;
    }

    /// <summary>
    /// Tamamı onaltılık <b>ve en az bir rakam taşıyor</b>.
    ///
    /// <para>
    /// Rakam şartı olmasa <c>deadbeefcafedecade</c> gibi yalnızca onaltılık
    /// harflerden kurulu bir sözcük de özet sanılırdı. Şart bir sözcük listesi
    /// değil, aynı mekanik eksende bir daralma.
    /// </para>
    /// </summary>
    private static bool Hexadecimal(string[] segments)
    {
        var digit = false;

        foreach (var segment in segments)
        {
            foreach (var c in segment)
            {
                if (!char.IsAsciiHexDigit(c))
                {
                    return false;
                }

                digit |= char.IsAsciiDigit(c);
            }
        }

        return digit;
    }

    /// <summary>
    /// Karakter sınıfı sayısı: küçük harf · büyük harf · rakam · diğer.
    /// Kültür duyarlı çağrı yok — <c>char.IsLower</c> Unicode kategorisine
    /// bakıyor, <c>ToLower()</c> gibi <c>tr-TR</c> tuzağı taşımıyor.
    /// </summary>
    private static int CharacterClasses(string segment)
    {
        var lower = false;
        var upper = false;
        var digit = false;
        var other = false;

        foreach (var c in segment)
        {
            if (char.IsLower(c))
            {
                lower = true;
            }
            else if (char.IsUpper(c))
            {
                upper = true;
            }
            else if (char.IsDigit(c))
            {
                digit = true;
            }
            else
            {
                other = true;
            }
        }

        return (lower ? 1 : 0) + (upper ? 1 : 0) + (digit ? 1 : 0) + (other ? 1 : 0);
    }
}
