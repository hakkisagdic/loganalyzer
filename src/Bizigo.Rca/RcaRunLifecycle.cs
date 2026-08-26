using Bizigo.ControlPlane;

namespace Bizigo.Rca;

/// <summary>
/// Bir koşumun neden bittiği — <b>yürütme tarafının</b> kapalı kümesi (T46).
///
/// <para>
/// <see cref="RcaRejectionReason"/>'dan ayrı, çünkü ikisi farklı anlara ait:
/// biri kapının cevabı ("kuyruğa girer mi"), bu ise koşumun hikâyesi
/// ("başına ne geldi"). Tek kümede toplamak, "hiç başlamadı" ile "yarıda
/// kesildi"yi aynı sorunun cevabı saymak olurdu.
/// </para>
/// </summary>
public enum RcaStopReason
{
    /// <summary>Kesilmedi; koşum kendi sonuna geldi.</summary>
    None = 0,

    /// <summary>Kanıt toplama süre tavanına takıldı.</summary>
    EvidenceDurationExceeded = 1,

    /// <summary>Akıl yürütme süre tavanına takıldı.</summary>
    ReasoningDurationExceeded = 2,

    /// <summary>Token bütçesi doldu.</summary>
    TokenBudgetExhausted = 3,

    /// <summary>Koşu başına toplam süre tavanı — ikisinin üstündeki üçüncü tavan.</summary>
    TotalDurationExceeded = 4,

    /// <summary>Operatör ya da sistem kapanışı iptal etti.</summary>
    OperatorCancelled = 5,

    /// <summary>Kanıt sağlayıcısı hata verdi ve tekrardan sonra hâlâ bozuk.</summary>
    ProviderFailure = 6,

    /// <summary>Model çağrısı hata verdi ve tekrardan sonra hâlâ bozuk.</summary>
    ModelFailure = 7,

    /// <summary>Beklenmeyen istisna. Öngörülemez, dolayısıyla <c>Failed</c>.</summary>
    Unexpected = 8,
}

/// <summary>
/// Koşum durumunun geçişleri — <b>saf fonksiyonlar</b> (T46).
///
/// <para>
/// Saf olmaları bilinçli: geçiş kararı bir veritabanı satırından ya da bir
/// saatten değil, <b>yalnızca girdilerden</b> çıkıyor. Testi ne bekleme ne
/// sahte bir depo istiyor — üç değer verilir, karar okunur. Bu depoda zamana
/// bağlı kararların testi bir kez pahalıya patladı.
/// </para>
/// </summary>
public static class RcaRunLifecycle
{
    /// <summary>
    /// Kesilen bir koşum <c>Cancelled</c> mı <c>Failed</c> mi.
    ///
    /// <para>
    /// <b>Ölçüt tek soru: operatör yapılandırmaya bakarak öngörebilir miydi?</b>
    /// Öngörebilirse sistem <i>bildiği bir sınırda kasten durdu</i> —
    /// <see cref="RcaRunState.Cancelled"/>. Öngöremezse bir şey <i>bozuldu</i> —
    /// <see cref="RcaRunState.Failed"/>.
    /// </para>
    ///
    /// <para>
    /// Ayrımın bedeli operatörün kararı: <c>Cancelled</c> "sınırı büyüt ya da
    /// kapsamı daralt" der, <c>Failed</c> "bir şey bozuk, bak" der. İkisini tek
    /// değere indirmek, her token tavanını bir arıza ihbarına çevirirdi.
    /// </para>
    /// </summary>
    public static RcaRunState Classify(RcaStopReason reason) => reason switch
    {
        // Hepsi yapılandırmada yazılı bir sayıya çarpıyor: operatör bakıp
        // "evet, 60 saniye koymuşum" diyebilir.
        RcaStopReason.EvidenceDurationExceeded => RcaRunState.Cancelled,
        RcaStopReason.ReasoningDurationExceeded => RcaRunState.Cancelled,
        RcaStopReason.TokenBudgetExhausted => RcaRunState.Cancelled,
        RcaStopReason.TotalDurationExceeded => RcaRunState.Cancelled,

        // İptal isteği de öngörülebilir: birisi bilerek durdurdu.
        RcaStopReason.OperatorCancelled => RcaRunState.Cancelled,

        // Bunların hiçbiri yapılandırmadan okunmuyor.
        RcaStopReason.ProviderFailure => RcaRunState.Failed,
        RcaStopReason.ModelFailure => RcaRunState.Failed,
        RcaStopReason.Unexpected => RcaRunState.Failed,

        // `None` bir kesinti değil; buraya düşmesi çağıranın hatası.
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "Kesinti sebebi olmadan sınıflandırma yapılamaz."),
    };

    /// <summary>
    /// Bu durum kotadan düşülüyor mu.
    ///
    /// <para>
    /// <b>Kota ekseni <see cref="Classify"/>'dan bağımsız</b> ve bu ayrı bir
    /// fonksiyon olmasının sebebi tam olarak bu: token bütçesi dolan koşum
    /// <c>Cancelled</c> <b>ve</b> düşülüyor. İkisi tek yerde karara bağlansaydı
    /// "iptal edildi, o hâlde bedava" çıkarımı doğardı.
    /// </para>
    ///
    /// <para>
    /// Düşülmeyen tek durum <see cref="RcaRunState.Rejected"/>: iş hiç
    /// başlamadı, maliyet ödenmedi. Diğer her durum — boş çıkan da, kesilen de,
    /// düşen de — kanıt topladı ya da modeli çağırdı.
    /// </para>
    /// </summary>
    public static bool CountsAgainstQuota(RcaRunState state) => state != RcaRunState.Rejected;

    /// <summary>
    /// Bu durumda koşum <b>bitmiş</b> sayılıyor mu — yani slotu bıraktı mı.
    ///
    /// <para>
    /// Eşzamanlılık muhasebesi bunu okuyor. <see cref="RcaRunState.Queued"/>
    /// slot tutmuyor (henüz almadı), <see cref="RcaRunState.Running"/> tutuyor,
    /// gerisi bıraktı.
    /// </para>
    /// </summary>
    public static bool IsTerminal(RcaRunState state) => state
        is RcaRunState.Rejected
        or RcaRunState.Complete
        or RcaRunState.Empty
        or RcaRunState.Truncated
        or RcaRunState.Cancelled
        or RcaRunState.Failed;

    /// <summary>
    /// Kullanıcıya gösterilecek tek cümle — <b>"neden RCA yok"</b> sorusunun cevabı.
    ///
    /// <para>
    /// F4 kota kararı §4.1'in taşıyıcı kuralı: kota yüzünden RCA üretilmemiş bir
    /// alarm, RCA'sı boş çıkmış alarmdan <b>ayırt edilebilmeli</b>. Ayrımı
    /// ekranın yorumuna bırakmak, üç ayrı ekranın üç farklı cümle uydurması
    /// demekti.
    /// </para>
    /// </summary>
    public static string Describe(RcaRunState state, RcaRejectionReason rejection) => state switch
    {
        RcaRunState.Rejected => rejection switch
        {
            RcaRejectionReason.QuotaExceeded => "Kota dolduğu için RCA hiç çalıştırılmadı.",
            RcaRejectionReason.Debounced => "Aynı pencerede zaten bir RCA koştu.",
            RcaRejectionReason.DepthExceeded => "Zincir derinliği sınırına ulaşıldı; RCA çalıştırılmadı.",
            RcaRejectionReason.AncestorRepeat => "Bu tetikleyici kendi zincirinde zaten var; döngü kırıldı.",
            _ => "RCA çalıştırılmadı.",
        },
        RcaRunState.Queued => "Sırada bekliyor — kota değil, slot.",
        RcaRunState.Running => "Çalışıyor.",
        RcaRunState.Complete => "Tamamlandı.",
        RcaRunState.Empty => "Çalıştı, ilişkili kanıt bulamadı.",
        RcaRunState.Truncated => "Kanıt toplandı, akıl yürütme kesildi; aynı paketle yeniden koşturulabilir.",
        RcaRunState.Cancelled => "Yapılandırmadaki bir sınırda durduruldu.",
        RcaRunState.Failed => "Beklenmeyen bir hata yüzünden tamamlanamadı.",
        _ => "Bilinmeyen durum.",
    };
}
