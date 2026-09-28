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

        Profile = profile;
        _time = time;
        _startedAt = time.GetTimestamp();
        _filledAt = _startedAt;

        var initial = profile.TargetAt(TimeSpan.Zero);

        Capacity = burstCapacity ?? Math.Max(1.0, initial ?? 1.0);
        _tokens = Math.Min(Capacity, initial ?? 0.0);
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

        if (_tokens >= 1.0)
        {
            _tokens -= 1.0;
            Granted++;

            return TimeSpan.Zero;
        }

        var rate = Profile.TargetAt(Elapsed);

        // Hedefi olmayan profil (`max`) hiç beklemiyor: doyma noktasını ölçmek
        // için elden gelen hızda basılıyor.
        if (rate is null or <= 0.0)
        {
            Granted++;

            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds((1.0 - _tokens) / rate.Value);
    }

    private void Refill()
    {
        var now = _time.GetTimestamp();
        var span = _time.GetElapsedTime(_filledAt, now);

        if (span <= TimeSpan.Zero)
        {
            return;
        }

        _filledAt = now;

        var rate = Profile.TargetAt(Elapsed);

        if (rate is null)
        {
            return;
        }

        var produced = rate.Value * span.TotalSeconds;

        Issued += produced;

        // Kapasite tavanı borcu SİLMİYOR — yalnızca tek seferde boşaltılmasını
        // engelliyor. `Issued` tavandan bağımsız birikiyor, dolayısıyla
        // `Debt` taşan jetonları da sayıyor ve hüküm gerçek sapmayı görüyor.
        _tokens = Math.Min(Capacity, _tokens + produced);
    }
}
