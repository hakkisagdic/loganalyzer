namespace Bizigo.Capacity;

/// <summary>
/// <b>Bir yük koşumunun hız profili</b> — beş ayrı soru, beş ayrı tip (B01).
///
/// <para>
/// Kapalı bir hiyerarşi ve <see langword="sealed"/>: yeni bir profil eklemek
/// bu dosyayı açmayı gerektiriyor, çünkü her profil <b>ne ölçtüğünü</b> yazmak
/// zorunda. Bir <c>enum</c> + yanında serbest alanlar olsaydı, hangi alanın
/// hangi profil için anlamlı olduğu çağıranın hafızasında kalırdı.
/// </para>
///
/// <para>
/// <b><see cref="Max"/>'ın hız alanı YOK ve bu tipin var olma sebebi.</b>
/// Plan §B01 şartı: <i>"`max` profili bir hedef değil bir ölçüm — bir sayı
/// girdisi almayacak, çünkü aldığı an ölçtüğü şey o sayı olur."</i> Alternatif
/// bir <c>double? Rate</c> alanı + çalışma anı reddi aynı şeyi söylerdi, ama
/// <b>unutulabilir</b> olurdu; burada <c>max</c>'a bir hedef vermek
/// <b>derlenmiyor</b>. Kalıp T41'in <c>RedactedPrompt</c>'undan ve T54'ün
/// <c>RcaModelBoundaryStamp</c>'inden.
/// </para>
/// </summary>
public abstract record PaceProfile
{
    private PaceProfile()
    {
    }

    /// <summary>Rapora ve koşum kaydına giden ad.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// Koşumun <b>t</b> anındaki hedef hızı (olay/saniye), ya da
    /// <see langword="null"/> — *"hedefim yok, elimden geleni yapıyorum"*.
    ///
    /// <para>
    /// <see langword="null"/> ile <c>0</c> aynı şey <b>değil</b>: sıfır
    /// *"hiç basma"* demek, <see langword="null"/> *"tavanı ölç"*. İkisini tek
    /// değere indirmek <see cref="Max"/>'ı sessizce durduran bir profil hâline
    /// getirirdi.
    /// </para>
    /// </summary>
    public abstract double? TargetAt(TimeSpan elapsed);

    /// <summary>Koşumun toplam süresi.</summary>
    public abstract TimeSpan Duration { get; }

    /// <summary>
    /// <b>Bu profilin bir hedefi var mı.</b> Hüküm bunu okuyor:
    /// hedefi olmayan bir koşum <c>GENERATOR-LIMITED</c> olamaz, çünkü
    /// ulaşılamayan bir hedef yok.
    /// </summary>
    public bool HasTarget => this is not Max;

    // -----------------------------------------------------------------
    // fixed — sabit yük
    // -----------------------------------------------------------------

    /// <summary>Sabit hız: <i>"şu hızda şu kadar süre, kayıp var mı"</i>.</summary>
    public sealed record Fixed(double EventsPerSecond, TimeSpan Length) : PaceProfile
    {
        public override string Name => "fixed";

        public override TimeSpan Duration => Length;

        public override double? TargetAt(TimeSpan elapsed) => EventsPerSecond;
    }

    // -----------------------------------------------------------------
    // ramp — kademeli tırmanma
    // -----------------------------------------------------------------

    /// <summary>
    /// Kademeli tırmanma: <i>"hangi hızda bozulmaya başlıyor"</i>.
    ///
    /// <para>
    /// <b>Doğrusal ve kademesiz</b> — basamaklı bir tırmanma
    /// <see cref="Max"/>'ın ikili aramasının işi (B03) ve ikisini karıştırmak,
    /// tırmanmanın bulduğu sayıyı basamak genişliğine bağlardı.
    /// </para>
    /// </summary>
    public sealed record Ramp(double FromEventsPerSecond, double ToEventsPerSecond, TimeSpan Length)
        : PaceProfile
    {
        public override string Name => "ramp";

        public override TimeSpan Duration => Length;

        public override double? TargetAt(TimeSpan elapsed)
        {
            if (Length <= TimeSpan.Zero)
            {
                return ToEventsPerSecond;
            }

            var ratio = Math.Clamp(elapsed / Length, 0.0, 1.0);

            return FromEventsPerSecond + ((ToEventsPerSecond - FromEventsPerSecond) * ratio);
        }
    }

    // -----------------------------------------------------------------
    // burst — kısa ani yük
    // -----------------------------------------------------------------

    /// <summary>
    /// Ani yük: taban hız üstüne <b>kısa</b> bir tepe.
    ///
    /// <para>
    /// Sorduğu soru <see cref="Fixed"/>'inkinden farklı: tepe hızında
    /// <b>tamponların</b> ne yaptığı. Bir <c>fixed</c> koşumu aynı tepeyi
    /// sürekli uygularsa tamponlar dolar ve ölçülen şey kararlı hâl olur —
    /// oysa gerçek cihaz filoları ani basar.
    /// </para>
    /// </summary>
    public sealed record Burst(
        double BaseEventsPerSecond,
        double PeakEventsPerSecond,
        TimeSpan PeakAt,
        TimeSpan PeakLength,
        TimeSpan Length) : PaceProfile
    {
        public override string Name => "burst";

        public override TimeSpan Duration => Length;

        public override double? TargetAt(TimeSpan elapsed) =>
            elapsed >= PeakAt && elapsed < PeakAt + PeakLength
                ? PeakEventsPerSecond
                : BaseEventsPerSecond;
    }

    // -----------------------------------------------------------------
    // soak — uzun süre kararlılık
    // -----------------------------------------------------------------

    /// <summary>
    /// Uzun süre kararlılık. Hız <see cref="Fixed"/> ile aynı biçimde sabit;
    /// <b>ayrı bir tip olmasının sebebi süre değil SORU</b>: burada aranan şey
    /// kayıp oranı değil <i>zamanla bozulma</i> — sızan bellek, dolan disk,
    /// büyüyen kuyruk.
    ///
    /// <para>
    /// Ayrı tip olmasa raporda <c>fixed</c> diye görünürdü ve *"neden 6 saat
    /// koştu"* sorusunun cevabı hiçbir yerde yazılı olmazdı.
    /// </para>
    /// </summary>
    public sealed record Soak(double EventsPerSecond, TimeSpan Length) : PaceProfile
    {
        public override string Name => "soak";

        public override TimeSpan Duration => Length;

        public override double? TargetAt(TimeSpan elapsed) => EventsPerSecond;
    }

    // -----------------------------------------------------------------
    // max — hedef değil ölçüm
    // -----------------------------------------------------------------

    /// <summary>
    /// <b>Doyma noktasını bul.</b> Hız alanı <b>yok</b> ve olmaması bu tipin
    /// tamamı: bir hedef verildiği an ölçüm o hedefi ölçer.
    ///
    /// <para>
    /// <see cref="TargetAt"/> <see langword="null"/> dönüyor, <c>0</c> değil —
    /// *"hedefim yok"* ile *"hiç basma"* farklı cümleler ve ikisini aynı değere
    /// indirmek bu profili sessizce durdurulmuş bir koşuma çevirirdi.
    /// </para>
    /// </summary>
    public sealed record Max(TimeSpan Length) : PaceProfile
    {
        public override string Name => "max";

        public override TimeSpan Duration => Length;

        public override double? TargetAt(TimeSpan elapsed) => null;
    }
}
