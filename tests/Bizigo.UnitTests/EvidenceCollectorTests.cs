using Bizigo.Contracts;
using Bizigo.Evidence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bizigo.UnitTests;

/// <summary>
/// T34 kabul kriterleri — kanıt sağlayıcı sözleşmesi (K21, K22).
///
/// <para>
/// Buradaki testlerin ortak konusu tek bir hata sınıfı: <b>raporun, bakmadığı
/// bir şeye bakmış gibi görünmesi.</b> "Sağlayıcı yok", "veri yok", "bakamadık"
/// ve "kırpıldı" birbirine karıştığı anda rapor iyimser yanılır ve bunu hiçbir
/// hata mesajı bozmaz.
/// </para>
/// </summary>
public sealed class EvidenceCollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 14, 0, 0, TimeSpan.Zero);

    private static RcaWindow Window() => new()
    {
        From = Now,
        To = Now.AddMinutes(45),
        BaselineFrom = Now.AddDays(-7),
        BaselineTo = Now.AddMinutes(-30),
    };

    private static EvidenceCollector Collector(params IEvidenceProvider[] providers) =>
        new(providers, NullLogger<EvidenceCollector>.Instance);

    private static Task<EvidenceReport> GatherAsync(EvidenceCollector collector) =>
        collector.GatherAsync(Window(), AccessScope.System("test"), GatherBudget.Default, TestContext.Current.CancellationToken);

    /// <summary>
    /// <b>Kabul kriteri: yeni bir sağlayıcı eklendiğinde motor değişmiyor.</b>
    ///
    /// <para>
    /// Toplayıcı bu testteki sağlayıcıları <b>tanımıyor</b>: türlerini,
    /// kimliklerini, kaç tane olduklarını bilmiyor. F5'te trace sağlayıcısı
    /// geldiğinde yapılacak tek şey onu DI'ye kaydetmek, ve bu test o günü
    /// bugünden koşuyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Yeni_saglayici_eklendiginde_toplayici_degismiyor()
    {
        var trace = new StubProvider("trace.spans", EvidenceKind.Trace);
        var report = await GatherAsync(Collector(
            new StubProvider("logs.x", EvidenceKind.Log),
            trace));

        Assert.Equal(1, trace.Calls);

        var slice = Assert.Single(report.Slices, s => s.ProviderId == "trace.spans");
        Assert.Equal(EvidenceStatus.Gathered, slice.Status);
        Assert.Equal(EvidenceKind.Trace, slice.Kind);

        // Ve o tür artık "kayıtlı değil" listesinde görünmüyor.
        Assert.DoesNotContain(EvidenceKind.Trace, report.NotConsulted.Select(s => s.Kind));
    }

    /// <summary>
    /// <b>Kabul kriteri: "sağlayıcı yok" ile "veri yok" ayrımı rapora kadar
    /// taşınıyor.</b>
    ///
    /// <para>
    /// Üç boşluk üç farklı cümle: bakılmadı (F5), bakıldı ve bir şey yok,
    /// bakılamadı. Aynı boş listeye düşürmek raporu sessizce yanıltır.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Saglayici_yok_ile_veri_yok_ayri_durumlar()
    {
        var report = await GatherAsync(Collector(
            new StubProvider("logs.x", EvidenceKind.Log, EvidenceStatus.Empty),
            new StubProvider("change.feed", EvidenceKind.Change, EvidenceStatus.NeverFed)));

        Assert.Equal(
            EvidenceStatus.Empty,
            Assert.Single(report.Slices, s => s.Kind == EvidenceKind.Log).Status);

        Assert.Equal(
            EvidenceStatus.NeverFed,
            Assert.Single(report.Slices, s => s.Kind == EvidenceKind.Change).Status);

        // Kayıtlı sağlayıcısı olmayan üç tür (F5) ayrı bir durumla görünüyor.
        Assert.Equal(
            [EvidenceKind.Metric, EvidenceKind.Trace, EvidenceKind.Topology],
            report.Slices
                .Where(s => s.Status == EvidenceStatus.NotRegistered)
                .Select(s => s.Kind)
                .Order());
    }

    /// <summary>
    /// Beş türün <b>hepsi</b> raporda. Sessizce atlanan bir tür, okuyanın neye
    /// bakılmadığını bilmemesi demek (RCA §3).
    /// </summary>
    [Fact]
    public async Task Bes_turun_hepsi_raporda()
    {
        var report = await GatherAsync(Collector(new StubProvider("logs.x", EvidenceKind.Log)));

        Assert.Equal(
            Enum.GetValues<EvidenceKind>().Order(),
            report.Slices.Select(s => s.Kind).Distinct().Order());
    }

    /// <summary>
    /// Kullanılamayan sağlayıcı raporda görünüyor — sessizce atlanmıyor. Rapor
    /// ekranındaki "kapalı — F5" satırlarının kaynağı.
    /// </summary>
    [Fact]
    public async Task Kullanilamayan_saglayici_raporda_gorunuyor()
    {
        var report = await GatherAsync(Collector(
            new StubProvider("metric.baseline", EvidenceKind.Metric, available: false)));

        var slice = Assert.Single(report.Slices, s => s.ProviderId == "metric.baseline");

        Assert.Equal(EvidenceStatus.Unavailable, slice.Status);
        Assert.NotEmpty(slice.Detail);
        Assert.False(slice.IsEvidence);
        Assert.True(report.IsPartial);
    }

    /// <summary>
    /// Tek sağlayıcının arızası paketi <b>düşürmüyor</b> — ama sessizce de
    /// geçmiyor. İkisi birden gerekli: düşerse RCA hiç çalışmaz, sessiz geçerse
    /// eksik kanıt tam kanıt sanılır.
    /// </summary>
    [Fact]
    public async Task Patlayan_saglayici_paketi_dusurmuyor_ama_gorunuyor()
    {
        var healthy = new StubProvider("logs.x", EvidenceKind.Log);
        var report = await GatherAsync(Collector(healthy, new ThrowingProvider("change.feed", EvidenceKind.Change)));

        Assert.Equal(1, healthy.Calls);
        Assert.Single(report.Items);

        var failed = Assert.Single(report.Slices, s => s.Status == EvidenceStatus.Failed);
        Assert.Contains("ClickHouse", failed.Detail, StringComparison.Ordinal);
        Assert.True(report.IsPartial);
    }

    /// <summary>
    /// Süre tavanı <b>toplayıcıda</b> uygulanıyor, sağlayıcının insafına
    /// bırakılmıyor: yeni bir sağlayıcının onu uygulamayı unutması, tek bir
    /// sorgunun bütün paketi bekletmesi demek olurdu.
    /// </summary>
    [Fact]
    public async Task Sure_tavani_saglayicinin_insafina_birakilmiyor()
    {
        var slow = new StubProvider("logs.slow", EvidenceKind.Log)
        {
            Before = ct => Task.Delay(TimeSpan.FromSeconds(30), ct),
        };

        var report = await Collector(slow).GatherAsync(
            Window(),
            AccessScope.System("test"),

            // Tavan bilerek çok kısa. Ölçülen şey süre değil, tavanın
            // uygulanıp uygulanmadığı — testin geçmesi duvar saatine bağlı
            // değil, iptal sinyaline bağlı.
            new GatherBudget(400, TimeSpan.FromMilliseconds(50)),
            TestContext.Current.CancellationToken);

        var slice = Assert.Single(report.Slices, s => s.ProviderId == "logs.slow");

        Assert.Equal(EvidenceStatus.Failed, slice.Status);
        Assert.True(slice.Truncated);
    }

    /// <summary>
    /// Çağıranın iptali bütçe aşımıyla <b>karıştırılmıyor</b>. İkisi de
    /// <c>OperationCanceledException</c> ile geliyor ama anlamları farklı:
    /// biri "kullanıcı vazgeçti", diğeri "kanıt eksik kaldı".
    /// </summary>
    [Fact]
    public async Task Caginin_iptali_butce_asimi_sayilmiyor()
    {
        using var cts = new CancellationTokenSource();

        var provider = new StubProvider("logs.x", EvidenceKind.Log)
        {
            Before = _ =>
            {
                cts.Cancel();
                return Task.CompletedTask;
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Collector(provider).GatherAsync(
                Window(), AccessScope.System("test"), GatherBudget.Default, cts.Token));
    }

    /// <summary>
    /// Kapsam dışı sayım toplanıyor ve rapora tek sayı olarak çıkıyor (RCA §3.2).
    /// </summary>
    [Fact]
    public async Task Kapsam_disi_sayim_toplaniyor()
    {
        var report = await GatherAsync(Collector(
            new CountingProvider("logs.x", EvidenceKind.Log, 300),
            new CountingProvider("change.feed", EvidenceKind.Change, 42)));

        Assert.Equal(342, report.OutOfScopeCount);
    }

    /// <summary>
    /// Örtüşen baseline <b>reddediliyor</b>.
    ///
    /// <para>
    /// Bu, sessiz bozulmaya en yakın parametre hatası: baseline olay
    /// penceresiyle örtüşürse pencerede beliren her imza tabanda da görünür,
    /// "ilk-görülen" tanım gereği <b>boş</b> döner ve sinyal ölmüş olduğunu
    /// hiçbir yerde söylemez. Hata vermek tek doğru davranış.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Ortusen_baseline_reddediliyor()
    {
        var window = Window() with { BaselineTo = Now.AddMinutes(10) };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Collector(new StubProvider("logs.x", EvidenceKind.Log)).GatherAsync(
                window, AccessScope.System("test"), GatherBudget.Default, TestContext.Current.CancellationToken));
    }

    private sealed class CountingProvider(string id, EvidenceKind kind, long outOfScope) : IEvidenceProvider
    {
        public string Id => id;

        public EvidenceKind Kind => kind;

        public bool IsAvailable => true;

        public Task<EvidenceSlice> GatherAsync(
            RcaWindow window, AccessScope scope, GatherBudget budget, CancellationToken cancellationToken) =>
            Task.FromResult(new EvidenceSlice
            {
                ProviderId = id,
                Kind = kind,
                Status = EvidenceStatus.Empty,
                Detail = "test",
                OutOfScopeCount = outOfScope,
            });
    }
}
