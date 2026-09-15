using Bizigo.Rca.Reasoning;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Cümle bölme kuralı — T47 §5'in üçüncü tereddüdünün ölçümü ve DÜZELTMESİ.</b>
///
/// <h3>Bu paket iki hâli birden çiviliyor</h3>
///
/// <para>
/// İlk yazıldığında bugünkü davranışı çiviliyordu ve düzeltmiyordu; gerekçe
/// *"bölme kuralını değiştirmek atılan cümle oranının tanımını değiştirir"*
/// idi. O gerekçenin bir <b>koşulu</b> vardı ve tutmuyordu: değiştirmenin
/// bedeli korunacak bir <b>geçmiş</b> olduğunda doğar, ve bu ölçü bugüne kadar
/// <b>bağlayıcı tek bir sayı üretmedi</b> (T47 açık, canlı model koşumu
/// yapılmadı). Yani düzeltme bir tanım değişikliği değil, tanımın <b>ilk
/// kullanımdan önce</b> doğru kurulması.
/// </para>
///
/// <para>
/// Her testin yanında <b>eski hâlin neden yanlış olduğu</b> duruyor. Ölçülüp
/// <b>yanlışlanan</b> iddia da duruyor: ticket *"ondalık yanlış
/// bölünebiliyor"* diyordu ve bu <b>doğru değildi</b>. Silinmiyor, çünkü bir
/// sonraki okuyan aynı endişeyi tekrar üretecek.
/// </para>
///
/// <h3>Ölçüt neden kuralın metninde değil davranışında</h3>
///
/// <para>
/// Kural <c>(?&lt;=[.!?])</c> + iki lookbehind + <c>\s+</c>. Regex'i test
/// etmek yerine <see cref="SentenceBinder.Bind"/> çağrılıyor: ölçülmek istenen
/// şey desen değil, <b>kaç cümle sayıldığı</b> — çünkü atılan cümle oranının
/// paydası o sayı.
/// </para>
/// </summary>
public sealed class SentenceSplitMeasurementTests
{
    private static readonly IReadOnlySet<string> Visible =
        new HashSet<string>(StringComparer.Ordinal) { "EV-1", "EV-2" };

    private static int SentenceCount(string text) => SentenceBinder.Bind(text, Visible).Sentences.Count;

    /// <summary>
    /// <b>Ondalık, IP, sürüm ve alan adı bölünmüyor — ve HİÇ bölünmüyordu.</b>
    ///
    /// <para>
    /// Ticket'ın *"ondalık yanlış bölünebiliyor"* endişesi ölçüldü ve
    /// <b>yanlış çıktı</b>: kural noktadan sonra <b>boşluk</b> istiyor,
    /// <c>3.14</c>'te boşluk yok. Bu yüzden düzeltme bu hâl için <b>hiçbir
    /// koruma eklemedi</b> — eklenecek bir şey yoktu.
    /// </para>
    ///
    /// <para>
    /// Test duruyor çünkü iddianın yanlışlandığı <b>kayıt</b>: bir sonraki
    /// okuyan aynı endişeyi tekrar üretip gereksiz bir koruma yazmaya kalkarsa
    /// burası cevabı veriyor.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Yanıt süresi 3.14 saniyeye çıktı [EV-1].")]
    [InlineData("Kaynak 10.0.0.1 adresinden geldi [EV-1].")]
    [InlineData("Sürüm net10.0 ile derlendi [EV-1].")]
    [InlineData("İstek api.kurum.local üzerinden gitti [EV-1].")]
    public void Noktadan_sonra_bosluk_yoksa_bolunmuyor(string text)
    {
        Assert.Equal(1, SentenceCount(text));
    }

    /// <summary>
    /// <b>Kısaltma artık cümleyi bölmüyor.</b>
    ///
    /// <para>
    /// <b>ESKİ HÂL YANLIŞTI:</b> bu girdilerin her biri <b>iki</b> cümle
    /// üretiyordu. Kısaltmadan sonraki parça çoğu zaman atfı taşımıyor, yani
    /// bölünme doğrudan <i>atılan cümle oranının payına</i> yazılıyordu —
    /// model kötü yazmadığı hâlde metrik *"kötü yazdı"* diyordu.
    /// </para>
    ///
    /// <para>
    /// Düzeltme bir sezgi değil <b>kapalı bir liste</b>
    /// (<c>SentenceBinder.Abbreviations</c>): türetmeye çalışmak, tam olması
    /// gereken ve tam olmadığı gün körleşen bir liste yazmak olurdu.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Arayüz, vekil vb. bileşenler etkilendi [EV-1].")]
    [InlineData("Örn. bağlantı havuzu doldu [EV-1].")]
    [InlineData("Ayrıntı bkz. kanıt paketi [EV-1].")]
    [InlineData("Havuz, kuyruk vs. hepsi doldu [EV-1].")]
    public void Kisaltma_cumleyi_artik_bolmuyor(string text)
    {
        Assert.Equal(1, SentenceCount(text));
    }

    /// <summary>
    /// <b>Numaralı liste maddesi artık bölünmüyor — ve ticket bunu
    /// SAYMIYORDU.</b>
    ///
    /// <para>
    /// <b>ESKİ HÂL YANLIŞTI ve en pahalı hâl buydu:</b> modeller gerekçeyi
    /// numaralı liste hâlinde yazıyor, yani kural <b>en sık karşılaştığı
    /// biçimi</b> bölüyordu. Madde numarası <c>1.</c> ardından boşluk geldiği
    /// için her madde iki parçaya ayrılıyor ve numarayı taşıyan parça atıfsız
    /// kalıyordu.
    /// </para>
    ///
    /// <para>
    /// <b>Neden ayrı bir kural:</b> *"noktadan sonra küçük harf geliyorsa
    /// bölme"* sezgisi bunu kapatmıyor — <c>1. Kök</c> <b>büyük</b> harfle
    /// devam ediyor. Tek sezgiyle çözmek, ölçülen iki hâlden birini sessizce
    /// açık bırakırdı.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("1. Kök neden bağlantı havuzu [EV-1]")]
    [InlineData("2. İkinci bulgu şu [EV-2]")]
    [InlineData("  3. Girintili madde de aynı [EV-1]")]
    public void Numarali_liste_maddesi_artik_bolunmuyor(string text)
    {
        Assert.Equal(1, SentenceCount(text));
    }

    /// <summary>
    /// <b>Numaralı liste yine de MADDELER ARASINDA bölünüyor.</b>
    ///
    /// <para>
    /// Koruma yalnızca madde numarasını hedefliyor; satır sonu <b>hâlâ</b> bir
    /// sınır. Aksi hâlde düzeltme bütün listeyi tek cümle sayardı ve bu kez
    /// oran <b>ters yöne</b> kayardı — atıfsız maddeler atıflı bir maddenin
    /// arkasına saklanırdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Maddeler_arasi_sinir_duruyor()
    {
        Assert.Equal(3, SentenceCount("1. Birinci [EV-1]\n2. İkinci [EV-2]\n3. Üçüncü"));
    }

    /// <summary>
    /// <b>Yan yana ölçüm — düzeltmenin asıl kanıtı.</b>
    ///
    /// <para>
    /// <b>ESKİ HÂL:</b> kısaltmalı cümle <c>Dropped = 1</c>, aynı cümlenin
    /// kısaltmasız hâli <c>Dropped = 0</c>. Yani <b>aynı bilgi</b>, farklı
    /// yazım, farklı metrik. <b>YENİ HÂL:</b> ikisi de <c>0</c>.
    /// </para>
    ///
    /// <para>
    /// Çift olarak ölçülüyor ve tek sayı yazılmıyor: <c>Dropped = 1</c> tek
    /// başına *"model kötü yazdı"* diye okunurdu. Farkın <b>yazımdan</b>
    /// geldiğini gösteren şey karşılaştırmanın kendisi.
    /// </para>
    /// </summary>
    [Fact]
    public void Ayni_bilgi_farkli_yazim_ayni_metrik()
    {
        var kisaltmali = SentenceBinder.Bind("Arayüz, vekil vb. bileşenler etkilendi [EV-1].", Visible);
        var kisaltmasiz = SentenceBinder.Bind("Arayüz ve vekil bileşenleri etkilendi [EV-1].", Visible);
        var numarali = SentenceBinder.Bind("1. Kök neden bağlantı havuzu [EV-1]", Visible);

        Assert.Equal(0, kisaltmali.Dropped);
        Assert.Equal(0, kisaltmasiz.Dropped);
        Assert.Equal(0, numarali.Dropped);

        // Ve üçünde de atıf tanınıyor: "atılmadı" ile "hiç görülmedi" ayrı
        // şeyler, ve ikincisi de bir kusur olurdu.
        Assert.Equal(1, kisaltmali.Sentences.Count(static s => s.Bound));
        Assert.Equal(1, numarali.Sentences.Count(static s => s.Bound));
    }

    /// <summary>
    /// <b>Listede OLMAYAN kısaltma hâlâ bölüyor</b> — kapalı listenin itirafı.
    ///
    /// <para>
    /// Bu bir kusur değil, listenin <b>sınırı</b>, ve ölçülü olması şart:
    /// düzeltmenin *"kısaltmaları çözdük"* diye okunması, bir sonraki kişinin
    /// listede olmayan bir kısaltmayı aramamasına yol açardı. Yeni bir kısaltma
    /// ölçüldüğünde çözüm listeye bir satır — sezgi değil.
    /// </para>
    /// </summary>
    [Fact]
    public void Listede_olmayan_kisaltma_hala_boluyor()
    {
        // `müh.` listede yok.
        Assert.Equal(2, SentenceCount("Rapor müh. ekibine gitti [EV-1]."));
    }

    /// <summary>
    /// <b>Ölçüm aracının kendisi:</b> bölme hiç çalışmıyorsa yukarıdaki
    /// sayıların çoğu 1 olur ve testlerin yarısı <b>sessizce</b> geçer. Bu test
    /// bölmenin gerçekten böldüğünü sabitliyor (§6) — ve düzeltmeden sonra iki
    /// kat daha gerekli oldu, çünkü artık *"bölmüyor"* beklentisi çoğunlukta.
    /// </summary>
    [Fact]
    public void Bolme_gercekten_boluyor()
    {
        Assert.Equal(3, SentenceCount("Birinci cümle [EV-1]. İkinci cümle [EV-2]. Üçüncü cümle."));
        Assert.Equal(2, SentenceCount("Satır sonu da sınır [EV-1]\nİkinci satır [EV-2]"));
        Assert.Equal(2, SentenceCount("Soru mu [EV-1]? Cevap bu [EV-2]."));
    }
}
