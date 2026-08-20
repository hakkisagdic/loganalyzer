using System.Reflection;
using System.Text.RegularExpressions;
using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Her kapsam kararının bir negatif testi var mı.</b>
///
/// <para>
/// <c>ScopeNegativeTests</c> bugüne kadar yolları <b>elle</b> sayıyordu: on iki
/// test, on iki okuma yolu, ve aralarındaki bağ kimsenin bakmadığı bir eşleşme.
/// Yeni bir okuma yolu eklendiğinde listeye satır eklenmezse <b>hiçbir şey
/// kırmızı yanmıyordu</b> — eksik olan bir uç değil, bir <b>kapsam sızıntısının
/// testi</b> olurdu.
/// </para>
///
/// <para>
/// <b>Bu depoda elle liste dört kez kör çıktı:</b> <c>ProducesContractTests</c>
/// (16 uç görünmedi, üç test de yeşildi), yaşam süresi bekçisi
/// (<c>AddBizigoAuthentication</c> hiç doğrulanmamıştı), <c>sigma_build</c>
/// Kapı 2 (tip denetimi hiç koşmuyordu), ve <c>CiCoverageTests</c>'i doğuran
/// sidecar paketi (dört pytest dosyası CI'da hiç koşmuyordu). Dördü de
/// yansımaya çevrilerek çözüldü ve çözüm hep aynıydı: <b>denetlenen kümeyi
/// keşfet, elle kalan tek şey beklenen küme olsun.</b>
/// </para>
///
/// <para>
/// <b>Denetlenen küme neden <see cref="IScopedQuery"/>:</b> K17 kapsam
/// zorlamasının <i>tek kapısı</i> orası ve <c>ScopeNegativeTests</c> de HTTP
/// katmanının değil onun üstünde koşuyor. Uçlar yalnızca oraya devrediyor;
/// devretmeyen bir uç <c>ApiSurfaceTests</c>'te kırmızı yanıyor. Yani arayüzün
/// metotları, kapsam kararlarının tam listesi.
/// </para>
///
/// <para>
/// <b>Kapsama işareti testin yanında duruyor</b>, ayrı bir listede değil:
/// <c>ScopeNegativeTests.cs</c> içinde <c>// kapsam: MetotAdı</c> yorumu.
/// Ayrı liste tutmak, kapatmaya çalıştığımız kör noktanın aynısını başka
/// kılıkta üretirdi — liste ile testler ayrışır ve ayrışma görünmezdi.
/// </para>
/// </summary>
public sealed partial class ScopeCoverageTests
{
    /// <summary>
    /// <b>Küçülen liste.</b> Negatif testi henüz olmayan kapsam kararları;
    /// karşısındaki not onu kapatacak işi söylüyor.
    ///
    /// <para>
    /// <c>ProducesContractTests.Pending</c> ile aynı sözleşme: satır silinirken
    /// test yazılmış olmalı, ve testi <b>olan</b> bir satırın listede kalması da
    /// hata sayılıyor — yani liste kendiliğinden bayatlayamıyor.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> Pending = new(StringComparer.Ordinal)
    {
        // BOŞ. Yedi satırla açıldı; yedisi de bu turda kapandı.
        //
        // Beşi T35'in korelasyonlarıydı ve ticket `status:2` görünüyordu —
        // yani "bitmiş" bir işin kapsam kapısı hiç sınanmamıştı. Kalan ikisi
        // değişiklik sayımı ve ham nesne kapısıydı.
        //
        // Buraya satır eklemek serbest ama bedeli görünür: eklenen her satır,
        // sınanmamış bir kapsam kararı demek.
    };

    /// <summary>
    /// <b>Kalıcı muafiyetler.</b> Bunlara negatif test yazılmayacak, ve sebebi
    /// "henüz yazılmadı" değil.
    ///
    /// <para>
    /// Muafiyet <b>bedava değil</b>: buraya satır eklemek
    /// <see cref="ExpectedExemptCount"/>'u da değiştirmeyi gerektiriyor (§8).
    /// "Bir gün kapanacak" ile "hiç kapanmayacak" aynı listede duramaz — dursa
    /// "liste boşaldı mı" sorusunun cevabı asla evet olamaz.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["CountEventsAsync"] =
            "Sayım, `SearchEventsAsync` ile AYNI yüklemi kuruyor (`ScopePredicate`); ayrı bir "
            + "negatif test aynı kodu ikinci kez sınardı. Sızıntı olsaydı arama testi de düşerdi.",
    };

    /// <summary>
    /// <see cref="Exempt"/> bu sayıda kalmalı. Sabitin tek işlevi kaçış kapısının
    /// sessizce genişlemesini engellemek.
    /// </summary>
    private const int ExpectedExemptCount = 1;

    /// <summary>
    /// <b>Denetlenen küme:</b> <see cref="IScopedQuery"/>'nin
    /// <see cref="AccessScope"/> alan bütün metotları — yansımayla.
    ///
    /// <para>
    /// <c>AccessScope</c> parametresi filtresi bilinçli: kapsam kararı vermeyen
    /// bir metot bir gün eklenirse (saf bir yardımcı gibi) bekçi onu boşuna
    /// istemez. Ölçüt "arayüzde olmak" değil, <b>kapsam almak</b>.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ScopeDecisions() =>
        [.. typeof(IScopedQuery)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(static m => m.GetParameters().Any(static p => p.ParameterType == typeof(AccessScope)))
            .Select(static m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)];

    /// <summary>
    /// <c>ScopeNegativeTests.cs</c>'teki kapsama işaretleri.
    ///
    /// <para>
    /// Dosyadan okunuyor, derlemeden değil: birim testi paketi entegrasyon
    /// paketine referans vermiyor ve vermesi de doğru olmazdı — konteyner
    /// gerektiren bir paketi birim testinin bağımlılığı yapmak, §2'nin
    /// bölünmesini bozardı. Kalıp <c>CiCoverageTests</c>'ten (<c>ci.yml</c>
    /// dosyadan okunuyor) ve <c>AlertLinkTargetTests</c>'ten (<c>criteria.ts</c>
    /// dosyadan okunuyor).
    /// </para>
    /// </summary>
    private static IReadOnlySet<string> Marked()
    {
        var path = Path.Combine(
            RepositoryLayout.Root, "tests", "Bizigo.IntegrationTests", "ScopeNegativeTests.cs");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Kapsam negatif testleri bulunamadı. Dosya taşındıysa bu bekçinin yolu da " +
                "güncellenmeli — yoksa bekçi sessizce hiçbir şey denetlemez.", path);
        }

        return CoverageMarker()
            .Matches(File.ReadAllText(path))
            .Select(m => m.Groups["method"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary><c>// kapsam: MetotAdı</c> — birden çok ad virgülle.</summary>
    [GeneratedRegex(@"//\s*kapsam:\s*(?<method>[A-Za-z]+Async)")]
    private static partial Regex CoverageMarker();

    /// <summary>
    /// Her kapsam kararının ya negatif testi var, ya iki listeden birinde.
    /// </summary>
    [Fact]
    public void Her_kapsam_kararinin_negatif_testi_var()
    {
        var marked = Marked();

        var uncovered = ScopeDecisions()
            .Where(name => !marked.Contains(name))
            .Where(name => !Pending.ContainsKey(name) && !Exempt.ContainsKey(name))
            .ToArray();

        Assert.True(
            uncovered.Length == 0,
            "Bu kapsam kararlarının negatif testi yok ve listelerde de değiller:\n  " +
            string.Join("\n  ", uncovered) +
            "\n\n`ScopeNegativeTests`'e bir test ekleyip `// kapsam: <MetotAdı>` işareti bırakın, " +
            "ya da gerekçesiyle `Pending`e yazın. Kapsam sızıntısının testi eksikse sızıntı " +
            "hiçbir yerde görünmez.");
    }

    /// <summary>
    /// <b>Listeler bayat giriş taşımıyor.</b>
    ///
    /// <para>
    /// Testi yazılmış bir kalem listede kalırsa liste sessizce şişer ve
    /// "boşaldı mı" sorusu anlamını kaybeder. <c>ProducesContractTests</c>'in
    /// aynı bekçisi bu depoda bir kez gerçekten iş gördü.
    /// </para>
    /// </summary>
    [Fact]
    public void Listeler_bayat_giris_tasimiyor()
    {
        var marked = Marked();
        var decisions = ScopeDecisions().ToHashSet(StringComparer.Ordinal);

        var covered = Pending.Keys.Where(marked.Contains).ToArray();
        Assert.True(
            covered.Length == 0,
            "Bu kalemlerin artık negatif testi var; `Pending`den silin:\n  " +
            string.Join("\n  ", covered));

        var unknown = Pending.Keys.Concat(Exempt.Keys)
            .Where(name => !decisions.Contains(name))
            .ToArray();

        Assert.True(
            unknown.Length == 0,
            "Bu kalemler artık `IScopedQuery`'de yok — metot silindiyse liste de temizlenmeli:\n  " +
            string.Join("\n  ", unknown));
    }

    /// <summary>
    /// Muafiyet listesi sessizce genişlemiyor (§8).
    /// </summary>
    /// <summary>
    /// <b>Liste boşaldı mı.</b>
    ///
    /// <para>
    /// Yedi satırla açıldı ve yedisi de kapandı; bu test o durumu <b>sabitliyor</b>.
    /// Ayrı bir test olması bilinçli: yukarıdaki bekçi "listede olan bir kalem
    /// sorun değil" diyor, bu ise "listede kalem <i>olmaması</i> gerekiyor"
    /// diyor. İkisi farklı iddialar ve ikincisi kaybolursa liste sessizce
    /// yeniden dolabilir.
    /// </para>
    ///
    /// <para>
    /// Kalıp <c>ProducesContractTests.Izin_listesi_bosaldi_mi</c>'dan; orada da
    /// listenin boşalması bir fazın bitiş şartıydı.
    /// </para>
    /// </summary>
    [Fact]
    public void Izin_listesi_bosaldi_mi()
    {
        Assert.True(
            Pending.Count == 0,
            "Sınanmamış kapsam kararı var:\n  " + string.Join("\n  ", Pending.Keys) +
            "\n\nHer satır, kapsam kapısının doğru çalıştığı gösterilmemiş bir yol demek.");
    }

    [Fact]
    public void Muafiyet_sayisi_sabit()
    {
        Assert.True(
            Exempt.Count == ExpectedExemptCount,
            $"Muafiyet listesi {ExpectedExemptCount} yerine {Exempt.Count} satır taşıyor. " +
            "Muafiyet eklemek iki ayrı bilinçli hareket gerektiriyor (§8): satırı yazmak ve " +
            "bu sabiti değiştirmek.");
    }

    /// <summary>
    /// <b>Bekçinin kendi bekçisi.</b> Keşif gerçekten iş görüyor mu?
    ///
    /// <para>
    /// Ayrı bir test, çünkü kaybı ayrı bir hata: yansıma bir gün hiçbir şey
    /// bulmazsa yukarıdaki iki test de <b>boş küme üzerinde geçer</b> ve
    /// yeşillikleri hiçbir şey ifade etmez. Bu depoda tam olarak böyle bir
    /// bekçi yaşandı.
    /// </para>
    /// </summary>
    [Fact]
    public void Kapsam_kararlari_kesfediliyor()
    {
        var decisions = ScopeDecisions();

        Assert.NotEmpty(decisions);

        // Kapının en bilinen iki kararı; keşif bunları bulamıyorsa filtre
        // yanlıştır.
        Assert.Contains("SearchEventsAsync", decisions);
        Assert.Contains("WriteChangeAsync", decisions);

        // İşaret okuma da çalışıyor olmalı: dosya duruyor ama hiç işaret yoksa
        // "hepsi Pending'de" diye geçen bir bekçi kalırdı.
        Assert.NotEmpty(Marked());
    }
}
