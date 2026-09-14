using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Query;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>F5 · S1 — topoloji sağlayıcısı.</b>
///
/// <para>
/// Bu sınıfın en önemli testi <see cref="Envanterde_topoloji_verisi_yoksa_never_fed"/>.
/// Sinyalin doğal başarısızlığı sessiz: kimsenin <c>upstream</c> girmediği bir
/// envanterde "ortak öznitelik yok" cümlesi kurulabiliyor ve <b>tam olarak
/// doğru bir cümle gibi görünüyor</b>. T34'ün boşluğu adlandırma disiplini
/// burada da geçerli.
/// </para>
/// </summary>
public class TopologyProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static RcaWindow Window(IReadOnlyList<string>? groups = null) => new()
    {
        From = Now,
        To = Now.AddMinutes(30),
        BaselineFrom = Now.AddHours(-24),
        BaselineTo = Now.AddHours(-1),
        OwnerGroups = groups ?? [],
    };

    private static SourceSummary Source(
        string id, string group = "net", string upstream = "", string vlan = "", string firmware = "") =>
        new(id, group, null, null, "test", "test", "p", "utf-8", "default", true, true, Now,
            upstream, vlan, firmware);

    private static SourceOnset Onset(string id, string group = "net", int minutes = 0) =>
        new(group, id, Now.AddMinutes(minutes), 10, 0);

    [Fact]
    public async Task Envanterde_topoloji_verisi_yoksa_never_fed()
    {
        var query = new RecordingScopedQuery();
        query.Onsets.AddRange([Onset("sw-1"), Onset("sw-2"), Onset("sw-3")]);

        // Envanter dolu ama topoloji alanları boş — "doldurulmamış" hâli.
        query.Inventory.AddRange([Source("sw-1"), Source("sw-2"), Source("sw-3")]);

        var slice = await new TopologyProvider(query).GatherAsync(
            Window(), AccessScope.System("test"), GatherBudget.Default, TestContext.Current.CancellationToken);

        // `Empty` OLSAYDI rapor "bu cihazların ortak bir özelliği yok" derdi —
        // oysa hiç bakılamadı. İkisi aynı boş listeye düşerse fark yalnızca
        // raporu okuyanın yanlış sonuca varmasıyla belli olur.
        Assert.Equal(EvidenceStatus.NeverFed, slice.Status);
        Assert.False(slice.IsEvidence);
        Assert.Contains("doldurulmamış", slice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ortak_upstream_bulunuyor_ve_paydalar_kanitin_icinde()
    {
        var query = new RecordingScopedQuery();
        query.Onsets.AddRange([Onset("sw-1"), Onset("sw-2", minutes: 2), Onset("sw-3", minutes: 5)]);

        // Bozulan üç cihazın ikisi core-01'in arkasında; kapsamda ise
        // core-01 azınlıkta (6 cihazın 2'si).
        query.Inventory.AddRange(
        [
            Source("sw-1", upstream: "core-01"),
            Source("sw-2", upstream: "core-01"),
            Source("sw-3", upstream: "core-02"),
            Source("sw-4", upstream: "core-02"),
            Source("sw-5", upstream: "core-02"),
            Source("sw-6", upstream: "core-02"),
        ]);

        var slice = await new TopologyProvider(query).GatherAsync(
            Window(), AccessScope.System("test"), GatherBudget.Default, TestContext.Current.CancellationToken);

        Assert.Equal(EvidenceStatus.Gathered, slice.Status);

        var item = Assert.Single(slice.Items);
        Assert.Equal("topology:upstream:core-01", item.Id);
        Assert.Equal(EvidenceKind.Topology, item.Kind);

        // (2/3) ÷ (2/6) = 2.0
        Assert.Equal(2.0, item.Weight, 3);

        // Paydalar satırın içinde: okuyan hangi iki oranın bölündüğünü tahmin
        // etmek zorunda kalmıyor.
        Assert.Equal("2", item.Payload["affected_count"]);
        Assert.Equal("3", item.Payload["affected_total"]);
        Assert.Equal("2", item.Payload["population_count"]);
        Assert.Equal("6", item.Payload["population_total"]);

        // Drilldown alana değil, o değeri paylaşan cihazlara iniyor: `upstream`
        // olay tablosunda kolon değil.
        Assert.Equal(["sw-1", "sw-2"], item.Drilldown!.SourceIds);
    }

    [Fact]
    public async Task Kapsamin_genelinde_de_yaygin_olan_deger_bulgu_degil()
    {
        var query = new RecordingScopedQuery();
        query.Onsets.AddRange([Onset("sw-1"), Onset("sw-2")]);

        // Herkes aynı firmware'de: bozulanların paylaşması bir şey anlatmıyor.
        query.Inventory.AddRange(
        [
            Source("sw-1", firmware: "7.4.1"),
            Source("sw-2", firmware: "7.4.1"),
            Source("sw-3", firmware: "7.4.1"),
            Source("sw-4", firmware: "7.4.1"),
        ]);

        var slice = await new TopologyProvider(query).GatherAsync(
            Window(), AccessScope.System("test"), GatherBudget.Default, TestContext.Current.CancellationToken);

        // lift = (2/2) ÷ (4/4) = 1.0 → ayrışma yok.
        Assert.Equal(EvidenceStatus.Empty, slice.Status);
        Assert.Empty(slice.Items);
        Assert.True(slice.IsEvidence);
    }

    [Fact]
    public async Task Tek_cihaz_bozulduysa_ortak_ozniteliginden_soz_edilmiyor()
    {
        var query = new RecordingScopedQuery();
        query.Onsets.Add(Onset("sw-1"));
        query.Inventory.AddRange([Source("sw-1", upstream: "core-01"), Source("sw-2", upstream: "core-02")]);

        var slice = await new TopologyProvider(query).GatherAsync(
            Window(), AccessScope.System("test"), GatherBudget.Default, TestContext.Current.CancellationToken);

        Assert.Equal(EvidenceStatus.Empty, slice.Status);
        Assert.Contains("en az 2", slice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Envanterde_olmayan_bozulan_cihazlar_sayiliyor()
    {
        var query = new RecordingScopedQuery();
        query.Onsets.AddRange([Onset("sw-1"), Onset("sw-2"), Onset("bilinmeyen-1"), Onset("bilinmeyen-2")]);

        query.Inventory.AddRange(
        [
            Source("sw-1", upstream: "core-01"),
            Source("sw-2", upstream: "core-01"),
            Source("sw-9", upstream: "core-02"),
        ]);

        var slice = await new TopologyProvider(query).GatherAsync(
            Window(), AccessScope.System("test"), GatherBudget.Default, TestContext.Current.CancellationToken);

        // Oran 4 cihazın 2'si üzerinden hesaplandı; bunu söylemeyen bir rapor
        // ölçmediği bir kapsamı ölçmüş gibi gösterir.
        Assert.Contains("2 tanesi envanterde yok", slice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Alani_bos_birakilmis_bozulan_cihazlar_satirda_gorunuyor()
    {
        var query = new RecordingScopedQuery();
        query.Onsets.AddRange(
            [Onset("sw-1"), Onset("sw-2"), Onset("sw-3"), Onset("sw-4"), Onset("sw-5")]);

        // Beş cihaz bozuldu; yalnızca ikisinin upstream'i girilmiş ve ikisi de
        // core-01'de. Payda sessizce 2'ye düşüyor.
        query.Inventory.AddRange(
        [
            Source("sw-1", upstream: "core-01"),
            Source("sw-2", upstream: "core-01"),
            Source("sw-3"),
            Source("sw-4"),
            Source("sw-5"),
            Source("sw-6", upstream: "core-02"),
            Source("sw-7", upstream: "core-02"),
            Source("sw-8", upstream: "core-02"),
        ]);

        var slice = await new TopologyProvider(query).GatherAsync(
            Window(), AccessScope.System("test"), GatherBudget.Default, TestContext.Current.CancellationToken);

        var item = Assert.Single(slice.Items);

        // "bozulan 2/2 cihaz core-01'de" cümlesi tek başına doğru ama okuyan
        // onu "bozulanların hepsi" diye okur. Ölçülmemiş üç cihaz raporda iz
        // bırakmadan kaybolurdu — `Detail`'daki "envanterde yok" sayısı da
        // onları göremez, çünkü envanterde varlar.
        Assert.Equal("3", item.Payload["affected_without_value"]);
        Assert.Contains("3 cihazda upstream boş", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Payda_pencerenin_grup_daraltmasini_goruyor()
    {
        var query = new RecordingScopedQuery();
        query.Onsets.AddRange([Onset("sw-1"), Onset("sw-2")]);

        // `net` grubunda core-01 herkes; `depo` grubunda kimse. Pencere `net`e
        // daraltılmışken paydanın `depo`yu da sayması, lifti sahte biçimde
        // yükseltirdi — sayı yanlış olmaz, ANLAMI yanlış olur.
        query.Inventory.AddRange(
        [
            Source("sw-1", group: "net", upstream: "core-01"),
            Source("sw-2", group: "net", upstream: "core-01"),
            Source("depo-1", group: "depo", upstream: "core-02"),
            Source("depo-2", group: "depo", upstream: "core-02"),
            Source("depo-3", group: "depo", upstream: "core-02"),
        ]);

        var slice = await new TopologyProvider(query).GatherAsync(
            Window(["net"]), AccessScope.System("test"), GatherBudget.Default, TestContext.Current.CancellationToken);

        // Daraltma uygulanırsa: (2/2) ÷ (2/2) = 1.0 → bulgu yok.
        // Uygulanmazsa:        (2/2) ÷ (2/5) = 2.5 → uydurma bir bulgu.
        Assert.Equal(EvidenceStatus.Empty, slice.Status);
        Assert.Empty(slice.Items);
    }

    [Fact]
    public async Task Bozulma_esigi_yayilma_saglayicisiyla_ayni()
    {
        var query = new RecordingScopedQuery();
        query.Inventory.Add(Source("sw-1", upstream: "core-01"));

        await new TopologyProvider(query).GatherAsync(
            Window(), AccessScope.System("test"), GatherBudget.Default, TestContext.Current.CancellationToken);

        // İkisi ayrışırsa aynı raporun iki bölümü farklı cihaz kümesinden
        // bahseder ve fark hiçbir yerde görünmez.
        Assert.Equal(new PropagationProvider(query).SeverityAtOrBelow, query.SeverityAsked);
    }
}
