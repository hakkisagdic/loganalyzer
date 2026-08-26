using System.Text.RegularExpressions;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Uçtan uca harness ürünün yolunu atlayarak veri yazmıyor</b> (S05'in
/// kapanış ölçütü).
///
/// <para>
/// FS'in var olma sebebi tek cümle: <i>"ürün gerçek cihaz olmadan uçtan uca
/// koşuyor mu"</i>. Harness kapsam eşlemesini ve envanteri elle SQL ile
/// kurduğu sürece cevap <b>hayır</b>: ekran dolu görünür ve doldurabilme
/// iddiası hiç sınanmamış olur.
/// </para>
///
/// <para>
/// <b>Üç kategori ayrı, ve bekçi ikisini karıştırmıyor:</b>
/// </para>
///
/// <list type="number">
///   <item><b>Elle tohumlama</b> — harness ürünün yolunu atlayıp veri yazıyor.
///   <b>Düşmeli.</b></item>
///   <item><b>Üretim yolundan geçen tohumlama</b> — <c>bizigo seed golden</c>
///   satırları <c>EncodingDetector → EventComposer → EventNormalizer →
///   EventWriter</c> zincirinden geçiriyor. <b>Düşmemeli</b>; bu, elle yazma
///   değil ürünü çalıştırma.</item>
///   <item><b>Test fixture'ı</b> — <c>DevStackSetup</c> ve entegrasyon
///   fixture'ları. <b>Kapsam dışı</b>, gerekçesi aşağıda.</item>
/// </list>
///
/// <para>
/// <b>Üçüncü kategorinin gerekçesi.</b> Ölçüt şu: <i>bu tohumlama ürünün bir
/// iddiasını atlıyor mu, yoksa yalnızca bir başlangıç durumu mu kuruyor?</i>
/// Bir entegrasyon testi kendi girdisini kurduğunda ürünün bir iddiasını
/// atlamıyor — <b>iddianın kendisi o testin gövdesinde</b>, ve girdi olmadan
/// sınanacak bir şey yok. Harness ise ürünün <i>ürettiğini iddia ettiği</i>
/// veriyi elle yazıyordu; atlanan şey iddianın kendisiydi.
/// </para>
///
/// <para>
/// Ayrım ölçüldü, tahmin değil: aranan yerlerde bulunan tek elle yazma
/// <c>DevStackSmokeTests</c>'in <c>smoke_a</c> tablosuydu ve o tablo ürünün
/// şemasında <b>yok</b> — SQL bölücüsünü sınamak için açılan bir çizik alan.
/// </para>
///
/// <para>
/// <b>Bekçinin kör noktası, beyan:</b> tarama <c>ui/tests/e2e</c> ağacını
/// gezyor. Maestro akışları (<c>ui/tests/maestro</c>) ve ekran görüntüsü
/// yardımcıları <b>arandı</b> ve kendi tohumlamaları yok — hepsi
/// <c>prepare.ts</c>'e dayanıyor. Ama bu bir <i>bugünkü durum</i> tespiti:
/// yarın Maestro kendi kurulumunu yazarsa bu bekçi onu <b>görmez</b>. Kapsamı
/// genişletmek gerektiğinde burası da genişlemeli.
/// </para>
/// </summary>
public sealed partial class HarnessSeedingTests
{
    /// <summary>
    /// Ürünün kontrol düzlemi şemasına <b>doğrudan yazma</b>.
    ///
    /// <para>
    /// <c>bizigo.</c> öneki kasıtlı: ürünün şemasına yazan bir ifade aranıyor,
    /// herhangi bir SQL değil. Bir testin kendi çizik tablosuna yazması bu
    /// desene takılmıyor ve takılmamalı.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"(INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+bizigo\.",
        RegexOptions.IgnoreCase)]
    private static partial Regex DirectWrite();

    /// <summary>
    /// <b>Muafiyet listesi.</b> Bugün boş ve boş kalması bir iddia.
    ///
    /// <para>
    /// Bir satır eklemek <see cref="ExpectedExemptCount"/>'u da değiştirmeyi
    /// gerektiriyor — iki ayrı bilinçli hareket (§8). Tek başına gerekçe bir
    /// kaçış kapısı, tek başına sayı gerekçesiz bir sabit.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        // BOŞ. S05'in kapanış ölçütü tam olarak bu listenin boş olması.
    };

    private const int ExpectedExemptCount = 0;

    /// <summary>
    /// Taranan dosyalar — <b>elle liste değil</b>, ağaç gezilerek.
    ///
    /// <para>
    /// Kalıp <c>CiCoverageTests</c>'ten: denetlenen kümeyi keşfet, elle kalan
    /// tek şey beklenen küme olsun. Elle bir dosya listesi tutsaydım yarın
    /// eklenen bir kurulum dosyası bekçiye <b>hiç görünmezdi</b> — bu depoda
    /// <c>Produces&lt;T&gt;</c> kapısının 16 ucu görmemesinin sebebi buydu.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> HarnessFiles()
    {
        var root = Path.Combine(RepositoryLayout.Root, "ui", "tests", "e2e");

        return Directory.Exists(root)
            ? [.. Directory.EnumerateFiles(root, "*.ts", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]
            : [];
    }

    [Fact]
    public void Harness_urunun_semasina_dogrudan_yazmiyor()
    {
        var files = HarnessFiles();

        // Keşif gerçekten iş görüyor mu: boş küme, boş farkla eşleşir ve test
        // yeşil kalırdı.
        Assert.NotEmpty(files);

        var offenders = files
            .Where(file => DirectWrite().IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(RepositoryLayout.Root, file))
            .Where(relative => !Exempt.ContainsKey(relative))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Uçtan uca harness ürünün şemasına doğrudan yazıyor:\n  " +
            string.Join("\n  ", offenders) +
            "\n\nBu, ürünün yolunu atlamak demek: ekran dolu görünür ve doldurabilme " +
            "iddiası hiç sınanmamış olur. Filo tanımından besleyin " +
            "(`bizigo fleet apply`), ya da gerekçesiyle `Exempt`e yazın.");
    }

    /// <summary>
    /// Muafiyet listesi sessizce genişlemiyor (§8).
    /// </summary>
    [Fact]
    public void Muafiyet_sayisi_sabit()
    {
        Assert.True(
            Exempt.Count == ExpectedExemptCount,
            $"Muafiyet listesi {ExpectedExemptCount} yerine {Exempt.Count} satır taşıyor.");
    }

    /// <summary>
    /// <b>Harness filoyu gerçekten uyguluyor.</b>
    ///
    /// <para>
    /// Ayrı bir test çünkü ayrı bir iddia: yukarıdaki bekçi <i>"elle yazma
    /// yok"</i> diyor, bu <i>"yerine bir şey kondu"</i> diyor. Yalnızca
    /// birincisi olsaydı, iki adımı <b>tümden silmek</b> de testi yeşil
    /// bırakırdı — ve o zaman envanter hiç kurulmaz, ekran boş kalırdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Harness_filoyu_uyguluyor()
    {
        var prepare = Path.Combine(RepositoryLayout.Root, "ui", "tests", "e2e", "prepare.ts");

        Assert.True(File.Exists(prepare), $"Harness dosyası bulunamadı: {prepare}");

        var text = File.ReadAllText(prepare);

        Assert.Contains("\"fleet\", \"apply\"", text, StringComparison.Ordinal);
    }
}
