using Bizigo.ControlPlane;

namespace Bizigo.Rca;

/// <summary>
/// Dört kaynağın <b>tek kapıya</b> giriş noktaları (T45, RCA §5).
///
/// <para>
/// Her fabrika aynı tipi üretiyor — <see cref="RcaTriggerRequest"/> — ve tek
/// tüketicisi <see cref="RcaAdmission"/>. Ayrı yollar olsaydı kota, debounce ve
/// döngü koruması dört kez yazılacaktı; dördüncüsü eksik kalırdı ve bunu ancak
/// bir döngü üretime çıktığında öğrenirdik.
/// </para>
///
/// <para>
/// <b>Bu sınıf karar vermiyor.</b> Yalnızca kaynağa özgü girdiyi ortak biçime
/// çeviriyor: hangi alanın "kimlik" sayıldığı kaynak başına farklı ve o çeviri
/// bir yerde yazılı olmalı. Kabul/ret kararının tamamı kapıda.
/// </para>
/// </summary>
public static class RcaTriggerSources
{
    /// <summary>
    /// Alarm ya da Sigma — giriş <c>alert_triggers</c> tablosuna düşen <b>satır</b>.
    ///
    /// <para>
    /// <c>AlertRaised</c> diye bir olay depoda <b>yok</b> ve tasarım onu
    /// varsayıyordu. Bağlantı noktası bir tablo satırı; olay veri yolu isteniyorsa
    /// o yazılacak iş. Bu imza o gerçeği taşıyor: parametre bir olay değil, bir
    /// varlık.
    /// </para>
    ///
    /// <para>
    /// Kimlik <c>rule_id</c>: debounce anahtarının §5'te yazılı hâli. Sigma
    /// kuralı da kullanıcının yazdığı alarm kuralı da aynı tabloya düştüğü için
    /// ikisi arasında burada da <b>yol farkı yok</b>.
    /// </para>
    /// </summary>
    public static RcaTriggerRequest FromAlert(AlertTriggerEntity trigger, RcaRunEntity? parent = null)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        return new RcaTriggerRequest
        {
            Source = RcaTriggerSource.Alert,
            Identity = trigger.RuleId.ToString(),
            OwnerGroup = RcaTriggerKey.Scope([trigger.OwnerGroup]),

            // Pencere tetiklenmenin kendi penceresi: RCA'nın bakacağı aralık,
            // alarmın baktığı aralıkla aynı olmalı. Farklı olsaydı rapor,
            // alarmı doğuran veriyi içermeyebilirdi.
            WindowFrom = trigger.WindowFrom,
            WindowTo = trigger.WindowTo,

            Parent = parent,
            RequestedBy = $"alert:{trigger.RuleId}",
        };
    }

    /// <summary>Kullanıcı, arayüzden: zaman aralığı + kapsam.</summary>
    public static RcaTriggerRequest FromUser(
        string subject,
        IEnumerable<string> ownerGroups,
        DateTimeOffset from,
        DateTimeOffset to,
        RcaRunEntity? parent = null) =>
        new()
        {
            Source = RcaTriggerSource.User,

            // Kimlik kullanıcının kendisi: debounce "aynı kişi aynı pencereyi
            // tekrar istedi mi" sorusuna bakıyor. Kimliği sabit bir dizge
            // yapsaydık iki farklı kullanıcının aynı kapsamdaki talebi
            // birbirini bastırırdı.
            Identity = subject,
            OwnerGroup = RcaTriggerKey.Scope(ownerGroups),
            WindowFrom = from,
            WindowTo = to,
            Parent = parent,
            RequestedBy = subject,
        };

    /// <summary>
    /// Dış API — <c>POST /v1/rca</c> + <c>Idempotency-Key</c>.
    ///
    /// <para>
    /// Anahtar taşıyan bir talep <b>farklı bir kaynak</b> sayılıyor, aynı uçtan
    /// gelse bile: idempotent bir istemcinin tekrar denemeleri ile bir kullanıcının
    /// düğmeye ikinci kez basması aynı şey değil. Birincisi aynı raporu
    /// beklerken ikincisi yeni bir koşum bekliyor.
    /// </para>
    /// </summary>
    public static RcaTriggerRequest FromApi(
        string subject,
        string idempotencyKey,
        IEnumerable<string> ownerGroups,
        DateTimeOffset from,
        DateTimeOffset to,
        RcaRunEntity? parent = null) =>
        new()
        {
            Source = RcaTriggerSource.Api,
            Identity = subject,
            OwnerGroup = RcaTriggerKey.Scope(ownerGroups),
            WindowFrom = from,
            WindowTo = to,
            IdempotencyKey = idempotencyKey,
            Parent = parent,
            RequestedBy = subject,
        };

    /// <summary>
    /// Takvim / cron.
    ///
    /// <para>
    /// Kotanın <b>en öngörülebilir</b> tüketicisi: diğer üçü olaya bağlı, bu
    /// takvime. Takvimli senaryolar günlük kotayı baştan tüketip olay tetikli bir
    /// RCA'yı kotasız bırakabilir — kararı T46'da, ama kaynağın burada ayrı bir
    /// değer olması o kararın verilebilmesi için gerekli.
    /// </para>
    /// </summary>
    public static RcaTriggerRequest FromSchedule(
        string scheduleId,
        IEnumerable<string> ownerGroups,
        DateTimeOffset from,
        DateTimeOffset to,
        RcaRunEntity? parent = null) =>
        new()
        {
            Source = RcaTriggerSource.Schedule,
            Identity = scheduleId,
            OwnerGroup = RcaTriggerKey.Scope(ownerGroups),
            WindowFrom = from,
            WindowTo = to,
            Parent = parent,
            RequestedBy = $"schedule:{scheduleId}",
        };
}
