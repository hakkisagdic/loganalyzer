using System.Text.Json;

using Bizigo.Api;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Rca.Reasoning;

using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// Üretilen belgenin kalıcılığı ve tel yüzeyi (T51).
///
/// <para>
/// <b>Karar 1'in ikinci yarısı burada tamamlanıyor.</b> T44 sayacı üretti ama
/// kimse göremiyordu: <i>"atıldığı sayılıyor ve <b>gösteriliyor</b>"</i>
/// cümlesinin ikinci fiili bir depolama değil bir <b>görünürlük</b> kararı, ve
/// yalnızca ölçüp saklamak <i>"ölçemedim"</i> ile <i>"sorun yok"</i>u yine aynı
/// çıktıya indirirdi.
/// </para>
/// </summary>
public sealed class RcaReportPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid BundleId = Guid.Parse("01920000-0000-7000-8000-0000000000aa");

    /// <summary>
    /// Üç sayının üçü de <b>farklı</b>: eşit olsalardı bir alanın diğerinin
    /// yerine yazıldığı fark edilmezdi.
    /// </summary>
    private static RcaReportDocument Document(
        int produced = 12,
        int dropped = 3,
        int fabricated = 1,
        IReadOnlyList<RcaReportFinding>? findings = null) => new()
        {
            BundleId = BundleId,
            ScenarioId = "builtin.rca.network",
            ScenarioVersion = "1.0.0",
            Findings = findings ??
            [
                new RcaReportFinding("ACL değişikliği BGP'yi düşürdü [EV-01].", ["EV-01"], ["EV-14"]),
            ],
            Actions = [new RcaReportAction("Değişikliği geri al [EV-01].", ["EV-01"])],
            ProducedSentenceCount = produced,
            DroppedSentenceCount = dropped,
            FabricatedCitationSentenceCount = fabricated,
            SentenceGateSkipped =
            [
                new SentenceGateStatus("rank-hypotheses", SentenceGateOutcome.NotApplicable, "ara adım: a:b içeriyor"),
            ],
            ModelInfo = new RcaReportModelInfo("vllm-kurum", "qwen3-32b", 18_400, 1_200, 0),
        };

    private static EvidenceBundle Bundle() => new()
    {
        Id = BundleId,
        GatheredAt = Now,
        Window = new RcaWindow
        {
            From = Now.AddMinutes(-30),
            To = Now,
            BaselineFrom = Now.AddDays(-7),
            BaselineTo = Now.AddMinutes(-30),
        },
        Scope = new BundleScope(["network/core"], IsSystem: false),
        Trust = new WindowTrust(1_204, 0),
        Slices = [],
    };

    private static RcaReportStore Store(InMemoryControlPlaneFactory factory) =>
        new(factory, new FakeTimeProvider(Now));

    // ------------------------------------------------------------ depolama ---

    /// <summary>
    /// Yazıp geri okumak <b>aynı belgeyi</b> veriyor. Bu bir tekrar değil bir
    /// dondurma: bugün yazılan bir rapor altı ay sonra bugünkü kodla okunacak
    /// ve F4'ün <i>"aynı kanıt, farklı model"</i> karşılaştırmasının tamamı
    /// buna dayanıyor.
    /// </summary>
    [Fact]
    public async Task Yazilan_belge_ayni_hâliyle_geri_okunuyor()
    {
        using var factory = new InMemoryControlPlaneFactory();
        var store = Store(factory);
        var ct = TestContext.Current.CancellationToken;

        var id = await store.SaveAsync(Document(), ct);
        var stored = await store.LatestForAsync(Bundle(), ct);

        Assert.NotNull(stored);
        Assert.Equal(id, stored.Id);
        Assert.Equal(Now, stored.CreatedAt);

        // KARŞILAŞTIRMA SERİLEŞTİRİLMİŞ HÂL ÜZERİNDEN, `Assert.Equal(record,
        // record)` ile DEĞİL — ve bu bir kolaylık değil bir düzeltme.
        //
        // Ölçüldü: kayıt tipinin ürettiği `==`, `IReadOnlyList` üyelerinde
        // değer eşitliği DEĞİL referans eşitliği yapıyor. Yani `Findings`,
        // `Actions` ve `SentenceGateSkipped` iki farklı liste örneği olduğu
        // için karşılaştırma her zaman düşüyor — ve tersi daha tehlikeliydi:
        // yalnızca skaler alanları karşılaştıran bir test, listelerin bozuk
        // döndüğü bir turda yeşil kalırdı.
        //
        // Serileştirilmiş biçim her alanı, iç içe listeler dahil, kapsıyor.
        Assert.Equal(
            ReasoningSerializer.Serialize(Document()),
            ReasoningSerializer.Serialize(stored.Document));
    }

    /// <summary>
    /// <b>Belge ile üst veri kolonları ayrışmıyor.</b>
    ///
    /// <para>
    /// Kopya kolonların bedeli budur ve bu test onu ödüyor. <c>evidence_bundles</c>
    /// aynı kalıbı aynı gerekçeyle taşıyor: T47 altın küme üzerinde oran
    /// hesaplarken her satır için JSON açmamalı — ama kopya, bir gün sessizce
    /// ayrışabilecek ikinci bir gerçek demek.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Ust_veri_kolonlari_belgeyle_ayni_seyi_soyluyor()
    {
        using var factory = new InMemoryControlPlaneFactory();
        var ct = TestContext.Current.CancellationToken;
        var document = Document();

        // GERÇEK yazma yolundan geçiyor: iç bir yardımcıyı doğrudan çağırmak,
        // deponun o yardımcıyı gerçekten kullandığını ölçmezdi — ve ayrışma tam
        // olarak orada doğardı.
        await Store(factory).SaveAsync(document, ct);

        await using var db = factory.CreateDbContext();
        var entity = Assert.Single(db.RcaReports);

        Assert.Equal(document.ProducedSentenceCount, entity.ProducedSentenceCount);
        Assert.Equal(document.DroppedSentenceCount, entity.DroppedSentenceCount);
        Assert.Equal(document.FabricatedCitationSentenceCount, entity.FabricatedCitationSentenceCount);
        Assert.Equal(document.ScenarioId, entity.ScenarioId);
        Assert.Equal(document.ScenarioVersion, entity.ScenarioVersion);
        Assert.Equal(document.BundleId, entity.BundleId);

        // Ve belgenin kendisi de aynı sayıları taşıyor: kolonlar belgeden
        // türetildi, ikinci bir hesap yapılmadı.
        var roundTrip = ReasoningSerializer.Deserialize(entity.Payload);

        Assert.Equal(document.ProducedSentenceCount, roundTrip.ProducedSentenceCount);
        Assert.Equal(document.DroppedSentenceCount, roundTrip.DroppedSentenceCount);
        Assert.Equal(document.FabricatedCitationSentenceCount, roundTrip.FabricatedCitationSentenceCount);
    }

    /// <summary>
    /// Aynı paket üzerinde iki rapor meşru (RCA §3 — aynı kanıt, farklı model)
    /// ve <b>ikisi de saklanıyor</b>. Ekran son sözü alıyor.
    /// </summary>
    [Fact]
    public async Task Ayni_paketin_iki_raporu_da_saklaniyor()
    {
        using var factory = new InMemoryControlPlaneFactory();
        var time = new FakeTimeProvider(Now);
        var store = new RcaReportStore(factory, time);
        var ct = TestContext.Current.CancellationToken;

        await store.SaveAsync(Document(produced: 10, dropped: 1, fabricated: 0), ct);
        time.Advance(TimeSpan.FromMinutes(5));
        await store.SaveAsync(Document(produced: 12, dropped: 3, fabricated: 1), ct);

        var all = await store.AllForAsync(Bundle(), ct);
        var latest = await store.LatestForAsync(Bundle(), ct);

        Assert.Equal(2, all.Count);
        Assert.NotNull(latest);

        // Zaman SAHTE ve ilerletiliyor: "son" kararının duvar saatiyle ilgisi
        // olmamalı, yoksa iki kayıt aynı milisaniyeye düştüğünde test kararsız
        // olurdu (§6).
        Assert.Equal(12, latest.Document.ProducedSentenceCount);
    }

    /// <summary>
    /// Okunamayan sürüm <b>istisna fırlatıyor</b>, boş dönmüyor. "Rapor yok"
    /// ile "rapor var ama okuyamıyoruz" ayrı şeyler; ikincisini birincisi gibi
    /// göstermek T47'nin ölçümünü sessizce eksik kümeye indirir — yani ölçüm
    /// kendi körlüğünü iyi haber diye raporlar.
    /// </summary>
    [Fact]
    public async Task Okunamayan_surum_sessizce_bos_donmuyor()
    {
        using var factory = new InMemoryControlPlaneFactory();
        var ct = TestContext.Current.CancellationToken;

        await Store(factory).SaveAsync(Document(), ct);

        // Gelecekten gelmiş bir sürüm: bugünkü kodun okuyamayacağı bir kayıt.
        await using (var db = factory.CreateDbContext())
        {
            db.RcaReports.Single().SchemaVersion = ReasoningSerializer.CurrentSchemaVersion + 1;
            await db.SaveChangesAsync(ct);
        }

        var hata = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Store(factory).LatestForAsync(Bundle(), ct));

        Assert.Contains("sürüm", hata.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Bulgu sırası modelin sırası ve depolama boyunca KORUNUYOR.</b>
    ///
    /// <para>
    /// T47'nin <c>accuracy@1</c> / <c>accuracy@3</c> ekseni buna dayanıyor:
    /// inceleyen <i>"doğru olan kaçıncı bulgu"</i> diyecek ve o sayı bu listenin
    /// sırasına atıfta bulunuyor. Metin eşleştirmesi bilerek reddedildi — bu
    /// depoda <i>"SQL doğru, kolon doğru, dizge yanlış"</i> sınıfının dört
    /// örneği var — dolayısıyla bütün eksen sıranın güvenilirliğine dayanıyor.
    /// </para>
    ///
    /// <para>
    /// Sıra sessizce değişirse ölçü sessizce yanlış olur, ve yanlışlığı hiçbir
    /// şey haber vermez: sayı yine makul bir sayı olur.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bulgu_sirasi_depolama_boyunca_korunuyor()
    {
        using var factory = new InMemoryControlPlaneFactory();
        var ct = TestContext.Current.CancellationToken;

        // Sıra ALFABETİK DEĞİL ve bilerek: alfabetik bir liste, sıralayan bir
        // hatayı kendi başına gizlerdi.
        var sirali = Document(findings:
        [
            new RcaReportFinding("ucuncu-gelen [EV-03].", ["EV-03"], []),
            new RcaReportFinding("birinci-gelen [EV-01].", ["EV-01"], []),
            new RcaReportFinding("ikinci-gelen [EV-02].", ["EV-02"], []),
        ]);

        await Store(factory).SaveAsync(sirali, ct);
        var stored = await Store(factory).LatestForAsync(Bundle(), ct);

        Assert.NotNull(stored);
        Assert.Equal(
            ["ucuncu-gelen [EV-03].", "birinci-gelen [EV-01].", "ikinci-gelen [EV-02]."],
            stored.Document.Findings.Select(f => f.Hypothesis));

        // Ve telde de aynı sıra: serileştirme ile tel tipi arasında bir
        // yeniden sıralama olsaydı ekran doğru veriyi yanlış sırada çizerdi.
        Assert.Equal(
            ["ucuncu-gelen [EV-03].", "birinci-gelen [EV-01].", "ikinci-gelen [EV-02]."],
            Response(stored.Document).Findings.Select(f => f.Hypothesis));
    }

    // ------------------------------------------------------------- üç hâl ---

    /// <summary>
    /// <b>A · Model hiç koşmadı.</b> <c>reasoning</c> <see langword="null"/> —
    /// ve bu "bulgu yok" DEĞİL.
    /// </summary>
    [Fact]
    public void Model_hic_kosmadiysa_reasoning_null()
    {
        var response = RcaReportResponse.Of(
            DeterministicReport.From(Bundle()), review: null, reasoning: null);

        Assert.Null(response.Reasoning);
    }

    /// <summary>
    /// <b>C · Koştu, HER CÜMLESİ ATILDI</b> — üç hâlin en pahalısı.
    ///
    /// <para>
    /// Boş bir bulgu listesi ekranda <i>"bulgu yok"</i> diye okunursa, modelin
    /// beş cümle uydurup hepsinin elendiği gerçeği kaybolur — ve F4'ün ölçmek
    /// istediği tam olarak o. Telde ayrım <b>sayılarda</b> duruyor: boş liste
    /// artı sıfırdan büyük bir <c>dropped</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void Her_cumlesi_atilan_rapor_bos_rapordan_ayirt_edilebiliyor()
    {
        var hepsiAtildi = Response(Document(produced: 5, dropped: 5, fabricated: 2, findings: []));
        var hicUretmedi = Response(Document(produced: 0, dropped: 0, fabricated: 0, findings: []));

        Assert.NotNull(hepsiAtildi);
        Assert.NotNull(hicUretmedi);

        // İkisinin de bulgu listesi BOŞ — ayrımı taşıyan şey liste değil.
        Assert.Empty(hepsiAtildi.Findings);
        Assert.Empty(hicUretmedi.Findings);

        Assert.Equal(5, hepsiAtildi.DroppedSentenceCount);
        Assert.Equal(0, hicUretmedi.DroppedSentenceCount);

        // Ve oran: biri ölçüldü, diğeri ÖLÇÜLEMEDİ.
        Assert.Equal(1.0, hepsiAtildi.DroppedSentenceRatio);
        Assert.Null(hicUretmedi.DroppedSentenceRatio);
    }

    /// <summary>
    /// <b>Payda sıfırken oran <see langword="null"/>, <c>0</c> değil.</b>
    ///
    /// <para>
    /// <c>0.0</c> bu alanda <i>"hiç cümle atılmadı"</i> demek — yani mükemmel
    /// kalite. Ölçülemeyen bir oranın en iyi sonuçla aynı baytları üretmesi,
    /// bu deponun defalarca adını koyduğu sınıfın kendisi olurdu. Karşılığı
    /// T47'de somut: <see langword="null"/> paydadan düşülebiliyor, <c>0.0</c>
    /// düşülemez.
    /// </para>
    /// </summary>
    [Fact]
    public void Payda_sifirken_oran_null_sifir_degil()
    {
        var olculemedi = Response(Document(produced: 0, dropped: 0, fabricated: 0, findings: []));
        var mukemmel = Response(Document(produced: 8, dropped: 0, fabricated: 0));

        Assert.Null(olculemedi.DroppedSentenceRatio);
        Assert.Equal(0.0, mukemmel.DroppedSentenceRatio);

        // İkisi telde de ayrı: `null` ile `0.0` aynı baytları üretmiyor.
        var olculemediJson = JsonSerializer.Serialize(olculemedi);
        var mukemmelJson = JsonSerializer.Serialize(mukemmel);

        Assert.Contains("\"dropped_sentence_ratio\":null", olculemediJson, StringComparison.Ordinal);
        Assert.Contains("\"dropped_sentence_ratio\":0", mukemmelJson, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- tel ---

    /// <summary>
    /// Atlanan kapı <b>yapısal</b>: <c>step_id</c> ayrı bir alan.
    ///
    /// <para>
    /// Dizge olsaydı onu okuyacak ilk kod iki nokta üstüne bölerdi ve
    /// <c>detail</c> içinde iki nokta geçtiği gün sessizce yanlış ayrışırdı —
    /// bu depoda adı konmuş sınıf. Fixture'ın <c>detail</c>'i bilerek iki nokta
    /// taşıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Atlanan_kapi_yapisal_dizge_ayristirmasi_gerekmiyor()
    {
        var gate = Assert.Single(Response(Document()).SentenceGateSkipped);

        Assert.Equal("rank-hypotheses", gate.StepId);
        Assert.Equal("not_applicable", gate.Reason);
        Assert.Contains("a:b", gate.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tel adları <c>snake_case</c> (§8). <c>camelCase</c> politikası bu depoda
    /// <c>idp_groups</c>'u bir kez sessizce kırdı.
    /// </summary>
    [Fact]
    public void Tel_adlari_snake_case()
    {
        var json = JsonSerializer.Serialize(Response(Document()));

        foreach (var field in new[]
        {
            "report_id", "bundle_id", "created_at", "scenario_id", "scenario_version",
            "produced_sentence_count", "dropped_sentence_count", "dropped_sentence_ratio",
            "fabricated_citation_sentence_count", "sentence_gate_skipped",
            "evidence_ids", "contradicting_evidence_ids",
            "prompt_tokens", "unreported_attempts", "tokens_complete",
        })
        {
            Assert.Contains($"\"{field}\"", json, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>Bildirilmemiş belirteç telde de <see langword="null"/></b>, ve toplamın
    /// eksik olduğu ayrıca söyleniyor. Kısmi bir toplam bir <b>alt sınır</b>;
    /// bunu söylemeyen bir sayı tam sanılır.
    /// </summary>
    [Fact]
    public void Bildirilmemis_belirtec_telde_de_null()
    {
        var document = Document() with
        {
            ModelInfo = new RcaReportModelInfo("yerel", "qwen3-8b", null, null, UnreportedAttempts: 2),
        };

        var response = Response(document);
        var json = JsonSerializer.Serialize(response);

        Assert.Null(response.Model.PromptTokens);
        Assert.False(response.Model.TokensComplete);
        Assert.Equal(2, response.Model.UnreportedAttempts);
        Assert.Contains("\"prompt_tokens\":null", json, StringComparison.Ordinal);
    }

    private static RcaReasoningResponse Response(RcaReportDocument document) =>
        RcaReasoningResponse.Of(new StoredRcaReport(
            Guid.Parse("01920000-0000-7000-8000-0000000000bb"), Now, document));
}
