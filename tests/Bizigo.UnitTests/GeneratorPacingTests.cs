using Bizigo.Capacity;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>B01 — üretecin kendi tavanı ölçülüyor ve hüküm onu saklamıyor.</b>
///
/// <para>
/// Tetikleyen olgu ölçüldü (koordinatör, `SyslogEmitter.RatePerMinute`, 200
/// satır, macOS/arm64): <c>10→10</c>, <c>100→91</c>, <c>1 000→824</c>,
/// <c>10 000→pacing YOK</c>. İkinci kusur bu deponun adını koyduğu sınıf —
/// hesaplanan gecikme zamanlayıcı çözünürlüğünün altına indiğinde
/// <c>Task.Delay</c> anında dönüyor ve <b>ayar sessizce hiçbir şey ifade
/// etmemeye başlıyor</b>. Yani 10 000 EPS <i>istemek</i> mümkün, <i>elde
/// etmek</i> değil, ve ikisi ayırt edilemiyor.
/// </para>
///
/// <para>
/// <b>Hiçbir test duvar saati okumuyor</b> ve bu §6'nın gerilimini çözen şey:
/// bir pacer testi doğası gereği zaman ölçer, ama kovanın saati
/// <see cref="FakeTimeProvider"/> ve hüküm saf. Zaman ileri <b>alınıyor</b>,
/// beklenmiyor — dolayısıyla bir testin geçme sebebi makinenin yükü olamıyor.
/// Bu depoda zamana bağlı testler iki kez pahalıya patladı.
/// </para>
/// </summary>
public sealed class GeneratorPacingTests
{
    private static readonly DateTimeOffset Basla = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private static FakeTimeProvider Saat() => new(Basla);

    /// <summary>
    /// Hedefe <b>tam</b> uyan bir koşumu taklit ediyor: her saniye hedef kadar
    /// jeton alınıyor.
    /// </summary>
    private static TokenBucketPacer MukemmelKosum(double eps, int saniye)
    {
        var saat = Saat();
        var pacer = new TokenBucketPacer(new PaceProfile.Fixed(eps, TimeSpan.FromSeconds(saniye)), saat);

        for (var s = 0; s < saniye; s++)
        {
            saat.Advance(TimeSpan.FromSeconds(1));

            for (var i = 0; i < eps; i++)
            {
                pacer.TryAcquire();
            }
        }

        return pacer;
    }

    // ---------------------------------------------------------------------
    // Hüküm
    // ---------------------------------------------------------------------

    /// <summary>Taban: hedefe ulaşan koşum <c>Attained</c>, ve kayıp yorumlanabilir.</summary>
    [Fact]
    public void Hedefe_ulasan_kosum_attained()
    {
        var hukum = GeneratorAttainment.From(MukemmelKosum(100, 10));

        Assert.Equal(GeneratorVerdict.Attained, hukum.Verdict);
        Assert.True(hukum.LossIsInterpretable);
        Assert.NotNull(hukum.Ratio);
        Assert.True(hukum.Ratio >= GeneratorAttainment.MinimumAttainment);
    }

    /// <summary>
    /// <b>Asıl iddia:</b> hedefin gerisinde kalan koşum <c>GENERATOR-LIMITED</c>.
    ///
    /// <para>
    /// Taklit edilen şey ölçülen hâlin kendisi: 1 000 EPS istenip <b>824</b>
    /// üretiliyor (%82). Sayı uydurulmadı — koordinatörün
    /// <c>Task.Delay</c> modelinde ölçtüğü değer.
    /// </para>
    /// </summary>
    [Fact]
    public void Hedefin_gerisinde_kalan_kosum_generator_limited()
    {
        var saat = Saat();
        var pacer = new TokenBucketPacer(
            new PaceProfile.Fixed(1_000, TimeSpan.FromSeconds(1)),
            saat);

        saat.Advance(TimeSpan.FromSeconds(1));

        // Ölçülen hâl: istenen 1 000, üretilen 824.
        for (var i = 0; i < 824; i++)
        {
            pacer.TryAcquire();
        }

        var hukum = GeneratorAttainment.From(pacer);

        Assert.Equal(GeneratorVerdict.GeneratorLimited, hukum.Verdict);
        Assert.Equal(824, hukum.Emitted);
    }

    /// <summary>
    /// Profil bittikten sonraki soket/flush gecikmesi hedefi sulandıramaz.
    /// Hedef bir saniyelik pencerede 1.000 EPS'tir; 1.000 kaydı on saniyede
    /// bitirmek 100 EPS gerçekleşmedir ve <c>Attained</c> olamaz.
    /// </summary>
    [Fact]
    public void Sure_asimi_hedef_eps_degerini_dusurmuyor()
    {
        var saat = Saat();
        var pacer = new TokenBucketPacer(
            new PaceProfile.Fixed(1_000, TimeSpan.FromSeconds(1)),
            saat);

        saat.Advance(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 1_000; i++)
        {
            Assert.Equal(TimeSpan.Zero, pacer.TryAcquire());
        }

        var hukum = GeneratorAttainment.From(pacer);

        Assert.Equal(1_000, hukum.RequestedEventsPerSecond);
        Assert.Equal(100, hukum.AchievedEventsPerSecond);
        Assert.Equal(GeneratorVerdict.GeneratorLimited, hukum.Verdict);
        Assert.False(hukum.LossIsInterpretable);
    }

    /// <summary>
    /// <b>Hükmün bir SONUCU var, yalnızca bir etiket değil.</b>
    ///
    /// <para>
    /// Plan §B01'in bitti ölçütü: <i>"üretecin ulaşamadığı bir hız hedefin
    /// kaybı diye raporlanamıyor"</i>. Bu ayrı bir kapı, çünkü etiketi doğru
    /// basıp kararı ona bağlamamak bu depoda ölçülmüş bir hâl — bir rozeti
    /// okumayan için hiçbir şey değişmez.
    /// </para>
    /// </summary>
    [Fact]
    public void Generator_limited_kaybi_yorumlanamaz_kiliyor()
    {
        var saat = Saat();
        var pacer = new TokenBucketPacer(new PaceProfile.Fixed(1_000, TimeSpan.FromSeconds(1)), saat);

        saat.Advance(TimeSpan.FromSeconds(1));

        for (var i = 0; i < 500; i++)
        {
            pacer.TryAcquire();
        }

        var hukum = GeneratorAttainment.From(pacer);

        Assert.Equal(GeneratorVerdict.GeneratorLimited, hukum.Verdict);
        Assert.False(
            hukum.LossIsInterpretable,
            "Üreteç hedefe ulaşamamışken kayıp yorumlanabilir sayılıyor — hiç basılmamış " +
            "bir satır kaybolmuş değildir.");

        Assert.Contains("KAYBI SAYILAMAZ", hukum.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Ölçülemeyen koşum <c>Attained</c> sayılmıyor.</b> Süre sıfırken
    /// gerçekleşen hız hesaplanamıyor ve hüküm bunu <b>söylüyor</b>.
    /// </summary>
    [Fact]
    public void Olculemeyen_kosum_attained_sayilmiyor()
    {
        var pacer = new TokenBucketPacer(new PaceProfile.Fixed(100, TimeSpan.FromSeconds(1)), Saat());

        var hukum = GeneratorAttainment.From(pacer);

        Assert.Equal(GeneratorVerdict.Unmeasured, hukum.Verdict);
        Assert.Null(hukum.AchievedEventsPerSecond);
        Assert.Null(hukum.Ratio);
        Assert.False(hukum.LossIsInterpretable);
    }

    /// <summary>
    /// <b>Ölçülemeyen koşum <c>GeneratorLimited</c> de sayılmıyor.</b>
    ///
    /// <para>
    /// Bir önceki kapının aynası ve ayrı olması zorunlu: ikisi tek teste
    /// konsaydı *"`Attained` değil"* iddiası geçerdi ve <b>arıza uydurmak</b>
    /// serbest kalırdı. <c>Unmeasured</c> *"yetişip yetişmediğini bilmiyorum"*
    /// demek; onu *"yetişemedi"*ye indirmek ölçülmemiş bir arızayı rapora
    /// yazmak olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public void Olculemeyen_kosum_generator_limited_de_sayilmiyor()
    {
        var pacer = new TokenBucketPacer(new PaceProfile.Fixed(100, TimeSpan.FromSeconds(1)), Saat());

        var hukum = GeneratorAttainment.From(pacer);

        Assert.NotEqual(GeneratorVerdict.GeneratorLimited, hukum.Verdict);
        Assert.DoesNotContain("GENERATOR-LIMITED", hukum.Describe(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(hukum.Basis));
    }

    // ---------------------------------------------------------------------
    // max — hedef değil ölçüm
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b><c>max</c> profili hedef almıyor</b> — ve alamaması derleyiciye bağlı.
    ///
    /// <para>
    /// Bu test hükmü sınıyor; *"hız alanı yok"* iddiasını sınayan şey testin
    /// kendisi değil <b>derleyici</b>: <c>PaceProfile.Max</c>'ın yapıcısında
    /// hız parametresi olmadığı için ona bir hedef vermek derlenmiyor. Plan
    /// §B01 şartı bu — <i>"aldığı an ölçtüğü şey o sayı olur"</i>.
    /// </para>
    /// </summary>
    [Fact]
    public void Max_profili_hedef_almiyor()
    {
        var saat = Saat();
        var pacer = new TokenBucketPacer(new PaceProfile.Max(TimeSpan.FromSeconds(2)), saat);

        saat.Advance(TimeSpan.FromSeconds(1));

        for (var i = 0; i < 5_000; i++)
        {
            Assert.Equal(TimeSpan.Zero, pacer.TryAcquire());
        }

        var hukum = GeneratorAttainment.From(pacer);

        Assert.Equal(GeneratorVerdict.NoTarget, hukum.Verdict);
        Assert.Null(hukum.RequestedEventsPerSecond);
        Assert.True(hukum.LossIsInterpretable);

        // Borç `max`'ta ANLAMSIZ ve `null` — `0` dönmek "borcu yok" demek,
        // yani ölçülemeyen bir boyutu mükemmel göstermek olurdu.
        Assert.Null(pacer.Debt);
        Assert.False(pacer.Profile.HasTarget);
    }

    // ---------------------------------------------------------------------
    // Kova
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Kova borcu biriktiriyor</b> — sapmanın görünür olduğu tek yer.
    ///
    /// <para>
    /// Eski modelde bu sayı hiç hesaplanmıyordu: %18 geride koşmak ile hedefe
    /// ulaşmak aynı çıktıyı veriyordu. Kova borcu tutmazsa hüküm de
    /// kurulamıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Kova_borcu_biriktiriyor()
    {
        var saat = Saat();
        var pacer = new TokenBucketPacer(
            new PaceProfile.Fixed(100, TimeSpan.FromSeconds(10)),
            saat,
            burstCapacity: 100);

        // İki saniye geçiyor, hiç jeton alınmıyor: hedef 200 üretmiş olmalı.
        saat.Advance(TimeSpan.FromSeconds(2));
        pacer.TryAcquire();

        Assert.NotNull(pacer.Debt);
        Assert.True(
            pacer.Debt > 100,
            $"Borç {pacer.Debt:F1} — kapasite tavanı borcu SİLMİŞ olmamalı; `Issued` " +
            "tavandan bağımsız birikmeli, yoksa taşan jetonlar sapmadan düşer ve hüküm " +
            "gerçek geriliği görmez.");
    }

    /// <summary>
    /// Hedefli bir profilde jeton tükendiğinde kova <b>bekleme süresi</b>
    /// döndürüyor — beklemeyi kendisi yapmıyor.
    ///
    /// <para>
    /// Kova içinde <c>Task.Delay</c> çağırmak, yerine geçtiği modelin kusurunu
    /// tipin içine gömmek olurdu; ve toplu yazma ancak çağıran beklemeyi
    /// yönetiyorsa mümkün.
    /// </para>
    /// </summary>
    [Fact]
    public void Jeton_tukendiginde_bekleme_suresi_donuyor()
    {
        var saat = Saat();
        var pacer = new TokenBucketPacer(
            new PaceProfile.Fixed(10, TimeSpan.FromSeconds(10)),
            saat,
            burstCapacity: 1);

        saat.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.Zero, pacer.TryAcquire());

        var bekleme = pacer.TryAcquire();

        Assert.True(
            bekleme > TimeSpan.Zero,
            "Jeton bittiğinde kova sıfır bekleme döndürüyor — hız kontrolü yok demektir, " +
            "ve bu tam olarak yerine geçtiği `Task.Delay` eşiğinin hâli.");

        Assert.True(bekleme <= TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Mevcut taban hızından hesaplanan bekleme sıradaki burst sınırını
    /// geçemez. Yoksa 200 ms'lik tepe tamamen uyunup yük bir saniye sonra
    /// gecikmiş bir yığın hâlinde gönderilir.
    /// </summary>
    [Fact]
    public void Bekleme_kisa_burst_penceresini_atlamiyor()
    {
        var saat = Saat();
        var pacer = new TokenBucketPacer(
            new PaceProfile.Burst(
                BaseEventsPerSecond: 1,
                PeakEventsPerSecond: 1_000,
                PeakAt: TimeSpan.FromMilliseconds(100),
                PeakLength: TimeSpan.FromMilliseconds(200),
                Length: TimeSpan.FromSeconds(2)),
            saat,
            burstCapacity: 1_000);

        Assert.Equal(TimeSpan.Zero, pacer.TryAcquire());

        var tepeyeKadar = pacer.TryAcquire();
        Assert.Equal(TimeSpan.FromMilliseconds(100), tepeyeKadar);

        saat.Advance(tepeyeKadar);
        var ilkTepeJetonunaKadar = pacer.TryAcquire();
        Assert.InRange(
            ilkTepeJetonunaKadar,
            TimeSpan.FromTicks(1),
            TimeSpan.FromMilliseconds(1));

        saat.Advance(ilkTepeJetonunaKadar);
        Assert.Equal(TimeSpan.Zero, pacer.TryAcquire());
        Assert.InRange(saat.GetUtcNow() - Basla, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300));
    }

    // ---------------------------------------------------------------------
    // Profiller
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>ramp</c> hedefi zamanla yükseltiyor, <c>burst</c> tepesini yalnızca
    /// penceresinde uyguluyor.
    /// </summary>
    [Fact]
    public void Profiller_hedefi_zamana_gore_veriyor()
    {
        var ramp = new PaceProfile.Ramp(100, 1_100, TimeSpan.FromSeconds(10));

        Assert.Equal(100, ramp.TargetAt(TimeSpan.Zero));
        Assert.Equal(600, ramp.TargetAt(TimeSpan.FromSeconds(5)));
        Assert.Equal(1_100, ramp.TargetAt(TimeSpan.FromSeconds(10)));

        var burst = new PaceProfile.Burst(
            BaseEventsPerSecond: 50,
            PeakEventsPerSecond: 5_000,
            PeakAt: TimeSpan.FromSeconds(3),
            PeakLength: TimeSpan.FromSeconds(1),
            Length: TimeSpan.FromSeconds(10));

        Assert.Equal(50, burst.TargetAt(TimeSpan.FromSeconds(2)));
        Assert.Equal(5_000, burst.TargetAt(TimeSpan.FromSeconds(3)));
        Assert.Equal(50, burst.TargetAt(TimeSpan.FromSeconds(4)));
    }

    /// <summary>
    /// Borç anlık hedef × süre değil profil integralidir. Tek büyük saat adımı
    /// ramp'i fazla, burst penceresini eksik saymamalı.
    /// </summary>
    [Fact]
    public void Profil_integrali_ramp_ve_burst_penceresini_koruyor()
    {
        var saat = Saat();
        var ramp = new TokenBucketPacer(
            new PaceProfile.Ramp(100, 1_100, TimeSpan.FromSeconds(10)),
            saat);

        saat.Advance(TimeSpan.FromSeconds(10));
        ramp.TryAcquire();

        // Ortalama 600 EPS × 10 saniye.
        Assert.Equal(6_000, ramp.Issued, precision: 6);

        saat = Saat();
        var burst = new TokenBucketPacer(
            new PaceProfile.Burst(
                BaseEventsPerSecond: 50,
                PeakEventsPerSecond: 5_000,
                PeakAt: TimeSpan.FromSeconds(3),
                PeakLength: TimeSpan.FromSeconds(1),
                Length: TimeSpan.FromSeconds(10)),
            saat);

        // Saat tepenin üzerinden tek adımda geçiyor; integral yine o bir
        // saniyeyi saymalı: 50×10 + (5000-50)×1 = 5450.
        saat.Advance(TimeSpan.FromSeconds(10));
        burst.TryAcquire();

        Assert.Equal(5_450, burst.Issued, precision: 6);
    }

    /// <summary>
    /// Değişken profilde hükmün paydası son anın hızı değil koşumun ortalama
    /// hedefidir. Kusursuz ramp bu yüzden <c>Attained</c> olmalı.
    /// </summary>
    [Fact]
    public void Kusursuz_ramp_son_hedefe_degil_integrale_gore_yargilaniyor()
    {
        var saat = Saat();
        var profile = new PaceProfile.Ramp(100, 1_100, TimeSpan.FromSeconds(10));
        var pacer = new TokenBucketPacer(profile, saat);
        var granted = 0;

        for (var second = 1; second <= 10; second++)
        {
            saat.Advance(TimeSpan.FromSeconds(1));
            var expected = (int)profile.IssuedBetween(TimeSpan.Zero, TimeSpan.FromSeconds(second))!.Value;

            while (granted < expected)
            {
                Assert.Equal(TimeSpan.Zero, pacer.TryAcquire());
                granted++;
            }
        }

        var hukum = GeneratorAttainment.From(pacer);

        Assert.Equal(6_000, hukum.Emitted);
        Assert.Equal(600, hukum.RequestedEventsPerSecond);
        Assert.Equal(600, hukum.AchievedEventsPerSecond);
        Assert.Equal(GeneratorVerdict.Attained, hukum.Verdict);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Gecersiz_hiz_profili_kovaya_giremiyor(double eps)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TokenBucketPacer(
                new PaceProfile.Fixed(eps, TimeSpan.FromSeconds(1)),
                Saat()));
    }

    /// <summary>
    /// <b><c>soak</c> raporda <c>fixed</c> diye görünmüyor.</b>
    ///
    /// <para>
    /// Hız modeli aynı, sorusu farklı: burada aranan kayıp oranı değil
    /// <i>zamanla bozulma</i>. Adı ayrışmasa *"neden 6 saat koştu"* sorusunun
    /// cevabı hiçbir yerde yazılı olmazdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Soak_raporda_fixed_diye_gorunmuyor()
    {
        var soak = new PaceProfile.Soak(100, TimeSpan.FromHours(6));
        var sabit = new PaceProfile.Fixed(100, TimeSpan.FromHours(6));

        Assert.Equal(soak.TargetAt(TimeSpan.FromMinutes(1)), sabit.TargetAt(TimeSpan.FromMinutes(1)));
        Assert.NotEqual(soak.Name, sabit.Name);
        Assert.Equal("soak", soak.Name);
    }

    // ---------------------------------------------------------------------
    // Bekçinin kendi bekçisi
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Bekçi boş küme üzerinde dönmüyor.</b>
    ///
    /// <para>
    /// Yukarıdaki kapıların çoğu hüküm değerlerini <b>adla</b> karşılaştırıyor.
    /// Hüküm kümesi daralırsa ya da bir değer yeniden adlandırılırsa iddiaların
    /// bir kısmı yine geçer — kapsamını yitirmiş bir bekçi, olmayan bekçiyle
    /// aynı sonucu verir (§7).
    /// </para>
    ///
    /// <para>
    /// Ayrıca <c>Unspecified = 0</c> sayısal olarak sınanıyor: bir gün biri
    /// <c>Attained</c>'ı sıfıra alırsa yukarıdaki testler hâlâ geçer ama hiç
    /// ölçülmemiş bir koşum *"hedefe ulaştı"* diye okunmaya başlar.
    /// </para>
    /// </summary>
    [Fact]
    public void Bekci_bos_kume_uzerinde_donmuyor()
    {
        Assert.Equal(0, (int)GeneratorVerdict.Unspecified);
        Assert.NotEqual(0, (int)GeneratorVerdict.Attained);

        Assert.Equal(5, Enum.GetValues<GeneratorVerdict>().Length);

        // Beş profil, beş ayrı ad — biri diğerinin adıyla raporlanamaz.
        var adlar = new[]
        {
            new PaceProfile.Fixed(1, TimeSpan.FromSeconds(1)).Name,
            new PaceProfile.Ramp(1, 2, TimeSpan.FromSeconds(1)).Name,
            new PaceProfile.Burst(1, 2, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)).Name,
            new PaceProfile.Soak(1, TimeSpan.FromSeconds(1)).Name,
            new PaceProfile.Max(TimeSpan.FromSeconds(1)).Name,
        };

        Assert.Equal(adlar.Length, adlar.Distinct(StringComparer.Ordinal).Count());

        // Tavan gerçekten bir kapı: bugünkü ölçülen hâlleri (%82, %91) REDDEDİYOR.
        Assert.True(GeneratorAttainment.MinimumAttainment > 0.91);
        Assert.True(GeneratorAttainment.MinimumAttainment < 0.99);
    }
}
