using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bizigo.Contracts.Security;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>T60 — entropi katmanının (T41 · katman A) terfi kararının ölçümü.</b>
///
/// <para>
/// T41 katman A'yı gölgede bıraktı ve sebebini yazdı: <i>ölçülmemiş bir eşik</i>.
/// Eşik T41'in uygulama turunda ölçüldü — altın korpusta
/// <c>aday=59 payda=96 oran=0.6146</c> — ve sayı bir soru daha doğurdu:
/// <b>bu 59 ne?</b> Elle bakıldığında listenin başında UUID'ler, oturum
/// kimlikleri ve FortiGate imza adları vardı, yani terfi ettirilseydi
/// maskelenecek ilk şeylerden biri <b>saldırının adı</b> olurdu.
/// </para>
///
/// <para>
/// <b>Bu dosya o gözlemi mekanik hâle getiriyor.</b> Elle bakmak bir ölçüm
/// değil: 59 satır bugün gözle geçilebilir, 5.900 satır geçilemez, ve gözün
/// gördüğü şey bir sonraki turda kaydedilmiş olmuyor. Dört ölçüm var ve
/// <b>kararı üçü birden veriyor</b> — biri tek başına yeterli olsaydı diğerleri
/// yazılmazdı:
/// </para>
///
/// <list type="number">
/// <item><see cref="Terfi_marjinal_kazanc_getirmiyor"/> — A'nın C+B'ye
/// <b>eklediği</b> yakalama sayısı, kaçırmanın ölçülebildiği tek korpusta.</item>
/// <item><see cref="Adaylari_ayiran_bir_esik_yok"/> — eşik ve minimum uzunluk
/// süpürülüyor; ayıran bir ayar var mı.</item>
/// <item><see cref="Sinif_gecidi_gercek_bir_sir_bicimini_muaf_tutardi"/> —
/// süpürme ayıramıyorsa <i>sınıf</i> ayırabilir mi, ve ayırma bedeli ne.</item>
/// <item><see cref="Altin_korpusun_golge_adaylari_siniflara_ayriliyor"/> —
/// bugünkü dağılım, sayısı çivili.</item>
/// </list>
///
/// <para>
/// <b>Konteyner gerektirmiyor</b> (§2): iki korpus da depo dosyaları, ölçülen
/// şey saf bir fonksiyon.
/// </para>
/// </summary>
public sealed class ShadowPromotionMeasurementTests
{
    /// <summary>
    /// Altın korpusta eşiği geçen aday sayısı — T41 §11'in ölçtüğü sayı.
    /// <b>Çivili</b>: korpus ya da eşik değişirse bu sayı değişir ve
    /// değiştiğinin görünmesi gerekiyor, çünkü T60'ın kararı ona dayanıyor.
    /// </summary>
    private const int GoldenCandidates = 59;

    private const int GoldenEvaluatedTokens = 96;

    /// <summary>
    /// Fixture'ların bildirdiği sahte sır sayısı (FS·S01 konvansiyonu).
    /// </summary>
    private const int DeclaredSecretCount = 11;

    // -----------------------------------------------------------------
    // 1 · Marjinal kazanç
    // -----------------------------------------------------------------

    /// <summary>
    /// <b>A'nın C+B'ye eklediği yakalama sayısı sıfır.</b>
    ///
    /// <para>
    /// Terfinin tek gerekçesi *"C+B'nin kaçırdığı bir sırrı A yakalıyor"*
    /// olabilirdi. Ölçüldü: bildirilen 11 sahte sırrın <b>11'ini</b> C+B zaten
    /// maskeliyor, yani A'nın ek katkısı <b>0/11</b>.
    /// </para>
    ///
    /// <para>
    /// <b>Bu sayının sınırı yazılı olmalı ve sınır dar:</b> fixture'lar
    /// <i>C ve B için</i> yazıldı — her satır bir üretici anahtar kelimesi ya
    /// da kendini tarif eden bir biçim taşıyor. Yani 0/11 kısmen fixture'ın
    /// şeklinin sonucu, ve gerçek müşteri verisinde anahtar kelimesiz bir sır
    /// bulunursa bu ölçüm yeniden açılır (<see cref="ShadowLayerPurpose.PromotionCandidate"/>
    /// bu yüzden duruyor). Ama T41 §4'ün asimetrisi burada da geçerli:
    /// <b>kaçırma yalnızca sırrın ne olduğu önceden biliniyorsa ölçülebiliyor</b>,
    /// ve bu, bilindiği tek küme.
    /// </para>
    ///
    /// <para>
    /// İkinci sayı da kayda geçiyor: 11 sırrın <b>10'u</b> tek başına bir
    /// entropi adayı olacak kadar uzun ve düzensiz; biri
    /// (<c>SAHTE-p9Wz</c> — ASA'nın 4 karakterlik SNMP community'si) minimum
    /// uzunluğun altında, yani A onu <b>hiçbir eşikte</b> göremiyor. Katman
    /// A'nın kaçırdığı sınıfın bir örneği fixture'da zaten duruyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Terfi_marjinal_kazanc_getirmiyor()
    {
        var bildirilen = RedactionFixtures.DeclaredSecrets();

        Assert.Equal(DeclaredSecretCount, bildirilen.Count);

        var cbMaskeledi = 0;
        var entropiGorurdu = 0;

        foreach (var profil in RedactionFixtures.Profiles)
        {
            var (payload, beklenen) = RedactionFixtures.Fixture(profil);
            var sonuc = RedactedPrompt.Redact(payload);

            foreach (var sir in beklenen)
            {
                if (!sonuc.Text.Contains(sir, StringComparison.Ordinal))
                {
                    cbMaskeledi++;
                }

                if (EntropiAdayiOlurdu(sir))
                {
                    entropiGorurdu++;
                }
            }
        }

        Assert.Equal(DeclaredSecretCount, cbMaskeledi);

        // Marjinal kazanç = A'nın gördüğü ve C+B'nin kaçırdığı sır sayısı.
        // C+B hepsini maskelediği için kesişim boş: 0.
        Assert.Equal(0, DeclaredSecretCount - cbMaskeledi);

        Assert.Equal(10, entropiGorurdu);

        // Ve kaçırdığı somut örnek fixture'da duruyor — "kısa sır" iddiası bir
        // akıl yürütme değil, bir satır.
        Assert.Contains(
            bildirilen,
            s => s.Length < RedactedPrompt.DefaultMinShadowTokenLength);
    }

    // -----------------------------------------------------------------
    // 2 · Ayıran eşik var mı
    // -----------------------------------------------------------------

    /// <summary>
    /// <b>Entropi ekseninde ayıran bir ayar yok — süpürüldü.</b>
    ///
    /// <para>
    /// T41 A'yı *"eşik ölçülmedi"* diye gölgede bıraktı; bu cümle örtük olarak
    /// <i>doğru eşiğin var olduğunu</i> varsayıyor. Ölçüm o varsayımı sınıyor:
    /// eşik <c>3.0–6.0</c> (0.1 adım) × minimum uzunluk <c>8–40</c> ızgarasında
    /// <b>hiçbir nokta</b> şu ikisini birden vermiyor: fixture sırlarının
    /// hepsini aday yapmak, ve altın korpusta <b>hiç</b> aday üretmemek.
    /// </para>
    ///
    /// <para>
    /// Sebep tek satırda: Shannon entropisi bir belirtecin <b>alfabe
    /// çeşitliliğini</b> ölçüyor, hassasiyetini değil. <c>outside:192.168.80.32/53</c>
    /// 4.05 bit/karakter veriyor, <c>SAHTE-Hj3nR8vQ2wE5tY7u</c> 4.28 —
    /// aradaki mesafe bir eşik koyacak kadar geniş değil ve <b>işaret ters
    /// dönebiliyor</b>: en yüksek entropili belirteç altın korpusta bir URL
    /// (4.80).
    /// </para>
    ///
    /// <para>
    /// <b>Bu test "en iyi nokta"yı da basıyor</b> — bir gün ayıran bir ayar
    /// doğarsa (yeni korpus, yeni fixture) düşen bu test onu <i>gerekçesiyle</i>
    /// gösterecek. Yalnızca <c>Assert.True(false)</c> yazılsaydı ölçüm bir
    /// karar kaydı olur, bir bekçi olmazdı.
    /// </para>
    /// <para>
    /// <b>Ölçüt 11 değil 10 sır üzerinden</b> ve sebebi yazılı: bildirilen 11
    /// sırdan biri (<c>SAHTE-p9Wz</c>) minimum uzunluğun altında ve ancak
    /// ızgaranın en alt köşesinde (min=8, eşik ≤3.3) aday oluyor. "11'ini
    /// yakala" diye kurulmuş bir ölçüt, entropi diğer 10'u kusursuz ayırsa bile
    /// <b>yeşil kalırdı</b> — yani bekçi doğru sebeple değil ulaşılmaz bir
    /// eşik yüzünden tutardı.
    /// </para>
    /// </summary>
    [Fact]
    public void Adaylari_ayiran_bir_esik_yok()
    {
        var sirlar = RedactionFixtures.DeclaredSecrets();
        var altin = RedactionFixtures.GoldenCorpus();

        // Entropiyle görülebilen sır sayısı — ölçütün paydası bu (§2.1).
        const int Gorulebilir = 10;

        (double Esik, int MinUzunluk, int Yakalanan, int YanlisPozitif)? enIyi = null;

        for (var minUzunluk = 8; minUzunluk <= 40; minUzunluk += 4)
        {
            // Altın korpusun belirteçleri bir kez, bütün eşikler için: eşik bir
            // OKUMA parametresi (T60 — `ShadowTokenize` bu yüzden eşiksiz).
            var altinBelirtecler = altin
                .SelectMany(satir => RedactedPrompt
                    .Redact(satir, minShadowTokenLength: minUzunluk)
                    .ShadowTokens)
                .ToArray();

            for (var esik = 3.0; esik <= 6.0001; esik += 0.1)
            {
                var yakalanan = sirlar.Count(s => EntropiAdayiOlurdu(s, esik, minUzunluk));
                var yanlis = altinBelirtecler.Count(t => t.Entropy >= esik);

                if (yakalanan >= Gorulebilir && yanlis == 0)
                {
                    Assert.Fail(string.Create(
                        CultureInfo.InvariantCulture,
                        $"AYIRAN AYAR BULUNDU: eşik={esik:F1} min_uzunluk={minUzunluk} — " +
                        $"sırların hepsi aday, altın korpusta yanlış pozitif yok. " +
                        $"T60'ın kararı bu ölçüme dayanıyordu; karar YENİDEN AÇILMALI " +
                        $"(`ShadowLayerPurpose.PromotionCandidate`)."));
                }

                if (enIyi is null
                    || yakalanan - yanlis > enIyi.Value.Yakalanan - enIyi.Value.YanlisPozitif)
                {
                    enIyi = (esik, minUzunluk, yakalanan, yanlis);
                }
            }
        }

        Assert.NotNull(enIyi);

        // Izgaranın en iyi noktası bile temiz değil — ve "temiz değil"in ölçüsü
        // yanlış pozitifin varlığı.
        Assert.True(
            enIyi.Value.YanlisPozitif > 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"En iyi nokta (eşik={enIyi.Value.Esik:F1}, min={enIyi.Value.MinUzunluk}) " +
                $"yakalanan={enIyi.Value.Yakalanan}/{sirlar.Count} yanlış_pozitif={enIyi.Value.YanlisPozitif}. " +
                $"Yanlış pozitif sıfıra indiyse sebebi yakalamanın da sıfıra inmesi olabilir — " +
                $"ızgaranın kenarına dayanmış bir ölçüm ayırma değildir."));

        // Ve ölçümün en keskin hâli, ızgaradan bağımsız ve ÇİVİLİ: sahte
        // sırların EN DÜŞÜK entropisi eşik yapılsa bile altın korpusta beş aday
        // kalıyor. Yani "eşiği sırların altına indir" hamlesinin son noktası da
        // temiz değil.
        var enDusukSir = RedactionFixtures.DeclaredSecrets()
            .Where(s => EntropiAdayiOlurdu(s))
            .Min(s => RedactedPrompt.ShadowTokensOf(s).Max(t => t.Entropy));

        var altinVarsayilan = altin
            .SelectMany(satir => RedactedPrompt.Redact(satir).ShadowTokens)
            .ToArray();

        Assert.Equal(4.28, Math.Round(enDusukSir, 2));
        Assert.Equal(5, altinVarsayilan.Count(t => t.Entropy >= enDusukSir));
    }

    // -----------------------------------------------------------------
    // 3 · Sınıf geçidi — ayırıyor, ama bedeli ölçülemeyen yönde
    // -----------------------------------------------------------------

    /// <summary>
    /// <b>Sınıf ayrımı çalışıyor:</b> fixture sırlarının entropi ile
    /// görülebilen hepsi <see cref="ShadowTokenClass.Opaque"/>, altın korpusun
    /// 59 adayının ezici kısmı değil.
    ///
    /// <para>
    /// Yani mekanik bir ayrım <b>var</b> ve sözcük listesi gerektirmiyor. Bu
    /// testin işi onu göstermek; <see cref="Sinif_gecidi_gercek_bir_sir_bicimini_muaf_tutardi"/>
    /// ise ayrımı bir <i>maskeleme kapısına</i> çevirmenin bedelini ölçüyor.
    /// İkisi ayrı testte, çünkü ilki doğru kaldığı hâlde ikincisi kararı ters
    /// çeviriyor — tek testte olsalardı "ayrım var" bulgusu "o hâlde terfi et"
    /// diye okunurdu.
    /// </para>
    /// </summary>
    [Fact]
    public void Sinif_ayrimi_sirlari_yukten_ayiriyor()
    {
        var opakOlmayan = RedactionFixtures.DeclaredSecrets()
            .Where(s => EntropiAdayiOlurdu(s))
            .Where(s => ShadowTokenClassifier.Classify(Belirtec(s)) != ShadowTokenClass.Opaque)
            .Select(s => $"{s[..Math.Min(28, s.Length)]}… → {ShadowTokenClassifier.Classify(Belirtec(s))}")
            .ToArray();

        Assert.True(
            opakOlmayan.Length == 0,
            "Entropi ile görülebilen bir sahte sır `Opaque` sınıfına düşmüyor:\n  " +
            string.Join("\n  ", opakOlmayan));

        var dagilim = AltinDagilim();

        Assert.True(
            dagilim[ShadowTokenClass.Opaque] * 4 < GoldenCandidates,
            $"Altın korpusun opak sayısı ({dagilim[ShadowTokenClass.Opaque]}) " +
            $"aday sayısının ({GoldenCandidates}) dörtte birinden büyük — " +
            "sınıf ayrımı yükü artık ayırmıyor, sınıflandırıcı yeniden okunmalı.");
    }

    /// <summary>
    /// <b>Kararı ters çeviren ölçüm: sınıf geçidi, gerçek bir sır biçimini muaf
    /// tutardı.</b>
    ///
    /// <para>
    /// "Yalnızca <see cref="ShadowTokenClass.Opaque"/> maskelenir" kuralı
    /// bugünkü korpusta işe yarıyor gibi görünüyor. Ama iki gerçek sır biçimi o
    /// sınıfın dışında kalıyor ve ikisi de ağ cihazı alanının tam ortasında:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><b>Onaltılık kodlanmış anahtar</b> — 64 karakterlik WPA PSK,
    /// Cisco <c>key-string</c>, <c>ENC</c> blob'u. Sınıfı
    /// <see cref="ShadowTokenClass.Hexadecimal"/>, yani bir sha256 özetiyle
    /// <b>aynı</b>. Bu testin gösterdiği şey ikisinin ayırt
    /// <b>edilemediği</b>: aynı alfabe, aynı uzunluk, aynı sınıf, ve entropi
    /// farkı işaretsiz.</item>
    /// <item><b>Sözcüklerden kurulu parola ifadesi</b> — sınıfı
    /// <see cref="ShadowTokenClass.Composite"/> ya da
    /// <see cref="ShadowTokenClass.Word"/>, yani imza adlarıyla aynı kutuda.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Sahte değerler burada üretiliyor, bir fixture dosyasında değil</b> —
    /// ve sebebi konvansiyonun kendisi: <c>sir-tasiyan.log</c>'un adı *"bu
    /// dosyadaki her satırda bir sır var ve kapı onu BULMALI"* diyor. Bu iki
    /// biçim, kapının anahtar kelime olmadan <b>yapısal olarak</b> bulamadığı
    /// biçimler; oraya konsa fixture'ın kendi adı yalan olurdu.
    /// </para>
    ///
    /// <para>
    /// Onaltılık değer <b>hesaplanıyor</b> (işaretin sha256'sı): onaltılık bir
    /// dizge <c>SAHTE</c> işaretini taşıyamıyor, ama hesaplanmış bir değer
    /// gerçek bir anahtar <b>olamaz</b> — kaynağı testin kendisi. Kriter 10'un
    /// istediği güvence böyle sağlanıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Sinif_gecidi_gercek_bir_sir_bicimini_muaf_tutardi()
    {
        // Değeri test üretiyor: gerçek bir anahtar olması imkânsız.
        var onaltilikAnahtar = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(RedactionFixtures.SahteIsaret + "-t60-wpa-psk")));

        var ozet = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("bizigo-t60-digest-not-a-secret")));

        Assert.Equal(64, onaltilikAnahtar.Length);

        // Aynı sınıf: bir kapı ikisini ayırt EDEMEZ.
        Assert.Equal(ShadowTokenClass.Hexadecimal, ShadowTokenClassifier.Classify(onaltilikAnahtar));
        Assert.Equal(ShadowTokenClass.Hexadecimal, ShadowTokenClassifier.Classify(ozet));

        // Ve entropi farkı bir işaret taşımıyor: fark eşik koyacak kadar
        // büyük olsaydı sınıf yerine eşik iş görürdü.
        var anahtarEntropi = RedactedPrompt.Redact(onaltilikAnahtar).ShadowTokens.Single().Entropy;
        var ozetEntropi = RedactedPrompt.Redact(ozet).ShadowTokens.Single().Entropy;

        Assert.True(
            Math.Abs(anahtarEntropi - ozetEntropi) < 0.3,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Onaltılık anahtar {anahtarEntropi:F2}, özet {ozetEntropi:F2} bit/krk — " +
                $"fark beklenenden büyük. Ayırt edilemezlik iddiası yeniden ölçülmeli."));

        // İkinci biçim: sözcüklerden kurulu parola ifadesi. `Opaque` DEĞİL,
        // yani opak-only bir kapı onu da kaçırırdı.
        Assert.NotEqual(
            ShadowTokenClass.Opaque,
            ShadowTokenClassifier.Classify("SAHTE.Uzun.Parola.Ifadesi.Buraya"));

        // Ve C+B onu anahtar kelimeyle BULUYOR — kaçıran şey entropi ekseni,
        // ürünün tamamı değil. Bu satır olmasa yukarıdaki bulgu "üründe açık
        // var" diye okunurdu.
        Assert.DoesNotContain(
            "SAHTE.Uzun.Parola.Ifadesi.Buraya",
            RedactedPrompt.Redact(
                "Sep  3 11:14:02 rb-sube-07 script: /interface wireless security-profiles " +
                "set wpa2-pre-shared-key=SAHTE.Uzun.Parola.Ifadesi.Buraya x").Text,
            StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // 4 · Bugünkü dağılım — çivili
    // -----------------------------------------------------------------

    /// <summary>
    /// Altın korpusun 59 adayının <b>mekanik dağılımı</b>, sayıları çivili.
    ///
    /// <para>
    /// <b>Neden çivili:</b> T60'ın kararı bu dağılıma dayanıyor. Korpus
    /// büyüdüğünde ya da sınıflandırıcı değiştiğinde sayı değişir ve
    /// <b>değişmesi görünmelidir</b> — bir karar belgesinin dayandığı sayı
    /// sessizce kayarsa belge ölçülmüş gibi okunan bir metne dönüşür.
    /// </para>
    ///
    /// <para>
    /// Düşerse yapılacak şey sayıyı güncellemek değil: yeni dağılımı okumak ve
    /// <b>kararın hâlâ aynı olup olmadığına bakmak</b>. Opak sayısı yükseldiyse
    /// terfi sorusu yeniden açılabilir.
    /// </para>
    /// </summary>
    [Fact]
    public void Altin_korpusun_golge_adaylari_siniflara_ayriliyor()
    {
        var dagilim = AltinDagilim();
        var toplam = dagilim.Values.Sum();

        var listeleme = string.Join(
            "\n  ",
            RedactionFixtures.GoldenCorpus()
                .SelectMany(satir => RedactedPrompt.Redact(satir).ShadowTokens)
                .Where(t => t.Entropy >= RedactedPrompt.DefaultEntropyThreshold)
                .OrderBy(t => t.Class)
                .ThenByDescending(t => t.Entropy)
                .Select(t => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{t.Class,-12} {t.Entropy:F2} {t.Text}")));

        Assert.True(
            toplam == GoldenCandidates,
            $"Altın korpusun aday sayısı {toplam}, çivili sayı {GoldenCandidates}.\n  " + listeleme);

        // Payda da çivili: oran yalnız başına okunamıyor, ve paydanın kayması
        // (yeni parser örnekleri) oranı sayı hiç değişmeden oynatır.
        var payda = RedactionFixtures.GoldenCorpus()
            .Sum(satir => RedactedPrompt.Redact(satir).EvaluatedTokens);

        Assert.Equal(GoldenEvaluatedTokens, payda);

        // Sınıf başına çiviler. Dördü ayrı ayrı yazılı: toplam sabit kalırken
        // sınıflar arası kayma OLABİLİR ve o kayma kararın döndüğü şey.
        Assert.Equal(3, dagilim[ShadowTokenClass.Opaque]);
        Assert.Equal(7, dagilim[ShadowTokenClass.Hexadecimal]);
        Assert.Equal(1, dagilim[ShadowTokenClass.Word]);
        Assert.Equal(48, dagilim[ShadowTokenClass.Composite]);
    }

    /// <summary>
    /// <b>Sınıflandırıcının kendi kör noktası — ölçüldü, kaydedildi.</b>
    ///
    /// <para>
    /// Yukarıdaki dağılımda <see cref="ShadowTokenClass.Hexadecimal"/> yedi
    /// belirteç sayıyor ama korpusta üç UUID ve bir sha256 var. Kalan üçü
    /// nginx'in zaman damgası: <c>07/Dec/2016:10:43:18</c> — ayraçlar
    /// çıkarıldığında <c>07Dec201610</c>… kalıyor ve <b><c>Dec</c>'in üç harfi
    /// de onaltılık</b>. <c>25/Oct/2016:14:49:33</c> aynı şeyi yapmıyor, çünkü
    /// <c>t</c> onaltılık değil.
    /// </para>
    ///
    /// <para>
    /// <b>Bu bir kusur ve bugün bedeli yok</b> — sınıflandırma hiçbir maskeleme
    /// kararını kapatmıyor, yalnızca raporun etiketini belirliyor. Yazılı
    /// olmasının sebebi tam olarak bu: <i>bedeli olmayan</i> bir kusur, A terfi
    /// ettiği gün <b>bedeli olan</b> bir muafiyete dönüşürdü. Kararın gerekçesi
    /// bu satırın kendisi.
    /// </para>
    ///
    /// <para>
    /// Düzeltilmedi ve gerekçesi yazılı: "ay adı" tanımak bir <b>sözcük
    /// listesi</b> ister, ve bu ticket'ın ölçtüğü şeylerden biri elle tutulan
    /// listelerin bekçiyi körleştirdiği. Bir kusuru sözcük listesiyle kapatmak,
    /// kusurun sınıfını değiştirmek olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public void Siniflandirici_ay_adini_onaltilik_sanabiliyor()
    {
        Assert.Equal(ShadowTokenClass.Hexadecimal, ShadowTokenClassifier.Classify("07/Dec/2016:10:43:18"));
        Assert.Equal(ShadowTokenClass.Composite, ShadowTokenClassifier.Classify("25/Oct/2016:14:49:33"));

        // Ve kusur maskelemeye ULAŞMIYOR: iki damganın ikisi de metinde duruyor.
        var sonuc = RedactedPrompt.Redact("… [07/Dec/2016:10:43:18] … [25/Oct/2016:14:49:33] …\n");

        Assert.Equal(0, sonuc.MaskedValues);
        Assert.Contains("07/Dec/2016:10:43:18", sonuc.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Sınıf ekseninde de temiz bir ayrım noktası yok — süpürüldü.</b>
    ///
    /// <para>
    /// Bu test bir eşiği <i>doğrulamak</i> için yazılmadı; <b>yanlışlamak</b>
    /// için yazıldı. İlk hâli *"opak dizi eşiği taşıyıcı değil"* diye kuruldu
    /// ve <b>düştü</b>: ölçüm eşiğin taşıyıcı olduğunu gösterdi. Sayı şu
    /// (altın korpusun opak sayısı ↔ 10 görülebilir sırdan kaçının opak
    /// kaldığı):
    /// </para>
    ///
    /// <code>
    ///   L= 4  altın=17  sır=10/10        L=13  altın= 3  sır=10/10
    ///   L= 8  altın= 9  sır=10/10        L=14  altın= 2  sır=10/10
    ///   L=12  altın= 3  sır=10/10        L=15  altın= 0  sır= 9/10
    /// </code>
    ///
    /// <para>
    /// <b>Altın sayısının sıfıra indiği ilk nokta (15), sırların düşmeye
    /// başladığı nokta.</b> Yani entropi eşiğinde bulunmayan şey burada da
    /// bulunmuyor: <i>hepsini yakala ve hiç yanlış pozitif üretme</i> noktası
    /// yok. Ürünün değeri (12) o uçurumun üç birim altında duruyor.
    /// </para>
    ///
    /// <para>
    /// <b>Kararı bu ölçüm de aynı yöne itiyor.</b> Sınıflandırma bir
    /// <i>rapor</i> etiketi olarak kalırsa eşiğin taşıyıcı olması bir sorun
    /// değil — etiket kayar, hiçbir metin kaybolmaz. Bir <i>maskeleme kapısına</i>
    /// çevrilirse aynı eşik, ölçülmemiş bir bilginin üstüne kurulmuş bir
    /// muafiyet hâline gelir; T41 §4'ün yasakladığı yön, ve T60'ın var olma
    /// sebebinin (ölçülmemiş bir eşik) tekrarı.
    /// </para>
    /// </summary>
    [Fact]
    public void Sinif_ekseninde_temiz_bir_ayrim_noktasi_yok()
    {
        var altinBelirtecler = RedactionFixtures.GoldenCorpus()
            .SelectMany(satir => RedactedPrompt.Redact(satir).ShadowTokens)
            .Where(t => t.Entropy >= RedactedPrompt.DefaultEntropyThreshold)
            .Select(t => t.Text)
            .ToArray();

        var sirBelirtecler = RedactionFixtures.DeclaredSecrets()
            .Where(s => EntropiAdayiOlurdu(s))
            .Select(Belirtec)
            .ToArray();

        Assert.Equal(10, sirBelirtecler.Length);

        var egri = new List<string>();
        var tamKapsamaSonu = 0;
        var temizBaslangic = int.MaxValue;

        for (var L = 4; L <= 24; L++)
        {
            var altinOpak = altinBelirtecler.Count(t => ShadowTokenClassifier.Classify(t, L) == ShadowTokenClass.Opaque);
            var sirOpak = sirBelirtecler.Count(t => ShadowTokenClassifier.Classify(t, L) == ShadowTokenClass.Opaque);

            egri.Add($"L={L} altın={altinOpak} sır={sirOpak}");

            if (sirOpak == sirBelirtecler.Length)
            {
                tamKapsamaSonu = L;
            }

            if (altinOpak == 0 && L < temizBaslangic)
            {
                temizBaslangic = L;
            }

            Assert.True(
                altinOpak > 0 || sirOpak < sirBelirtecler.Length,
                $"L={L}: altın korpusta hiç opak yok VE sırların hepsi opak — temiz bir " +
                "ayrım noktası bulundu. T60'ın kararı bu ölçüme dayanıyordu, YENİDEN AÇILMALI.\n  " +
                string.Join("\n  ", egri));
        }

        // Uçurum tam kapsamanın BİTTİĞİ yerde başlıyor: aralarında boşluk yok.
        Assert.Equal(14, tamKapsamaSonu);
        Assert.Equal(15, temizBaslangic);

        // Ve ürünün değeri o uçurumun altında — yani etiket bugün doğru çalışıyor.
        Assert.InRange(ShadowTokenClassifier.OpaqueRunLength, 12, tamKapsamaSonu);
    }

    // -----------------------------------------------------------------
    // 5 · Kararın kodda durduğu yer
    // -----------------------------------------------------------------

    /// <summary>
    /// <b>Gölge sayaç kalıcı bir ölçüm, bekleyen bir kalem değil</b> — ve ayrım
    /// kanıt paketinde yazılı.
    ///
    /// <para>
    /// <c>CLAUDE.md</c> §8: *"bir gün kapanacak" ile "hiç kapanmayacak" aynı
    /// listede duramaz.* Burada iki liste yerine iki <b>değer</b> var
    /// (<see cref="ShadowLayerPurpose"/>) ve sayının yanında yayılıyor; alan
    /// olmasaydı sayıyı okuyan taraf varsayılanı seçerdi ve varsayılan T41'in
    /// bıraktığı hâl — <i>terfi bekliyor</i> — olurdu.
    /// </para>
    ///
    /// <para>
    /// Sınıf toplamının aday sayısına eşitliği de burada: beşinci bir sınıf
    /// eklenip yayılmazsa eşitlik bozuluyor, yani <b>sessiz genişleme</b>
    /// kırmızı yanıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Golge_sayac_kalici_olcum_olarak_yayiliyor()
    {
        Assert.Equal(ShadowLayerPurpose.PermanentInstrument, RedactedPrompt.ShadowPurpose);

        var bos = RedactedPrompt.Redact("Sep  3 11:04:12 asa-dc-01 : %ASA-6-302014: Teardown TCP 1\n");
        var dolu = RedactedPrompt.Redact(
            "<189>date=2026-09-03 devname=\"fw-ankara-01\" attack=\"HTTP.BROWSER_Firefox\" " +
            "sessionid=ae28f494-5735-51e9-f247-d1d2ce663f4b\n");

        foreach (var sonuc in new[] { bos, dolu })
        {
            var alanlar = sonuc.EvidenceFields();

            Assert.Equal("permanentinstrument", alanlar["redaction_shadow_purpose"]);

            // Dört sınıf da yazılı — SIFIRKEN DE.
            foreach (var sinif in Enum.GetValues<ShadowTokenClass>())
            {
                var ad = "redaction_shadow_class_" + sinif.ToString().ToLowerInvariant();

                Assert.True(alanlar.ContainsKey(ad), $"`{ad}` yayılmıyor.");
            }

            var toplam = Enum.GetValues<ShadowTokenClass>()
                .Sum(s => (int)alanlar["redaction_shadow_class_" + s.ToString().ToLowerInvariant()]);

            Assert.Equal(sonuc.ShadowCandidates, toplam);
        }

        // Ve katman A hâlâ maskelemiyor: kararın "kalıcı ölçüm" olması, sessizce
        // maskelemeye başlamasının bahanesi değil.
        Assert.True(dolu.ShadowCandidates > 0);
        Assert.Equal(0, dolu.MaskedValues);
        Assert.DoesNotContain(SecretRedactor.Mask, dolu.Text, StringComparison.Ordinal);

        // Özet satırı sınıfları da taşıyor — rapora giren tek satır bu.
        Assert.Contains("opak=", RedactedPrompt.Describe(dolu), StringComparison.Ordinal);
        Assert.Contains("amaç=PermanentInstrument", RedactedPrompt.Describe(dolu), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // Yardımcılar
    // -----------------------------------------------------------------

    /// <summary>
    /// Bir değer <b>tek başına</b> entropi adayı olur muydu — yani C+B onu
    /// kaçırsaydı A görür müydü.
    ///
    /// <para>
    /// <see cref="RedactedPrompt.ShadowTokensOf"/> kullanılıyor, çünkü soru
    /// C+B'nin <b>dışında</b>: <see cref="RedactedPrompt.Redact"/> üzerinden
    /// ölçülse JWT gibi bir değer B tarafından maskelenir ve A'nın onu görüp
    /// görmediği "görmedi" diye okunurdu. Entropi hesabı yine ürünün kendi
    /// hesabı — ikinci bir kopya yazılmadı (§9).
    /// </para>
    /// </summary>
    private static bool EntropiAdayiOlurdu(
        string deger,
        double esik = RedactedPrompt.DefaultEntropyThreshold,
        int minUzunluk = RedactedPrompt.DefaultMinShadowTokenLength) =>
        RedactedPrompt.ShadowTokensOf(deger, minUzunluk).Any(t => t.Entropy >= esik);

    /// <summary>
    /// Değerin belirteç hâli — çok parçalı değerlerde (PEM gövdesi) entropisi en
    /// yüksek belirteç. Sınıflandırma bir <b>belirtece</b> bakıyor, bir satıra
    /// değil.
    /// </summary>
    private static string Belirtec(string deger) =>
        RedactedPrompt.ShadowTokensOf(deger)
            .OrderByDescending(t => t.Entropy)
            .First()
            .Text;

    private static Dictionary<ShadowTokenClass, int> AltinDagilim()
    {
        var dagilim = Enum.GetValues<ShadowTokenClass>().ToDictionary(s => s, _ => 0);

        foreach (var satir in RedactionFixtures.GoldenCorpus())
        {
            foreach (var (sinif, sayi) in RedactedPrompt.Redact(satir).ShadowClasses)
            {
                dagilim[sinif] += sayi;
            }
        }

        return dagilim;
    }
}
