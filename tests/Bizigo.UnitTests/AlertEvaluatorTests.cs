using Bizigo.Alerting;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// Alarm değerlendiricisinin bekçileri (T21).
///
/// <para>
/// <b>Hiçbiri duvar saati beklemiyor.</b> "Şimdi" değerlendirme bağlamına
/// parametre olarak giriyor, zaman aşımı sahte saatten tetikleniyor. F1'de aynı
/// hata sınıfı beş ayrı yerde çıktı ve belirtisi her seferinde "test kararsız"dı.
/// </para>
/// </summary>
public sealed class AlertEvaluatorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static AlertEvaluator Evaluator(AlertingOptions options, TimeProvider? time = null) =>
        new(options, new AlertingStats(), NullLogger<AlertEvaluator>.Instance, time);

    private static AlertEvaluationContext Context(
        FakeScopedQuery query,
        AlertingStats? stats = null,
        TimeProvider? time = null,
        DateTimeOffset? now = null) =>
        new(query, stats ?? new AlertingStats(), now ?? Now,
            TimeSpan.FromHours(6), TimeSpan.FromSeconds(20), default, time);

    private static AlertRuleEntity Rule(
        AlertRuleType type,
        string groups,
        double threshold = 100,
        int windowSeconds = 300,
        int silenceSeconds = 900,
        AlertSearch? search = null) => new()
        {
            Name = "sınama",
            OwnerSubject = "tester",
            OwnerGroups = groups,
            RuleType = type,
            Threshold = threshold,
            WindowSeconds = windowSeconds,
            SilenceSeconds = silenceSeconds,
            SearchJson = AlertSearchCodec.Serialize(search ?? new AlertSearch()),
        };

    [Fact]
    public async Task Esik_kurali_sinir_asilinca_tetikleniyor()
    {
        var query = new FakeScopedQuery();
        for (var i = 0; i < 150; i++)
        {
            query.Events.Add(new FakeEvent("network/core", "fw-01", Now.AddMinutes(-1), "deny"));
        }

        var rule = Rule(AlertRuleType.Threshold, "network/core", threshold: 100,
            search: new AlertSearch { Filters = [FieldFilter.Eq("action", "deny")] });

        var outcome = await Evaluator(new AlertingOptions()).EvaluateAsync(rule, Context(query), Token);

        Assert.Equal(AlertRunState.Fired, outcome.State);
        Assert.Equal(150, outcome.Hits[0].Value);
        Assert.Equal(Now.AddMinutes(-5), outcome.Hits[0].WindowFrom);
        Assert.Equal(Now, outcome.Hits[0].WindowTo);
    }

    [Fact]
    public async Task Esik_kurali_sinir_asilmazsa_sessiz()
    {
        var query = new FakeScopedQuery();
        query.Events.Add(new FakeEvent("network/core", "fw-01", Now.AddMinutes(-1), "deny"));

        var rule = Rule(AlertRuleType.Threshold, "network/core", threshold: 100);
        var outcome = await Evaluator(new AlertingOptions()).EvaluateAsync(rule, Context(query), Token);

        Assert.Equal(AlertRunState.Quiet, outcome.State);
        Assert.Empty(outcome.Hits);
    }

    /// <summary>
    /// T21 kabul kriteri. Bu bekçinin kırmızı yanabildiği,
    /// <c>AlertEvaluator.ScopeOf</c> geçici olarak <c>AccessScope.System</c>
    /// döndürülerek doğrulandı: o hâlde sayım 150 yerine 250 çıkıyor ve test düşüyor.
    /// </summary>
    [Fact]
    public async Task Bir_ekibin_kurali_baska_ekibin_olaylarini_saymiyor()
    {
        var query = new FakeScopedQuery();

        for (var i = 0; i < 150; i++)
        {
            query.Events.Add(new FakeEvent("network/core", "fw-01", Now.AddMinutes(-1), "deny"));
        }

        for (var i = 0; i < 100; i++)
        {
            query.Events.Add(new FakeEvent("network/edge", "fw-99", Now.AddMinutes(-1), "deny"));
        }

        var rule = Rule(AlertRuleType.Threshold, "network/core", threshold: 10);
        var outcome = await Evaluator(new AlertingOptions()).EvaluateAsync(rule, Context(query), Token);

        Assert.Equal(AlertRunState.Fired, outcome.State);
        Assert.Equal(150, outcome.Hits[0].Value);
    }

    [Fact]
    public async Task Kapsamsiz_kural_sorguya_hic_cikmiyor()
    {
        var query = new FakeScopedQuery();
        var rule = Rule(AlertRuleType.Threshold, string.Empty);

        var outcome = await Evaluator(new AlertingOptions()).EvaluateAsync(rule, Context(query), Token);

        Assert.Equal(AlertRunState.Failed, outcome.State);
        Assert.Equal(0, query.TotalCalls);
    }

    [Fact]
    public async Task Oran_kurali_onceki_pencereye_gore_hesapliyor()
    {
        var query = new FakeScopedQuery();

        // Şimdiki pencere (son 5 dk): 90 olay.
        for (var i = 0; i < 90; i++)
        {
            query.Events.Add(new FakeEvent("network/core", "fw-01", Now.AddMinutes(-2), "deny"));
        }

        // Taban penceresi (önceki 5 dk): 30 olay → 3×.
        for (var i = 0; i < 30; i++)
        {
            query.Events.Add(new FakeEvent("network/core", "fw-01", Now.AddMinutes(-7), "deny"));
        }

        var rule = Rule(AlertRuleType.Ratio, "network/core", threshold: 2.5);
        var outcome = await Evaluator(new AlertingOptions()).EvaluateAsync(rule, Context(query), Token);

        Assert.Equal(AlertRunState.Fired, outcome.State);
        Assert.Equal(3, outcome.Hits[0].Value, 3);
    }

    [Fact]
    public async Task Oran_kurali_sifir_tabanda_bire_yuvarliyor()
    {
        var query = new FakeScopedQuery();

        // Taban boş, şimdiki pencerede 10 olay. Bölme tanımsız olurdu;
        // taban bire çekilerek katsayı 10 çıkıyor.
        for (var i = 0; i < 10; i++)
        {
            query.Events.Add(new FakeEvent("network/core", "fw-01", Now.AddMinutes(-2), "deny"));
        }

        var rule = Rule(AlertRuleType.Ratio, "network/core", threshold: 3);
        var outcome = await Evaluator(new AlertingOptions()).EvaluateAsync(rule, Context(query), Token);

        Assert.Equal(AlertRunState.Fired, outcome.State);
        Assert.Equal(10, outcome.Hits[0].Value, 3);
    }

    [Fact]
    public async Task Oran_kurali_iki_pencere_de_bossa_tetiklenmiyor()
    {
        var query = new FakeScopedQuery();
        var rule = Rule(AlertRuleType.Ratio, "network/core", threshold: 3);

        var outcome = await Evaluator(new AlertingOptions()).EvaluateAsync(rule, Context(query), Token);

        Assert.Equal(AlertRunState.Quiet, outcome.State);
    }

    /// <summary>T21'in en zor tipi: verinin yokluğu üzerinde çalışan tek kural.</summary>
    [Fact]
    public async Task Sessizlik_kurali_susan_kaynagi_buluyor()
    {
        var query = new FakeScopedQuery();
        query.Sources.Add(FakeScopedQuery.Source("fw-core-01", "network/core", Now.AddDays(-30)));
        query.Sources.Add(FakeScopedQuery.Source("fw-core-02", "network/core", Now.AddDays(-30)));

        // fw-core-02 iki dakika önce konuştu; fw-core-01 yarım saattir susuyor.
        query.Events.Add(new FakeEvent("network/core", "fw-core-01", Now.AddMinutes(-30), "accept"));
        query.Events.Add(new FakeEvent("network/core", "fw-core-02", Now.AddMinutes(-2), "accept"));

        var rule = Rule(AlertRuleType.Silence, "network/core", silenceSeconds: 900);
        var outcome = await Evaluator(new AlertingOptions()).EvaluateAsync(rule, Context(query), Token);

        Assert.Equal(AlertRunState.Fired, outcome.State);
        var hit = Assert.Single(outcome.Hits);
        Assert.Equal("fw-core-01", hit.SourceId);
        Assert.Equal(1800, hit.Value, 0);

        // Bildirimdeki bağlantı susmanın BAŞLADIĞI ana açılmalı, sabit bir
        // geriye bakış penceresine değil.
        Assert.Equal(Now.AddMinutes(-30), hit.WindowFrom);
        Assert.Equal(Now, hit.WindowTo);
    }

    [Fact]
    public async Task Hic_gorulmemis_kaynakta_baglanti_araligi_geriye_bakisla_sinirli()
    {
        var query = new FakeScopedQuery();

        // Envantere bir ay önce girmiş ve hiç konuşmamış: aralık aksi hâlde
        // bir aya uzardı.
        query.Sources.Add(FakeScopedQuery.Source("fw-hic", "network/core", Now.AddDays(-30)));

        var rule = Rule(AlertRuleType.Silence, "network/core", silenceSeconds: 900);
        var outcome = await Evaluator(new AlertingOptions()).EvaluateAsync(rule, Context(query), Token);

        var hit = Assert.Single(outcome.Hits);
        Assert.Equal(Now.AddHours(-6), hit.WindowFrom);
    }

    [Fact]
    public async Task Sessizlik_kurali_hic_veri_gondermemis_eski_kaynagi_yakaliyor()
    {
        var query = new FakeScopedQuery();
        query.Sources.Add(FakeScopedQuery.Source("fw-yeni", "network/core", Now.AddDays(-3)));

        var rule = Rule(AlertRuleType.Silence, "network/core", silenceSeconds: 900);
        var outcome = await Evaluator(new AlertingOptions()).EvaluateAsync(rule, Context(query), Token);

        Assert.Equal(AlertRunState.Fired, outcome.State);
        Assert.Contains("hiç veri göndermedi", outcome.Hits[0].Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Mühlet olmasaydı envantere eklenen her cihaz, eklendiği dakika alarm
    /// üretirdi — motor daha ilk gününde güvenilirliğini kaybederdi.
    /// </summary>
    [Fact]
    public async Task Sessizlik_kurali_yeni_kaynaga_muhlet_veriyor()
    {
        var query = new FakeScopedQuery();
        query.Sources.Add(FakeScopedQuery.Source("fw-dun-eklendi", "network/core", Now.AddMinutes(-5)));

        var rule = Rule(AlertRuleType.Silence, "network/core", silenceSeconds: 900);
        var outcome = await Evaluator(new AlertingOptions()).EvaluateAsync(rule, Context(query), Token);

        Assert.Equal(AlertRunState.Quiet, outcome.State);
    }

    [Fact]
    public async Task Sessizlik_kurali_pasif_kaynagi_atliyor()
    {
        var query = new FakeScopedQuery();
        var disabled = FakeScopedQuery.Source("fw-kapali", "network/core", Now.AddDays(-30)) with { Enabled = false };
        query.Sources.Add(disabled);

        var rule = Rule(AlertRuleType.Silence, "network/core", silenceSeconds: 900);
        var outcome = await Evaluator(new AlertingOptions()).EvaluateAsync(rule, Context(query), Token);

        Assert.Equal(AlertRunState.Quiet, outcome.State);
    }

    [Fact]
    public async Task Sessizlik_kurali_baska_ekibin_kaynagini_izlemiyor()
    {
        var query = new FakeScopedQuery();
        query.Sources.Add(FakeScopedQuery.Source("fw-edge-01", "network/edge", Now.AddDays(-30)));

        var rule = Rule(AlertRuleType.Silence, "network/core", silenceSeconds: 900);
        var outcome = await Evaluator(new AlertingOptions()).EvaluateAsync(rule, Context(query), Token);

        Assert.Equal(AlertRunState.Quiet, outcome.State);
    }

    /// <summary>
    /// <b>F1'in en pahalı dersinin bekçisi.</b> Zaman aşımı "eşik aşılmadı" ile
    /// aynı sonuca bağlanamaz; bağlansaydı yavaş bir sorgu sessizce "her şey
    /// yolunda"ya dönüşürdü. Test hiçbir şey beklemiyor: sahte sorgu girdiğini
    /// haber veriyor, saat ileri alınıyor, sonuç okunuyor.
    /// </summary>
    [Fact]
    public async Task Zaman_asimi_sessiz_degil_ayri_bir_durum_uretiyor()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FakeTimeProvider(Now);
        var stats = new AlertingStats();

        var query = new FakeScopedQuery
        {
            BeforeCount = async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            },
        };

        var rule = Rule(AlertRuleType.Threshold, "network/core", threshold: 1);
        var options = new AlertingOptions { EvaluationTimeoutSeconds = 20 };
        var evaluator = new AlertEvaluator(options, stats, NullLogger<AlertEvaluator>.Instance, time);

        var running = evaluator.EvaluateAsync(rule, Context(query, stats, time), Token);

        await entered.Task.WaitAsync(Token);
        time.Advance(TimeSpan.FromSeconds(21));

        var outcome = await running;

        Assert.Equal(AlertRunState.TimedOut, outcome.State);
        Assert.NotEqual(AlertRunState.Quiet, outcome.State);
        Assert.Empty(outcome.Hits);
        Assert.Equal(1, stats.TimedOut);
    }

    /// <summary>
    /// T21 maliyet kabul kriteri: kural sayısı arttığında ClickHouse'a atılan
    /// sorgu sayısı doğrusal ötesi büyümüyor.
    ///
    /// <para>
    /// Sessizlikte fazlası da doğru: aynı kapsamdaki elli kural, kural başına
    /// değil <b>tur başına</b> iki sorgu üretiyor (envanter + etkinlik). Naif
    /// uygulama kural × kaynak kadar sorgu atardı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Elli_sessizlik_kurali_kapsam_basina_iki_sorgu_atiyor()
    {
        var query = new FakeScopedQuery();
        query.Sources.Add(FakeScopedQuery.Source("fw-core-01", "network/core", Now.AddDays(-30)));
        query.Sources.Add(FakeScopedQuery.Source("fw-core-02", "network/core", Now.AddDays(-30)));

        var context = Context(query);
        var evaluator = Evaluator(new AlertingOptions());

        var rules = Enumerable.Range(0, 50)
            .Select(_ => Rule(AlertRuleType.Silence, "network/core", silenceSeconds: 900))
            .ToArray();

        foreach (var rule in rules)
        {
            await evaluator.EvaluateAsync(rule, context, Token);
        }

        Assert.Equal(1, query.InventoryCalls);
        Assert.Equal(1, query.ActivityCalls);
        Assert.Equal(0, query.CountCalls);
    }

    [Fact]
    public async Task Iki_farkli_kapsam_iki_ayri_sorgu_kumesi_uretiyor()
    {
        var query = new FakeScopedQuery();
        var context = Context(query);
        var evaluator = Evaluator(new AlertingOptions());

        await evaluator.EvaluateAsync(Rule(AlertRuleType.Silence, "network/core"), context, Token);
        await evaluator.EvaluateAsync(Rule(AlertRuleType.Silence, "network/edge"), context, Token);
        await evaluator.EvaluateAsync(Rule(AlertRuleType.Silence, "network/core"), context, Token);

        Assert.Equal(2, query.InventoryCalls);
        Assert.Equal(2, query.ActivityCalls);
    }

    /// <summary>Grup sırası paylaşımı bozmamalı; bozsaydı sessizce iki kat sorgu olurdu.</summary>
    [Fact]
    public async Task Ayni_gruplar_farkli_sirada_ayni_sorguyu_paylasiyor()
    {
        var query = new FakeScopedQuery();
        var context = Context(query);
        var evaluator = Evaluator(new AlertingOptions());

        await evaluator.EvaluateAsync(Rule(AlertRuleType.Silence, "network/core,network/edge"), context, Token);
        await evaluator.EvaluateAsync(Rule(AlertRuleType.Silence, "network/edge,network/core"), context, Token);

        Assert.Equal(1, query.InventoryCalls);
        Assert.Equal(1, query.ActivityCalls);
    }
}
