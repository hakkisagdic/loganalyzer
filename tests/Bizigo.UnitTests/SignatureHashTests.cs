using System.Text;
using Bizigo.Parsing.Grok;

namespace Bizigo.UnitTests;

/// <summary>
/// <c>events.signature_hash</c>'in sözleşmesi (K35, T29).
///
/// <para>
/// Bu dosyanın varlık sebebi tek bir hata sınıfı: <b>yanlış hesaplanmış bir</b>
/// <b>hash hiçbir yerde hata vermez.</b> İstisna atmaz, sorgu düşürmez, log
/// yazmaz — yalnızca RCA'nın "ilk-görülen imza" ve "hacim sapması" sinyallerini
/// sessizce bozar ve bu aylar sonra, yanlış bir kök neden raporunda görünür.
/// </para>
///
/// <para>
/// O yüzden burada sınanan şey "çalışıyor mu" değil, <b>tanımın kendisi</b>:
/// hash neyin üstünden alınıyor, neyin üstünden alınmıyor, ve hangi girdi hangi
/// çıktıyı vermek zorunda. Bu kararlar geriye dönük değiştirilemez — değişirse
/// bugünden sonraki satırlar geçmiştekilerle eşleşmez ve ilk-görülen bir gün
/// boyunca her imzayı yeni sanar.
/// </para>
/// </summary>
public sealed class SignatureHashTests
{
    private static readonly MaskCatalog Catalog = MaskCatalog.LoadFromFile(RepositoryLayout.MaskFile);

    /// <summary>
    /// <b>Altın vektör.</b> Sayı burada yazılı olmazsa "hash değişti" hiçbir
    /// zaman kırmızı yanmaz — algoritma, kodlama ya da hash'lenen metin sessizce
    /// değişebilir. Bu sabitin değişmesi gereken tek durum bilinçli bir
    /// sözleşme değişikliğidir ve o zaman geçmiş satırların eşleşmeyeceği de
    /// kabul edilmiş olur.
    ///
    /// <para>
    /// Değer <c>XXH64(UTF-8("&lt;IPV4&gt;"))</c>, seed 0 — ClickHouse'ta
    /// <c>SELECT xxHash64('&lt;IPV4&gt;')</c> aynısını veriyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Altin_vektor_sabit()
    {
        Assert.Equal(14_733_834_131_172_344_067UL, SignatureHash.Of("<IPV4>"));
        // Uçtan uca: ham satır → maskeleme → hash. Yukarıdaki tek başına
        // algoritmayı sabitliyor, bu ise **hash'lenen metni** — yani maskelemenin
        // çıktısının hash girdisi olduğunu.
        Assert.Equal(
            15_959_370_284_002_640_506UL,
            Catalog.Compute("Failed password for admin from 10.1.2.3 port 51234 ssh2").Hash);
    }

    /// <summary>
    /// Hash <b>maskelenmiş metnin</b> üstünden alınıyor, ham satırın değil.
    ///
    /// <para>
    /// Ters olsaydı her olay benzersiz bir hash alırdı (IP'ler, portlar, pid'ler
    /// her satırda farklı) ve "ilk-görülen imza" her satır için ateşlerdi — yani
    /// sinyal gürültüye dönüşürdü, üstelik hiçbir belirti vermeden.
    /// </para>
    /// </summary>
    [Fact]
    public void Hash_maskelenmis_metnin_uzerinden_aliniyor()
    {
        const string Raw = "Failed password for admin from 10.1.2.3 port 51234 ssh2";

        var signature = Catalog.Compute(Raw);

        Assert.Equal(Catalog.Signature(Raw), signature.Text);
        Assert.Equal(SignatureHash.Of(signature.Text), signature.Hash);
        Assert.NotEqual(SignatureHash.Of(Raw), signature.Hash);
    }

    /// <summary>
    /// Maskelenen alanlar (IP, port, sayı) farklı olsa bile hash aynı — kabul
    /// kriterinin tamamı bu.
    /// </summary>
    [Fact]
    public void Maskelenen_alanlar_farkli_olsa_da_hash_ayni()
    {
        Assert.Equal(
            Catalog.Compute("Failed password for admin from 10.1.2.3 port 51234 ssh2").Hash,
            Catalog.Compute("Failed password for admin from 192.168.9.9 port 22 ssh2").Hash);
    }

    /// <summary>
    /// Aynı imza <b>iki farklı kaynaktan</b> aynı hash'i veriyor: vendor, host ve
    /// kaynak kimliği hash'e <b>girmiyor</b>.
    ///
    /// <para>
    /// Girseydi RCA'nın "yayılma sırası" sinyali çalışmazdı: o sinyal tam olarak
    /// "aynı şey kaç cihazda, hangi sırayla belirdi" sorusunu soruyor. Kaynak
    /// bazlı ayrım gerektiğinde SQL zaten <c>source_id</c>/<c>vendor</c>
    /// kolonlarına <c>GROUP BY</c> yapabiliyor — hash'e gömmek o seçimi sorgudan
    /// alıp yazma anına hapsederdi ve geri alınamazdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Ayni_imza_iki_farkli_kaynaktan_ayni_hash()
    {
        // Aynı şablon, iki farklı cihazdan, farklı adreslerle.
        var edge = Catalog.Compute("%ASA-6-302013: Built outbound TCP connection 11757 for 10.0.0.1");
        var core = Catalog.Compute("%ASA-6-302013: Built outbound TCP connection 98211 for 172.16.4.9");

        Assert.Equal(edge.Hash, core.Hash);
        Assert.NotEqual(SignatureHash.None, edge.Hash);
    }

    /// <summary>Farklı imzalar farklı hash veriyor — yukarıdakinin tersi olmadan bir şey kanıtlamaz.</summary>
    [Fact]
    public void Farkli_imzalar_farkli_hash()
    {
        var denied = Catalog.Compute("denied tcp 10.0.0.1 -> 10.0.0.2 port 443");
        var accepted = Catalog.Compute("accepted tcp 10.0.0.1 -> 10.0.0.2 port 443");

        Assert.NotEqual(denied.Hash, accepted.Hash);
    }

    /// <summary>
    /// 16 KB sınırını aşan satır: hash <b>boş</b> ve sayaç artıyor.
    ///
    /// <para>
    /// O satırlar RCA'nın ilk-görülen sinyalinde görünmüyor ve rapor bunu
    /// söylemek zorunda — sayaç tam olarak o cümlenin kurulabilmesi için var.
    /// </para>
    /// </summary>
    [Fact]
    public void Uzunluk_sinirini_asan_satirin_hashi_bos_ve_sayiliyor()
    {
        var catalog = MaskCatalog.LoadFromFile(RepositoryLayout.MaskFile);
        var before = catalog.SkippedTooLong;

        var skipped = catalog.Compute(new string('x', MaskCatalog.MaxInputLength + 1));

        Assert.True(skipped.IsEmpty);
        Assert.Equal(SignatureHash.None, skipped.Hash);
        Assert.Equal(before + 1, catalog.SkippedTooLong);

        // Sınırın tam üstündeki satır normal işleniyor — sınır kapsayıcı.
        Assert.NotEqual(SignatureHash.None, catalog.Compute(new string('x', MaskCatalog.MaxInputLength)).Hash);
        Assert.Equal(before + 1, catalog.SkippedTooLong);
    }

    [Fact]
    public void Bos_govde_imza_uretmiyor()
    {
        Assert.True(Catalog.Compute(string.Empty).IsEmpty);
        Assert.Equal(SignatureHash.None, SignatureHash.Of(string.Empty));
    }

    /// <summary>
    /// Maskesiz katalog imza <b>üretmiyor</b> — ham satırı hash'lemiyor.
    ///
    /// <para>
    /// Bu yol sözlük yüklenemediğinde devreye giriyor. Ham satırı hash'lemek en
    /// kötü sonucu verirdi: her olay benzersiz bir hash alır, kolon "dolu"
    /// görünür ve bozukluk yalnızca aylar sonra, RCA raporunda ortaya çıkar.
    /// </para>
    /// </summary>
    [Fact]
    public void Maskesiz_katalog_imza_uretmiyor()
    {
        Assert.True(MaskCatalog.Empty.Compute("denied 10.0.0.1").IsEmpty);
    }

    /// <summary>
    /// <c>0</c> "imza yok" için ayrılmış; gerçek bir imza asla 0'a düşmüyor.
    /// </summary>
    [Fact]
    public void Sifir_imza_yok_icin_ayrilmis()
    {
        // 2⁻⁶⁴ olasılıkla gerçek bir metin 0'a düşerse 1'e kaydırılıyor. Onu
        // doğrudan sınayacak bir girdi bilinmiyor; sınanan şey sözleşmenin
        // kendisi: hiçbir dolu imza 0 dönmüyor.
        foreach (var sample in Catalog.Golden)
        {
            var signature = Catalog.Compute(sample.Input);

            if (signature.Text.Length > 0)
            {
                Assert.NotEqual(SignatureHash.None, signature.Hash);
            }
        }
    }

    /// <summary>
    /// UTF-8 baytları üzerinden — kodlama sözleşmenin parçası.
    ///
    /// <para>
    /// UTF-16 olsaydı ClickHouse'un <c>xxHash64()</c>'ü ile ayrışırdı ve hash'in
    /// doğruluğunu veritabanına karşı sınama imkânı kaybolurdu. Türkçe/CJK
    /// gövdeler bu farkı üretecek tek girdi sınıfı.
    /// </para>
    /// </summary>
    [Fact]
    public void Kodlama_utf8()
    {
        const string Text = "kullanıcı girişi başarısız — 用户登录失败";

        Assert.Equal(
            System.IO.Hashing.XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(Text)),
            SignatureHash.Of(Text));
    }

    /// <summary>
    /// <b>Maske sözlüğü sürümü ile hash'in ilişkisi — yazılı ve bilinçli.</b>
    ///
    /// <para>
    /// Sürüm hash girdisine <b>dahil değil</b>. Dahil olsaydı sözlüğün her
    /// güncellemesi bütün geçmiş hash'leri geçersiz kılardı ve ilk-görülen o gün
    /// <b>her</b> satır için ateşlerdi. Dışarıda kaldığında yalnızca gerçekten
    /// etkilenen maskelerin imzaları kayıyor — ki o satırların maskelenmiş metni
    /// fiilen değişmiş oluyor, yani kayma doğru davranış.
    /// </para>
    ///
    /// <para>
    /// Sözleşme: <c>signature_hash</c> <b>maskelenmiş metnin</b> kimliğidir,
    /// sözlüğün değil. Bedeli, sözlük değişiminin sınırlı ve bir seferlik bir
    /// ilk-görülen dalgası üretmesi. Bu bedel kabul edildiği için sözlük
    /// sürümü aşağıda <b>sabitlendi</b>: sürümü değiştiren kişi bu testi de
    /// değiştirmek zorunda kalıyor, yani kayma kazara değil bilerek yapılıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Maske_sozlugu_surumu_sabit()
    {
        Assert.Equal(1, Catalog.Version);
    }

    /// <summary>
    /// Sözlük değişince <b>etkilenen</b> satırın hash'i değişiyor — yukarıdaki
    /// kararın gerçekten böyle davrandığının kanıtı.
    /// </summary>
    [Fact]
    public void Sozluk_degisince_etkilenen_satirin_hashi_degisiyor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bizigo-masks-{Guid.NewGuid():N}.yaml");

        try
        {
            // Aynı maske adı, `NUMBER`ı da yutacak kadar genişletilmiş bir regex.
            File.WriteAllText(path, """
                version: 2
                mask_prefix: "<"
                mask_suffix: ">"
                masks:
                  - name: WORD
                    regex: '[A-Za-z]+'
                """);

            var altered = MaskCatalog.LoadFromFile(path);

            Assert.Equal(2, altered.Version);
            Assert.NotEqual(
                Catalog.Compute("denied tcp 10.0.0.1").Hash,
                altered.Compute("denied tcp 10.0.0.1").Hash);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
