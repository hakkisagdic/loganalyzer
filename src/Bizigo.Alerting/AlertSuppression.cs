using Bizigo.ControlPlane;

namespace Bizigo.Alerting;

/// <summary>Bir tetiklenmenin neden bastırıldığı. Boş sebep "bastırılmadı" demek.</summary>
public enum SuppressionReason
{
    None = 0,

    /// <summary>Açık bir bakım penceresi var.</summary>
    MaintenanceWindow = 1,

    /// <summary>Kural son tetiklenmenin üstünden tekrar aralığı geçmeden yeniden tetiklendi.</summary>
    RepeatInterval = 2,
}

/// <summary>
/// Bastırma kararları — <b>saf fonksiyonlar</b> (T21: susturma ve tekrar aralığı).
///
/// <para>
/// <b>Neden saf ve neden ayrı bir sınıf:</b> ikisi de zamana bağlı kararlar ve
/// zamana bağlı kararlar bu depoda bir kez pahalıya patladı. Fonksiyon "şimdi"yi
/// parametre olarak alıyor, hiçbir saate dokunmuyor ve dolayısıyla testi
/// beklemeye, yoklamaya ya da duvar saati bütçesine ihtiyaç duymuyor —
/// üç değer verilir, karar okunur.
/// </para>
/// </summary>
public static class AlertSuppression
{
    /// <summary>
    /// Pencere <b>şu an açık mı</b> — kuraldan bağımsız, yalnızca aralık.
    ///
    /// <para>
    /// Aralık <b>kapalı aralık başı, açık aralık sonu</b>: <c>[StartsAt, EndsAt)</c>.
    /// Kabul kriteri "pencere bitince tetiklenme var" diyor; sonu kapalı almak,
    /// pencerenin bittiği saniyede hâlâ bastırıyor olmak demekti.
    /// </para>
    ///
    /// <para>
    /// <b>Neden ayrı bir metot.</b> Aralık kuralı iki farklı sorunun ortak
    /// yarısı: <see cref="IsInMaintenanceWindow"/> <i>"bu kural bastırılıyor
    /// mu"</i> diye soruyor, <c>alerts.maintenance</c> aracı ise <i>"bu pencere
    /// şu an yürürlükte mi"</i>. İkincisi bir kural taşımıyor, dolayısıyla
    /// birinciyi çağıramıyor ve <b>aralığı kendisi yazması</b> gerekiyordu — bu
    /// deponun §9'da yasakladığı ikinci kopya, ve kopyanın ayrıştığı gün
    /// gösterge ile bastırma zıt cevap verirdi (biri "pencere kapandı" derken
    /// diğeri hâlâ bastırıyor olurdu). Karar tek yerde, iki çağıranı var.
    /// </para>
    /// </summary>
    public static bool IsOpen(MaintenanceWindowEntity window, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);

        return now >= window.StartsAt && now < window.EndsAt;
    }

    /// <summary>
    /// Pencere bu kuralı <b>şu an</b> bastırıyor mu. Aralık kararı
    /// <see cref="IsOpen"/>'da; burada kalan tek soru <i>hangi kurallar</i>.
    /// </summary>
    public static bool IsInMaintenanceWindow(
        MaintenanceWindowEntity window,
        AlertRuleEntity rule,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(rule);

        if (!IsOpen(window, now))
        {
            return false;
        }

        // Kurala bağlı pencere yalnızca o kuralı susturuyor.
        if (window.RuleId is { } ruleId)
        {
            return ruleId == rule.Id;
        }

        // Kurala bağlı olmayan pencere, grubu kuralın kapsamıyla kesişen her
        // kuralı susturuyor: bakım bir cihaza değil bir ekibin altyapısına
        // yapılıyor ve o sırada o ekibin kuralları gürültü üretir.
        return rule.OwnerGroups
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(window.OwnerGroup, StringComparer.Ordinal);
    }

    /// <summary>
    /// Kural son tetiklenmesinden bu yana tekrar aralığını doldurdu mu.
    ///
    /// <para>
    /// Gürültü kontrolünün <b>ilk</b> kademesi ve kanal tarafındaki gruplamadan
    /// önce geliyor: en ucuz bastırma, hiç mesaj üretmeyen bastırmadır.
    /// </para>
    /// </summary>
    public static bool IsWithinRepeatInterval(AlertRuleEntity rule, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.LastFiredAt is not { } last || rule.RepeatIntervalSeconds <= 0)
        {
            return false;
        }

        return now - last < TimeSpan.FromSeconds(rule.RepeatIntervalSeconds);
    }

    /// <summary>Açık pencereleri ve tekrar aralığını birlikte değerlendirir.</summary>
    public static SuppressionReason Evaluate(
        AlertRuleEntity rule,
        IEnumerable<MaintenanceWindowEntity> windows,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(windows);

        if (windows.Any(w => IsInMaintenanceWindow(w, rule, now)))
        {
            return SuppressionReason.MaintenanceWindow;
        }

        return IsWithinRepeatInterval(rule, now)
            ? SuppressionReason.RepeatInterval
            : SuppressionReason.None;
    }
}
