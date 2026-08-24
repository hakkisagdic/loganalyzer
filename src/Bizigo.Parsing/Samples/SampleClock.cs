namespace Bizigo.Parsing.Samples;

/// <summary>
/// <b>Altın örneklerin hangi ana damgalanacağının tek tanımı</b> (T39 · FS S02).
///
/// <para>
/// Örnek dosyalar 2015–2022 tarihleri taşıyor ve <b>hiçbir tüketici onları
/// olduğu gibi basmıyor</b>: yükleyici geçmişe yayarken, simülatör basım anına
/// kaydırırken aynı kuralı uyguluyor. Kural iki cümle, ve ikisinin de gerekçesi
/// ölçülmüş bir olaydan geliyor.
/// </para>
///
/// <h3>1 · Çapa "şimdi"dir — çünkü eski satır sessizce siliniyor</h3>
///
/// <para>
/// <c>events</c> tablosunda <c>TTL ts + 90 gün</c> var. Süresi dolmuş bir satır
/// yazıldığında ClickHouse <b>hata vermiyor</b>: yazımı kabul ediyor, satır
/// sayısını dönüyor, sonra satırı siliyor. Yani istemci "yazdım" diye
/// okuyor ve tabloda hiçbir şey yok. Gerçek bir cihaz da zaten 2016 tarihi
/// basmaz; kaydırma hem doğru hem zorunlu.
/// </para>
///
/// <h3>2 · Çapa tam saniyeye iniyor — çünkü biçimler saniyenin altını taşımıyor</h3>
///
/// <para>
/// Syslog (<c>MMM d HH:mm:ss</c>) ve HTTP (<c>dd/MMM/yyyy:HH:mm:ss zzz</c>)
/// biçimlerinde saniyenin altı <b>yok</b>. Kesirli bir an ekilirse yeniden
/// yazılan satır onu kaybeder ve <c>ts</c> ekilen andan farklı çıkar — küçük
/// bir fark, ama "ektiğim an ile yazılan an aynı" doğrulamasını her satırda
/// düşürür ve doğrulama düşünce onunla birlikte <b>gerçek</b> kaymaları da
/// göremez hâle geliriz.
/// </para>
///
/// <para>
/// <b>Neden burada:</b> kırpma kuralı dört ayrı yerde yazılıydı (CLI'ın iki
/// komutu, yayılım planı, baseline ölçümü) ve beşincisi simülatör olacaktı.
/// Ayrışmaları sessiz: biri TTL'e takılır, diğeri takılmaz, hiçbir sayaç bunu
/// söylemez. <c>Bizigo.Parsing.Samples</c> ortak evleri — örnek satırların
/// zamanını bilen tek yer zaten burası (<see cref="SampleTimeRewriter"/>).
/// </para>
/// </summary>
public static class SampleClock
{
    /// <summary>
    /// Basım/yayılım çapası: <b>şimdi</b>, UTC, tam saniye.
    /// </summary>
    /// <param name="timeProvider">
    /// Testler duvar saatine bağlı kalmasın diye dışarıdan verilebiliyor;
    /// <c>null</c> ise sistem saati.
    /// </param>
    public static DateTimeOffset Anchor(TimeProvider? timeProvider = null) =>
        Truncate((timeProvider ?? TimeProvider.System).GetUtcNow());

    /// <summary>
    /// Verilen anı tam saniyeye indirir ve UTC'ye çevirir.
    ///
    /// <para>
    /// UTC'ye çevirmek bilinçli: çapa bir <b>an</b>, bir yerel saat değil.
    /// Yeniden yazıcı her biçimi kendi diliminde render ediyor
    /// (<see cref="SampleTimeRewriter"/>), dolayısıyla çapanın ofseti taşınması
    /// gereken bir bilgi değil — taşınırsa iki tüketici aynı andan farklı
    /// metinler üretebilir.
    /// </para>
    /// </summary>
    public static DateTimeOffset Truncate(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    /// <summary>
    /// <c>TTL</c>'e takılmadan yazılabilecek <b>en eski</b> an.
    ///
    /// <para>
    /// Saklama süresi <b>parametre</b>, sabit değil: değeri
    /// <c>db/clickhouse/0001_events.sql</c>'deki <c>TTL … INTERVAL N DAY</c>
    /// satırı belirliyor ve buraya ikinci kez yazmak, şema değiştiği gün ikisinin
    /// sessizce ayrışması demek olurdu. Çağıran onu kaynaktan okuyup veriyor.
    /// </para>
    /// </summary>
    public static DateTimeOffset EarliestWritable(DateTimeOffset now, TimeSpan retention)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        return Truncate(now) - retention;
    }

    /// <summary>
    /// Bu an yazıldığında tabloda <b>kalır mı</b>.
    ///
    /// <para>
    /// <c>false</c> dönen bir an için ClickHouse yazımı kabul eder, satır
    /// sayısını döner ve satırı siler. Çağıranın bunu yazmadan önce sorması
    /// gerekiyor; sonradan sormanın yolu yok, çünkü ortada bir hata yok.
    /// </para>
    /// </summary>
    public static bool Survives(DateTimeOffset instant, DateTimeOffset now, TimeSpan retention) =>
        instant >= EarliestWritable(now, retention);
}
