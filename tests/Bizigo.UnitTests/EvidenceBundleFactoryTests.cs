using Bizigo.Contracts;
using Bizigo.Evidence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// Toplama koşusunun <b>saklanabilir pakete</b> dönüşü (T36).
///
/// <para>
/// Buradaki asıl konu zaman dürüstlüğü: rapor "penceredeki şu kadar olayın
/// zamanı cihazdan gelmiyor" diyebilmek zorunda ve o sayının <b>nereden</b>
/// geldiği kritik. Yayılma sağlayıcısından türetilseydi, yayılma hiçbir şey
/// döndürmediğinde sessizce sıfır olurdu — yani pencere baştan sona güvenilmez
/// zamanlı olsa bile rapor "sorun yok" derdi.
/// </para>
/// </summary>
public sealed class EvidenceBundleFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 14, 0, 0, TimeSpan.Zero);

    private static RcaWindow Window() => new()
    {
        From = Now,
        To = Now.AddMinutes(45),
        BaselineFrom = Now.AddDays(-7),
        BaselineTo = Now.AddMinutes(-30),
        OwnerGroups = ["network/core"],
    };

    private static EvidenceBundleFactory Factory(RecordingScopedQuery query, params IEvidenceProvider[] providers) =>
        new(new EvidenceCollector(providers, NullLogger<EvidenceCollector>.Instance),
            query,
            NullLogger<EvidenceBundleFactory>.Instance,
            new FakeTimeProvider(Now));

    private static Task<EvidenceBundle> BuildAsync(EvidenceBundleFactory factory, AccessScope? scope = null) =>
        factory.BuildAsync(
            Window(),
            scope ?? AccessScope.ForGroups("analyst", ["network/core"]),
            GatherBudget.Default,
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Zaman güvenilirliği <b>pencerenin tamamı</b> üzerinden ölçülüyor,
    /// sağlayıcıdan türetilmiyor: hiç sağlayıcı yokken bile sayı doğru.
    /// </summary>
    [Fact]
    public async Task Zaman_guvenilirligi_pencereden_olculuyor()
    {
        var query = new RecordingScopedQuery
        {
            CountOverride = q => q.Filters.Count == 0 ? 1_204 : 142,
        };

        var bundle = await BuildAsync(Factory(query));

        Assert.True(bundle.Trust.Measured);
        Assert.Equal(1_204, bundle.Trust.TotalEvents);
        Assert.Equal(142, bundle.Trust.UnreliableTimeEvents);
    }

    /// <summary>
    /// Ölçüm <b>üçüncü bir sorgu yüzeyi yazmıyor</b>: <c>time_source</c> zaten
    /// olay sorgusunun filtrelenebilir alanı ve sayım kapsam kapısından
    /// geçiyor. Kendi SQL'ini yazan bir yoklama, kapsamı ikinci kez tanımlamak
    /// olurdu (K17).
    /// </summary>
    [Fact]
    public async Task Olcum_olay_sorgusundan_geciyor()
    {
        var query = new RecordingScopedQuery { CountOverride = _ => 0 };

        await BuildAsync(Factory(query));

        Assert.Equal(2, query.CountQueries.Count);

        var filtered = query.CountQueries.Single(q => q.Filters.Count > 0);
        var filter = filtered.Filters.Single();

        Assert.Equal("time_source", filter.Field);
        Assert.Equal(FilterOperator.NotEquals, filter.Operator);
        Assert.Equal([TimeSources.Parsed], filter.Values);

        // Pencere ve kapsam daraltması aynen taşınıyor — ölçüm başka bir
        // pencereyi ölçseydi rapor yanlış bir orana dayanırdı.
        Assert.All(query.CountQueries, q =>
        {
            Assert.Equal(Now, q.From);
            Assert.Equal(Now.AddMinutes(45), q.To);
            Assert.Equal(["network/core"], q.OwnerGroups);
        });
    }

    /// <summary>
    /// Ölçüm patlarsa paket <b>düşmüyor</b> ama sayı da uydurulmuyor:
    /// <see cref="WindowTrust.Unmeasured"/> — "bilinmiyor", "sıfır" değil.
    /// </summary>
    [Fact]
    public async Task Olcum_patlarsa_bilinmiyor_yaziliyor()
    {
        var bundle = await BuildAsync(Factory(new ThrowingCountQuery()));

        Assert.False(bundle.Trust.Measured);
        Assert.Contains("ölçülemedi", DeterministicReport.From(bundle).ToMarkdown(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Paket, üretildiği <b>kapsamı</b> taşıyor. Onsuz altı ay sonra okuyan
    /// kişi "bu rapor neyi görebiliyordu" sorusunu cevaplayamaz — ve aynı
    /// pencerede farklı kapsamla toplanmış iki paket farklı şeyler görür,
    /// ikisi de doğrudur.
    /// </summary>
    [Fact]
    public async Task Paket_uretildigi_kapsami_tasiyor()
    {
        var restricted = await BuildAsync(
            Factory(new RecordingScopedQuery()),
            AccessScope.ForGroups("analyst", ["network/edge", "network/core"]));

        Assert.False(restricted.Scope.IsSystem);
        Assert.Equal(["network/core", "network/edge"], restricted.Scope.OwnerGroups);

        var system = await BuildAsync(Factory(new RecordingScopedQuery()), AccessScope.System("rca"));
        Assert.True(system.Scope.IsSystem);

        // Kapsam hash'e giriyor: aynı pencerede farklı kapsamla toplanan iki
        // paket "aynı kanıt" sayılmamalı.
        Assert.NotEqual(restricted.ContentHash, system.ContentHash);
    }

    /// <summary>
    /// <b>Aynı girdiyle aynı paket</b> — kabul kriteri, uçtan uca.
    /// Kimlik ve toplama zamanı farklı olsa bile içerik hash'i aynı.
    /// </summary>
    [Fact]
    public async Task Iki_kosum_ayni_paketi_uretiyor()
    {
        var first = await BuildAsync(Factory(new RecordingScopedQuery { CountOverride = _ => 7 }));
        var second = await BuildAsync(Factory(new RecordingScopedQuery { CountOverride = _ => 7 }));

        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    /// <summary>
    /// Paket beş türü de taşıyor: F5'in üç türü <c>NotRegistered</c> olarak
    /// rapora kadar geliyor, sessizce atlanmıyor.
    /// </summary>
    [Fact]
    public async Task Paket_bes_turu_de_tasiyor()
    {
        var query = new RecordingScopedQuery();
        var bundle = await BuildAsync(Factory(
            query,
            new Bizigo.Evidence.Providers.LogWindowProvider(query),
            new Bizigo.Evidence.Providers.ChangeFeedProvider(query)));

        Assert.Equal(
            Enum.GetValues<EvidenceKind>().ToHashSet(),
            bundle.Slices.Select(s => s.Kind).ToHashSet());

        // F5'in üç türü: metrik, trace, topoloji.
        Assert.Equal(3, bundle.NotConsulted.Count(s => s.Status == EvidenceStatus.NotRegistered));
    }

    /// <summary>Sayım patlıyor — depolama erişilemez.</summary>
    private sealed class ThrowingCountQuery : RecordingScopedQuery
    {
        public override Task<long> CountEventsAsync(
            EventQuery query, AccessScope scope, CancellationToken cancellationToken = default) =>
            Task.FromException<long>(new InvalidOperationException("ClickHouse yok."));
    }
}
