using Bizigo.Rca.Reasoning;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>T47 §5'in üçüncü tereddüdünün ölçümü</b> — cümle bölme noktalama tabanlı,
/// ve *"kısaltma ve ondalık sayı yanlış bölünebiliyor"* deniyordu. <b>Ölçüldü,
/// ve iddianın yarısı yanlıştı.</b>
///
/// <h3>Ölçüt neden bu testte duruyor</h3>
///
/// <para>
/// Bölme kuralı <c>(?&lt;=[.!?])\s+|\r?\n+</c>, yani <b>noktalama + boşluk</b>.
/// Buradan çıkan ayrım tahmin edilenden keskin: bir nokta ancak <b>ardından
/// boşluk gelirse</b> sınır sayılıyor.
/// </para>
///
/// <list type="bullet">
/// <item><b>Ondalık sayı GÜVENLİ</b> — <c>3.14</c>'te noktadan sonra boşluk
/// yok. Aynı sebeple IP adresi (<c>10.0.0.1</c>), sürüm (<c>net10.0</c>) ve
/// alan adı da güvenli. Ticket'ın *"ondalık yanlış bölünebiliyor"* cümlesi
/// <b>bugünkü kural için doğru değil</b>.</item>
/// <item><b>Kısaltma GÜVENLİ DEĞİL</b> — <c>vb.</c>, <c>örn.</c>, <c>bkz.</c>
/// ardından boşluk geliyor ve cümle ikiye ayrılıyor.</item>
/// <item><b>Numaralı liste de güvenli değil ve ticket bunu SAYMIYORDU</b> —
/// <c>1. Kök neden …</c> madde numarasından sonra bölünüyor. Modellerin
/// numaralı liste üretmesi kısaltmadan daha sık.</item>
/// </list>
///
/// <h3>Bedeli neden ölçüme giriyor</h3>
///
/// <para>
/// Yanlış bölme <b>sessiz</b> değil ama <b>ücretsiz</b> de değil: atıf
/// parçalardan yalnızca birinde kalıyorsa diğer parça <b>atıfsız</b> sayılıyor
/// ve <i>atılan cümle oranının</i> payına yazılıyor. Yani ölçünün paydası ile
/// payı birlikte oynuyor — T47'nin *"ölçünün tamamı paydada"* cümlesinin
/// arkasındaki sınıf.
/// </para>
///
/// <para>
/// <b>Bu testler bugünkü davranışı ÇİVİLİYOR, düzeltmiyor.</b> Bölme kuralını
/// değiştirmek atılan cümle oranının tanımını değiştirir — yani ölçünün kendi
/// tabanını. Bu bir ajan kararı değil; öneri T47 §5'te yazılı, karar
/// koordinatörde.
/// </para>
/// </summary>
public sealed class SentenceSplitMeasurementTests
{
    private static readonly IReadOnlySet<string> Visible =
        new HashSet<string>(StringComparer.Ordinal) { "EV-1", "EV-2" };

    private static int SentenceCount(string text) => SentenceBinder.Bind(text, Visible).Sentences.Count;

    /// <summary>
    /// <b>Ondalık, IP, sürüm ve alan adı bölünmüyor.</b> Sebep tek: noktadan
    /// sonra boşluk yok.
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
    /// <b>Kısaltma cümleyi ikiye bölüyor</b> — ticket'ın doğru çıkan yarısı.
    /// </summary>
    [Theory]
    [InlineData("Arayüz, vekil vb. bileşenler etkilendi [EV-1].")]
    [InlineData("Örn. bağlantı havuzu doldu [EV-1].")]
    [InlineData("Ayrıntı bkz. kanıt paketi [EV-1].")]
    public void Kisaltma_cumleyi_ikiye_boluyor(string text)
    {
        Assert.Equal(2, SentenceCount(text));
    }

    /// <summary>
    /// <b>Numaralı liste de bölünüyor — ve ticket bunu saymıyordu.</b>
    ///
    /// <para>
    /// Modeller gerekçeyi numaralı liste hâlinde yazıyor; madde numarası
    /// <c>1.</c> ardından boşluk geldiği için her madde <b>iki</b> cümleye
    /// ayrılıyor. Yani en sık karşılaşılacak hâl, ticket'ta hiç anılmayan hâl.
    /// </para>
    /// </summary>
    [Fact]
    public void Numarali_liste_maddesi_ikiye_bolunuyor()
    {
        Assert.Equal(2, SentenceCount("1. Kök neden bağlantı havuzu [EV-1]"));
    }

    /// <summary>
    /// <b>Bedelin ölçümü: yanlış bölme atılan cümle sayısını ARTIRIYOR.</b>
    ///
    /// <para>
    /// Atıf ikinci parçada kalıyor, birinci parça atıfsız sayılıyor. Aynı
    /// cümlenin kısaltmasız hâli tek parça ve <b>hiç</b> atılan cümle
    /// üretmiyor. İkisinin yan yana ölçülmesi, farkın bölmeden geldiğini
    /// kanıtlayan şey — tek başına ölçülen bir sayı "model kötü yazdı" diye de
    /// okunabilirdi.
    /// </para>
    /// </summary>
    [Fact]
    public void Yanlis_bolme_atilan_cumle_sayisini_artiriyor()
    {
        var kisaltmali = SentenceBinder.Bind("Arayüz, vekil vb. bileşenler etkilendi [EV-1].", Visible);
        var kisaltmasiz = SentenceBinder.Bind("Arayüz ve vekil bileşenleri etkilendi [EV-1].", Visible);

        Assert.Equal(1, kisaltmali.Dropped);
        Assert.Equal(0, kisaltmasiz.Dropped);

        // Ve atıf taşıyan parça bağlanıyor: kayıp bir "atıf tanınmadı" hâli
        // değil, bir "cümle ikiye ayrıldı" hâli.
        Assert.Equal(1, kisaltmali.Sentences.Count(static s => s.Bound));
    }

    /// <summary>
    /// <b>Ölçüm aracının kendisi:</b> bölme hiç çalışmıyorsa yukarıdaki
    /// sayıların tamamı 1 olur ve testlerin yarısı sessizce geçer. Bu test
    /// bölmenin gerçekten böldüğünü sabitliyor (§6).
    /// </summary>
    [Fact]
    public void Bolme_gercekten_boluyor()
    {
        Assert.Equal(3, SentenceCount("Birinci cümle [EV-1]. İkinci cümle [EV-2]. Üçüncü cümle."));
        Assert.Equal(2, SentenceCount("Satır sonu da sınır [EV-1]\nİkinci satır [EV-2]"));
    }
}
