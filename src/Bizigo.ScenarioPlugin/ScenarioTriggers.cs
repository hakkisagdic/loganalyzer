using Bizigo.Contracts;

namespace Bizigo.ScenarioPlugin;

/// <summary>
/// <c>trigger.on</c>'un <b>kapalı</b> değer kümesi (K20 + F4 plugin formatı §3.1).
///
/// <para>
/// Küme kapalı çünkü açık olsaydı K20'nin <i>"dördü tek kuyrukta buluşur;
/// debounce, döngü koruması ve kota tek yerde"</i> garantisi yeni değerler için
/// <b>tanımsız</b> kalırdı. Yeni tetikleyici eklemek bir çekirdek kararı, plugin
/// kararı değil.
/// </para>
///
/// <para>
/// <b>Liste artık elle yazılmıyor — <see cref="RcaTriggerSource"/>'tan
/// türüyor</b> (T45). T43 bu dosyayı beş sabitle bırakmıştı ve o beş sabit
/// K20'den alıntıydı; T45 tetikleyicileri kendi tipiyle getirince alıntının
/// ikinci bir liste olarak yaşaması, iki listenin sessizce ayrışması demekti.
/// Ayrışmanın nasıl göründüğünü bu depo S04'te ölçtü: baseline'ın iki gösterimi
/// vardı, sözlük birleştirilmiş <b>predicate birleştirilmemişti</b>, iki taraf
/// da kendi içinde tutarlıydı ve birim paketi sessiz kaldı.
/// </para>
///
/// <para>
/// <b>Enum'u silip bu sabitleri korumak da meşruydu; tersini seçtim.</b> Enum
/// kabul kapısının kararını taşıyor — koşum kaydına yazılıyor, debounce ve
/// soyağacı anahtarlarına giriyor, kotanın ayrım noktası olacak (T46). Sözlük
/// ise bu kararın <i>okunabilir yüzü</i>. Karar veren tarafın kaynak, okuyan
/// tarafın türev olması gerekiyor; tersi, bir YAML kelimesinin veritabanı
/// şemasını belirlemesi olurdu.
/// </para>
/// </summary>
public static class ScenarioTriggers
{
    public const string Alert = "alert";
    public const string Manual = "manual";
    public const string External = "external";

    /// <summary>Beşinci — F4'ün yan çıktısı olarak K20'ye girdi.</summary>
    public const string Schedule = "schedule";

    /// <summary>
    /// <b>Kümenin tek fazlalığı, ve adı konmuş.</b>
    ///
    /// <para>
    /// Anomali zinciri <see cref="RcaTriggerSource"/>'ta <b>yok</b>, çünkü orada
    /// soru "bu koşumu ne doğurdu": zincir bir kaynak değil bir devam, ve izi
    /// koşumun derinliğinde. Burada soru başka: <i>"bir senaryo neye bakarak
    /// koşabilir"</i>. Bir senaryonun "bir zincir devam ettiğinde koş" demesi
    /// meşru, dolayısıyla kelime sözlükte kalıyor.
    /// </para>
    ///
    /// <para>
    /// Fazlalığın ayrı bir alan olarak durması bilinçli: gizli bir altıncı üye
    /// olarak kümeye karışsaydı "sözlük neden enum'dan bir fazla" sorusunun
    /// cevabı hiçbir yerde yazılı olmazdı.
    /// </para>
    /// </summary>
    public const string Anomaly = "anomaly";

    /// <summary>
    /// Devam kuralının kelimeleri — kaynak <b>olmayan</b>, ama bir senaryonun
    /// bekleyebileceği tetiklenmeler.
    /// </summary>
    public static readonly IReadOnlySet<string> Continuations =
        new HashSet<string>(StringComparer.Ordinal) { Anomaly };

    /// <summary>
    /// Bir pluginin <c>trigger.on</c>'da yazabileceği her şey: dört kaynak,
    /// artık <see cref="Continuations"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> Known =
        RcaTriggerVocabulary.WireNames
            .Concat(Continuations)
            .ToHashSet(StringComparer.Ordinal);
}
