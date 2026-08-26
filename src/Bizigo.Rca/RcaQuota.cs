using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Rca;

/// <summary>Günlük kota penceresi — <b>politika</b>, ölçümle çözülmüyor.</summary>
public enum RcaQuotaWindow
{
    /// <summary>
    /// Takvim günü (UTC). Öngörülebilir ve kullanıcıya "gece yarısı sıfırlanır"
    /// denebiliyor — §4.2 kotanın <b>ne zaman sıfırlanacağını</b> göstermeyi
    /// şart koşuyor. Bedeli: gece yarısı sürüsü.
    /// </summary>
    CalendarDay = 0,

    /// <summary>
    /// Kayan 24 saat. Daha adil ama "ne zaman sıfırlanır" sorusunun cevabı
    /// koşuma göre değişiyor; §4.2'nin istediği cümle zorlaşıyor.
    /// </summary>
    Rolling24Hours = 1,
}

/// <summary>
/// Kuyruk kısıtlarının yapılandırması (T46, F4 kota kararı).
///
/// <para>
/// <b>Varsayılanlar sayı önermiyor.</b> §5'in kararı açık: kota sayıları F3
/// telemetrisinden seçilir, F4 tasarımında değil. Buradaki varsayılanlar
/// <i>seçilmiş</i> değil <b>yer tutuyor</b> ve hepsi kapalı ya da en muhafazakâr
/// uçta. Ölçülene kadar bir sayının yanlış olması, yokluğundan pahalı.
/// </para>
/// </summary>
public sealed class RcaQuotaOptions
{
    public const string SectionName = "Rca:Quota";

    /// <summary>
    /// Grup başına günlük RCA hakkı. <c>0</c> = <b>sınırsız</b>.
    ///
    /// <para>
    /// Varsayılan sınırsız, çünkü ölçülmemiş bir kota ölçülmüş gibi davranır ve
    /// "gerekçesi kayıtta olmayan sabit" envanterine yedinci satır olarak
    /// girerdi. Mekanizma hazır; sayı F3 telemetrisinden gelecek.
    /// </para>
    /// </summary>
    public int DailyPerGroup { get; set; }

    public RcaQuotaWindow Window { get; set; } = RcaQuotaWindow.CalendarDay;

    /// <summary>
    /// Aynı anda koşabilecek toplam RCA sayısı — <b>sunucu kapasitesi, fiziksel</b>.
    ///
    /// <para>
    /// Grup başına limitten <b>ayrı bir sayı</b> ve bu tartışmaya açık değil
    /// (§2.1): global limit sunucuyu korur, grup başına limit komşuyu. Tek sayıya
    /// indirgemek iki farklı sorunun cevabını birbirine bağlamak olur.
    /// </para>
    ///
    /// <para>
    /// Varsayılan <b>1</b>: §5 "ölçülene kadar tek slot, yavaş ama yanlış değil"
    /// diyor. ⚠️ Alarm motorunun <c>MaxConcurrentEvaluations = 4</c>'ü buraya
    /// <b>devralınmadı</b> — §9 dördüncü bulgu: RCA koşumu alarm
    /// değerlendirmesinden ağır, aynı sayı devralınmamalı.
    /// </para>
    /// </summary>
    public int MaxConcurrentGlobal { get; set; } = 1;

    /// <summary>
    /// Bir grubun aynı anda tutabileceği slot sayısı — <b>adalet, politik</b>.
    /// <c>0</c> = global limitin dışında ek bir sınır yok.
    /// </summary>
    public int MaxConcurrentPerGroup { get; set; }

    /// <summary>
    /// Olay tetikli kaynaklara <b>ayrılan</b> günlük kota yüzdesi. <c>0</c> =
    /// rezervasyon yok.
    ///
    /// <para>
    /// <b>§6.1'in kararı burada veriliyor: tek havuz + kaynak başına muhasebe,
    /// rezervasyon isteğe bağlı.</b> Takvimli senaryolar kotayı öngörülebilir
    /// biçimde ve <i>baştan</i> tüketebilir; o gün gerçek bir alarm geldiğinde
    /// grubun kotası çoktan bitmiş olur. Yani bir kaynağın öngörülebilirliği,
    /// başka bir kaynağın öngörülemezliğini eziyor.
    /// </para>
    ///
    /// <para>
    /// Neden <c>schedule</c>'a tavan değil, olaya <b>rezervasyon</b>: korunmak
    /// istenen şey "takvim az koşsun" değil, <i>olay tetikli iş aç kalmasın</i>.
    /// Tavan her yeni kaynak eklendiğinde yeniden ayarlanmak zorunda; rezervasyon
    /// korunan şeyi adıyla söylüyor ve kaynak sayısından bağımsız.
    /// </para>
    ///
    /// <para>
    /// Varsayılan <b>0</b> — çünkü §5 sayı önermiyor. Ama risk sessiz kalmıyor:
    /// kaynak başına tüketim <b>her zaman</b> sayılıyor
    /// (<see cref="RcaQuotaUsage.BySource"/>), yani operatör rezervasyonu
    /// gerektiğini veriden görebiliyor. Mekanizma hazır, sayı ölçümden gelecek.
    /// </para>
    /// </summary>
    public int EventReservePercent { get; set; }
}

/// <param name="BySource">
/// Kaynak başına tüketim. <b>Her zaman doldurulur</b>, rezervasyon kapalı olsa
/// bile: §6.1'in riskini görünür kılan tek şey bu sayaç.
/// </param>
public sealed record RcaQuotaUsage(
    string OwnerGroup,
    DateTimeOffset WindowStart,
    int Used,
    int Limit,
    IReadOnlyDictionary<RcaTriggerSource, int> BySource)
{
    /// <summary><c>Limit = 0</c> sınırsız demek; kalan sonsuz.</summary>
    public int Remaining => Limit <= 0 ? int.MaxValue : Math.Max(0, Limit - Used);

    public bool Exhausted => Limit > 0 && Used >= Limit;
}

/// <summary>
/// Günlük kota kapısı (T46) — T45'in bıraktığı <see cref="IRcaQuotaGate"/> dikişini dolduruyor.
///
/// <para>
/// <b>Kapı dört kaynağa da aynı davranıyor.</b> §4.2 alarm satırında
/// "reddetme — kaydet" istiyor ama bu ihtiyaç <b>okuma tarafında</b>
/// karşılanıyor: kayıt zaten oluşuyor (<c>Accepted=false</c> +
/// <c>Rejection</c>), eksik olan alarmın kendi tetikleyici anahtarıyla
/// <c>rca_runs</c>'a bakabilmesiydi. Kapıya kaynak-bağımlı bir dal eklemek
/// T45'in taşıyıcı ilkesini — <i>dört kaynak tek yol</i> — bir okuma ihtiyacı
/// için bozardı; o ilkenin gerekçesi §5'te yazılı: ayrı yollar olsaydı kota ve
/// döngü koruması beş kez yazılacaktı.
/// </para>
/// </summary>
public sealed class RcaQuotaGate(
    IDbContextFactory<ControlPlaneDbContext> factory,
    RcaQuotaOptions options,
    TimeProvider? timeProvider = null) : IRcaQuotaGate
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Pencerenin başlangıcı — <b>saf fonksiyon</b>.
    ///
    /// <para>Takvim günü UTC'ye hizalanıyor: yerel saate hizalamak, aynı grubun
    /// kotasının sunucunun bulunduğu yere göre farklı anda sıfırlanması
    /// demekti.</para>
    /// </summary>
    public static DateTimeOffset WindowStart(DateTimeOffset now, RcaQuotaWindow window) => window switch
    {
        RcaQuotaWindow.CalendarDay => new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero),
        RcaQuotaWindow.Rolling24Hours => now - TimeSpan.FromHours(24),
        _ => new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero),
    };

    /// <summary>
    /// Bu kaynak için etkin sınır — rezervasyon uygulanmış hâli.
    ///
    /// <para>
    /// Olay tetikli kaynaklar (<c>Alert</c>, <c>User</c>, <c>Api</c>) tam havuzu
    /// görüyor; <c>Schedule</c> havuzdan rezervasyon düşülmüş hâlini görüyor.
    /// Rezervasyon sıfırsa ikisi aynı — yani varsayılan davranış tek havuz.
    /// </para>
    /// </summary>
    public static int EffectiveLimit(int dailyLimit, int reservePercent, RcaTriggerSource source)
    {
        if (dailyLimit <= 0 || reservePercent <= 0 || source != RcaTriggerSource.Schedule)
        {
            return dailyLimit;
        }

        var reserved = (int)Math.Ceiling(dailyLimit * (reservePercent / 100.0));
        return Math.Max(0, dailyLimit - reserved);
    }

    public async ValueTask<RcaRejectionReason> CheckAsync(
        RcaTriggerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (options.DailyPerGroup <= 0)
        {
            return RcaRejectionReason.None;
        }

        var usage = await UsageAsync(request.OwnerGroup, cancellationToken).ConfigureAwait(false);
        var limit = EffectiveLimit(options.DailyPerGroup, options.EventReservePercent, request.Source);

        // Sınırsız kaynak (rezervasyon yok) ile rezervasyonlu kaynak aynı sayacı
        // okuyor: havuz TEK. Rezervasyon havuzu bölmüyor, yalnızca bir kaynağın
        // tamamını yiyememesini sağlıyor.
        return limit > 0 && usage.Used >= limit
            ? RcaRejectionReason.QuotaExceeded
            : RcaRejectionReason.None;
    }

    /// <summary>
    /// Grubun bu penceredeki tüketimi.
    ///
    /// <para>
    /// <b>Yalnızca <see cref="RcaRunEntity.CountsAgainstQuota"/> olan satırlar
    /// sayılıyor.</b> §9 birinci bulgu: girişte reddedilen düşülmez, süre ya da
    /// token tavanına takılan düşülür. Sayacı <c>Accepted</c> üzerinden kursaydık
    /// kesilen koşumlar bedava olurdu ve kota gerçek harcamadan kopardı.
    /// </para>
    /// </summary>
    public async Task<RcaQuotaUsage> UsageAsync(string ownerGroup, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerGroup);

        var start = WindowStart(_time.GetUtcNow(), options.Window);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rows = await db.RcaRuns
            .AsNoTracking()
            .Where(r => r.OwnerGroup == ownerGroup
                && r.CountsAgainstQuota
                && r.RequestedAt >= start)
            .GroupBy(r => r.Source)
            .Select(g => new { Source = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new RcaQuotaUsage(
            ownerGroup,
            start,
            rows.Sum(r => r.Count),
            options.DailyPerGroup,
            rows.ToDictionary(r => r.Source, r => r.Count));
    }
}
