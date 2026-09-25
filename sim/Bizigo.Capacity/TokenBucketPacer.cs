namespace Bizigo.Capacity;

/// <summary>
/// <b>Token kovası</b> — <c>SyslogEmitter</c>'ın satır başına
/// <c>Task.Delay</c>'inin yerine geçen hız kontrolü (B01).
///
/// <para>
/// <b>Neyin yerine geçtiği ölçüldü</b> (koordinatör, <c>RatePerMinute</c> →
/// <c>TimeSpan.FromMinutes(1.0/rate)</c>, 200 satır, macOS/arm64):
/// </para>
///
/// <list type="table">
/// <item><term>10 EPS</term><description>10 — tutuyor</description></item>
/// <item><term>100 EPS</term><description>91 — %9 geride</description></item>
/// <item><term>1 000 EPS</term><description>824 — %18 geride</description></item>
/// <item><term>10 000 EPS</term><description><b>pacing YOK</b></description></item>
/// </list>
///
/// <para>
/// İki ayrı kusur ve ikincisi bu deponun adını koyduğu sınıf. <b>Sapma:</b>
/// gecikme satırın kendi işini saymıyor, yani hedef yükseldikçe geriye
/// düşülüyor. <b>Sessiz anlamsızlaşma:</b> hesaplanan gecikme zamanlayıcı
/// çözünürlüğünün altına indiğinde <c>Task.Delay</c> <b>anında dönüyor</b> ve
/// ayar hiçbir şey ifade etmemeye başlıyor — hata yok, sayaç yok, uyarı yok.
/// Yani <i>10 000 EPS istemek</i> mümkün, <i>elde etmek</i> değil, ve ikisi
/// ayırt edilemiyor.
/// </para>
///
/// <para>
/// <b>Bu kova o eşiği ortadan kaldırmıyor — ONU ÖLÇÜLEBİLİR YAPIYOR.</b>
/// Hiçbir yazılım kovası makinenin tavanını yükseltmiyor; fark şu ki kova
/// <b>borcunu biriktiriyor</b>: bir tur geç kalındığında jeton havuzda kalıyor
/// ve sonraki turda harcanıyor, dolayısıyla üretilen toplam sayı hedefin
/// <i>arkasında kaldığında bunu bilen bir yer</i> oluyor.
/// <see cref="Debt"/> tam olarak o yer, ve <c>GENERATOR-LIMITED</c> hükmü onu
/// okuyor.
/// </para>
///
/// <para>
/// <b>Duvar saati yok.</b> Zaman <see cref="TimeProvider"/>'dan geliyor, yani
/// kovanın bütün mantığı <c>FakeTimeProvider</c> ile sınanıyor: bekçiler ne
/// bekleme ne konteyner istiyor (§2), ve bir testin geçme sebebi makinenin
/// yükü olmuyor (§6). Bu depoda zamana bağlı testler iki kez pahalıya patladı.
/// </para>
/// </summary>
public sealed class TokenBucketPacer
{
    private readonly TimeProvider _time;
    private readonly long _startedAt;

    /// <summary>Kovanın son doldurulduğu an (saat tick'i).</summary>
    private long _filledAt;

    /// <summary>Harcanabilir jeton — kesirli, çünkü hız kesirli olabiliyor.</summary>
    private double _tokens;

    /// <param name="profile">Hız profili; hedefi zamana göre veriyor.</param>
    /// <param name="time">
    /// Saat. Enjekte olması bir test kolaylığı değil kapının şartı — bkz. tip
    /// belgesi.
    /// </param>
    /// <param name="burstCapacity">
    /// Kovanın taşıma kapasitesi (jeton). <b>Varsayılan 1 saniyelik hedef</b>
    /// ve sınırı olması gerekli: sınırsız bir kova, uzun bir duraksamadan sonra
    /// biriken borcun tamamını tek seferde boşaltır ve ölçülen şey <i>profil</i>
    /// değil <i>duraksama</i> olur.
    /// </param>
    public TokenBucketPacer(PaceProfile profile, TimeProvider time, double? burstCapacity = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(time);
        profile.Validate();

        Profile = profile;
        _time = time;
        _startedAt = time.GetTimestamp();
        _filledAt = _startedAt;

        Capacity = burstCapacity ?? SuggestedCapacity(profile);

        if (!double.IsFinite(Capacity) || Capacity < 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(burstCapacity), burstCapacity, "Kova kapasitesi sonlu ve en az bir olmalı.");
        }

        // İlk satır hemen çıkabilir; bir saniyelik dolu kova kısa koşumun
        // ölçülen hızını hedefin üstüne şişirirdi.
        _tokens = profile.HasTarget ? 1.0 : 0.0;
    }

    public PaceProfile Profile { get; }

    /// <summary>Kovanın taşıma kapasitesi.</summary>
    public double Capacity { get; }

    /// <summary>Bu kovadan alınmış jeton sayısı — yani basılan satır.</summary>
    public long Granted { get; private set; }

    /// <summary>
    /// <b>Borç: hedefin ürettiği jeton ile gerçekten alınan arasındaki fark.</b>
    ///
    /// <para>
    /// Sıfırdan büyük olması <i>"üreteç hedefin arkasında"</i> demek ve bunu
    /// <b>söyleyebilen</b> tek yer burası. Eski modelde bu sayı hiç
    /// hesaplanmıyordu, dolayısıyla %18 geride koşmak ile hedefe ulaşmak aynı
    /// çıktıyı veriyordu.
    /// </para>
    ///
    /// <para>
    /// <see cref="PaceProfile.Max"/> için <b>anlamsız</b> ve
    /// <see langword="null"/> dönüyor: hedefi olmayan bir koşumun borcu olamaz.
    /// <c>0</c> dönmek *"borcu yok"* demek olurdu — yani ölçülemeyen bir boyutu
    /// **mükemmel** göstermek, bu deponun beş kez adını koyduğu sınıf.
    /// </para>
    /// </summary>
    public double? Debt => Profile.HasTarget ? Math.Max(0.0, Issued - Granted) : null;

    /// <summary>
    /// Hedefin şu ana kadar üretmesi gereken jeton sayısı — profilin
    /// integrali.
    /// </summary>
    public double Issued { get; private set; }

    /// <summary>Koşum başından beri geçen süre.</summary>
    public TimeSpan Elapsed => _time.GetElapsedTime(_startedAt);

    /// <summary>Koşum süresi doldu mu.</summary>
    public bool Finished => Elapsed >= Profile.Duration;

    /// <summary>
    /// <b>Bir satır basmak için izin ister.</b>
    ///
    /// <para>
    /// Beklemiyor — <see cref="TimeSpan"/> döndürüyor ve <b>beklemeyi çağırana
    /// bırakıyor</b>. Gerekçe: kova içinde <c>Task.Delay</c> çağırmak, yerine
    /// geçtiği modelin kusurunu tipin içine gömmek olurdu; ve toplu yazma
    /// (çağıranın birden çok satırı tek sokete vermesi) ancak çağıran beklemeyi
    /// yönetiyorsa mümkün.
    /// </para>
    ///
    /// <para>
    /// <see cref="TimeSpan.Zero"/> = *"hemen bas"*. Sıfırdan büyük bir değer =
    /// *"bu kadar bekle, sonra tekrar sor"*. Beklemenin gerçekten uygulanıp
    /// uygulanmadığı kovanın bilmediği bir şey ve bilmemesi doğru — ama
    /// <see cref="Debt"/> uygulanmadığını <b>gösteriyor</b>.
    /// </para>
    /// </summary>
    public TimeSpan TryAcquire()
    {
        Refill();

        // Integral arithmetic at a rate boundary may leave a sub-ulp deficit.
        if (_tokens >= 1.0 - 1e-12)
        {
            _tokens -= 1.0;
            Granted++;

            return TimeSpan.Zero;
        }

        var rate = Profile.TargetAt(Elapsed);

        // Hedefi olmayan profil (`max`) hiç beklemiyor: doyma noktasını ölçmek
        // için elden gelen hızda basılıyor.
        if (rate is null)
        {
            Granted++;

            return TimeSpan.Zero;
        }

        var wait = TimeSpan.FromSeconds((1.0 - _tokens) / rate.Value);
        var elapsed = Elapsed;
        var nextRateChange = Profile.NextRateChangeAfter(elapsed);

        if (nextRateChange is not null && nextRateChange.Value > elapsed)
        {
            wait = Min(wait, nextRateChange.Value - elapsed);
        }

        // Son jeton bekleyişi profil süresini de aşmasın. Çağıran sınırda
        // yeniden değerlendirip koşumu bitirebilir; aksi hâlde son uyku veya
        // soket yazımı hedef EPS'i gerçek sürenin içine yayar.
        var untilFinished = Profile.Duration - elapsed;
        if (untilFinished > TimeSpan.Zero)
        {
            wait = Min(wait, untilFinished);
        }

        // A sub-tick delay can round to zero without granting a token. Zero
        // means permission to emit, so preserve a positive wait in that case.
        return wait > TimeSpan.Zero ? wait : TimeSpan.FromTicks(1);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) =>
        left <= right ? left : right;

    private void Refill()
    {
        var now = _time.GetTimestamp();
        var from = _time.GetElapsedTime(_startedAt, _filledAt);
        var to = _time.GetElapsedTime(_startedAt, now);
        var span = to - from;

        if (span <= TimeSpan.Zero)
        {
            return;
        }

        _filledAt = now;

        var produced = Profile.IssuedBetween(from, to);

        if (produced is null)
        {
            return;
        }

        Issued += produced.Value;

        // Kapasite tavanı borcu SİLMİYOR — yalnızca tek seferde boşaltılmasını
        // engelliyor. `Issued` tavandan bağımsız birikiyor, dolayısıyla
        // `Debt` taşan jetonları da sayıyor ve hüküm gerçek sapmayı görüyor.
        _tokens = Math.Min(Capacity, _tokens + produced.Value);
    }

    /// <summary>
    /// Hüküm alınmadan önce sayacı bugüne getirir. Jeton tüketmez; yalnızca
    /// geçen zamanın profil integralini <see cref="Issued"/> değerine işler.
    /// </summary>
    internal void Synchronize() => Refill();

    private static double SuggestedCapacity(PaceProfile profile) => profile switch
    {
        PaceProfile.Fixed fixedProfile => Math.Max(1.0, fixedProfile.EventsPerSecond),
        PaceProfile.Ramp ramp => Math.Max(1.0, Math.Max(ramp.FromEventsPerSecond, ramp.ToEventsPerSecond)),
        PaceProfile.Burst burst => Math.Max(1.0, burst.PeakEventsPerSecond),
        PaceProfile.Soak soak => Math.Max(1.0, soak.EventsPerSecond),
        PaceProfile.Max => 1.0,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Bilinmeyen hız profili."),
    };
}
