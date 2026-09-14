using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>F5 · S1 — kanıt türlerinin kapsam kaderi.</b>
///
/// <para>
/// Bu sınıf tek bir ayrımı koruyor ve ayrım §8'in kuralı: <i>"bir gün
/// kapanacak"</i> ile <i>"hiç kapanmayacak"</i> aynı listede duramaz. Karardan
/// önce üç tür tek listedeydi ve ekran üçü için de <i>"(F5)"</i> yazıyordu —
/// yani bir söz. Karar ikisini kalıcı olarak kapsam dışına aldı; söz orada
/// kalsaydı, verilmiş bir karardan sonra da bekletmeye devam ederdi ve
/// yanlışlığı <b>hiçbir yerde kırmızı yanmazdı</b>.
/// </para>
/// </summary>
public class EvidenceKindScopeTests
{
    [Fact]
    public void Muafiyet_sayisi_civili()
    {
        // Muafiyet eklemek İKİ bilinçli hareket: listeye ad eklemek ve bu
        // sayıyı artırmak. Tek hareketle büyüyen bir muafiyet listesi,
        // muafiyeti kararsız hâle getirir — emsali `ProducesContractTests`.
        Assert.Equal(EvidenceKinds.ExpectedExemptCount, EvidenceKinds.Exempt.Count);
    }

    [Fact]
    public void Metric_ve_trace_muaf_topology_degil()
    {
        Assert.True(EvidenceKinds.IsExempt(EvidenceKind.Metric));
        Assert.True(EvidenceKinds.IsExempt(EvidenceKind.Trace));

        // Topoloji **karşılanıyor** — sınırlı ama karşılanıyor. Muaf sayılsaydı
        // üçüncü bir hâl doğardı ve sağlayıcısı olan bir tür "bakmıyoruz" diye
        // raporlanırdı.
        Assert.False(EvidenceKinds.IsExempt(EvidenceKind.Topology));
        Assert.False(EvidenceKinds.IsExempt(EvidenceKind.Log));
        Assert.False(EvidenceKinds.IsExempt(EvidenceKind.Change));
    }

    [Fact]
    public async Task Muaf_turler_out_of_scope_diyor_not_registered_degil()
    {
        var collector = new EvidenceCollector([], NullLogger<EvidenceCollector>.Instance);

        var report = await collector.GatherAsync(
            TopologyWindow(), Bizigo.Contracts.AccessScope.System("test"), GatherBudget.Default, TestContext.Current.CancellationToken);

        foreach (var kind in EvidenceKinds.Exempt)
        {
            var slice = Assert.Single(report.Slices, s => s.Kind == kind);

            Assert.Equal(EvidenceStatus.OutOfScope, slice.Status);
            Assert.Contains("bakmıyor", slice.Detail, StringComparison.Ordinal);
        }

        // Muaf olmayan ve sağlayıcısı olmayan tür hâlâ `NotRegistered`:
        // değerin kendisi ölmedi, çünkü enum'a yeni bir tür eklenmesi mümkün.
        var waiting = report.Slices.Where(s => s.Status == EvidenceStatus.NotRegistered).ToArray();
        Assert.All(waiting, s => Assert.False(EvidenceKinds.IsExempt(s.Kind)));
    }

    [Fact]
    public void Kayitli_saglayicilar_muaf_bir_ture_dokunmuyor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IScopedQuery>(new RecordingScopedQuery());
        services.AddBizigoEvidence();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        using var container = provider.CreateScope();

        var kinds = container.ServiceProvider
            .GetServices<IEvidenceProvider>()
            .Select(p => p.Kind)
            .ToHashSet();

        // Muaf bir tür için sağlayıcı kaydetmek, kararı koddan sessizce geri
        // almanın en kolay yolu olurdu: `UnregisteredKinds` onu listeden
        // düşürür ve ekran hiçbir şey söylemez.
        Assert.DoesNotContain(EvidenceKind.Metric, kinds);
        Assert.DoesNotContain(EvidenceKind.Trace, kinds);

        // Topolojinin sağlayıcısı VAR — S1'in tek satırlık karşılığı.
        Assert.Contains(EvidenceKind.Topology, kinds);
    }

    /// <summary>
    /// <b>İki liste birbirini yalanlayamaz.</b> Topoloji izin listesindeki her
    /// ad <see cref="SourceSummary"/>'de bir özelliğe karşılık gelmek zorunda;
    /// ayrışırlarsa sağlayıcı var olmayan bir alan üzerinden sessizce boş
    /// sonuç üretir. <see cref="CorrelationFields.Lift"/> ile depolama
    /// tarafındaki izin listesinin eşitlenmesiyle aynı gerekçe.
    /// </summary>
    [Fact]
    public void Topoloji_izin_listesi_envanter_alanlariyla_esit()
    {
        var properties = typeof(SourceSummary)
            .GetProperties()
            .Select(p => p.Name.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(
            CorrelationFields.Topology,
            field => Assert.Contains(field, properties));

        // Ve liste **boş kalmasın**: boş bir izin listesi sağlayıcıyı sessizce
        // hiçbir şey bulamaz hâle getirirdi ve `Empty` diye raporlanırdı.
        Assert.NotEmpty(CorrelationFields.Topology);
    }

    private static RcaWindow TopologyWindow() => new()
    {
        From = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero),
        To = new DateTimeOffset(2026, 9, 5, 12, 30, 0, TimeSpan.Zero),
        BaselineFrom = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
        BaselineTo = new DateTimeOffset(2026, 9, 5, 11, 0, 0, TimeSpan.Zero),
    };
}
