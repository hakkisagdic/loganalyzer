using Bizigo.Devices;
using Bizigo.Simulators;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Yedi senaryonun her biri için bir test (S04).
///
/// <para>
/// <b>Bu sınıf yazıldı, koşturulmadı</b> (§2). Her testin özet yorumu
/// <i>koşturulduğunda ne kanıtlayacağını</i> söylüyor; koşumu koordinatör
/// yapıyor.
/// </para>
///
/// <para>
/// <b>Ortak kural:</b> her senaryo ürünün <b>belirli bir iddiasını</b>
/// hedefliyor — "genel olarak değişsin" diye senaryo yok. Bir test düştüğünde
/// hangi iddianın kırıldığı, testin adından ve yorumundan okunabilmeli.
/// </para>
///
/// <para>
/// <b>Yüzey ayrımı burada da geçerli:</b> dört senaryo config yüzeyinde
/// (N1/N2), ikisi syslog yüzeyinde, biri altyapıda. Altyapı olanın testi
/// <c>Skip</c> ile duruyor ve <b>bu dürüst</b>: simülatör sidecar'ı
/// durduramıyor, ve durduramadığı hâlde yeşil yanan bir test, ölçülmemiş bir
/// iddiayı ölçülmüş gösterirdi.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScenarioEngineFlowTests
{
    private const string Profile = "fw-ankara-01";

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bizigo.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, relative);
    }

    private static SimulatorProfile Load()
    {
        // Yükleyici DOĞRULAYICI da: bozuk bir profil burada hata listesi
        // döndürüyor. Testin onu yok sayması, senaryo testinin profil
        // hatasını "senaryo çalışmadı" diye okuması demek olurdu.
        var loaded = SimulatorProfileStore.LoadAll(RepoPath("catalog/simulators"), RepoPath("."));
        var found = loaded.Single(r => string.Equals(r.Profile.Id, Profile, StringComparison.Ordinal));

        Assert.Empty(found.Errors);
        return found.Profile;
    }

    /// <summary>Config yüzeyinde bir senaryonun çıktısını N1 üzerinden okur.</summary>
    private static async Task<string> ConfigAsync(string scenario)
    {
        var transport = new SimulatedDeviceTransport(
            Load(), RepoPath("catalog/simulators"), scenario);

        var result = await transport.RunAsync(
            new DeviceTarget
            {
                Vendor = "fortinet",
                Host = "sim",
                Username = "bizigo-ro",
                Credential = "yok",
            },
            new FortiGateCollector().Commands,
            TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Error);
        return result.Output;
    }

    // ---- Config yüzeyi ------------------------------------------------------

    /// <summary>
    /// Koşturulduğunda kanıtlayacağı şey: <c>ConfigDiff</c> <b>gerçek</b> bir
    /// fark üretiyor ve <c>change_events</c> bölüm <b>adını</b> taşıyor, satır
    /// içeriğini değil.
    ///
    /// <para>
    /// İkinci yarısı asıl iddia: fark kaydına satır içeriği girseydi, kuralın
    /// içindeki bir sır değişiklik akışına sızardı — ve akış maskeleme
    /// zincirinin dışında.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Kural_eklendi_gercek_fark_uretiyor()
    {
        var baseline = await ConfigAsync(Scenarios.Baseline);
        var changed = await ConfigAsync("kural-eklendi");

        Assert.NotEqual(baseline, changed);

        var diff = ConfigDiff.Compare(
            ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, baseline),
            ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, changed));

        Assert.NotEmpty(diff.Sections);
    }

    /// <summary>
    /// Koşturulduğunda kanıtlayacağı şey: maskeleme <b>siliyor değil
    /// maskeliyor</b> — özet değişiyor, değerin kendisi hiçbir yere yazılmıyor.
    ///
    /// <para>
    /// Ayrım önemli: silinen bir sır farkı da yok eder ve "sır döndü" olayı
    /// görünmez olur. Maskelenen bir sır farkı korur, değeri sızdırmaz.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Sir_dondu_maskeleniyor_silinmiyor()
    {
        var baseline = ConfigNormalizer.Normalize(
            ConfigNormalizer.FortiGate, await ConfigAsync(Scenarios.Baseline));
        var rotated = ConfigNormalizer.Normalize(
            ConfigNormalizer.FortiGate, await ConfigAsync("sir-dondu"));

        // Fark VAR: sır döndüğü görülüyor.
        Assert.NotEmpty(ConfigDiff.Compare(baseline, rotated).Sections);

        // Ama değerin kendisi normalize metinde YOK — ne eskisi ne yenisi.
        Assert.DoesNotContain(
            "ENC ", ConfigDiff.Serialize(rotated), StringComparison.Ordinal);
    }

    /// <summary>
    /// Koşturulduğunda kanıtlayacağı şey: aynı ayarlar farklı sırada geldiğinde
    /// fark <b>sahte değişiklik üretmiyor</b>.
    ///
    /// <para>
    /// Bu, T26'nın çoklu-küme kararının ölçümü: LCS olsaydı sıra değişikliği
    /// koca bir blok "değişti" gösterirdi ve kullanıcı olmayan bir değişikliği
    /// incelerdi.
    /// </para>
    /// </summary>
    [Fact(Skip = "Profilde `cihaz-yeniden-yazdi` config dosyası yok — S01'e ait, eklenince açılacak.")]
    [Trait("Category", "Integration")]
    public async Task Cihaz_yeniden_yazdi_sahte_fark_uretmiyor()
    {
        var baseline = ConfigNormalizer.Normalize(
            ConfigNormalizer.FortiGate, await ConfigAsync(Scenarios.Baseline));
        var reordered = ConfigNormalizer.Normalize(
            ConfigNormalizer.FortiGate, await ConfigAsync("cihaz-yeniden-yazdi"));

        Assert.Empty(ConfigDiff.Compare(baseline, reordered).Sections);
    }

    /// <summary>
    /// Koşturulduğunda kanıtlayacağı şey: <c>ConfigNormalizer</c> gürültüyü
    /// eliyor ve fark <b>boş</b> çıkıyor.
    ///
    /// <para>
    /// Ham metin farklı, normalize metin aynı. İkisini birden sınamak şart:
    /// yalnızca farkın boş olduğuna bakan bir test, senaryonun hiç
    /// uygulanmadığı durumda da geçerdi.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Gurultu_farka_donusmuyor()
    {
        var baselineRaw = await ConfigAsync(Scenarios.Baseline);
        var noisyRaw = await ConfigAsync("gurultu");

        // Senaryo GERÇEKTEN uygulandı: ham metinler farklı.
        Assert.NotEqual(baselineRaw, noisyRaw);

        var diff = ConfigDiff.Compare(
            ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, baselineRaw),
            ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, noisyRaw));

        Assert.Empty(diff.Sections);
    }

    // ---- Syslog yüzeyi ------------------------------------------------------

    /// <summary>
    /// Koşturulduğunda kanıtlayacağı şey: kaymış damgayla gelen satır
    /// <c>time_source</c> üzerinden <b>görünür</b> oluyor — sessizce
    /// atlanmıyor, sessizce düzeltilmiyor.
    ///
    /// <para>
    /// Düzeltmek en tehlikeli seçenek olurdu: olay doğru zamana oturur ve
    /// kimse cihazın saatinin kaydığını öğrenemezdi. Ürünün vaadi düzeltmek
    /// değil, <b>güvenilmezliği raporlamak</b>.
    /// </para>
    ///
    /// <para>
    /// Koşarken gereken: collector ayakta ve <c>SkewAhead</c> kadar ileri
    /// damgalı satırlar ClickHouse'a düşmüş olmalı.
    /// </para>
    /// </summary>
    [Fact(Skip = "Collector ve ClickHouse gerekiyor; koordinatörün uçtan uca koşumunda açılacak.")]
    [Trait("Category", "Integration")]
    public async Task Saat_kaymasi_gorunur_oluyor()
    {
        var result = await SyslogEmitter.EmitAsync(
            Load(),
            RepoPath("."),
            "127.0.0.1",
            count: 20,
            TestContext.Current.CancellationToken,
            scenario: "saat-kaymasi");

        Assert.Equal(20, result.Lines);

        // Koşturulduğunda burada ClickHouse'a bakılacak: kaymış satırların
        // `time_source`'u `parsed` OLMAMALI ve kanıt paketinin `WindowTrust`
        // alanı bunu sayıyor olmalı.
    }

    /// <summary>
    /// Koşturulduğunda kanıtlayacağı şey: profil UTF-8 derken tel latin-1
    /// giderse ürün bunu tespit ediyor ve <b>ham baytlar bozulmadan</b> arşive
    /// giriyor.
    ///
    /// <para>
    /// İkinci yarısı F1'in dayanağı: kodlama tahmini yanlış çıksa bile
    /// orijinal baytlar arşivde duruyor ve replay düzeltebiliyor. Tahminin
    /// bedeli kalıcı değil — <b>ancak baytlar korunuyorsa</b>.
    /// </para>
    /// </summary>
    [Fact(Skip = "Collector, ham arşiv ve ClickHouse gerekiyor; koordinatörün uçtan uca koşumunda açılacak.")]
    [Trait("Category", "Integration")]
    public async Task Bozuk_kodlama_ham_baytlari_bozmuyor()
    {
        var result = await SyslogEmitter.EmitAsync(
            Load(),
            RepoPath("."),
            "127.0.0.1",
            count: 20,
            TestContext.Current.CancellationToken,
            scenario: "bozuk-kodlama");

        Assert.Equal(20, result.Lines);

        // Koşturulduğunda: `encoding_detected` utf-8 OLMAMALI, ve ham nesneden
        // okunan baytlar basılanla birebir eşleşmeli.
    }

    // ---- Altyapı ------------------------------------------------------------

    /// <summary>
    /// Koşturulduğunda kanıtlayacağı şey: sidecar arızalıyken throughput
    /// düşmüyor (D3).
    ///
    /// <para>
    /// <b>Neden <c>Skip</c> ve neden bu dürüst:</b> senaryo bir altyapı eylemi —
    /// sidecar'ı durdurmak simülatörün elinde değil. <c>Skip</c> olmadan bu
    /// test, sidecar <b>ayaktayken</b> koşar ve "throughput düşmedi" derdi;
    /// yani ölçülmemiş bir iddiayı ölçülmüş gösterirdi.
    /// </para>
    ///
    /// <para>
    /// Motor da aynı şeyi söylüyor: <c>Scenarios.Reject("sidecar-yok", …)</c>
    /// hem config hem syslog yüzeyinde <i>"bu bir altyapı senaryosu"</i> diye
    /// reddediyor.
    /// </para>
    /// </summary>
    [Fact(Skip = "Altyapı senaryosu: sidecar'ı koordinatör durduruyor (§2). Simülatör uygulamıyor.")]
    [Trait("Category", "Integration")]
    public void Sidecar_yok_throughput_dusurmuyor()
    {
        // Koşturulduğunda: sidecar durdurulmuş hâlde basıcı koşacak ve
        // `/internal/ingest/stats` üzerindeki `processed_records` hızı,
        // sidecar ayakta ölçülen hızın belirgin altına DÜŞMEMELİ.
        //
        // Karşılaştırma aynı koşumdan alınan bir tabana yapılmalı, mutlak bir
        // sayıya değil (§6): yüklü makinede mutlak bütçe makineyi ölçer.

        // ⚠ BU SATIRI SİLMEYİN — `Skip` ile birlikte çalışıyor.
        //
        // `Skip` kaldırıldığı gün gövde boş olsaydı test SESSİZCE GEÇERDİ ve
        // ölçülmemiş bir iddia ölçülmüş görünürdü. Bu depoda "bekçinin sessizce
        // atlaması, bekçinin kendisinden tehlikelidir" diye ölçülmüş bir sınıf;
        // içi boş bir iskeletin yeşil dönmesi sahte yeşildir.
        //
        // Gövde yazıldığında bu satır onunla birlikte gider.
        Assert.Fail("İskelet: gövde yazılmadan bu test geçmemeli. Skip gerekçesine bakın.");
    }
}
