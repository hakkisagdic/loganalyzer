using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Rca;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Api.Rca;

/// <summary>Bir takvim koşumu tanımı — <b>yapılandırmadan</b>, veritabanından değil.</summary>
///
/// <remarks>
/// <para>
/// Tablo + CRUD ekranı yazmadım, çünkü bugün o ekranın bir tüketicisi yok (§8):
/// tanımı yönetecek kimse doğmadan tablo açmak, şeklini tahminle çivilemek olurdu.
/// Yapılandırma dosyası ilk tanımları taşımak için yeterli ve geri dönüşü var —
/// tablo açıldıktan sonra şekli değiştirmek göç işi.
/// </para>
/// </remarks>
public sealed class RcaScheduleEntry
{
    /// <summary>
    /// Tetikleyici kimliği. <c>rca_runs.trigger_identity</c> olarak yazılıyor,
    /// yani okuma ucunda <c>?trigger=</c> ile aranan şey bu.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Bu takvimin baktığı kapsam. <b>Boş bırakılamaz</b>: kapsamsız bir takvim
    /// sistemin tamamını tarardı ve kimin kotasından düşeceği belirsiz kalırdı.
    /// </summary>
    public IList<string> OwnerGroups { get; set; } = [];

    /// <summary>
    /// Tekrar aralığı. Sınırlar <b>UTC epoch'a hizalı</b>: 24 saatlik bir aralık
    /// UTC gece yarısında ateşleniyor — yani takvim günü kotasının sıfırlandığı
    /// anda. §6.1'in uyardığı senaryo tam bu ve temsil edilebilir olması gerekiyor.
    /// </summary>
    public TimeSpan Every { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Olay penceresinin uzunluğu — ateşleme anından geriye.</summary>
    public TimeSpan Lookback { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Taban penceresinin uzunluğu; olay penceresinin <b>hemen öncesinde</b>
    /// duruyor. Örtüşmesi <c>RcaWindow.Validate</c> tarafından reddediliyor:
    /// örtüşen taban "ilk-görülen" sinyalini tanım gereği boşaltır.
    /// </summary>
    public TimeSpan BaselineLookback { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Takvim tetikleyicisi ve kuyruk boşaltıcısı (T46).
///
/// <para>
/// <b>Varsayılan kapalı.</b> Yapılandırılmamış bir takvim, kimsenin istemediği
/// bir RCA'yı her gün koşturup grubun kotasını yerdi.
/// </para>
/// </summary>
public sealed class RcaScheduleOptions
{
    public const string SectionName = "Rca:Schedules";

    public bool Enabled { get; set; }

    /// <summary>
    /// Turlar arası bekleme. Takvim çözünürlüğünün üst sınırı: bir dakikalık tur
    /// aralığıyla bir dakikadan sık ateşlenen bir tanım anlamını yitirir.
    /// </summary>
    public TimeSpan TurnInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Bir turda en fazla kaç kuyruk satırı koşturulacak.</summary>
    public int MaxDrainPerTurn { get; set; } = 5;

    public IList<RcaScheduleEntry> Entries { get; set; } = [];
}

/// <summary>Bir <see cref="RcaScheduleWorker.RunTurnAsync"/> turunun sonucu.</summary>
/// <param name="Admitted">Kabul edilen takvim talebi sayısı.</param>
/// <param name="Rejected">
/// Kapıda reddedilen takvim talebi sayısı — çoğunlukla kota. <b>Ayrı sayılıyor:</b>
/// "hiç ateşlenmedi" ile "ateşlendi, kota reddetti" aynı sayıya düşseydi
/// operatör rezervasyonun gerekli olduğunu veriden göremezdi (§6.1).
/// </param>
/// <param name="Executed">Bu turda koşturulan kuyruk satırı sayısı.</param>
public readonly record struct RcaScheduleTurn(int Admitted, int Rejected, int Executed)
{
    public static RcaScheduleTurn Disabled { get; }
}

/// <summary>
/// Takvim kaynağını kuyruğa bağlayan arka plan işi (T46).
///
/// <para>
/// <b>İki iş, tek tur:</b> önce vadesi gelen tanımlar kabul kapısından geçiriliyor,
/// sonra kuyruk boşaltılıyor. Sıra bu, çünkü ters olsaydı bu turda vadesi gelen
/// bir tanım bir tur — varsayılanda bir dakika — bekleyecekti; kuyruk zaten
/// <c>RequestedAt</c>'e göre eskiden yeniye boşaldığı için sıra adaleti bozulmuyor.
/// </para>
///
/// <para>
/// <b><see cref="RunTurnAsync"/> public ve testler onu çağırıyor.</b> F1'in en
/// pahalı dersi: arka plan görevini başlatıp etkiyi duvar saatiyle yoklayan test,
/// sağlıklı kodu yüklü makinede düşürüyor.
/// </para>
///
/// <para>
/// <b>Bilinen boşluk — eşzamanlı uç slot muhasebesine girmiyor.</b>
/// <c>POST /v1/rca</c> paketi isteğin içinde kuruyor; slot beklemesi orada bir
/// kullanıcı tıklamasını süresiz askıya almak demekti. Yani
/// <see cref="RcaQuotaOptions.MaxConcurrentGlobal"/> bugün <b>bu işçinin</b>
/// tavanı. Kota muhasebesi ikisini de kapsıyor — eksik olan slot, hak değil.
/// </para>
///
/// <para>
/// <b>İkinci boşluk — tek örnek varsayımı.</b> Kuyruktan devralma "oku, durumu
/// değiştir, yaz" deseninde; iki API örneği aynı anda koşarsa ikisi de aynı satırı
/// devralabilir. Bugün dağıtım tek örnek. Çözümü koşullu güncelleme, ama onu
/// <b>ölçmeden</b> yazmak, sınanmamış bir kilit eklemek olurdu.
/// </para>
/// </summary>
public sealed class RcaScheduleWorker(
    RcaScheduleOptions options,
    RcaAdmission admission,
    IDbContextFactory<ControlPlaneDbContext> factory,
    IServiceScopeFactory scopes,
    RcaQuotaOptions quota,
    ILogger<RcaScheduleWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Bu tanımın <paramref name="now"/> anında ait olduğu sınır — <b>saf fonksiyon</b>.
    ///
    /// <para>
    /// Hizalama UTC epoch'a: "en son ne zaman koştu" değerine göre ilerleyen bir
    /// zamanlayıcı her turda birkaç saniye kayar ve günler içinde tanımın saati
    /// sürüklenirdi. Sabit bir sınıra hizalamak sürüklenmeyi imkânsız kılıyor —
    /// işçi geç uyansa bile aynı sınırı hesaplıyor.
    /// </para>
    /// </summary>
    public static DateTimeOffset BoundaryFor(DateTimeOffset now, TimeSpan every)
    {
        var ticks = Math.Max(every.Ticks, TimeSpan.TicksPerMinute);
        var epoch = now.UtcTicks / ticks * ticks;

        return new DateTimeOffset(epoch, TimeSpan.Zero);
    }

    /// <summary>
    /// Tanımın penceresi — <b>saf fonksiyon</b>. Taban, olay penceresinin hemen
    /// öncesinde ve onunla <b>örtüşmüyor</b>.
    /// </summary>
    public static RcaWindow WindowFor(RcaScheduleEntry entry, DateTimeOffset boundary)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var from = boundary - entry.Lookback;

        return new RcaWindow
        {
            From = from,
            To = boundary,
            BaselineFrom = from - entry.BaselineLookback,
            BaselineTo = from,
            OwnerGroups = [.. entry.OwnerGroups],
        };
    }

    /// <summary>Döngünün <b>tek turu</b>: vadesi geleni kabul et, sonra kuyruğu boşalt.</summary>
    public async Task<RcaScheduleTurn> RunTurnAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return RcaScheduleTurn.Disabled;
        }

        var (admitted, rejected) = await FireDueAsync(cancellationToken).ConfigureAwait(false);
        var executed = await DrainAsync(cancellationToken).ConfigureAwait(false);

        return new RcaScheduleTurn(admitted, rejected, executed);
    }

    /// <summary>Vadesi gelmiş tanımları kabul kapısından geçirir.</summary>
    private async Task<(int Admitted, int Rejected)> FireDueAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var live = options.Entries.Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Id)).ToArray();

        if (live.Length == 0)
        {
            return (0, 0);
        }

        // Tek sorgu, tanım başına değil: yirmi tanım yirmi gidiş-dönüş demekti.
        // Alarm motorundaki tur başına paylaşılan sorgu kalıbının aynısı.
        var identities = live.Select(e => e.Id).ToArray();
        var oldest = now - live.Max(e => e.Every) - TimeSpan.FromDays(1);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var lastSeen = await db.RcaRuns
            .AsNoTracking()
            .Where(r => r.Source == RcaTriggerSource.Schedule
                && identities.Contains(r.TriggerIdentity)
                && r.RequestedAt >= oldest)
            .GroupBy(r => r.TriggerIdentity)
            .Select(g => new { Identity = g.Key, Last = g.Max(r => r.RequestedAt) })
            .ToDictionaryAsync(x => x.Identity, x => x.Last, cancellationToken)
            .ConfigureAwait(false);

        var admitted = 0;
        var rejected = 0;

        foreach (var entry in live)
        {
            var boundary = BoundaryFor(now, entry.Every);

            // "Bu sınırda zaten bir satır var mı" — işçi yeniden başlasa da aynı
            // cevabı veriyor. Bellekte tutulan bir "son koşum" haritası her
            // yeniden başlatmada tanımı tekrar ateşlerdi.
            if (lastSeen.TryGetValue(entry.Id, out var last) && last >= boundary)
            {
                continue;
            }

            if (entry.OwnerGroups.Count == 0)
            {
                logger.LogWarning(
                    "Takvim tanımı {Schedule} kapsamsız; ateşlenmedi. Kapsamsız takvim kimin kotasından düşeceğini söylemiyor.",
                    entry.Id);
                continue;
            }

            var request = RcaTriggerSources.FromSchedule(
                entry.Id, entry.OwnerGroups, boundary - entry.Lookback, boundary);

            var result = await admission.AdmitAsync(request, cancellationToken).ConfigureAwait(false);

            if (result.Accepted)
            {
                admitted++;
                continue;
            }

            rejected++;

            // Ret sessiz değil ve kaynağı adıyla anılıyor: §6.1'in riski
            // ("takvim kotayı baştan yedi") ancak bu satır loga düştüğünde
            // görünür oluyor.
            logger.LogInformation(
                "Takvim tetikleyicisi {Schedule} reddedildi: {Reason}. Rezervasyon yüzdesi {Reserve}.",
                entry.Id,
                result.Rejection,
                quota.EventReservePercent);
        }

        return (admitted, rejected);
    }

    /// <summary>
    /// Kuyruğu eskiden yeniye, boş slot kadar boşaltır.
    ///
    /// <para>
    /// Kaynak ayrımı <b>yok</b>: kuyrukta bekleyen bir alarm koşumu da buradan
    /// koşuyor. §5'in taşıyıcı ilkesi "dört kaynak tek yol" ve bir boşaltıcının
    /// kaynağa bakması o ilkeyi arka kapıdan bozardı.
    /// </para>
    /// </summary>
    private async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        var slots = Math.Max(quota.MaxConcurrentGlobal, 1) - await admission.RunningCountAsync(cancellationToken)
            .ConfigureAwait(false);

        if (slots <= 0)
        {
            return 0;
        }

        var take = Math.Min(slots, Math.Max(options.MaxDrainPerTurn, 1));

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var queued = await db.RcaRuns
            .AsNoTracking()
            .Where(r => r.State == RcaRunState.Queued)
            .OrderBy(r => r.RequestedAt)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var executed = 0;

        foreach (var run in queued)
        {
            if (await ExecuteAsync(run, cancellationToken).ConfigureAwait(false))
            {
                executed++;
            }
        }

        return executed;
    }

    private async Task<bool> ExecuteAsync(RcaRunEntity run, CancellationToken cancellationToken)
    {
        // `NotEngaged`, ve bu bir yer tutucu DEĞİL: bu işçi kanıt topluyor ve
        // akıl yürütmeyi hiç çağırmıyor, dolayısıyla bu koşum hakkında model
        // sınırı konusunda söylenecek bir şey yok. `Verified` yazmak, modele hiç
        // konuşmamış bir koşum hakkında güvence beyan etmek olurdu (T54).
        //
        // Model yolu bağlandığı gün derleyici bu satırı okumaya zorluyor:
        // `TryStartAsync`'in damga parametresi zorunlu ve varsayılanı yok, yani
        // `RcaModelBoundaryStamp.From(endpoint)`'e geçmek unutulamıyor.
        if (!await admission.TryStartAsync(run.Id, RcaModelBoundaryStamp.NotEngaged(), cancellationToken)
            .ConfigureAwait(false))
        {
            // Başkası devraldı ya da durum değişti. Sessiz geçmek doğru: bu bir
            // hata değil, yarışın kaybedilmesi.
            return false;
        }

        try
        {
            var window = new RcaWindow
            {
                From = run.WindowFrom,
                To = run.WindowTo,

                // Taban penceresi koşum satırında saklanmıyor; olay penceresinin
                // uzunluğu kadarı hemen öncesinden alınıyor. Kolon eklemek,
                // türetilebilir bir değeri dondurmak olurdu — `rca_runs`'a
                // pencere başlangıcı koymamakla aynı gerekçe.
                BaselineFrom = run.WindowFrom - (run.WindowTo - run.WindowFrom),
                BaselineTo = run.WindowFrom,
                OwnerGroups = [run.OwnerGroup],
            };

            window.Validate();

            // Kapsam takvimin kendi grubuyla sınırlı: `AccessScope.System`
            // kullanmak, bir takvim tanımının tüm kiracıları taraması demekti
            // (K17). Koşum satırındaki `owner_group` zaten kapsamın kendisi.
            var scope = AccessScope.ForGroups($"schedule:{run.TriggerIdentity}", [run.OwnerGroup]);

            // Koşum başına kendi DI kapsamı. Kanıt fabrikası `Scoped` — bir
            // istek boyunca yaşayacak şekilde kurulmuş — ve onu tek bir uzun
            // ömürlü işçiye bağlamak, sağlayıcıların bağlantılarını işçinin
            // ömrü boyunca açık tutmak olurdu. Kapsam doğrulaması bunu zaten
            // reddediyor; buradaki kapsam onu tatmin etmek için değil, koşumlar
            // arası durum sızmasını engellemek için.
            await using var services = scopes.CreateAsyncScope();

            var bundles = services.ServiceProvider.GetRequiredService<EvidenceBundleFactory>();
            var store = services.ServiceProvider.GetRequiredService<EvidenceBundleStore>();

            var bundle = await bundles.BuildAsync(window, scope, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await store.SaveAsync(bundle, cancellationToken).ConfigureAwait(false);

            // <b>`Empty` burada gerçekten üretiliyor.</b> "Bakıldı, bulunamadı"
            // ile "hiç bakılmadı" ayrımı T46'nın taşıyıcı kuralı ve ancak bir
            // koşturucu bu ayrımı yazdığında anlam kazanıyor.
            //
            // `IsPartial` → `Truncated` EŞLEMESİ YAPILMADI: `Truncated` "kanıt
            // toplandı, akıl yürütme kesildi" diyor; `IsPartial` ise bir
            // sağlayıcının bütçesine takılması. İkisini birleştirmek, kapalı
            // kümenin bir değerini belgelediğinden başka bir şeye kullanmak olurdu.
            var state = bundle.Items.Any() ? RcaRunState.Complete : RcaRunState.Empty;

            await admission.AttachBundleAsync(run.Id, bundle.Id, state, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await admission.StopAsync(run.Id, RcaStopReason.OperatorCancelled, "kapanış", CancellationToken.None)
                .ConfigureAwait(false);

            throw;
        }
#pragma warning disable CA1031 // Tek koşumun hatası işçiyi öldürmemeli.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "RCA koşumu {RunId} tamamlanamadı.", run.Id);

            // `Failed`, `Cancelled` değil: buraya düşen hiçbir şey
            // yapılandırmadan öngörülemezdi. Ayrımın bedeli operatörün kararı —
            // biri "sınırı büyüt" der, diğeri "bir şey bozuk, bak".
            await admission.StopAsync(run.Id, RcaStopReason.Unexpected, ex.Message, CancellationToken.None)
                .ConfigureAwait(false);

            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("RCA takvimi kapalı (Rca:Schedules:Enabled=false).");
            return;
        }

        logger.LogInformation(
            "RCA takvimi başladı: {Count} tanım, tur {Turn}, eşzamanlılık {Slots}.",
            options.Entries.Count,
            options.TurnInterval,
            quota.MaxConcurrentGlobal);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunTurnAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // İşçiyi öldürmek, takvimi sessizce kapatmak olurdu.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger.LogError(ex, "RCA takvim turunda beklenmedik hata; döngü sürüyor.");
            }

            try
            {
                await Task.Delay(options.TurnInterval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
