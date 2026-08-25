using System.Text.RegularExpressions;
using Bizigo.Simulators;

namespace Bizigo.UnitTests;

/// <summary>
/// Adlandırılmış geçişlerin motoru (S04).
///
/// <para>
/// <b>S04'ün taşıyıcı bulgusu bir yüzey ayrımı.</b> S03'e kadar senaryo adı,
/// profilin <c>config.scenarios</c> sözlüğünde aranan bir anahtardı — yani her
/// senaryonun bir config dosyası olduğu varsayılıyordu. Yedi senaryonun
/// yalnızca dördü öyle: ikisi <b>basıcıyı</b> değiştiriyor, biri bir
/// <b>altyapı</b> eylemi.
/// </para>
///
/// <para>
/// Yüzey yazılmasaydı <c>saat-kaymasi</c> config sözlüğünde aranır, bulunamaz,
/// ve hata <i>"profilde böyle bir senaryo yok"</i> derdi — arıza profil
/// dosyasında aranırdı. Yanlış yere işaret eden bir hata mesajı bu depoda bir
/// teşhis turunu tümden harcamış bir sınıf.
/// </para>
/// </summary>
public sealed partial class ScenarioEngineTests
{
    /// <summary>
    /// <b>Sözlük faz belgesiyle örtüşüyor.</b>
    ///
    /// <para>
    /// Bu bekçi, koddaki listeyi <c>fs-simulatorler/index.md</c> §7'nin
    /// tablosundan <b>okuyarak</b> karşılaştırıyor. Elle yazılmış iki liste
    /// olsaydı biri düzeltilip diğeri eski kalabilirdi ve ayrışma sessiz
    /// olurdu — belge yedi senaryo anlatır, motor altısını tanır, ve yedinci
    /// senaryoyu isteyen test <i>"senaryo yok"</i> alırdı.
    /// </para>
    ///
    /// <para>
    /// Kalıp <c>CiCoverageTests</c> (<c>ci.yml</c> dosyadan okunuyor) ve
    /// <c>AlertLinkTargetTests</c> (<c>criteria.ts</c> dosyadan okunuyor) ile
    /// aynı: <b>denetlenen kümeyi keşfet, elle kalan tek şey beklenen küme
    /// olsun.</b>
    /// </para>
    /// </summary>
    [Fact]
    public void Sozluk_faz_belgesiyle_ortusuyor()
    {
        var path = Path.Combine(
            RepositoryLayout.Root, "docs", "epic", "fs-simulatorler", "index.md");

        Assert.True(File.Exists(path), $"Faz belgesi bulunamadı: {path}");

        var documented = ScenarioRow()
            .Matches(File.ReadAllText(path))
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        // Belgeyi hiç okuyamamak sessiz bir geçiş üretirdi: boş küme, boş
        // farkla eşleşir ve test yeşil kalırdı.
        Assert.NotEmpty(documented);

        var known = Scenarios.Known.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        Assert.True(
            documented.SetEquals(known),
            "Motorun senaryo sözlüğü faz belgesiyle ayrıştı.\n" +
            "  Belgede olup motorda olmayan: " + string.Join(", ", documented.Except(known)) + "\n" +
            "  Motorda olup belgede olmayan: " + string.Join(", ", known.Except(documented)));
    }

    /// <summary>§7 tablosundaki satır adı: <c>| `ad` | …</c>.</summary>
    [GeneratedRegex(@"^\|\s*`(?<name>[a-z-]+)`\s*\|", RegexOptions.Multiline)]
    private static partial Regex ScenarioRow();

    /// <summary>
    /// Her senaryo bir <b>iddia</b> taşıyor.
    ///
    /// <para>
    /// Fazın kuralı: <i>"genel olarak değişsin" diye bir senaryo yok</i>. Boş
    /// bir iddia, senaryoyu ne sınadığı yazılmamış bir değişikliğe indirger —
    /// ve o senaryonun testi bir gün düştüğünde neyin kırıldığı bilinmez.
    /// </para>
    /// </summary>
    [Fact]
    public void Her_senaryo_bir_iddia_tasiyor()
    {
        Assert.All(Scenarios.Known, s => Assert.False(string.IsNullOrWhiteSpace(s.Claim)));
    }

    /// <summary>
    /// Bilinmeyen ad <b>reddediliyor</b> — sessizce baseline'a düşmüyor.
    ///
    /// <para>
    /// Düşseydi adı yanlış yazılmış bir senaryo testi yeşil bırakır ve
    /// "fark yok" sonucu doğru sanılırdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Bilinmeyen_senaryo_reddediliyor()
    {
        var error = Scenarios.Reject("kural-eklendii", ScenarioSurface.Config);

        Assert.NotNull(error);
        Assert.Contains("diye bir senaryo yok", error, StringComparison.Ordinal);

        // Hata, bilinen adları da söylüyor: kullanıcı yazım hatasını
        // görebilmeli.
        Assert.Contains("kural-eklendi", error, StringComparison.Ordinal);
    }

    /// <summary>Boş ad ve <c>baseline</c> her yüzeyde geçerli.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("baseline")]
    public void Varsayilan_her_yuzeyde_gecerli(string? name)
    {
        Assert.Null(Scenarios.Reject(name, ScenarioSurface.Config));
        Assert.Null(Scenarios.Reject(name, ScenarioSurface.Syslog));
    }

    /// <summary>
    /// <b>Yanlış yüzey, "yok" demiyor — "burada değil" diyor.</b>
    ///
    /// <para>
    /// Asıl kafa karışıklığı <i>"bu senaryo yok"</i> ile <i>"bu senaryo burada
    /// değil"</i> arasında. Birincisi profil dosyasını, ikincisi çağıran kodu
    /// işaret ediyor — ve yanlış olanı okuyan kişi yanlış dosyayı açıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Yanlis_yuzey_dogru_yeri_gosteriyor()
    {
        var error = Scenarios.Reject("saat-kaymasi", ScenarioSurface.Config);

        Assert.NotNull(error);
        Assert.DoesNotContain("diye bir senaryo yok", error, StringComparison.Ordinal);
        Assert.Contains("syslog", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Altyapı senaryosu <b>simülatörün elinde değil</b> ve motor bunu açıkça
    /// söylüyor.
    ///
    /// <para>
    /// Sessizce hiçbir şey yapmak, senaryonun koştuğu sanılan bir test
    /// bırakırdı: <c>sidecar-yok</c> verilir, simülatör normal basar, test
    /// "sidecar arızalıyken throughput düşmedi" der — oysa sidecar hiç
    /// durdurulmamıştır.
    /// </para>
    /// </summary>
    [Fact]
    public void Altyapi_senaryosu_simulatorde_uygulanmiyor()
    {
        foreach (var surface in new[] { ScenarioSurface.Config, ScenarioSurface.Syslog })
        {
            var error = Scenarios.Reject("sidecar-yok", surface);

            Assert.NotNull(error);
            Assert.Contains("altyapı", error, StringComparison.Ordinal);
        }

        // Kendi yüzeyinde ise geçerli: motor onu TANIYOR, yalnızca uygulamıyor.
        Assert.Null(Scenarios.Reject("sidecar-yok", ScenarioSurface.Infrastructure));
    }

    /// <summary>
    /// Yüzey dağılımı: dört config, iki syslog, bir altyapı.
    ///
    /// <para>
    /// Sayılar elle yazılı ve <b>öyle olmalı</b> — bu, "beklenen küme"nin
    /// kendisi. Bir senaryo yüzey değiştirdiğinde ya da yeni bir senaryo
    /// eklendiğinde burası kırmızı yanıyor, yani dağılım sessizce kayamıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Yuzey_dagilimi_sabit()
    {
        Assert.Equal(4, Scenarios.For(ScenarioSurface.Config).Count());
        Assert.Equal(2, Scenarios.For(ScenarioSurface.Syslog).Count());
        Assert.Single(Scenarios.For(ScenarioSurface.Infrastructure));
    }
}
