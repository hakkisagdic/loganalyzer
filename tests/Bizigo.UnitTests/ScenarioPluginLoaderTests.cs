using Bizigo.ScenarioPlugin;

namespace Bizigo.UnitTests;

/// <summary>
/// Senaryo plugin çekirdeğinin <b>yükleme anı</b> kapısı (T43).
///
/// <para>
/// Bu sınıfın neredeyse her testi bir <b>ret</b> sınıyor, ve sebebi §3.2'nin
/// bulgusu: bugünkü formatta <c>constraint</c> satırı yazılmazsa senaryo
/// <b>sessizce geçerli</b> sayılıyordu. Yani format, tek gerçek güvencesinin
/// uygulandığı senaryoyu uygulanmadığından ayırt edemiyordu — ve geçen bir test
/// bu boşluğu hiç göstermezdi.
/// </para>
///
/// <para>
/// Kabul eden iki test var (<see cref="Iskelet_yukleniyor"/> ve
/// <see cref="Referans_senaryo_yukleniyor"/>) ve işleri kapının <b>yanlış
/// yere</b> kapanmadığını göstermek: iskelet bozuk olsaydı bütün ret testleri
/// kendi bozdukları yere değil iskelete borçlu olurdu.
/// </para>
/// </summary>
public sealed class ScenarioPluginLoaderTests
{
    /// <summary>Üretimde kayıtlı olan yedi kanıt sağlayıcısı.</summary>
    private static readonly IScenarioProviderRegistry Registry = new ScenarioProviderRegistry(
    [
        "logs.window", "logs.first-seen", "logs.volume", "logs.silence",
        "logs.attribute-lift", "logs.propagation", "change.feed",
    ]);

    /// <summary>
    /// Geçerli bir iskelet. Testler tek bir parçasını bozup <b>o parçanın</b>
    /// kapıyı kırmızıya çevirdiğini ölçüyor; iki şey aynı anda bozulursa
    /// hangisinin yakalandığı bilinmez.
    /// </summary>
    private const string Valid = """
        apiVersion: bizigo.dev/v1
        kind: Scenario
        metadata:
          id: test.scenario
          version: 1.0.0
          owner: platform-team
        spec:
          trigger:
            on: [manual]
          evidence:
            providers: [logs.volume]
            window: { lead: 30m }
          steps:
            - id: first-step
              task: "Tek iş yap."
              input: evidence.items
              output:
                schema: some_list
                constraints: [evidence_ids_must_exist]
          publish:
            requires_review: true
        """;

    private const string ConstraintLine = "constraints: [evidence_ids_must_exist]";

    private static ScenarioLoadResult Load(string yaml) =>
        ScenarioYamlLoader.Load(yaml, Registry, "test.yaml");

    private static ScenarioLoadResult LoadReplacing(string from, string to)
    {
        Assert.Contains(from, Valid, StringComparison.Ordinal);
        return Load(Valid.Replace(from, to, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ taban

    [Fact]
    public void Iskelet_yukleniyor()
    {
        var result = Load(Valid);

        Assert.True(result.Ok, result.Describe());
        Assert.Equal("test.scenario", result.Value.Metadata.Id);
        Assert.Single(result.Value.Steps);
        Assert.False(result.Value.Steps[0].Output.IsWaived);
        Assert.Empty(result.Value.WaivedStepKeys);
    }

    // ------------------------------------------- §3.2 · kısıt ya da gerekçesi

    /// <summary>
    /// <b>Kapının kendisi.</b> Ne <c>constraints</c> ne <c>constraints_waived</c>
    /// yazan bir adım eskiden sessizce geçerliydi.
    /// </summary>
    [Fact]
    public void Kisitsiz_ve_gerekcesiz_adim_yuklenmiyor()
    {
        var result = LoadReplacing(ConstraintLine, "max_items: 3");

        Assert.False(result.Ok);
        Assert.Contains(
            "`constraints` ya da `constraints_waived` zorunlu", result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Boş bir <c>constraints</c> listesi de gerekçesiz geçmiyor — anahtarı
    /// yazmak muafiyet değil.
    /// </summary>
    [Fact]
    public void Bos_kisit_listesi_gerekcesiz_yuklenmiyor()
    {
        var result = LoadReplacing(ConstraintLine, "constraints: []");

        Assert.False(result.Ok);
        Assert.Contains(
            "`constraints` ya da `constraints_waived` zorunlu", result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Boş dize reddediliyor.</b> Kabul edilseydi muafiyet tek harekete
    /// inerdi: anahtarı yazmak yeterdi. Gerekçe yazmak ile gerekçe yazmış
    /// GÖRÜNMEK arasındaki farkı kapatan satır bu.
    /// </summary>
    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void Bos_gerekce_reddediliyor(string waiver)
    {
        var result = LoadReplacing(ConstraintLine, $"constraints: []\n        constraints_waived: {waiver}");

        Assert.False(result.Ok);
        Assert.Contains("`constraints_waived` boş olamaz", result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>Gerekçesi olan boş liste geçiyor — ve muaf olarak işaretleniyor.</summary>
    [Fact]
    public void Gerekceli_muafiyet_yukleniyor()
    {
        var result = LoadReplacing(
            ConstraintLine,
            "constraints: []\n        constraints_waived: \"Kanıt toplama; öğe kimliği yok.\"");

        Assert.True(result.Ok, result.Describe());
        Assert.True(result.Value.Steps[0].Output.IsWaived);
        Assert.Equal("test.scenario/first-step", Assert.Single(result.Value.WaivedStepKeys));
    }

    /// <summary>
    /// Dolu kısıt listesi + muafiyet birlikte yazılamıyor: uygulanan bir kapıyı
    /// muaf gösterirdi ve muaf sayısı sabiti anlamını kaybederdi.
    /// </summary>
    [Fact]
    public void Dolu_liste_ile_muafiyet_birlikte_yazilamiyor()
    {
        var result = LoadReplacing(ConstraintLine, ConstraintLine + "\n        constraints_waived: \"Gerekçe.\"");

        Assert.False(result.Ok);
        Assert.Contains("birlikte yazılamaz", result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Eski tekil <c>constraint</c> adı konmuş bir hata veriyor. Bulanık bir
    /// "bilinmeyen anahtar" mesajı, iki belgede hâlâ örnekli olan bir yazımı
    /// yazım hatası gibi gösterirdi.
    /// </summary>
    [Fact]
    public void Tekil_constraint_adi_konmus_bir_hatayla_reddediliyor()
    {
        var result = LoadReplacing(ConstraintLine, "constraint: evidence_ids_must_exist");

        Assert.False(result.Ok);
        Assert.Contains("`constraint` (tekil) kaldırıldı", result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Kısıt <b>adları</b> kümesi açık: <c>pattern_must_compile</c> format
    /// kararı yazılırken doğdu ve kaç tane olacağını kimse bilmiyor. Küme
    /// kapatılsaydı her yeni senaryo çekirdeği değiştirirdi (§5.2).
    /// </summary>
    [Fact]
    public void Kisit_adlari_kapali_bir_kume_degil()
    {
        var result = LoadReplacing(ConstraintLine, "constraints: [pattern_must_compile, bir_gun_dogacak_kisit]");

        Assert.True(result.Ok, result.Describe());
        Assert.Equal(2, result.Value.Steps[0].Output.Constraints.Count);
    }

    [Fact]
    public void Ayni_kisit_iki_kez_yazilamiyor()
    {
        var result = LoadReplacing(ConstraintLine, "constraints: [evidence_ids_must_exist, evidence_ids_must_exist]");

        Assert.False(result.Ok);
        Assert.Contains("iki kez yazılmış", result.Describe(), StringComparison.Ordinal);
    }

    // -------------------------------------------------- §6.1 · zarf ve içerik

    /// <summary>
    /// <b>Kayıtlı olmayan sağlayıcı YÜKLEME anında düşüyor</b>, koşum anında
    /// değil. Koşuma ertelenseydi bozuk bir senaryo kayıtlı görünür, arızası
    /// ilk tetiklendiğinde çıkardı.
    /// </summary>
    [Fact]
    public void Kayitli_olmayan_saglayici_yukleme_aninda_dusuyor()
    {
        var result = LoadReplacing("providers: [logs.volume]", "providers: [parse.failures]");

        Assert.False(result.Ok);
        Assert.Contains(
            "Kayıtlı olmayan kanıt sağlayıcısı 'parse.failures'", result.Describe(), StringComparison.Ordinal);

        // Hata kayıtlı olanları da söylüyor: yazım hatası görülebilmeli.
        Assert.Contains("logs.volume", result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Kanıt bloğunun <b>içeriği</b> çekirdek tarafından yorumlanmıyor: üç
    /// senaryo üç ayrı şekil istedi (<c>window</c>, <c>horizon</c>,
    /// <c>sample</c>) ve dördüncüsünü yasaklamak uzantı noktasını kapatmak
    /// olurdu.
    /// </summary>
    [Theory]
    [InlineData("horizon: { span: 90d, bucket: 1d }")]
    [InlineData("sample: { max_items: 50 }")]
    [InlineData("bugun-kimsenin-dusunmedigi-sekil: [1, 2, 3]")]
    public void Kanit_blogunun_icerigi_cekirdege_kapali_degil(string shape)
    {
        var result = LoadReplacing("window: { lead: 30m }", shape);

        Assert.True(result.Ok, result.Describe());
        Assert.NotEmpty(result.Value.Evidence.Content.Fields);
    }

    /// <summary>
    /// Zarf yine de bir zarf: dizi biçimli bir <c>evidence</c> sağlayıcıya hiç
    /// ulaşmadan burada duruyor. "Çekirdek şekli bilemez" denen şey
    /// <b>içerik</b>; zarf onun kapsamında değil.
    /// </summary>
    [Fact]
    public void Iyi_bicimli_olmayan_kanit_blogu_yukleme_aninda_dusuyor()
    {
        var result = LoadReplacing(
            "evidence:\n    providers: [logs.volume]\n    window: { lead: 30m }",
            "evidence: [logs.volume]");

        Assert.False(result.Ok);
        Assert.Contains("`evidence` bir eşleme olmalı", result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// İçerik doğrulaması <b>koşum anının</b> işi ve yükleme anında
    /// çağrılmıyor: aynı dosya, şemalı kayıtta da yükleniyor — koşum kapısında
    /// duruyor.
    /// </summary>
    [Fact]
    public void Icerik_dogrulamasi_kosum_anina_ait()
    {
        var withSchema = new ScenarioProviderRegistry(["logs.volume"], [new BaselineRequiredSchema()]);

        var loaded = ScenarioYamlLoader.Load(Valid, withSchema, "test.yaml");
        Assert.True(loaded.Ok, loaded.Describe());

        var violations = ScenarioEvidenceGate.Check(loaded.Value, withSchema);
        Assert.Contains(violations, v => v.Contains("baseline", StringComparison.Ordinal));
    }

    /// <summary>
    /// Şeması olmayan sağlayıcının kanıt bloğu doğrulanmıyor — ve bu
    /// <b>sessiz değil</b>. "Bakılmadı" ile "bakıldı, temiz" ayrı cümleler.
    /// </summary>
    [Fact]
    public void Semasiz_saglayici_dogrulanmadigini_soyluyor()
    {
        var scenario = Load(Valid).Value;

        Assert.Empty(ScenarioEvidenceGate.Check(scenario, Registry));
        Assert.Contains(
            ScenarioEvidenceGate.Describe(scenario, Registry),
            line => line.Contains("şema kayıtlı DEĞİL", StringComparison.Ordinal));
    }

    /// <summary>
    /// Kayıtlı olmayan bir sağlayıcı için şema verilmesi bir kurulum hatası:
    /// şema hiç koşmaz ve kimse fark etmez.
    /// </summary>
    [Fact]
    public void Sahipsiz_sema_kurulumda_patliyor()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new ScenarioProviderRegistry(["logs.window"], [new BaselineRequiredSchema()]));

        Assert.Contains("şema hiç koşmazdı", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Test-yerel şema: <c>window.baseline</c> arıyor, başka hiçbir şeye bakmıyor.</summary>
    private sealed class BaselineRequiredSchema : IScenarioEvidenceSchema
    {
        public string ProviderId => "logs.volume";

        public IReadOnlyList<string> Validate(ScenarioValue.Mapping content) =>
            content.Fields.TryGetValue("window", out var window) &&
            window is ScenarioValue.Mapping mapping &&
            mapping.Fields.ContainsKey("baseline")
                ? []
                : ["`window.baseline` zorunlu ve varsayılanı yok (RCA §8.1)."];
    }

    // ----------------------------------------------------- §3.1 · tetikleyici

    /// <summary>
    /// Tetikleyici kümesi <b>kapalı</b>: açık olsaydı K20'nin tek-kuyruk
    /// garantisi yeni değerler için tanımsız kalırdı.
    /// </summary>
    [Fact]
    public void Bilinmeyen_tetikleyici_reddediliyor()
    {
        var result = LoadReplacing("on: [manual]", "on: [webhook]");

        Assert.False(result.Ok);
        Assert.Contains("Bilinmeyen tetikleyici 'webhook'", result.Describe(), StringComparison.Ordinal);
        Assert.Contains("çekirdek kararı", result.Describe(), StringComparison.Ordinal);
    }

    /// <summary><c>schedule</c> beşinci tetikleyici — F4'ün yan çıktısı, K20'ye girdi.</summary>
    [Theory]
    [InlineData("alert")]
    [InlineData("manual")]
    [InlineData("anomaly")]
    [InlineData("external")]
    [InlineData("schedule")]
    public void K20nin_besi_de_geciyor(string trigger)
    {
        var result = LoadReplacing("on: [manual]", $"on: [{trigger}]");
        Assert.True(result.Ok, result.Describe());
    }

    /// <summary>
    /// Küme <b>beş</b>. Büyürse burası kırmızı yanıyor — tetikleyici eklemek
    /// T45'in alanı ve çekirdeğe sessizce sızmamalı.
    /// </summary>
    [Fact]
    public void Tetikleyici_kumesi_bes_degerde_sabit()
    {
        Assert.Equal(5, ScenarioTriggers.Known.Count);
    }

    [Fact]
    public void Bos_tetikleyici_listesi_reddediliyor()
    {
        var result = LoadReplacing("on: [manual]", "on: []");

        Assert.False(result.Ok);
        Assert.Contains("`trigger.on` boş olamaz", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Tetikleyici_blogu_zorunlu()
    {
        var result = LoadReplacing("  trigger:\n    on: [manual]\n", string.Empty);

        Assert.False(result.Ok);
        Assert.Contains("`trigger` zorunlu", result.Describe(), StringComparison.Ordinal);
    }

    // ----------------------------------------------------------- adım grafiği

    /// <summary>
    /// İleri atıf reddediliyor. Kısıt doğrulaması <b>adımın gördüğü</b> kanıta
    /// karşı yapılıyor (§6); henüz koşmamış bir adımın çıktısına atıf yapan
    /// adım, görmediği bir şeye dayanmış olurdu.
    /// </summary>
    [Fact]
    public void Ileri_atif_reddediliyor()
    {
        var result = LoadReplacing("input: evidence.items", "input: steps.later-step");

        Assert.False(result.Ok);
        Assert.Contains("yalnızca kendinden ÖNCEKİ", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Kendine_atif_reddediliyor()
    {
        var result = LoadReplacing("input: evidence.items", "input: steps.first-step");

        Assert.False(result.Ok);
        Assert.Contains("kendi adımına atıf", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Tanimsiz_atif_bicimi_reddediliyor()
    {
        var result = LoadReplacing("input: evidence.items", "input: bundle.items");

        Assert.False(result.Ok);
        Assert.Contains("Geçersiz `input` atfı", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Bos_adim_listesi_reddediliyor()
    {
        var result = Load("""
            apiVersion: bizigo.dev/v1
            kind: Scenario
            metadata: { id: test.scenario, version: 1.0.0, owner: platform-team }
            spec:
              trigger: { on: [manual] }
              evidence: { providers: [logs.volume] }
              steps: []
              publish: { requires_review: false }
            """);

        Assert.False(result.Ok);
        Assert.Contains("en az bir adım zorunlu", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Ayni_adim_id_iki_kez_tanimlanamiyor()
    {
        var result = LoadReplacing(
            "  publish:",
            "    - id: first-step\n" +
            "      task: \"İkinci kez.\"\n" +
            "      input: evidence.items\n" +
            "      output:\n" +
            "        schema: some_list\n" +
            "        constraints: [evidence_ids_must_exist]\n" +
            "  publish:");

        Assert.False(result.Ok);
        Assert.Contains("iki kez tanımlanmış", result.Describe(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------- zarf

    [Theory]
    [InlineData("apiVersion: bizigo.dev/v1", "apiVersion: bizigo.dev/v2", "Desteklenmeyen apiVersion")]
    [InlineData("kind: Scenario", "kind: Parser", "Desteklenmeyen kind")]
    [InlineData("id: test.scenario", "id: Test.Scenario", "Geçersiz senaryo id")]
    [InlineData("version: 1.0.0", "version: birinci", "Geçersiz sürüm")]
    [InlineData("requires_review: true", "requires_review: belki", "true veya false olmalı")]
    [InlineData("id: first-step", "id: First_Step", "Geçersiz adım id")]
    public void Zarf_kusurlari_reddediliyor(string from, string to, string expected)
    {
        var result = LoadReplacing(from, to);

        Assert.False(result.Ok);
        Assert.Contains(expected, result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>requires_review</c>'nun varsayılanı YOK. Varsayılan verilseydi alanı
    /// yazmayı unutan bir senaryo, aksiyon alsa bile onaysız yayınlanırdı (K16).
    /// </summary>
    [Fact]
    public void Requires_review_zorunlu()
    {
        var result = LoadReplacing("requires_review: true", "target: repo_artifact");

        Assert.False(result.Ok);
        Assert.Contains("varsayılanı YOK", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sahipsiz_senaryo_yuklenmiyor()
    {
        var result = LoadReplacing("  owner: platform-team\n", string.Empty);

        Assert.False(result.Ok);
        Assert.Contains("`owner` zorunlu", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Bilinmeyen_anahtar_oneriyle_reddediliyor()
    {
        var result = LoadReplacing("  publish:", "  publsh:");

        Assert.False(result.Ok);
        Assert.Contains("'publish' mi demek istediniz?", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Bozuk_yaml_satiriyla_birlikte_reddediliyor()
    {
        var result = Load("apiVersion: [bizigo.dev/v1\nkind: Scenario");

        Assert.False(result.Ok);
        Assert.Contains("YAML söz dizimi hatası", result.Describe(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------ referans senaryo

    /// <summary>
    /// Depodaki referans senaryo gerçekten yükleniyor. Kapının yanlış yere
    /// kapanması, yukarıdaki bütün ret testlerini anlamsız kılardı.
    /// </summary>
    [Fact]
    public void Referans_senaryo_yukleniyor()
    {
        var path = Path.Combine(RepositoryLayout.ScenarioDirectory, "builtin.rca.network.yaml");
        var result = ScenarioYamlLoader.LoadFile(path, Registry);

        Assert.True(result.Ok, result.Describe());

        var scenario = result.Value;
        Assert.Equal("builtin.rca.network", scenario.Metadata.Id);
        Assert.Equal(3, scenario.Steps.Count);
        Assert.Equal(6, scenario.Evidence.Providers.Count);

        // §8.1'in işaretli sayıları TAŞINIYOR ama yorumlanmıyor — zorlaması
        // T44'ün.
        Assert.Equal(3, scenario.Steps[0].Output.MaxItems);
        Assert.Null(scenario.Steps[1].Output.MaxItems);
        Assert.Equal(2, scenario.Steps[2].Output.MaxItems);

        // Halüsinasyon kapısı ortadaki adımda kapanıyor.
        Assert.Equal("evidence_ids_must_exist", Assert.Single(scenario.Steps[1].Output.Constraints));
    }
}
