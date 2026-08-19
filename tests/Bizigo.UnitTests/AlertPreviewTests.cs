using Bizigo.Alerting;
using Bizigo.Contracts;
using Bizigo.ControlPlane;

namespace Bizigo.UnitTests;

/// <summary>
/// Önizleme bekçileri (T23).
///
/// <para>
/// Ticket'ın taşıyıcı maddesi bu ekran ve taşıyıcı kararı da tek cümle:
/// <b>sunucu eşikten bağımsız veri döndürüyor.</b> Buradaki testlerin çoğu o
/// kararın sonuçlarını sabitliyor — pencere tamamlanıyor mu, boşluklar doğru mu,
/// kapsam kesişiyor mu.
/// </para>
/// </summary>
public sealed class AlertPreviewTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static AlertRuleInput Input(
        AlertRuleType type = AlertRuleType.Threshold,
        int windowSeconds = 300,
        int silenceSeconds = 900,
        double threshold = 10,
        IReadOnlyList<string>? groups = null) => new()
        {
            Name = "önizleme",
            OwnerGroups = groups ?? ["network/core"],
            RuleType = type,
            WindowSeconds = windowSeconds,
            SilenceSeconds = silenceSeconds,
            Threshold = threshold,
        };

    private static AccessScope Core => AccessScope.ForGroups("analyst.core", ["network/core"]);

    [Fact]
    public async Task Esik_onizlemesi_kova_serisi_donduruyor()
    {
        var query = new FakeScopedQuery();

        // Son 24 saatte iki farklı pencerede olay: biri 20, biri 3.
        for (var i = 0; i < 20; i++)
        {
            query.Events.Add(new FakeEvent("network/core", "fw-01", Now.AddMinutes(-10), "deny"));
        }

        for (var i = 0; i < 3; i++)
        {
            query.Events.Add(new FakeEvent("network/core", "fw-01", Now.AddHours(-5), "deny"));
        }

        var preview = new AlertPreview(query);
        var result = await preview.RunAsync(Input(), Core, null, Now, Token);

        Assert.Equal(AlertRuleType.Threshold, result.RuleType);
        Assert.Equal(Now.AddHours(-24), result.From);

        // Seri boş kovaları da içeriyor: 24 saat / 5 dakika = 288.
        Assert.Equal(288, result.Points.Count);
        Assert.Equal(23, result.Points.Sum(p => p.Count));
    }

    /// <summary>
    /// T23 kabul kriterinin çekirdeği: eşik değiştiğinde <b>yeni sorgu
    /// atılmıyor</b>. Sunucu tarafındaki karşılığı, dönen serinin eşikten
    /// bağımsız olması.
    /// </summary>
    [Fact]
    public async Task Farkli_esikler_ayni_seriyi_uretiyor()
    {
        var query = new FakeScopedQuery();

        for (var i = 0; i < 50; i++)
        {
            query.Events.Add(new FakeEvent("network/core", "fw-01", Now.AddMinutes(-10), "deny"));
        }

        var preview = new AlertPreview(query);

        var low = await preview.RunAsync(Input(threshold: 1), Core, null, Now, Token);
        var high = await preview.RunAsync(Input(threshold: 10_000), Core, null, Now, Token);

        // Seri birebir aynı; yalnızca hesaplanan tetiklenme sayısı farklı.
        Assert.Equal(low.Points.Select(p => p.Count), high.Points.Select(p => p.Count));
        Assert.Equal(1, low.FiringCount);
        Assert.Equal(0, high.FiringCount);
    }

    [Fact]
    public async Task Onizleme_baska_ekibin_verisini_saymiyor()
    {
        var query = new FakeScopedQuery();

        for (var i = 0; i < 10; i++)
        {
            query.Events.Add(new FakeEvent("network/core", "fw-01", Now.AddMinutes(-10), "deny"));
            query.Events.Add(new FakeEvent("network/edge", "fw-99", Now.AddMinutes(-10), "deny"));
        }

        var result = await new AlertPreview(query).RunAsync(Input(), Core, null, Now, Token);

        Assert.Equal(10, result.Points.Sum(p => p.Count));
    }

    /// <summary>
    /// Önizleme <b>kaydedilmemiş</b> bir kuralı koşturuyor, yani kural henüz
    /// doğrulamadan geçmemiş olabilir. Kesişim alınmazsa kullanıcı,
    /// kaydedemeyeceği bir kuralı önizleyerek kapsamı dışındaki veriyi sayardı.
    /// </summary>
    [Fact]
    public async Task Kapsam_disi_grup_isteyen_onizleme_veri_gostermiyor()
    {
        var query = new FakeScopedQuery();

        for (var i = 0; i < 10; i++)
        {
            query.Events.Add(new FakeEvent("network/edge", "fw-99", Now.AddMinutes(-10), "deny"));
        }

        var result = await new AlertPreview(query)
            .RunAsync(Input(groups: ["network/edge"]), Core, null, Now, Token);

        Assert.Empty(result.Points);
        Assert.Equal(0, result.FiringCount);
        Assert.Contains("kesişmiyor", result.Note, StringComparison.Ordinal);
        Assert.Equal(0, query.TotalCalls);
    }

    [Fact]
    public void Kesisim_kapsami_genisletemiyor()
    {
        var intersected = AlertPreview.Intersect(["network/core", "network/edge"], Core);

        Assert.Equal(["network/core"], intersected.OwnerGroups.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Grup_secilmemisse_kullanicinin_kendi_kapsami_kullaniliyor()
    {
        // Form daha doldurulmadan da önizleme anlamlı bir şey göstersin diye.
        Assert.Equal(Core.OwnerGroups, AlertPreview.Intersect([], Core).OwnerGroups);
    }

    [Fact]
    public async Task Geriye_bakis_ust_sinirla_kirpiliyor()
    {
        var result = await new AlertPreview(new FakeScopedQuery())
            .RunAsync(Input(), Core, TimeSpan.FromDays(90), Now, Token);

        Assert.Equal(Now - AlertPreview.MaxLookback, result.From);
    }

    [Fact]
    public async Task Sessizlik_onizlemesi_kaynak_basina_bosluk_donduruyor()
    {
        var query = new FakeScopedQuery();
        query.Sources.Add(FakeScopedQuery.Source("fw-01", "network/core", Now.AddDays(-30)));
        query.Sources.Add(FakeScopedQuery.Source("fw-02", "network/core", Now.AddDays(-30)));

        // fw-01 pencerenin başında ve sonunda konuştu; ortada uzun bir boşluk.
        query.Events.Add(new FakeEvent("network/core", "fw-01", Now.AddHours(-24), "accept"));
        query.Events.Add(new FakeEvent("network/core", "fw-01", Now.AddMinutes(-5), "accept"));

        // fw-02 hiç konuşmadı: tek boşluk, pencerenin tamamı.
        var result = await new AlertPreview(query)
            .RunAsync(Input(AlertRuleType.Silence, silenceSeconds: 900), Core, null, Now, Token);

        Assert.Equal(2, result.Sources.Count);

        var first = result.Sources.Single(s => s.SourceId == "fw-01");
        Assert.NotNull(first.LastSeen);
        Assert.Contains(first.Gaps, gap => gap > 20 * 3600);

        var second = result.Sources.Single(s => s.SourceId == "fw-02");
        Assert.Null(second.LastSeen);
        Assert.Single(second.Gaps);
    }

    [Fact]
    public void Bosluk_hesabi_pencerenin_sonuna_kadar_sayiyor()
    {
        // Süren sessizlik alarm açısından en önemlisi; kapanmamış boşluk
        // sayılmasaydı "hâlâ susuyor" hiç görünmezdi.
        var gaps = AlertPreview.Gaps(
            [Now.AddHours(-10)],
            Now.AddHours(-12),
            Now,
            bucketSeconds: 300);

        Assert.Equal(2, gaps.Count);
        Assert.Equal(2 * 3600, gaps[0], 0);
        Assert.Equal((10 * 3600) - 300, gaps[1], 0);
    }

    [Fact]
    public void Bosluk_hesabi_kesintisiz_veride_bosluk_uretmiyor()
    {
        var stamps = Enumerable.Range(0, 12)
            .Select(i => Now.AddHours(-1).AddMinutes(i * 5))
            .ToArray();

        var gaps = AlertPreview.Gaps(stamps, Now.AddHours(-1), Now, bucketSeconds: 300);

        Assert.Empty(gaps);
    }

    /// <summary>
    /// Boş kovalar SQL'den dönmüyor; oran hesabında <b>taban</b> olarak
    /// gerekiyorlar. Eksik kovayı atlamak, iki gerçek kovanın yan yana
    /// sayılması yani uydurma bir oran demek.
    /// </summary>
    [Fact]
    public void Seri_bos_kovalari_dolduruyor()
    {
        var series = AlertPreview.Densify(
            [new HistogramBucket(Now.AddMinutes(-10), string.Empty, 7)],
            Now.AddMinutes(-30),
            Now,
            bucketSeconds: 300);

        Assert.Equal(6, series.Count);
        Assert.Equal(7, series.Sum(s => s.Count));
        Assert.Contains(series, s => s.Count == 0);
    }

    [Fact]
    public void Esik_karsilastirmasi_motorunkiyle_ayni_kumeden()
    {
        IReadOnlyList<PreviewPoint> points =
        [
            new(Now, 5, 5),
            new(Now.AddMinutes(5), 15, 15),
            new(Now.AddMinutes(10), 10, 10),
        ];

        Assert.Equal(1, AlertPreview.CountFirings(points, 10, AlertComparison.GreaterThan));
        Assert.Equal(2, AlertPreview.CountFirings(points, 10, AlertComparison.GreaterThanOrEqual));
        Assert.Equal(1, AlertPreview.CountFirings(points, 10, AlertComparison.LessThan));
    }

    [Fact]
    public async Task Oran_onizlemesi_ilk_kovayi_saymiyor()
    {
        var query = new FakeScopedQuery();

        for (var i = 0; i < 30; i++)
        {
            query.Events.Add(new FakeEvent("network/core", "fw-01", Now.AddMinutes(-2), "deny"));
        }

        var result = await new AlertPreview(query)
            .RunAsync(Input(AlertRuleType.Ratio, threshold: 2), Core, null, Now, Token);

        // İlk kovanın tabanı yok; onu saymak "hiçten bir şeye" geçişi
        // tetiklenme saymak olurdu.
        Assert.Equal(287, result.Points.Count);
    }
}
