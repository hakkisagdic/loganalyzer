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
/// <b>Bu küme T45'in alanı.</b> Buradaki beş değer K20'nin kararından
/// alıntı, ikinci bir karar değil. T45 tetikleyicileri kendi tipiyle
/// getirdiğinde bu liste <b>ondan beslenmeli</b>; ikinci bir enum doğarsa iki
/// liste sessizce ayrışır. Ayrışmayı görünür tutan şey
/// <c>ScenarioTriggerVocabularyTests</c>: küme büyürse kırmızı yanıyor.
/// </para>
/// </summary>
public static class ScenarioTriggers
{
    public const string Alert = "alert";
    public const string Manual = "manual";
    public const string Anomaly = "anomaly";
    public const string External = "external";

    /// <summary>Beşinci — F4'ün yan çıktısı olarak K20'ye girdi.</summary>
    public const string Schedule = "schedule";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        Alert, Manual, Anomaly, External, Schedule,
    };
}
