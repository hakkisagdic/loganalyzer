using System.Globalization;

namespace Bizigo.Capacity;

/// <summary>
/// Bir yük koşumunun <b>üreteç tarafındaki</b> hükmü (B01).
///
/// <para>
/// syslog-bench'ten alınan şey kod değil <b>hüküm</b>: ölçüm kendi kusurunu
/// ölçtüğünde suçu hedefe yıkmayı reddediyor.
/// </para>
/// </summary>
public enum GeneratorVerdict
{
    /// <summary>
    /// <b>Hiçbir şey söylenmedi.</b> Hüküm kurulmadı — sıfır olması bilinçli:
    /// varsayılanı <see cref="Attained"/> olsaydı, hiç ölçülmemiş bir koşum
    /// *"üreteç hedefe ulaştı"* diye okunurdu.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Üreteç hedefe ulaştı; kayıp ölçümü <b>yorumlanabilir</b>.
    /// </summary>
    Attained = 1,

    /// <summary>
    /// <b><c>GENERATOR-LIMITED</c></b> — üreteç istenen hıza ulaşamadı.
    ///
    /// <para>
    /// Bu koşumdaki eksik olay <b>hedefin kaybı sayılamaz</b>: hiç basılmamış
    /// bir satır kaybolmuş değildir. Plan §B01'in bitti ölçütü tam olarak bu
    /// cümlenin bir bekçiyle tutulmasını istiyor.
    /// </para>
    /// </summary>
    GeneratorLimited = 2,

    /// <summary>
    /// <b>Ölçemedim.</b> Gerçekleşen hız okunamadı ya da koşum hiç süre
    /// almadı.
    ///
    /// <para>
    /// <see cref="GeneratorLimited"/>'dan ayrı olması zorunlu: biri
    /// *"üreteç yetişemedi"*, öteki *"yetişip yetişmediğini bilmiyorum"*.
    /// İkincisini birincisine indirmek bir arıza uydurmak, birincisine
    /// <see cref="Attained"/> demek ise arızayı gizlemek olurdu.
    /// </para>
    /// </summary>
    Unmeasured = 3,

    /// <summary>
    /// Profilin <b>hedefi yok</b> (<see cref="PaceProfile.Max"/>), dolayısıyla
    /// ulaşılamayan bir hedef de yok.
    ///
    /// <para>
    /// <see cref="Attained"/> demek yanlış olurdu — ulaşılan bir şey yok;
    /// <see cref="Unmeasured"/> demek de yanlış: ölçüm yapıldı, yalnızca
    /// karşılaştırılacak bir hedef yoktu.
    /// </para>
    /// </summary>
    NoTarget = 4,
}

/// <summary>
/// <b>Üretecin kendi tavanının ölçümü</b> — istenen ↔ gerçekleşen (B01).
///
/// <para>
/// Plan §B01'in birinci şartı: <i>"Üretecin doyduğu nokta bilinmiyorsa hiçbir
/// kayıp ölçümü yorumlanamaz: kayıp mı, üretilememe mi?"</i> Bu tip o soruyu
/// cevaplayan tek yer, ve <b>saf</b>: sayılardan karar veriyor, duvar saati
/// okumuyor, dosya açmıyor. Dolayısıyla hüküm mantığının bekçileri ne konteyner
/// ne bekleme istiyor (§2, §6).
/// </para>
///
/// <para>
/// <b>Üç hâl, üç ayrı cümle</b> ve ayrımın kalıbı B02'nin
/// <c>LedgerReading.Value == null</c> kararının aynısı — bu depoda beşinci
/// örneği (T36 <c>Measured=false</c>, T47 <c>Unspecified</c>, T51
/// <c>dropped_sentence_ratio</c>, T54 <c>RcaModelBoundary</c>).
/// </para>
/// </summary>
public sealed record GeneratorAttainment
{
    [System.Text.Json.Serialization.JsonConstructor]
    public GeneratorAttainment(
        GeneratorVerdict verdict,
        double? requestedEventsPerSecond,
        double? achievedEventsPerSecond,
        long emitted,
        TimeSpan elapsed,
        string basis)
    {
        Verdict = verdict;
        RequestedEventsPerSecond = requestedEventsPerSecond;
        AchievedEventsPerSecond = achievedEventsPerSecond;
        Emitted = emitted;
        Elapsed = elapsed;
        Basis = basis;
    }

    /// <summary>
    /// <b>Kabul edilen en düşük ulaşım oranı</b> — syslog-bench'in
    /// <c>--min-generator-attainment 95</c>'inin karşılığı.
    ///
    /// <para>
    /// Sayı <b>ölçümden</b> geldi, seçilmedi: bugünkü <c>Task.Delay</c> modeli
    /// 1 000 EPS'de <b>%82</b> ulaşıyor (824/1 000), 100 EPS'de <b>%91</b>.
    /// %95 tavanı ikisini de <b>reddediyor</b>, yani kapı bugünkü hâli
    /// geçirmiyor — bir tavanın işe yaradığının kanıtı, mevcut davranışı
    /// kutsamamasıdır.
    /// </para>
    ///
    /// <para>
    /// <b>Neden daha yükseği değil:</b> %99 seçilseydi kova ile gerçek soket
    /// arasındaki normal jitter kapıyı gürültüyle kırmızı yakardı ve tavan
    /// rutin olarak yükseltilen bir sabite dönerdü — M02'de ölçülen ve
    /// kaldırılan hâl.
    /// </para>
    /// </summary>
    public const double MinimumAttainment = 0.95;

    public GeneratorVerdict Verdict { get; }

    /// <summary>
    /// Hedef hız; <see langword="null"/> ise profilin hedefi yok
    /// (<see cref="PaceProfile.Max"/>).
    /// </summary>
    public double? RequestedEventsPerSecond { get; }

    /// <summary>
    /// Gerçekleşen hız; <see langword="null"/> ise <b>ölçülemedi</b> — sıfır
    /// değil. Sıfır *"hiç basmadı"* demek.
    /// </summary>
    public double? AchievedEventsPerSecond { get; }

    public long Emitted { get; }

    public TimeSpan Elapsed { get; }

    /// <summary>
    /// Hükmün <b>nasıl kurulduğu</b> — boş olamaz. Kalıp M06'nın
    /// <c>McpBoundaryDeclaration.Basis</c>'inden: bu üründe bir hüküm hiçbir
    /// yerde gerekçesiz görünmüyor.
    /// </summary>
    public string Basis { get; }

    /// <summary>
    /// Ulaşım oranı; hedef ya da ölçüm yoksa <see langword="null"/>.
    /// </summary>
    public double? Ratio =>
        RequestedEventsPerSecond is > 0.0 && AchievedEventsPerSecond is not null
            ? AchievedEventsPerSecond.Value / RequestedEventsPerSecond.Value
            : null;

    /// <summary>
    /// <b>Bu koşumun kayıp ölçümü yorumlanabilir mi.</b>
    ///
    /// <para>
    /// Yalnızca <see cref="GeneratorVerdict.Attained"/> ve
    /// <see cref="GeneratorVerdict.NoTarget"/> için <see langword="true"/>.
    /// <see cref="GeneratorVerdict.Unmeasured"/>'ın <b>false</b> olması
    /// kritik: ölçülmemiş bir üreteç tavanı, kayıp sayısını *"hedefin kaybı"*
    /// diye okumaya izin vermiyor.
    /// </para>
    /// </summary>
    public bool LossIsInterpretable =>
        Verdict is GeneratorVerdict.Attained or GeneratorVerdict.NoTarget;

    /// <summary>
    /// Hükmü <b>kovanın kendi sayaçlarından</b> kuruyor.
    ///
    /// <para>
    /// Gerçekleşen hızı çağırandan almıyor: alsaydı çağıran hedefin kendisini
    /// geçirebilir ve hüküm her koşumda <see cref="GeneratorVerdict.Attained"/>
    /// çıkardı — ölçümün cevabını ölçümün girdisine taşımak.
    /// </para>
    /// </summary>
    public static GeneratorAttainment From(TokenBucketPacer pacer)
    {
        ArgumentNullException.ThrowIfNull(pacer);

        pacer.Synchronize();
        var elapsed = pacer.Elapsed;
        var targetWindow = elapsed < pacer.Profile.Duration
            ? elapsed
            : pacer.Profile.Duration;
        double? target = targetWindow > TimeSpan.Zero && pacer.Profile.HasTarget
            ? pacer.Issued / targetWindow.TotalSeconds
            : null;

        if (!pacer.Profile.HasTarget)
        {
            return new GeneratorAttainment(
                GeneratorVerdict.NoTarget,
                null,
                Achieved(pacer.Granted, elapsed),
                pacer.Granted,
                elapsed,
                $"profil `{pacer.Profile.Name}` hedef almıyor; sayı doyma ölçümüdür");
        }

        var achieved = Achieved(pacer.Granted, elapsed);

        if (achieved is null)
        {
            return new GeneratorAttainment(
                GeneratorVerdict.Unmeasured,
                target,
                null,
                pacer.Granted,
                elapsed,
                "koşum ölçülebilir bir süre almadı; gerçekleşen hız hesaplanamaz");
        }

        if (target is null or <= 0.0)
        {
            return new GeneratorAttainment(
                GeneratorVerdict.Unmeasured,
                target,
                achieved,
                pacer.Granted,
                elapsed,
                "hedef hız sıfır ya da tanımsız; ulaşım oranının paydası yok");
        }

        var ratio = achieved.Value / target.Value;

        return new GeneratorAttainment(
            ratio >= MinimumAttainment ? GeneratorVerdict.Attained : GeneratorVerdict.GeneratorLimited,
            target,
            achieved,
            pacer.Granted,
            elapsed,
            string.Create(
                CultureInfo.InvariantCulture,
                $"ulaşım {ratio:P1} (taban {MinimumAttainment:P0}), borç {pacer.Debt:F1} jeton"));
    }

    private static double? Achieved(long emitted, TimeSpan elapsed) =>
        elapsed > TimeSpan.Zero ? emitted / elapsed.TotalSeconds : null;

    /// <summary>Rapor satırı — hükmü ve gerekçesini birlikte basıyor.</summary>
    public string Describe()
    {
        var requested = RequestedEventsPerSecond?.ToString("F0", CultureInfo.InvariantCulture) ?? "—";
        var achieved = AchievedEventsPerSecond?.ToString("F0", CultureInfo.InvariantCulture) ?? "ölçülemedi";

        return Verdict switch
        {
            GeneratorVerdict.GeneratorLimited =>
                $"GENERATOR-LIMITED: istenen {requested} EPS, gerçekleşen {achieved} EPS — "
                + $"{Basis}. Bu koşumdaki eksik olay HEDEFİN KAYBI SAYILAMAZ.",

            GeneratorVerdict.Unmeasured => $"ÖLÇÜLEMEDİ: {Basis}. Kayıp sayısı yorumlanamaz.",

            GeneratorVerdict.NoTarget => $"HEDEF YOK: doyma {achieved} EPS — {Basis}.",

            GeneratorVerdict.Attained =>
                $"ULAŞILDI: istenen {requested} EPS, gerçekleşen {achieved} EPS — {Basis}.",

            _ => "HÜKÜM KURULMADI.",
        };
    }
}
