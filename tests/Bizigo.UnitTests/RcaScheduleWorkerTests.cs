using Bizigo.Api.Rca;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Rca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// Takvim işçisi ve kuyruk boşaltıcısı (T46).
///
/// <para>
/// Saat sahte ve tur <b>doğrudan çağrılıyor</b>: arka plan görevini başlatıp
/// etkiyi duvar saatiyle yoklamak F1'in en pahalı dersiydi. Burada test hiçbir
/// şey beklemiyor ve üretimle aynı kodu koşuyor.
/// </para>
/// </summary>
public sealed class RcaScheduleWorkerTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>UTC gece yarısının 12 saat sonrası — sınır hizalaması görünür olsun diye.</summary>
    private static readonly DateTimeOffset Noon = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryControlPlaneFactory _factory = new();
    private readonly FakeTimeProvider _time = new(Noon);

    public void Dispose() => _factory.Dispose();

    private static RcaScheduleEntry Entry(string id = "gunluk-ozet") => new()
    {
        Id = id,
        OwnerGroups = ["network/core"],
        Every = TimeSpan.FromHours(1),
        Lookback = TimeSpan.FromMinutes(30),
        BaselineLookback = TimeSpan.FromHours(6),
    };

    private RcaAdmission Admission() =>
        new(_factory, new AlwaysAllowQuotaGate(), NullLogger<RcaAdmission>.Instance, _time);

    /// <summary>
    /// Kanıt fabrikası <b>kapsamlı</b> (scoped) ve işçi tek örnek; ikisini
    /// doğrudan bağlamak DI kapsam doğrulamasının reddettiği bir şey. Test de
    /// üretimle aynı yolu kullanıyor — sahte bir kapsam fabrikası yazmak,
    /// üretimde patlayan kurulumu testte yeşil göstermenin yolu olurdu.
    /// </summary>
    private IServiceScopeFactory Scopes(params IEvidenceProvider[] providers)
    {
        var services = new ServiceCollection();

        services.AddScoped(_ => new EvidenceBundleFactory(
            new EvidenceCollector(providers, NullLogger<EvidenceCollector>.Instance),
            new RecordingScopedQuery(),
            NullLogger<EvidenceBundleFactory>.Instance,
            _time));

        services.AddScoped(_ => new EvidenceBundleStore(_factory));

        return services
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true })
            .GetRequiredService<IServiceScopeFactory>();
    }

    private RcaScheduleWorker Worker(
        RcaScheduleOptions options,
        RcaQuotaOptions? quota = null,
        params IEvidenceProvider[] providers) =>
        new(options,
            Admission(),
            _factory,
            Scopes(providers),
            quota ?? new RcaQuotaOptions(),
            NullLogger<RcaScheduleWorker>.Instance,
            _time);

    private static RcaScheduleOptions Options(params RcaScheduleEntry[] entries) => new()
    {
        Enabled = true,
        Entries = [.. entries],
    };

    // ---- Saf fonksiyonlar -------------------------------------------------

    /// <summary>
    /// <b>Günlük tanım UTC gece yarısına düşüyor</b> — yani takvim günü kotasının
    /// sıfırlandığı ana. §6.1'in uyardığı senaryo temsil edilebilir olmalı ki
    /// rezervasyonun ne işe yaradığı sorulabilsin.
    /// </summary>
    [Fact]
    public void Gunluk_sinir_UTC_gece_yarisina_hizali()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
            RcaScheduleWorker.BoundaryFor(Noon, TimeSpan.FromDays(1)));
    }

    /// <summary>
    /// <b>Sürüklenme imkânsız.</b> "En son ne zaman koştu"ya göre ilerleyen bir
    /// zamanlayıcı her turda birkaç saniye kayar ve günler içinde tanımın saati
    /// kayardı. Sabit sınır, işçi geç uyansa bile aynı cevabı veriyor.
    /// </summary>
    [Fact]
    public void Gec_uyanan_isci_ayni_siniri_hesapliyor()
    {
        var every = TimeSpan.FromHours(1);
        var onTime = RcaScheduleWorker.BoundaryFor(Noon, every);
        var late = RcaScheduleWorker.BoundaryFor(Noon.AddMinutes(37).AddSeconds(11), every);

        Assert.Equal(onTime, late);
    }

    /// <summary>
    /// Taban penceresi olay penceresiyle <b>örtüşmüyor</b>: örtüşen taban
    /// "ilk-görülen" sinyalini tanım gereği boşaltır ve rapor sessizce hiçbir
    /// şey söylemez.
    /// </summary>
    [Fact]
    public void Pencere_gecerli_ve_taban_ortusmuyor()
    {
        var window = RcaScheduleWorker.WindowFor(Entry(), Noon);

        window.Validate();

        Assert.Equal(Noon, window.To);
        Assert.Equal(Noon.AddMinutes(-30), window.From);
        Assert.Equal(window.From, window.BaselineTo);
    }

    // ---- Tur davranışı ----------------------------------------------------

    [Fact]
    public async Task Kapali_takvim_hicbir_sey_yapmiyor()
    {
        var turn = await Worker(new RcaScheduleOptions { Entries = [Entry()] }).RunTurnAsync(Token);

        Assert.Equal(default, turn);
        Assert.Equal(0, await CountRunsAsync());
    }

    [Fact]
    public async Task Vadesi_gelen_tanim_kabul_ediliyor()
    {
        var turn = await Worker(Options(Entry())).RunTurnAsync(Token);

        Assert.Equal(1, turn.Admitted);

        await using var db = _factory.CreateDbContext();
        var run = await db.RcaRuns.SingleAsync(Token);

        Assert.Equal(RcaTriggerSource.Schedule, run.Source);
        Assert.Equal("gunluk-ozet", run.TriggerIdentity);
    }

    /// <summary>
    /// <b>Aynı sınırda ikinci kez ateşlenmiyor</b> ve bunun hafızası
    /// veritabanında: bellekte tutulan bir "son koşum" haritası her yeniden
    /// başlatmada tanımı tekrar ateşlerdi — yani kotayı yeniden başlatma sayısı
    /// kadar yerdi.
    /// </summary>
    [Fact]
    public async Task Ayni_sinirda_ikinci_kez_atesleniyor_degil()
    {
        var options = Options(Entry());

        await Worker(options).RunTurnAsync(Token);

        // Aynı saat diliminde ikinci tur — ve işçi yepyeni bir örnek.
        _time.SetUtcNow(Noon.AddMinutes(20));
        var second = await Worker(options).RunTurnAsync(Token);

        Assert.Equal(0, second.Admitted);
        Assert.Equal(1, await CountRunsAsync());
    }

    [Fact]
    public async Task Sinir_gecince_yeniden_atesleniyor()
    {
        var options = Options(Entry());

        await Worker(options).RunTurnAsync(Token);

        _time.SetUtcNow(Noon.AddHours(1));
        var second = await Worker(options).RunTurnAsync(Token);

        Assert.Equal(1, second.Admitted);
        Assert.Equal(2, await CountRunsAsync());
    }

    /// <summary>
    /// Kapsamsız tanım ateşlenmiyor: kimin kotasından düşeceği belirsiz bir
    /// koşum, kotayı ölçülemez hâle getirirdi.
    /// </summary>
    [Fact]
    public async Task Kapsamsiz_tanim_ateslenmiyor()
    {
        var entry = Entry();
        entry.OwnerGroups = [];

        var turn = await Worker(Options(entry)).RunTurnAsync(Token);

        Assert.Equal(0, turn.Admitted);
        Assert.Equal(0, await CountRunsAsync());
    }

    [Fact]
    public async Task Pasif_tanim_ateslenmiyor()
    {
        var entry = Entry();
        entry.Enabled = false;

        Assert.Equal(0, (await Worker(Options(entry)).RunTurnAsync(Token)).Admitted);
    }

    // ---- Kuyruk boşaltma --------------------------------------------------

    /// <summary>
    /// <b>Boşaltıcı `Empty`'yi gerçekten üretiyor.</b> "Bakıldı, bulunamadı" ile
    /// "hiç bakılmadı" ayrımı T46'nın taşıyıcı kuralı ve ancak bir koşturucu o
    /// ayrımı yazdığında anlam kazanıyor — sağlayıcısız bir koşum tam olarak
    /// birinci hâl.
    /// </summary>
    [Fact]
    public async Task Kanit_bulunamayan_kosum_Empty_kapaniyor()
    {
        var turn = await Worker(Options(Entry())).RunTurnAsync(Token);

        Assert.Equal(1, turn.Executed);

        await using var db = _factory.CreateDbContext();
        var run = await db.RcaRuns.SingleAsync(Token);

        Assert.Equal(RcaRunState.Empty, run.State);
        Assert.NotNull(run.FinishedAt);

        // Kuyrukta gerçekten beklendi, yani başlangıç damgası ölçülmüş bir şey.
        Assert.NotNull(run.StartedAt);
    }

    [Fact]
    public async Task Kanit_bulunan_kosum_Complete_kapaniyor()
    {
        var provider = new StubProvider("test", EvidenceKind.Log);

        var turn = await Worker(Options(Entry()), quota: null, providers: provider).RunTurnAsync(Token);

        Assert.Equal(1, turn.Executed);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(RcaRunState.Complete, (await db.RcaRuns.SingleAsync(Token)).State);
        Assert.NotNull((await db.RcaRuns.SingleAsync(Token)).EvidenceBundleId);
    }

    /// <summary>
    /// <b>Öngörülemeyen arıza <c>Failed</c>, <c>Cancelled</c> değil.</b> Ölçüt
    /// operatörün yapılandırmaya bakarak öngörebilmesi; sağlayıcının patlaması
    /// öngörülemez. İkisini tek değere indirmek her token tavanını bir arıza
    /// ihbarına çevirirdi.
    /// </summary>
    [Fact]
    public async Task Koşum_patlarsa_Failed_yaziliyor()
    {
        // Toplayıcı sağlayıcı hatasını yutuyor, o yüzden kırığı bir kat aşağıya
        // koyuyoruz: geçersiz pencere `Validate` içinde patlıyor.
        var entry = Entry();
        entry.Lookback = TimeSpan.Zero;

        var turn = await Worker(Options(entry)).RunTurnAsync(Token);

        Assert.Equal(0, turn.Executed);

        await using var db = _factory.CreateDbContext();
        var run = await db.RcaRuns.SingleAsync(Token);

        Assert.Equal(RcaRunState.Failed, run.State);
        Assert.NotEmpty(run.StateDetail);
    }

    /// <summary>
    /// <b>Slot dolu olduğunda kuyruk bekliyor, reddedilmiyor</b> (§9 ikinci
    /// bulgu). Bekleyen satır <c>Queued</c> kalıyor ve bir sonraki turda
    /// koşuyor; tek bir "şu an çalıştırılamıyor" cevabı kullanıcıyı kotasını
    /// sorgulamaya gönderirdi.
    /// </summary>
    [Fact]
    public async Task Slot_doluyken_kuyruk_bekliyor()
    {
        await SeedRunningAsync();

        var turn = await Worker(Options(Entry()), new RcaQuotaOptions { MaxConcurrentGlobal = 1 })
            .RunTurnAsync(Token);

        Assert.Equal(1, turn.Admitted);
        Assert.Equal(0, turn.Executed);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.RcaRuns.CountAsync(r => r.State == RcaRunState.Queued, Token));
    }

    /// <summary>
    /// <b>Kuyruk eskiden yeniye boşalıyor.</b> Bu turda ateşlenen tanımın
    /// bekleyen bir koşumun önüne geçmesi, kabul sırasını sessizce tersine
    /// çevirirdi.
    /// </summary>
    [Fact]
    public async Task Kuyruk_eskiden_yeniye_bosaliyor()
    {
        var older = await SeedQueuedAsync("eski", Noon.AddHours(-3));

        var turn = await Worker(Options(Entry()), new RcaQuotaOptions { MaxConcurrentGlobal = 1 })
            .RunTurnAsync(Token);

        Assert.Equal(1, turn.Executed);

        await using var db = _factory.CreateDbContext();
        Assert.True(RcaRunLifecycle.IsTerminal((await db.RcaRuns.SingleAsync(r => r.Id == older, Token)).State));
        Assert.Equal(1, await db.RcaRuns.CountAsync(r => r.State == RcaRunState.Queued, Token));
    }

    /// <summary>
    /// <b>Boşaltıcı kaynağa bakmıyor.</b> §5'in taşıyıcı ilkesi "dört kaynak tek
    /// yol"; kaynağa bakan bir boşaltıcı onu arka kapıdan bozardı.
    /// </summary>
    [Fact]
    public async Task Alarm_kaynakli_kuyruk_satiri_da_kosuyor()
    {
        await SeedQueuedAsync("alarm", Noon.AddMinutes(-5), RcaTriggerSource.Alert);

        var turn = await Worker(new RcaScheduleOptions { Enabled = true }).RunTurnAsync(Token);

        Assert.Equal(1, turn.Executed);
    }

    // ---- Yardımcılar ------------------------------------------------------

    private async Task<int> CountRunsAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.RcaRuns.CountAsync(Token);
    }

    private async Task<Guid> SeedQueuedAsync(
        string identity,
        DateTimeOffset requestedAt,
        RcaTriggerSource source = RcaTriggerSource.Schedule)
    {
        var run = new RcaRunEntity
        {
            OwnerGroup = "network/core",
            Source = source,
            TriggerIdentity = identity,
            RequestedAt = requestedAt,
            WindowFrom = requestedAt.AddMinutes(-30),
            WindowTo = requestedAt,
            State = RcaRunState.Queued,
            Accepted = true,
            CountsAgainstQuota = true,
        };

        await using var db = _factory.CreateDbContext();
        db.RcaRuns.Add(run);
        await db.SaveChangesAsync(Token);

        return run.Id;
    }

    private async Task SeedRunningAsync()
    {
        await using var db = _factory.CreateDbContext();

        db.RcaRuns.Add(new RcaRunEntity
        {
            OwnerGroup = "network/core",
            Source = RcaTriggerSource.User,
            TriggerIdentity = "analyst",
            RequestedAt = Noon.AddMinutes(-1),
            State = RcaRunState.Running,
            Accepted = true,
            CountsAgainstQuota = true,
        });

        await db.SaveChangesAsync(Token);
    }
}
