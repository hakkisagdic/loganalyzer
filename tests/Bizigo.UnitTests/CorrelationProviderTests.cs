using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.UnitTests;

/// <summary>
/// F3'ün beş deterministik korelasyonu (T35) — her biri için altın örnek.
///
/// <para>
/// Sağlayıcılar <c>IScopedQuery</c>'den geçiyor ve testler o kapıyı
/// sahteleyerek koşuyor: sınanan şey ClickHouse değil, sağlayıcının <b>ne
/// sorduğu</b>, dönen sayıları <b>nasıl yorumladığı</b> ve boşluğu <b>nasıl
/// adlandırdığı</b>. Sorgunun kendisi entegrasyon testinde.
/// </para>
/// </summary>
public sealed class CorrelationProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 14, 0, 0, TimeSpan.Zero);
    private static readonly AccessScope Scope = AccessScope.System("test");

    /// <summary>45 dakikalık olay penceresi, 7 günlük taban — örtüşmeyen.</summary>
    private static RcaWindow Window() => new()
    {
        From = Now,
        To = Now.AddMinutes(45),
        BaselineFrom = Now.AddDays(-7),
        BaselineTo = Now.AddMinutes(-30),
        OwnerGroups = ["network/core"],
    };

    private static Task<EvidenceSlice> GatherAsync(IEvidenceProvider provider, GatherBudget? budget = null) =>
        provider.GatherAsync(Window(), Scope, budget ?? GatherBudget.Default, TestContext.Current.CancellationToken);

    // ---- 1 · ilk-görülen imza --------------------------------------------

    /// <summary>
    /// <b>Kabul kriteri:</b> ilk-görülen imza <b>başarıyla ayrıştırılmış</b>
    /// olaylarda da çalışıyor — T29'dan önce imkânsızdı (%1 örnekleme).
    ///
    /// <para>
    /// Sahte, ayrıştırma durumuna hiç bakmayan bir satır döndürüyor ve sağlayıcı
    /// onu kanıta çeviriyor; sinyalin <c>parse_status</c>'a bağlı olmadığının
    /// doğrudan gösterimi.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Ilk_gorulen_imza_kanit_uretiyor()
    {
        var query = new RecordingScopedQuery();
        query.FirstSeen.Add(new SignatureCount(4242, 17, Now.AddMinutes(2), 14, "%BGP-5-ADJCHANGE neighbor down"));

        var slice = await GatherAsync(new FirstSeenSignatureProvider(query));

        Assert.Equal(EvidenceStatus.Gathered, slice.Status);

        var item = Assert.Single(slice.Items);
        Assert.Equal("4242", item.Payload["signature_hash"]);

        // Ağırlık kaynak yayılımından: aynı yeni imza 14 cihazda.
        Assert.Equal(14, item.Weight);

        // Ham loga inen yol imzayı filtreliyor.
        Assert.Equal("signature_hash", Assert.Single(item.Drilldown!.Filters).Field);
    }

    /// <summary>
    /// Yeni imza yoksa <c>Empty</c> — ve bu bir kanıt: "pencerede beliren her
    /// şey daha önce de görülmüş" kurulabilir bir cümle.
    /// </summary>
    [Fact]
    public async Task Yeni_imza_yoksa_Empty_ve_gerekce_yazili()
    {
        var slice = await GatherAsync(new FirstSeenSignatureProvider(new RecordingScopedQuery()));

        Assert.Equal(EvidenceStatus.Empty, slice.Status);
        Assert.True(slice.IsEvidence);
        Assert.NotEmpty(slice.Detail);
    }

    /// <summary>
    /// Korelasyon penceresi <see cref="RcaWindow"/>'dan <b>olduğu gibi</b>
    /// taşınıyor: taban sağlayıcının kendi seçimi değil. İki sağlayıcı farklı
    /// taban seçseydi rapor kendi içinde tutarsız olurdu — hiçbir yerde
    /// görünmeden.
    /// </summary>
    [Fact]
    public async Task Taban_penceresi_saglayicinin_secimi_degil()
    {
        var query = new RecordingScopedQuery();

        await GatherAsync(new FirstSeenSignatureProvider(query));

        var asked = Assert.Single(query.CorrelationWindows);
        Assert.Equal(Now.AddDays(-7), asked.BaselineFrom);
        Assert.Equal(Now.AddMinutes(-30), asked.BaselineTo);
        Assert.Equal(["network/core"], asked.OwnerGroups);
    }

    // ---- 2 · hacim sapması ------------------------------------------------

    /// <summary>
    /// <b>Kabul kriteri:</b> hacim sapması gerçek sayılar üzerinde, örnekleme
    /// düzeltmesi <b>yok</b> — çünkü örnekleme yok (T29).
    ///
    /// <para>
    /// Taban 7 gün <b>eksi 30 dk</b> = 10.050 dk (pencereyle örtüşmemesi için).
    /// 700 olay → 45 dakikada beklenen 700 × 45/10050 = 3,134. Gözlenen 30 →
    /// z ≈ (30−3,134)/√3,134 ≈ 15,2. Eşiğin (3) çok üstünde.
    /// </para>
    ///
    /// <para>
    /// Beklenen değeri elle hesaplarken tabanı 7 tam gün sanmıştım ve test
    /// kırmızı yandı. Kaymanın kaynağı kod değil, örtüşmeyi engelleyen 30
    /// dakikalık boşluktu — testin sayıyı sabitlemesinin sebebi tam olarak bu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Hacim_sapmasi_beklenenin_uzerini_yakaliyor()
    {
        var query = new RecordingScopedQuery();
        query.Volumes.Add(new SignatureVolume(7, 30, 700, "deny tcp"));

        var slice = await GatherAsync(new VolumeDeviationProvider(query));

        var item = Assert.Single(slice.Items);
        Assert.Equal(15.2, double.Parse(item.Payload["z_score"], System.Globalization.CultureInfo.InvariantCulture), 1);
        Assert.Equal("3.134", item.Payload["expected"]);
    }

    /// <summary>
    /// Normal seyreden imza kanıt üretmiyor: beklenen ≈ gözlenen, z ≈ 0.
    /// Bekçinin kırmızı yanabildiğinin karşılığı — her satırı kanıt sayan bir
    /// eşik hiçbir şey söylemez.
    /// </summary>
    [Fact]
    public async Task Normal_seyreden_imza_kanit_uretmiyor()
    {
        var query = new RecordingScopedQuery();

        // 7 günde 10.080 olay → 45 dakikada beklenen 45; gözlenen 45.
        query.Volumes.Add(new SignatureVolume(7, 45, 10_080, "accept tcp"));

        var slice = await GatherAsync(new VolumeDeviationProvider(query));

        Assert.Equal(EvidenceStatus.Empty, slice.Status);
    }

    /// <summary>
    /// Küçük sayılar elenmiyor olsaydı: beklenen 0,2 iken gözlenen 2 olması
    /// z ≈ 4 verir ve eşiği geçer, ama söylediği bir şey yoktur.
    /// </summary>
    [Fact]
    public async Task Kucuk_sayilar_sahte_sinyal_uretmiyor()
    {
        var query = new RecordingScopedQuery();
        query.Volumes.Add(new SignatureVolume(7, 2, 45, "nadir satır"));

        var slice = await GatherAsync(new VolumeDeviationProvider(query));

        Assert.Empty(slice.Items);
    }

    /// <summary>
    /// Tabanda hiç görülmemiş imza burada <b>susuyor</b>: onu ilk-görülen
    /// sinyali çok daha iyi anlatıyor ve "sonsuz z-score" sıralamayı bozardı.
    /// </summary>
    [Fact]
    public async Task Tabanda_olmayan_imza_hacim_sinyali_uretmiyor()
    {
        var query = new RecordingScopedQuery();
        query.Volumes.Add(new SignatureVolume(7, 500, 0, "yepyeni satır"));

        var slice = await GatherAsync(new VolumeDeviationProvider(query));

        Assert.Empty(slice.Items);
    }

    // ---- 3 · sessizlik ----------------------------------------------------

    /// <summary>
    /// Tabanda düzenli gönderip pencerede susan kaynak yakalanıyor. Ağ
    /// tarafında kritik: çöken cihaz log göndermez.
    /// </summary>
    [Fact]
    public async Task Susan_kaynak_yakalaniyor()
    {
        var query = new SilenceScopedQuery
        {
            Baseline =
            [
                new SourceActivityRow("network/core", "rtr-quiet", Now.AddHours(-2), Now.AddHours(-2), 100_000),
                new SourceActivityRow("network/core", "rtr-loud", Now.AddMinutes(1), Now.AddMinutes(1), 100_000),
            ],
            Current =
            [
                new SourceActivityRow("network/core", "rtr-loud", Now.AddMinutes(1), Now.AddMinutes(1), 400),
            ],
        };

        var slice = await GatherAsync(new SilenceProvider(query));

        var item = Assert.Single(slice.Items);
        Assert.Equal("rtr-quiet", item.Payload["source_id"]);

        // Kanıtın zamanı son **haber alınan** an — cihazın kendi saati değil.
        Assert.Equal(Now.AddHours(-2), item.Timestamp);
    }

    /// <summary>
    /// Seyrek kaynak susunca alarm üretmiyor. Bu eşik olmadan sinyal yanlış
    /// alarm makinesi olurdu.
    /// </summary>
    [Fact]
    public async Task Seyrek_kaynak_susunca_kanit_uretmiyor()
    {
        var query = new SilenceScopedQuery
        {
            // 7 günde 10 olay → 45 dakikalık pencerede beklenen ~0,045.
            Baseline = [new SourceActivityRow("network/core", "rtr-rare", Now.AddDays(-1), Now.AddDays(-1), 10)],
            Current = [],
        };

        var slice = await GatherAsync(new SilenceProvider(query));

        Assert.Equal(EvidenceStatus.Empty, slice.Status);
        Assert.NotEmpty(slice.Detail);
    }

    /// <summary>
    /// <b>Üçüncü kopya yazılmadı:</b> sessizlik T21'in
    /// <c>GetSourceActivityAsync</c> yüzeyini kullanıyor ve iki pencere için iki
    /// kez çağırıyor. Kendi SQL'ini yazsaydı üç farklı zaman kolonu seçimi ve üç
    /// farklı kapsam davranışı doğardı.
    /// </summary>
    [Fact]
    public async Task Sessizlik_ortak_yuzeyi_kullaniyor()
    {
        var query = new SilenceScopedQuery { Baseline = [], Current = [] };

        await GatherAsync(new SilenceProvider(query));

        Assert.Equal(2, query.ActivityWindows.Count);
        Assert.Equal(Now.AddDays(-7), query.ActivityWindows[0].From);
        Assert.Equal(Now, query.ActivityWindows[1].From);
    }

    // ---- 4 · ortak öznitelik (lift) ---------------------------------------

    /// <summary>
    /// "Hepsi aynı switch'in arkasında" — topoloji olmadan topoloji sezgisi.
    ///
    /// <para>
    /// Pencerede <c>core-sw-02</c> 80/100, tabanda 100/1000 → lift 8×.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Ortak_oznitelik_yogunlasmayi_yakaliyor()
    {
        var query = new RecordingScopedQuery();
        query.LiftRows.Add(new FieldValueCount("host", "core-sw-02", 80, 100));
        query.LiftRows.Add(new FieldValueCount("host", "edge-rtr-07", 20, 900));

        var slice = await GatherAsync(new AttributeLiftProvider(query));

        var item = Assert.Single(slice.Items);
        Assert.Equal("core-sw-02", item.Payload["value"]);
        Assert.Equal(8.0, double.Parse(item.Payload["lift"], System.Globalization.CultureInfo.InvariantCulture), 3);
    }

    /// <summary>
    /// Sorulan alanlar depolama tarafındaki <b>izin listesiyle aynı</b>.
    /// Ayrışırlarsa sorgu istisna fırlatıyor (sessizce atlamıyor) — ama o
    /// yalnızca canlı veritabanında görünürdü, o yüzden burada da sabitlendi.
    /// </summary>
    [Fact]
    public async Task Sorulan_alanlar_izin_listesiyle_ayni()
    {
        var query = new RecordingScopedQuery();

        await GatherAsync(new AttributeLiftProvider(query));

        Assert.Equal(CorrelationReader.LiftFields.Order(), query.LiftFieldsAsked.Order());
    }

    // ---- 5 · yayılma sırası ----------------------------------------------

    /// <summary>
    /// İlk bozulan çoğu zaman kök nedene en yakın olandır; sıra ve gecikmeler
    /// kanıta çıkıyor.
    /// </summary>
    [Fact]
    public async Task Yayilma_sirasi_gecikmeleriyle_cikiyor()
    {
        var query = new RecordingScopedQuery();
        query.Onsets.Add(new SourceOnset("network/core", "edge-rtr-07", Now.AddMinutes(2), 12, 0));
        query.Onsets.Add(new SourceOnset("network/core", "edge-rtr-09", Now.AddMinutes(3), 4, 0));

        var slice = await GatherAsync(new PropagationProvider(query));

        Assert.Equal(2, slice.Items.Count);
        Assert.Equal("edge-rtr-07", slice.Items[0].Payload["source_id"]);
        Assert.Equal("0", slice.Items[0].Payload["lag_seconds"]);
        Assert.Equal("60", slice.Items[1].Payload["lag_seconds"]);

        // Erken bozulan daha ağır.
        Assert.True(slice.Items[0].Weight > slice.Items[1].Weight);
    }

    /// <summary>
    /// <b>Kabul kriteri:</b> penceresinde <c>time_source != parsed</c> olan
    /// olay varsa çıktı bunu <b>bildiriyor</b>.
    ///
    /// <para>
    /// Bu sinyalin tamamı sıralama, ve gözlem zamanına düşmüş bir olayın gerçek
    /// zamanı dakikalarca önce olabilir. Sıralamayı sunup zamanın güvenilmez
    /// olduğunu söylememek, ölçülmemiş bir kesinlik iddia etmek olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Guvenilmez_zamanli_olay_varsa_bildiriliyor()
    {
        var query = new RecordingScopedQuery();
        query.Onsets.Add(new SourceOnset("network/core", "edge-rtr-07", Now.AddMinutes(2), 12, 5));

        var slice = await GatherAsync(new PropagationProvider(query));

        Assert.Contains("time_source", slice.Detail, StringComparison.Ordinal);
        Assert.Contains("5", slice.Detail, StringComparison.Ordinal);
        Assert.Equal("5", Assert.Single(slice.Items).Payload["unreliable_time_count"]);
    }

    /// <summary>
    /// Zamanların hepsi güvenilirse uyarı <b>yok</b> — yukarıdaki bekçinin
    /// kazara yanmadığının kanıtı. Her raporda duran bir uyarı hiçbir şey
    /// söylemez.
    /// </summary>
    [Fact]
    public async Task Zamanlar_guvenilirse_uyari_yok()
    {
        var query = new RecordingScopedQuery();
        query.Onsets.Add(new SourceOnset("network/core", "edge-rtr-07", Now.AddMinutes(2), 12, 0));

        var slice = await GatherAsync(new PropagationProvider(query));

        Assert.DoesNotContain("time_source", slice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Yayilmaya_gecirilen_onem_esigi_yazili()
    {
        var query = new RecordingScopedQuery();

        await GatherAsync(new PropagationProvider(query));

        Assert.Equal(3, query.SeverityAsked);
    }

    // ---- beşi birden ------------------------------------------------------

    /// <summary>
    /// Bütçe tavanı beşinde de <b>söyleniyor</b>. Sessizce kırpılmış bir kanıt
    /// listesi "hepsi bu" diye okunur.
    /// </summary>
    [Fact]
    public async Task Butce_asimi_bes_sinyalde_de_soyleniyor()
    {
        var query = new RecordingScopedQuery();
        for (var index = 0; index < 5; index++)
        {
            query.FirstSeen.Add(new SignatureCount((ulong)index + 1, 10, Now, 2, "x"));
            query.Onsets.Add(new SourceOnset("g", $"s-{index}", Now.AddSeconds(index), 3, 0));
        }

        var budget = new GatherBudget(2, TimeSpan.FromSeconds(5));

        var firstSeen = await GatherAsync(new FirstSeenSignatureProvider(query), budget);
        var propagation = await GatherAsync(new PropagationProvider(query), budget);

        Assert.True(firstSeen.Truncated);
        Assert.Equal(2, firstSeen.Items.Count);
        Assert.NotEmpty(firstSeen.Detail);

        Assert.True(propagation.Truncated);
        Assert.Equal(2, propagation.Items.Count);
    }

    /// <summary>
    /// Sessizliğin iki farklı pencere için iki çağrı yapması gerektiğinden
    /// kendi sahtesi var; <see cref="RecordingScopedQuery"/> tek liste
    /// döndürüyor.
    /// </summary>
    private sealed class SilenceScopedQuery : RecordingScopedQuery
    {
        public required IReadOnlyList<SourceActivityRow> Baseline { get; init; }

        public required IReadOnlyList<SourceActivityRow> Current { get; init; }

        public List<SourceActivityWindow> ActivityWindows { get; } = [];

        public override Task<IReadOnlyList<SourceActivityRow>> GetSourceActivityAsync(
            SourceActivityWindow window, AccessScope scope, CancellationToken cancellationToken = default)
        {
            ActivityWindows.Add(window);
            return Task.FromResult(ActivityWindows.Count == 1 ? Baseline : Current);
        }
    }
}
