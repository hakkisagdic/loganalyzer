using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// Alarm kapatma ve inceleme zorunluluğu (T38).
///
/// <para>
/// Buradaki iddia bir arayüz kuralının <b>yapıya çevrilmiş</b> hâli:
/// "kullanıcı inceleme adımını atlayamıyor" ekranda yazılsaydı, ekranı atlayan
/// her yol zorunluluğu da atlardı. <c>CloseAsync</c>'in imzası incelemesiz bir
/// kapatmayı kabul etmiyor — testler bunun sonuçlarını sabitliyor.
/// </para>
/// </summary>
public sealed class AlertClosureTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 10, 0, 0, TimeSpan.Zero);

    private readonly InMemoryControlPlaneFactory _factory = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly RecordingBundleSource _bundles = new(Now);

    private AlertClosureService Service() =>
        new(_factory, _bundles, new EvidenceBundleStore(_factory), new GoldenReviewStore(_factory, _time), _time);

    private static AccessScope Scope(params string[] groups) =>
        AccessScope.ForGroups("analyst.core", groups.Length == 0 ? ["network-core"] : groups);

    private async Task<AlertTriggerEntity> SeedTriggerAsync(string ownerGroup = "network-core")
    {
        var trigger = new AlertTriggerEntity
        {
            RuleId = Guid.NewGuid(),
            FiredAt = Now.AddMinutes(-10),
            WindowFrom = Now.AddHours(-1),
            WindowTo = Now,
            Value = 120,
            Threshold = 80,
            SourceId = "fw-01",
            OwnerGroup = ownerGroup,
            Summary = "eşik aşıldı",
        };

        await using var db = _factory.CreateDbContext();
        db.AlertTriggers.Add(trigger);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return trigger;
    }

    /// <summary>
    /// <b>Paket yoksa kapatma üretimi tetikliyor.</b>
    ///
    /// <para>
    /// İnceleme bir pakete bağlanmak zorunda; kapatma anında paket olmayabilir.
    /// Üretim T37'nin elle tetiklemesiyle <b>aynı yolu</b> çağırıyor — ikinci
    /// bir üretim yolu yazılsaydı iki paket biçimi zamanla ayrışır ve ayrışma
    /// tam olarak F4'ün karşılaştırmasında ortaya çıkardı (§9).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kapatma_paket_yoksa_uretimi_tetikliyor()
    {
        var trigger = await SeedTriggerAsync();

        var closure = await Service().CloseAsync(
            trigger.Id,
            ReviewVerdict.Correct,
            ContradictingEvidenceVerdict.NotPresent,
            "gerçek pozitif",
            Scope(),
            TestContext.Current.CancellationToken);

        Assert.True(closure.BundleGenerated);
        Assert.Equal(1, _bundles.Calls);

        // Üretilen paket tetiklenmenin penceresini taşıyor ve tabanı ONUN
        // ÖNCESİNDE — örtüşen taban "ilk kez görüldü"yü tanım gereği boşaltırdı.
        var window = Assert.Single(_bundles.Windows);
        Assert.Equal(trigger.WindowFrom, window.From);
        Assert.Equal(trigger.WindowTo, window.To);
        Assert.Equal(trigger.WindowFrom, window.BaselineTo);
        Assert.True(window.BaselineFrom < window.From);

        // Paket saklanmış olmalı: F4 aynı kanıt üzerinde yeniden koşacak.
        await using var db = _factory.CreateDbContext();
        Assert.Single(db.EvidenceBundles);
    }

    /// <summary>
    /// Pencereye ait paket zaten varsa <b>yeniden üretilmiyor</b>.
    ///
    /// <para>
    /// Kullanıcı alarmı rapor ekranından bakıp kapatıyorsa paket dakikalar önce
    /// üretilmiş oluyor; her kapatmada yeniden toplamak kapatmayı gereksizce
    /// pahalı yapardı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Pencereye_ait_paket_varsa_yeniden_uretilmiyor()
    {
        var trigger = await SeedTriggerAsync();
        var existing = Guid.CreateVersion7(Now);

        await using (var db = _factory.CreateDbContext())
        {
            db.EvidenceBundles.Add(new EvidenceBundleEntity
            {
                Id = existing,
                GatheredAt = Now.AddMinutes(-5),
                SchemaVersion = 1,
                ContentHash = "hash",
                WindowFrom = trigger.WindowFrom,
                WindowTo = trigger.WindowTo,
                BaselineFrom = trigger.WindowFrom.AddDays(-7),
                BaselineTo = trigger.WindowFrom,
                Payload = "{}",
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var closure = await Service().CloseAsync(
            trigger.Id,
            ReviewVerdict.Correct,
            ContradictingEvidenceVerdict.NotPresent,
            string.Empty,
            Scope(),
            TestContext.Current.CancellationToken);

        Assert.False(closure.BundleGenerated);
        Assert.Equal(0, _bundles.Calls);
        Assert.Equal(existing, closure.Review.BundleId);
    }

    /// <summary>
    /// Kapatma tetiklenmeyi <b>kapalı</b> yapıyor ve incelemeye bağlıyor.
    ///
    /// <para>
    /// Bağ olmadan "atlayamıyor" iddiası yalnızca ekranda kalırdı: kapalı ama
    /// incelemesiz bir satır, kimsenin bakmadığı bir boşluk olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kapatilan_tetiklenme_incelemeye_bagli()
    {
        var trigger = await SeedTriggerAsync();

        var closure = await Service().CloseAsync(
            trigger.Id,
            ReviewVerdict.Incomplete,
            ContradictingEvidenceVerdict.Sound,
            "eksikti",
            Scope(),
            TestContext.Current.CancellationToken);

        await using var db = _factory.CreateDbContext();
        var stored = Assert.Single(db.AlertTriggers);

        Assert.Equal(AlertTriggerState.Closed, stored.State);
        Assert.Equal("analyst.core", stored.ClosedBySubject);
        Assert.Equal(Now, stored.ClosedAt);
        Assert.Equal(closure.Review.Id, stored.ReviewId);

        // İnceleme alarmın grubuna yazıldı — kapatanın kapsamından seçilmedi.
        Assert.Equal(trigger.OwnerGroup, closure.Review.OwnerGroup);
        Assert.Equal(trigger.Id, closure.Review.TriggerId);
    }

    /// <summary>
    /// İnceleme, alarmın grubuna yazılıyor — <b>kapatanın</b> grubuna değil.
    ///
    /// <para>
    /// Kapsamı geniş bir kişi başka bir ekibin alarmını kapattığında kayıt
    /// kendi grubuna yazılsaydı, gösterge sessizce yanlış ekibe mal edilirdi:
    /// alarmı çıkaran ekibin doğruluk oranı hiç değişmez, ilgisiz bir ekibinki
    /// bozulurdu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Inceleme_alarmin_grubuna_yaziliyor()
    {
        var trigger = await SeedTriggerAsync("network-edge");

        var closure = await Service().CloseAsync(
            trigger.Id,
            ReviewVerdict.Correct,
            ContradictingEvidenceVerdict.NotPresent,
            string.Empty,
            Scope("network-core", "network-edge"),
            TestContext.Current.CancellationToken);

        Assert.Equal("network-edge", closure.Review.OwnerGroup);
    }

    /// <summary>Kapsam dışı bir alarm kapatılamıyor.</summary>
    [Fact]
    public async Task Kapsam_disi_alarm_kapatilamiyor()
    {
        var trigger = await SeedTriggerAsync("network-edge");

        await Assert.ThrowsAsync<ReviewRejectedException>(() =>
            Service().CloseAsync(
                trigger.Id,
                ReviewVerdict.Correct,
                ContradictingEvidenceVerdict.NotPresent,
                string.Empty,
                Scope("network-core"),
                TestContext.Current.CancellationToken));

        await using var db = _factory.CreateDbContext();
        Assert.Empty(db.GoldenReviews);
        Assert.Equal(AlertTriggerState.Open, Assert.Single(db.AlertTriggers).State);
    }

    /// <summary>
    /// Kapatılmış alarm ikinci kez kapatılamıyor.
    ///
    /// <para>
    /// Kapatılabilseydi aynı olgu altın kümede iki kayıt olurdu ve doğruluk
    /// oranı, iki kez cevaplanan bir soruya iki kez ağırlık verirdi.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kapatilmis_alarm_ikinci_kez_kapatilamiyor()
    {
        var trigger = await SeedTriggerAsync();
        var service = Service();

        await service.CloseAsync(
            trigger.Id,
            ReviewVerdict.Correct,
            ContradictingEvidenceVerdict.NotPresent,
            string.Empty,
            Scope(),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ReviewRejectedException>(() =>
            service.CloseAsync(
                trigger.Id,
                ReviewVerdict.Wrong,
                ContradictingEvidenceVerdict.NotPresent,
                string.Empty,
                Scope(),
                TestContext.Current.CancellationToken));

        await using var db = _factory.CreateDbContext();
        Assert.Single(db.GoldenReviews);
    }

    public void Dispose() => _factory.Dispose();
}

/// <summary>
/// Üretimin <b>çağrıldığını</b> kaydeden paket kaynağı.
///
/// <para>
/// İkinci bir üretim yolu değil — gerçek fabrika <c>IScopedQuery</c> ve
/// ClickHouse istiyor, oysa buradaki testlerin ölçtüğü şey "üretim tetiklendi
/// mi" ve "hangi pencereyle". Paketin <i>içeriği</i> T36'nın testlerinde.
/// </para>
/// </summary>
internal sealed class RecordingBundleSource(DateTimeOffset now) : IEvidenceBundleSource
{
    public int Calls { get; private set; }

    public List<RcaWindow> Windows { get; } = [];

    public Task<EvidenceBundle> BuildAsync(
        RcaWindow window,
        AccessScope scope,
        GatherBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(scope);

        Calls++;
        Windows.Add(window);

        return Task.FromResult(new EvidenceBundle
        {
            Id = Guid.CreateVersion7(now),
            GatheredAt = now,
            Window = window,
            Scope = new BundleScope([.. scope.OwnerGroups], scope.IsUnrestricted),
            Slices = [],
            Trust = WindowTrust.Unmeasured,
        });
    }
}
