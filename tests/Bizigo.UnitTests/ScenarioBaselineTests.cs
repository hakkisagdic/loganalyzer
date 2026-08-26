using Bizigo.Devices;
using Bizigo.Simulators;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Baseline'ın tek bir anlamı var</b> (S04 düzeltmesi).
///
/// <para>
/// <b>Kusurun şekli:</b> S01 taşıyıcıya "boş dize = baseline" kuralını koydu.
/// S04 <c>Scenarios.Baseline = "baseline"</c> sabitini getirdi ve
/// <c>Scenarios.Reject</c> ikisini de tanıdı — ama taşıyıcının <b>config
/// yolu</b> oraya hiç uğramıyordu. Adlandırılmış baseline sözlükte aranıyor,
/// bulunamıyor, ve senaryo <i>"profilde tanımlı değil"</i> diye reddediliyordu.
/// </para>
///
/// <para>
/// <b>Ders, kusurdan taşınabilir:</b> S04 tek <i>sözlük</i> yaptı ama tek
/// <i>predicate</i> yapmadı. Bir kavramı tekilleştirmek, onu <b>tanıyan</b>
/// predicate'i tekilleştirmekle aynı şey değil — depodaki "ikinci kopya yazma"
/// başlığı <i>veriyi</i> anlatıyor, <b>tanımayı</b> değil.
/// </para>
///
/// <para>
/// <b>Neden birim testi:</b> bu kusur 974 birim testi yeşilken CI'da kırmızı
/// yandı. Sebep ölçüldü — mevcut zincir testi baseline'ı <b>parametresiz
/// kurucuyla</b> alıyor, yani taşıyıcının config yolunu adlandırılmış
/// baseline'la geçen tek bir test yoktu. Boşluk tam oradaydı.
/// </para>
/// </summary>
public sealed class ScenarioBaselineTests
{
    private static readonly string ProfileDirectory =
        Path.Combine(RepositoryLayout.Root, "catalog", "simulators");

    private static DeviceTarget Target(string vendor) => new()
    {
        Vendor = vendor,
        Host = "sim",
        Username = "bizigo-ro",
        Credential = "yok",
    };

    private static SimulatorProfile Load(string profileId) => SimulatorProfileStore
        .LoadAll(ProfileDirectory, RepositoryLayout.Root)
        .Single(r => string.Equals(r.Profile.Id, profileId, StringComparison.Ordinal))
        .Profile;

    private static async Task<DeviceCommandResult> ReadAsync(SimulatorProfile profile, string? scenario)
    {
        var transport = new SimulatedDeviceTransport(profile, ProfileDirectory, scenario);

        return await transport.RunAsync(
            Target($"{profile.Vendor}.{profile.Product}"),
            ["show"],
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// <b>Boş dize, boşluk ve <c>"baseline"</c> aynı config'i veriyor.</b>
    ///
    /// <para>
    /// Üçü de "değişim yok" demenin yolları ve üçünün de aynı dosyayı
    /// döndürmesi gerekiyor. Ayrıştıkları gün belirti şuydu: senaryo testi
    /// <i>"profilde 'baseline' tanımlı değil"</i> alıyor ve arıza <b>profil
    /// dosyasında</b> aranıyor — oysa profil doğru, iki predicate ayrışmış.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("fw-ankara-01")]
    [InlineData("asa-dc-01")]
    [InlineData("rb-sube-07")]
    public async Task Bos_dize_ve_adlandirilmis_baseline_ayni_config(string profileId)
    {
        var profile = Load(profileId);

        var empty = await ReadAsync(profile, string.Empty);
        var named = await ReadAsync(profile, Scenarios.Baseline);
        var padded = await ReadAsync(profile, "  baseline  ");

        Assert.True(empty.Ok, empty.Error);
        Assert.True(named.Ok, named.Error);
        Assert.True(padded.Ok, padded.Error);

        Assert.Equal(empty.Output, named.Output);
        Assert.Equal(empty.Output, padded.Output);

        // Fixture'ın kendisi anlamlı mı: boş bir config üçünü de eşitler ve
        // yukarıdaki iddia yanlış sebeple geçerdi.
        Assert.NotEmpty(empty.Output.Trim());
    }

    /// <summary>
    /// <c>null</c> da baseline — parametresiz kurucunun yolu.
    ///
    /// <para>
    /// Ayrı bir test çünkü ayrı bir çağrı yolu: mevcut zincir testi tam olarak
    /// bunu kullanıyordu ve bu yüzden kusuru hiç görmedi.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Null_senaryo_da_baseline()
    {
        var profile = Load("fw-ankara-01");

        var implicitBaseline = await ReadAsync(profile, null);
        var named = await ReadAsync(profile, Scenarios.Baseline);

        Assert.True(implicitBaseline.Ok, implicitBaseline.Error);
        Assert.Equal(implicitBaseline.Output, named.Output);
    }

    /// <summary>
    /// Motor ile taşıyıcı <b>aynı</b> baseline kavramını kullanıyor.
    ///
    /// <para>
    /// Yukarıdaki testler <b>sonucu</b> sınıyor; bu, <b>kaynağı</b> sınıyor.
    /// İkisi ayrı: taşıyıcı kendi kopyasını tutup aynı sonucu üretebilir ve
    /// testler yeşil kalır — ta ki biri sabiti değiştirene kadar. "Tek
    /// predicate" iddiası ancak burada kırmızı yanabiliyor.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("baseline")]
    [InlineData("  baseline  ")]
    public void Motor_bu_adlari_baseline_sayiyor(string? name)
    {
        Assert.True(Scenarios.IsBaseline(name));
    }

    /// <summary>
    /// <c>"Baseline"</c> (büyük B) bilerek dışarıda: senaryo adları bu depoda
    /// ordinal eşleşiyor ve büyük/küçük harf toleransı, belgelenen ile kabul
    /// edilenin ayrışmasının en yaygın yolu.
    /// </summary>
    [Theory]
    [InlineData("kural-eklendi")]
    [InlineData("Baseline")]
    [InlineData("baseline2")]
    public void Motor_baska_adlari_baseline_saymiyor(string name)
    {
        Assert.False(Scenarios.IsBaseline(name));
    }

    /// <summary>
    /// <b>Gölgeleme reddediliyor:</b> bir profil <c>scenarios:</c> altında
    /// <c>baseline</c> tanımlayamıyor.
    ///
    /// <para>
    /// <b>Karar ve gerekçesi.</b> <c>baseline</c> sihirli bir ad ve sihirli bir
    /// ad, veriyle çakışabilen bir addır. İki seçenek vardı:
    /// </para>
    ///
    /// <list type="bullet">
    ///   <item><b>Profil kazansın:</b> "değişim yok" isteyen bir çağrı sessizce
    ///   <i>değişmiş</i> bir config alır. Karşılaştırmanın tabanı kayar ve fark
    ///   testleri <b>yanlış sebeple geçer</b> — hiçbir hata üretmeden.</item>
    ///   <item><b>Reddedilsin:</b> profil yazarı hatayı <b>yükleme anında</b>
    ///   görür ve adı değiştirir.</item>
    /// </list>
    ///
    /// <para>
    /// İkincisi seçildi. Ret ucuz — bir profil dosyası düzeltilir; sessiz taban
    /// kayması pahalı, çünkü belirtisi yok.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("  baseline ")]
    public void Profil_baseline_adini_golgeleyemiyor(string shadowing)
    {
        var errors = Scenarios.ShadowingErrors([shadowing, "kural-eklendi"]).ToArray();

        var error = Assert.Single(errors);
        Assert.Contains("sihirli bir ad", error, StringComparison.Ordinal);
    }

    /// <summary>Sıradan senaryo adları gölgeleme sayılmıyor.</summary>
    [Fact]
    public void Siradan_adlar_golgeleme_sayilmiyor()
    {
        Assert.Empty(Scenarios.ShadowingErrors(["kural-eklendi", "sir-dondu", "gurultu"]));
    }

    /// <summary>
    /// <b>Depodaki profillerin hiçbiri gölgelemiyor.</b>
    ///
    /// <para>
    /// Kuralın var olması ile bugün <i>ihlal edilmemesi</i> ayrı şeyler; bu
    /// test ikincisini sabitliyor. Bir profil yarın <c>baseline</c> eklerse
    /// yükleyici zaten reddediyor — ama o gün hatanın nerede çıktığını bu test
    /// söylüyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Depodaki_profiller_golgelemiyor()
    {
        var loaded = SimulatorProfileStore.LoadAll(ProfileDirectory, RepositoryLayout.Root);

        Assert.NotEmpty(loaded);
        Assert.All(loaded, r => Assert.Empty(r.Errors));
    }
}
