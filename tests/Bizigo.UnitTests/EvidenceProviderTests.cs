using System.Net;
using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// T34'ün iki uygulanan sağlayıcısı: <c>logs.window</c> ve <c>change.feed</c>.
///
/// <para>
/// Sağlayıcılar <c>IScopedQuery</c>'den geçiyor ve testler o kapıyı sahteleyerek
/// koşuyor — sınanan şey ClickHouse değil, sağlayıcının <b>ne sorduğu</b> ve
/// dönen boşluğu <b>nasıl adlandırdığı</b>.
/// </para>
/// </summary>
public sealed class EvidenceProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 14, 0, 0, TimeSpan.Zero);
    private static readonly AccessScope Scope = AccessScope.System("test");

    private static RcaWindow Window() => new()
    {
        From = Now,
        To = Now.AddMinutes(45),
        BaselineFrom = Now.AddDays(-7),
        BaselineTo = Now.AddMinutes(-30),
        OwnerGroups = ["network/core"],
    };

    private static Task<EvidenceSlice> GatherAsync(IEvidenceProvider provider, GatherBudget? budget = null) =>
        provider.GatherAsync(
            Window(), Scope, budget ?? GatherBudget.Default, TestContext.Current.CancellationToken);

    // ---- change.feed ------------------------------------------------------

    /// <summary>
    /// <b>Bu ticket'ın en önemli ayrımı.</b> Akışta kayıt var ama pencerede yok:
    /// "bu pencerede değişiklik olmadı" kurulabilir bir cümle, ve <b>kanıt</b>.
    /// </summary>
    [Fact]
    public async Task Akis_dolu_ama_pencere_bos_ise_Empty()
    {
        var query = new RecordingScopedQuery();
        query.EverChanges.Add(Change(Now.AddDays(-3)));

        var slice = await GatherAsync(new ChangeFeedProvider(query, Clock()));

        Assert.Equal(EvidenceStatus.Empty, slice.Status);
        Assert.True(slice.IsEvidence);
    }

    /// <summary>
    /// Akışta <b>hiç</b> kayıt yok: bu kanıt değil, ölçümün yokluğu. RCA
    /// artifact'ının 4. riski — besleme bağlanmamışsa "değişiklik yok" diyen bir
    /// sağlayıcı, ölçmediği bir şeyi ölçmüş gibi gösterir.
    /// </summary>
    [Fact]
    public async Task Akis_hic_beslenmemisse_NeverFed()
    {
        var slice = await GatherAsync(new ChangeFeedProvider(new RecordingScopedQuery(), Clock()));

        Assert.Equal(EvidenceStatus.NeverFed, slice.Status);
        Assert.False(slice.IsEvidence);
        Assert.Contains("DEĞİL", slice.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Hiç beslenmiş mi" yoklaması <b>yalnızca boş sonuç yolunda</b> koşuyor —
    /// akış doluysa maliyeti sıfır.
    /// </summary>
    [Fact]
    public async Task Dolu_pencerede_ikinci_sorgu_atilmiyor()
    {
        var query = new RecordingScopedQuery();
        query.Changes.Add(Change(Now.AddMinutes(-12)));

        var slice = await GatherAsync(new ChangeFeedProvider(query, Clock()));

        Assert.Equal(EvidenceStatus.Gathered, slice.Status);
        Assert.Single(query.ChangeQueries);
    }

    /// <summary>
    /// Değişiklik akışı olay penceresinden <b>önceye</b> de bakıyor: bir ACL
    /// push'unun etkisi anında değil, dakikalar sonra görünür.
    /// </summary>
    [Fact]
    public async Task Pencere_oncesine_de_bakiliyor()
    {
        var query = new RecordingScopedQuery();
        query.Changes.Add(Change(Now.AddMinutes(-12)));

        await GatherAsync(new ChangeFeedProvider(query, Clock()));

        Assert.Equal(Now - ChangeFeedProvider.Lead, query.ChangeQueries[0].From);
    }

    /// <summary>
    /// Bütçe tavanı aşıldığında <b>söyleniyor</b>. Sessizce kırpılmış bir liste
    /// "hepsi bu" diye okunur ve rapor eksik kanıta tam kanıt muamelesi yapar.
    /// </summary>
    [Fact]
    public async Task Butce_asimi_kirpiliyor_ve_soyleniyor()
    {
        var query = new RecordingScopedQuery();
        for (var index = 0; index < 5; index++)
        {
            query.Changes.Add(Change(Now.AddMinutes(-index)));
        }

        var slice = await GatherAsync(new ChangeFeedProvider(query, Clock()), new GatherBudget(2, TimeSpan.FromSeconds(5)));

        Assert.True(slice.Truncated);
        Assert.Equal(2, slice.Items.Count);
        Assert.NotEmpty(slice.Detail);
    }

    /// <summary>
    /// Kapsam dışı sayım geliyor ve <b>yalnızca sayı</b>: dilimde kapsam dışı
    /// içerik taşıyan tek bir alan yok (K17, RCA §3.2).
    /// </summary>
    [Fact]
    public async Task Kapsam_disi_yalnizca_sayi()
    {
        var query = new RecordingScopedQuery { OutOfScopeChanges = 342 };
        query.EverChanges.Add(Change(Now.AddDays(-3)));

        var slice = await GatherAsync(new ChangeFeedProvider(query, Clock()));

        Assert.Equal(342, slice.OutOfScopeCount);

        // Kapsam dışı olan hiçbir şey `Items`'a sızmıyor: sayım ayrı sorgudan
        // geliyor ve sonucu hiçbir zaman satıra dönüşmüyor.
        Assert.Empty(slice.Items);
    }

    // ---- logs.window ------------------------------------------------------

    /// <summary>
    /// Kanıt satırı ham loga inen yolu taşıyor — ve o yol <b>ham SQL değil</b>,
    /// yapılandırılmış bir sorgu. Kanıt paketi saklandığı için (T36) SQL
    /// dizgisi yazmak, kapsam kapısını atlayan bir yolu diske yazmak olurdu.
    /// </summary>
    [Fact]
    public async Task Kanit_satiri_ham_loga_inen_yolu_tasiyor()
    {
        var query = new RecordingScopedQuery();
        query.Events.Add(Event("fg-01", ParseStatus.Failed));

        var slice = await GatherAsync(new LogWindowProvider(query));

        var item = Assert.Single(slice.Items);
        Assert.NotNull(item.Drilldown);
        Assert.Equal(["fg-01"], item.Drilldown.SourceIds);
    }

    /// <summary>
    /// Kanıt satırı <c>time_source</c>'u taşıyor. Zamanı <c>observed</c> olan
    /// bir olayın gerçek zamanı dakikalarca önce olabilir ve korelasyon
    /// penceresi bunu bilmeden kayar; T35 bunu rapora taşıyacak, taşıyabilmesi
    /// için satırın getirmesi şart (F3 planı).
    /// </summary>
    [Fact]
    public async Task Kanit_satiri_time_source_tasiyor()
    {
        var query = new RecordingScopedQuery();
        query.Events.Add(Event("fg-01", ParseStatus.Partial) with { TimeSource = TimeSources.Observed });

        var slice = await GatherAsync(new LogWindowProvider(query));

        Assert.Equal(TimeSources.Observed, Assert.Single(slice.Items).Payload["time_source"]);
    }

    /// <summary>
    /// <b>Boş sonuçta bile kapsam dışı sayılıyor.</b> "Senin kapsamında bir şey
    /// yok ama dışarıda 342 var" tam da raporun söylemesi gereken cümle; boş
    /// sonuçta sormamak onu sessizce yutardı.
    /// </summary>
    [Fact]
    public async Task Bos_sonucta_bile_kapsam_disi_sayiliyor()
    {
        var slice = await GatherAsync(
            new LogWindowProvider(new RecordingScopedQuery { OutOfScopeEvents = 342 }));

        Assert.Equal(EvidenceStatus.Empty, slice.Status);
        Assert.Equal(342, slice.OutOfScopeCount);
    }

    /// <summary>
    /// Sağlayıcı yalnızca sorunlu satırları istiyor: filtresiz bir pencere
    /// dökümü kanıt değil, veri yığınıdır.
    /// </summary>
    [Fact]
    public async Task Yalnizca_sorunlu_satirlar_isteniyor()
    {
        var query = new RecordingScopedQuery();

        await GatherAsync(new LogWindowProvider(query));

        Assert.Equal(
            [ParseStatus.Failed, ParseStatus.Partial],
            query.EventQueries[0].ParseStatuses);

        // Kapsam daraltması pencereden geçiyor — sağlayıcı kendi grubunu seçmiyor.
        Assert.Equal(["network/core"], query.EventQueries[0].OwnerGroups);
    }

    /// <summary>Kırpma <c>HasMore</c>'dan okunuyor ve söyleniyor.</summary>
    [Fact]
    public async Task Log_kirpmasi_soyleniyor()
    {
        var query = new RecordingScopedQuery { EventsHaveMore = true };
        query.Events.Add(Event("fg-01", ParseStatus.Failed));

        var slice = await GatherAsync(new LogWindowProvider(query));

        Assert.True(slice.Truncated);
        Assert.NotEmpty(slice.Detail);
    }

    private static FakeTimeProvider Clock() => new(Now);

    private static ChangeEvent Change(DateTimeOffset ts) => new()
    {
        ChangeId = Guid.CreateVersion7(ts),
        Timestamp = ts,
        OwnerGroup = "network/core",
        TargetKind = ChangeTargetKind.Config,
        TargetId = "core-sw-02",
        ChangeKind = "acl.push",
        Actor = "m.yilmaz",
        Summary = "ACL güncellendi",
    };

    private static LogEvent Event(string sourceId, ParseStatus status) => new()
    {
        EventId = Guid.CreateVersion7(Now),
        Timestamp = Now.AddMinutes(2),
        OwnerGroup = "network/core",
        SourceId = sourceId,
        Host = sourceId,
        ParseStatus = status,
        ParserId = "cisco.asa.network",
        SrcIp = IPAddress.IPv6Any,
        DstIp = IPAddress.IPv6Any,
        Attrs = new Dictionary<string, string>(StringComparer.Ordinal),
        Body = "%ASA-6-302013: Built outbound TCP connection",
    };
}
