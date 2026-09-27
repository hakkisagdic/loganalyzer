using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bizigo.UnitTests;

/// <summary>
/// Sprint04: Metric/Trace muafiyeti kaldırıldı; eksik sağlayıcı kayıtları
/// kapsam dışı diye gizlenmez. Topoloji envanter alanları sınırını korur.
/// </summary>
public class EvidenceKindScopeTests
{
    [Fact]
    public void Muafiyet_sayisi_civili()
    {
        // Muafiyet eklemek İKİ bilinçli hareket: listeye ad eklemek ve bu
        // sayıyı artırmak. Tek hareketle büyüyen bir muafiyet listesi,
        // muafiyeti kararsız hâle getirir — emsali `ProducesContractTests`.
        Assert.Equal(0, EvidenceKinds.ExpectedExemptCount);
        Assert.Empty(EvidenceKinds.Exempt);
    }

    [Fact]
    public void Metric_trace_ve_diger_turler_muaf_degil()
    {
        Assert.False(EvidenceKinds.IsExempt(EvidenceKind.Metric));
        Assert.False(EvidenceKinds.IsExempt(EvidenceKind.Trace));

        // Topoloji **karşılanıyor** — sınırlı ama karşılanıyor. Muaf sayılsaydı
        // üçüncü bir hâl doğardı ve sağlayıcısı olan bir tür "bakmıyoruz" diye
        // raporlanırdı.
        Assert.False(EvidenceKinds.IsExempt(EvidenceKind.Topology));
        Assert.False(EvidenceKinds.IsExempt(EvidenceKind.Log));
        Assert.False(EvidenceKinds.IsExempt(EvidenceKind.Change));
    }

    [Fact]
    public async Task Kayitsiz_turler_not_registered_olarak_gorunur()
    {
        var collector = new EvidenceCollector([], NullLogger<EvidenceCollector>.Instance);

        var report = await collector.GatherAsync(
            TopologyWindow(), Bizigo.Contracts.AccessScope.System("test"), GatherBudget.Default, TestContext.Current.CancellationToken);

        foreach (var kind in Enum.GetValues<EvidenceKind>())
        {
            var slice = Assert.Single(report.Slices, s => s.Kind == kind);

            Assert.Equal(EvidenceStatus.NotRegistered, slice.Status);
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

        // Her iki sinyal artık gerçek sağlayıcılara sahip.
        Assert.Contains(EvidenceKind.Metric, kinds);
        Assert.Contains(EvidenceKind.Trace, kinds);

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
