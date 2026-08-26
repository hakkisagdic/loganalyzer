namespace Bizigo.Contracts;

/// <summary>
/// Bir RCA koşumunu doğuran kaynak (RCA §5, K20) — <b>tek liste</b>.
///
/// <para>
/// <b>Neden burada.</b> Bu küme iki yerden okunuyor: koşum kaydı
/// (<c>Bizigo.ControlPlane</c>) ve senaryo plugin sözlüğü
/// (<c>Bizigo.ScenarioPlugin</c>). İkisi birbirini görmüyor — plugin çekirdeği
/// kanıt katmanının üstünde duruyor ve EF'i hiç tanımıyor. Tip depolama
/// projesinde kalsaydı plugin tarafı kendi listesini yazmak zorunda kalırdı, ve
/// <b>iki liste sessizce ayrışırdı</b>: bu deponun S04'te tam olarak ödediği
/// bedel — sözlük birleştirilmiş, predicate birleştirilmemişti; birim paketi
/// sessiz kaldı, kırığı CI gördü.
/// </para>
///
/// <para>
/// <b>Dört değer, ve beşinci burada YOK.</b> RCA §5 tablosu beş satır sayıyor
/// ama beşincisi — anomali zinciri — bir kaynak değil bir <b>devam</b> kuralı:
/// girdisi bir sinyal değil, daha önce koşmuş bir RCA. Zincirin ilk halkası her
/// zaman bu dört değerden biri, ve devamın tek izi <c>RcaRunEntity.Depth &gt; 0</c>;
/// kaynağını kökünden miras alıyor.
/// </para>
///
/// <para>
/// Plugin sözlüğünde <c>anomaly</c> yine de var, çünkü orada soru başka: bir
/// senaryo <i>"bir zincir devam ettiğinde koş"</i> diyebilmeli. O fazlalık
/// <c>ScenarioTriggers</c>'ta <b>adı konmuş tek bir istisna</b> olarak duruyor,
/// gizli bir altıncı üye olarak değil.
/// </para>
///
/// <para>
/// <b>Değer adları plugin sözlüğünün kelimeleriyle aynı</b> (<c>manual</c>,
/// <c>external</c>). Alternatif bir eşleme tablosu tutmaktı; tablo tam da
/// ayrışmanın saklanacağı yer olurdu. Kelimeler F4 plugin formatı §3.1'de zaten
/// yazılı ve sahadaki YAML dosyaları onları kullanıyor, dolayısıyla sabit olan
/// taraf o: uyum sağlayan enum.
/// </para>
///
/// <para>
/// <b>Sayısal değerler sabit</b> — veritabanında <c>int</c> olarak duruyorlar.
/// Yeniden numaralandırmak yazılmış koşum kayıtlarının kaynağını sessizce
/// değiştirir.
/// </para>
/// </summary>
public enum RcaTriggerSource
{
    /// <summary>
    /// Alarm ya da Sigma — ikisi arasında çalışma zamanında <b>yol farkı yok</b>.
    /// Giriş noktası <c>alert_triggers</c> tablosuna düşen satır; <c>AlertRaised</c>
    /// diye bir olay depoda hiç yok ve tasarım onu varsayıyordu.
    /// </summary>
    Alert = 0,

    /// <summary>Kullanıcı, arayüzden.</summary>
    Manual = 1,

    /// <summary>Dış API — <c>POST /v1/rca</c> + <c>Idempotency-Key</c>.</summary>
    External = 2,

    /// <summary>
    /// Takvim / cron. Kotanın <b>en öngörülebilir</b> tüketicisi: diğer üçü
    /// olaya bağlı, bu takvime.
    /// </summary>
    Schedule = 3,
}

/// <summary>
/// <see cref="RcaTriggerSource"/>'un tel/YAML karşılıkları.
///
/// <para>
/// Çeviri <b>hesaplanıyor</b>, elle yazılmıyor: enum büyüdüğünde sözlük de
/// büyüyor ve kimsenin bir listeyi güncellemesi gerekmiyor.
/// </para>
/// </summary>
public static class RcaTriggerVocabulary
{
    /// <summary>Kaynağın plugin formatındaki kelimesi.</summary>
    public static string ToWireName(this RcaTriggerSource source) =>
        source.ToString().ToLowerInvariant();

    /// <summary>Dört kaynağın kelimeleri — sıralı ve tekil.</summary>
    public static IReadOnlySet<string> WireNames { get; } =
        Enum.GetValues<RcaTriggerSource>()
            .Select(ToWireName)
            .ToHashSet(StringComparer.Ordinal);
}
