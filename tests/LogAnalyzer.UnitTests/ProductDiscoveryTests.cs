using System.Text.RegularExpressions;

namespace LogAnalyzer.UnitTests;

/// <summary>
/// <b>Ortak keşif yüzeyinin kendi bekçileri</b> (T55).
///
/// <para>
/// Konsolidasyonun riski <b>kapsamı sessizce değiştirmek</b>: üç kapı üç farklı
/// küme buluyor ve ortak yüzey birini büyütür ya da küçültürse <b>hiçbir test
/// kırmızı yanmaz</b> — üçü de kendi içinde tutarlı kalır. Bu sınıf o riski
/// kapatıyor: kapsamların <b>ilişkisi</b> burada iddia ediliyor, sayıları
/// değil.
/// </para>
///
/// <para>
/// <b>Neden ilişki, neden sayı değil:</b> <c>Assert.Equal(18, …)</c> yazsaydık
/// M02'nin eklediği <c>LogAnalyzer.Commands</c> testi <b>ilgisiz bir sebeple</b>
/// kırardı ve düzeltmesi sayıyı büyütmek olurdu — yani bekçi, kendisini
/// susturmayı öğreten bir bekçiye dönüşürdü. İlişkiler ise ürün büyüdükçe
/// doğru kalıyor.
/// </para>
/// </summary>
public sealed class ProductDiscoveryTests
{
    /// <summary>
    /// <b>Ürün projeleri yalnızca beyan edilen alanlarda</b> — kapsamın
    /// beyanı ve bekçisi bir arada.
    ///
    /// <para>
    /// Keşif <b>diskten</b> gidiyor ve yalnızca <see cref="ProductDiscovery.ProductAreas"/>
    /// altına bakıyor. Yani oraya konmayan bir ürün projesi <b>hiçbir kapıya
    /// görünmez</b> — ve görünmediği hiçbir yerde şikâyet üretmez: kapılar
    /// çalışmaya, yeşil yanmaya devam eder.
    /// </para>
    ///
    /// <para>
    /// Bu yüzden beyan bir yorum olarak bırakılmadı. Ölçüt <b>çözümün kendi
    /// proje listesi</b>: <c>LogAnalyzer.sln</c> hangi projeleri derliyorsa, test
    /// projeleri dışındakilerin hepsi beyan edilen alanların altında olmak
    /// zorunda. Dördüncü bir ürün projesi başka bir dizine konduğu gün burası
    /// kırmızı yanıyor.
    /// </para>
    ///
    /// <para>
    /// <b>Aciliyeti bugün gerçek:</b> M02 <c>LogAnalyzer.Commands</c> ve
    /// <c>LogAnalyzer.Commands.Mcp</c>, M04 <c>LogAnalyzer.Mcp.Product</c> getiriyor.
    /// Üçü de <c>src/</c> altında — ama beyan yazılmasaydı dördüncüsü sessizce
    /// dışarıda kalırdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Urun_projeleri_yalnizca_beyan_edilen_alanlarda()
    {
        var solution = Path.Combine(RepositoryLayout.Root, "LogAnalyzer.sln");

        Assert.True(File.Exists(solution), $"Çözüm dosyası yok: {solution}");

        // `Project(...) = "Ad", "göreli\yol.csproj", "{GUID}"`
        var listed = Regex.Matches(File.ReadAllText(solution), @"""([^""]+\.csproj)""")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(listed);

        // Test projeleri ürün değil; kapsam beyanı onları kapsamıyor.
        var products = listed
            .Where(path => !path.StartsWith("tests/", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(products);

        var outside = products
            .Where(path => !ProductDiscovery.ProductAreas.Any(
                area => path.StartsWith(area + "/", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            outside.Length == 0,
            $"Bu ürün projeleri beyan edilen alanların ({string.Join(", ", ProductDiscovery.ProductAreas)}) " +
            $"dışında: {string.Join(", ", outside)}.\n\n" +
            "Keşif diskten gidiyor ve yalnızca o alanlara bakıyor: dışarıdaki bir projenin kayıt " +
            "uzantıları HİÇBİR kapıya görünmez ve görünmediği de hiçbir yerde şikâyet üretmez. " +
            "Ya projeyi beyan edilen bir alana taşıyın, ya ProductDiscovery.ProductAreas'ı genişletin.");
    }

    /// <summary>
    /// <b>Çözümdeki her ürün projesi keşfediliyor.</b>
    ///
    /// <para>
    /// Yukarıdaki test <i>"beyan edilen alanların dışında proje yok"</i> diyor;
    /// bu test <i>"beyan edilen alanların içindekiler gerçekten bulunuyor"</i>
    /// diyor. İkisi ayrı sorular: dizin doğru ama tarama bozuksa birincisi
    /// <b>yeşil kalır</b>.
    /// </para>
    /// </summary>
    [Fact]
    public void Cozumdeki_her_urun_projesi_kesfediliyor()
    {
        var solution = Path.Combine(RepositoryLayout.Root, "LogAnalyzer.sln");

        var expected = Regex.Matches(File.ReadAllText(solution), @"""([^""]+\.csproj)""")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .Where(path => ProductDiscovery.ProductAreas.Any(
                area => path.StartsWith(area + "/", StringComparison.Ordinal)))
            .Select(Path.GetFileNameWithoutExtension)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var discovered = ProductDiscovery.Projects
            .Select(static p => p.Project)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, discovered);
    }

    /// <summary>
    /// <b>Her ürün projesinin derlemesi yükleniyor.</b>
    ///
    /// <para>
    /// Yüklenemeyen bir derleme, içindeki her uzantının <b>"yok" sayılması</b>
    /// demek — ve keşif çalışıyor görünmeye devam eder. Bu depoda adı konmuş
    /// sınıf: sessizce atlayan bir bekçi, bekçinin kendisinden tehlikeli (§7).
    /// </para>
    /// </summary>
    [Fact]
    public void Her_urun_derlemesi_yuklenebiliyor()
    {
        Assert.True(
            ProductDiscovery.AllProducts.Unloadable.Count == 0,
            "Bu ürün projelerinin derlemesi yüklenemedi:\n  " +
            string.Join("\n  ", ProductDiscovery.AllProducts.Unloadable) +
            "\n\nBirim test projesine referans ekleyin — görülemeyen bir derleme sessizce boş sayılır.");
    }

    /// <summary>
    /// <b>Bağımsız ürün istisnası yalnızca <c>LogAnalyzer.Lite</c>'ı kapsıyor</b>
    /// ve keşif onu gerçekten istisna olarak işliyor.
    ///
    /// <para>
    /// İstisna listesi <see cref="Her_urun_derlemesi_yuklenebiliyor"/>'u
    /// susturmanın en kısa yolu, o yüzden çivili: ikinci bir giriş eklemek bu
    /// testi de değiştirmeyi gerektiriyor (§8'in "iki ayrı bilinçli hareket"
    /// kuralı). Bayat giriş de yanıyor — beyan edilip keşfin hiç görmediği bir
    /// proje, istisnanın neyi koruduğunu okunmaz kılar.
    /// </para>
    /// </summary>
    [Fact]
    public void Bagimsiz_urun_istisnasi_yalnizca_Lite_icin()
    {
        var declared = ProductDiscovery.StandaloneProducts.Keys.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["LogAnalyzer.Lite"], declared);
        Assert.All(ProductDiscovery.StandaloneProducts.Values,
            static reason => Assert.False(string.IsNullOrWhiteSpace(reason)));

        Assert.Equal(declared, ProductDiscovery.AllProducts.Standalone);
        Assert.DoesNotContain(ProductDiscovery.AllProducts.Assemblies,
            static a => ProductDiscovery.StandaloneProducts.ContainsKey(a.GetName().Name!));
    }

    /// <summary>
    /// <b>Beyan edilen bağımsız ürün GERÇEKTEN bağımsız</b> — diskten ölçülüyor.
    ///
    /// <para>
    /// Üç koşul, her biri istisnanın bir kör noktasını kapatıyor: (1) kendi
    /// <c>global.json</c>'u ve kök props'u içe aktarmayan kendi
    /// <c>Directory.Build.props</c>'u var — yani ayrı bir derleme; (2) hiçbir
    /// ürün/simülatör/test projesi ona referans vermiyor — yani Core
    /// kompozisyonunun parçası değil; (3) <c>Add*</c>/<c>Map*</c> uzantısı
    /// tanımlamıyor — olsaydı kapılar onu göremezdi ve istisna bir kör nokta
    /// olurdu. Üçüncüsü metin taraması (derleme yüklenemiyor); yanlış pozitif
    /// kırmızı yakar, sessiz bir yeşil üretmez.
    /// </para>
    /// </summary>
    [Fact]
    public void Bagimsiz_urun_gercekten_bagimsiz()
    {
        var projectFiles = ProductDiscovery.ProductAreas.Append("tests")
            .Select(static area => Path.Combine(RepositoryLayout.Root, area))
            .Where(Directory.Exists)
            .SelectMany(static dir => Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories))
            .Where(static file => !IsBuildOutput(file))
            .ToArray();

        foreach (var name in ProductDiscovery.StandaloneProducts.Keys)
        {
            var project = Assert.Single(ProductDiscovery.Projects, p => p.Project == name);
            var directory = Path.GetDirectoryName(project.File)!;

            Assert.True(File.Exists(Path.Combine(directory, "global.json")),
                $"{name} kendi global.json'una sahip değil — bağımsız bir derleme değil.");
            var props = Path.Combine(directory, "Directory.Build.props");
            Assert.True(File.Exists(props), $"{name} kendi Directory.Build.props'una sahip değil.");
            Assert.DoesNotContain("<Import", File.ReadAllText(props), StringComparison.Ordinal);

            // Ürünün kendi test projesi (tests/<ad>.Tests) onu sınamak için
            // referans vermek zorunda; kompozisyona katılmak sayılmaz. Başka
            // hiçbir proje referans veremez.
            var ownTests = Path.Combine(RepositoryLayout.Root, "tests", name + ".Tests", name + ".Tests.csproj");
            var referencing = projectFiles
                .Where(file => !string.Equals(file, project.File, StringComparison.Ordinal))
                .Where(file => !string.Equals(Path.GetFullPath(file), Path.GetFullPath(ownTests), StringComparison.Ordinal))
                .Where(file => File.ReadAllText(file).Contains(name + ".csproj", StringComparison.Ordinal))
                .ToArray();
            Assert.True(referencing.Length == 0,
                $"{name} bağımsız beyan edilmiş ama şu projeler ona referans veriyor: " +
                string.Join(", ", referencing) + " — artık kompozisyonun parçası; istisnadan çıkarın.");

            var registrars = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(static file => !IsBuildOutput(file))
                .Where(static file => Regex.IsMatch(File.ReadAllText(file),
                    @"\bthis\s+(IServiceCollection|IEndpointRouteBuilder)\b"))
                .ToArray();
            Assert.True(registrars.Length == 0,
                $"{name} kayıt uzantısı tanımlıyor ({string.Join(", ", registrars)}); keşif onu " +
                "yükleyemediği için kapılar bu uzantıları göremez.");
        }
    }

    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar) is var parts
        && (parts.Contains("bin", StringComparer.Ordinal) || parts.Contains("obj", StringComparer.Ordinal));

    // ------------------------------------------------- kapsamların İLİŞKİSİ

    /// <summary>
    /// <b>Kompozisyon kapanışı, bütün ürünün bir ALT KÜMESİ.</b>
    ///
    /// <para>
    /// Konsolidasyonun sessiz genişlemesini yakalayan iddia. Kapanış
    /// büyütülüp bütün ürüne eşitlenirse <c>ArchitectureTests</c> simülatörün
    /// ve CLI'nin <c>Add*</c> uzantılarını <b>gerçek bir
    /// <c>WebApplicationBuilder</c> üzerinde çağırmaya</b> başlar.
    /// </para>
    /// </summary>
    [Fact]
    public void Kompozisyon_kapanisi_urunun_alt_kumesi()
    {
        var all = ProductDiscovery.AllProducts.Assemblies
            .Select(static a => a.GetName().Name)
            .ToHashSet(StringComparer.Ordinal);

        var closure = ProductDiscovery.CompositionClosure
            .Select(static a => a.GetName().Name)
            .ToArray();

        Assert.NotEmpty(closure);

        var stray = closure.Where(name => !all.Contains(name!)).ToArray();

        Assert.True(
            stray.Length == 0,
            $"Kapanışta ürün olmayan derleme var: {string.Join(", ", stray)}.");

        // ÖZ alt küme: eşitlenirse kapsam ayrımı yok olmuş demektir.
        Assert.True(
            closure.Length < all.Count,
            $"Kapanış ({closure.Length}) bütün ürüne ({all.Count}) EŞİTLENDİ. " +
            "İki kapsamın ayrı olmasının sebebi ölçülmüş bir şey: üretim host'u " +
            "simülatörü ve CLI'yi yüklemiyor, ve ArchitectureTests bulduğu her kayıt " +
            "uzantısını gerçekten çağırıyor.");
    }

    /// <summary>
    /// <b>Kompozisyon kökü kapanışın içinde</b> — ve kapanış ondan başlıyor.
    /// </summary>
    [Fact]
    public void Kompozisyon_koku_kapanisin_icinde()
    {
        Assert.Contains(
            ProductDiscovery.CompositionRoot.GetName().Name,
            ProductDiscovery.CompositionClosure.Select(static a => a.GetName().Name),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// <b>Üretim kompozisyonuna girmeyen ürünler kapanışın DIŞINDA.</b>
    ///
    /// <para>
    /// Bu iddia bir <i>eksikliği</i> değil bir <b>kararı</b> sabitliyor:
    /// simülatör ve CLI üretim host'unun parçası değil, ve kapanışta
    /// görünmeleri bir kusur olurdu. Ölçüldü — bugün ikisi de dışarıda.
    /// </para>
    ///
    /// <para>
    /// Liste elle yazılmıyor: <b>hangi ürün projesinin üretim host'undan
    /// erişilebilir olmadığı</b> sorusunun cevabı keşfin kendisinden geliyor,
    /// yani ürün büyüdükçe iddia doğru kalıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Uretim_disindaki_urunler_kapanisin_disinda()
    {
        var closure = ProductDiscovery.CompositionClosure
            .Select(static a => a.GetName().Name)
            .ToHashSet(StringComparer.Ordinal);

        var outside = ProductDiscovery.AllProducts.Assemblies
            .Select(static a => a.GetName().Name)
            .Where(name => !closure.Contains(name!))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Boş olması, kapanışın bütün ürüne eşitlendiği anlamına gelir.
        Assert.NotEmpty(outside);
    }

    /// <summary>
    /// <b>Ürün süzgeci, önekin reddedeceği bir adı KABUL ediyor.</b>
    ///
    /// <para>
    /// <b>Bu testin var olma sebebi bir ölçüm.</b> Önek körlüğünü
    /// <see cref="ProductDiscovery.CompositionClosure"/>'ın içine geri koydum
    /// ve <b>üç kapı da yeşil kaldı</b> — çünkü bugün yeniden adlandırılmış
    /// hiçbir proje kapanışta değil. Yani düzeltmenin <b>bekçisi yoktu</b> ve
    /// bir sonraki kişi onu gerekçesiz bir süsleme sanıp geri alabilirdi.
    /// </para>
    ///
    /// <para>
    /// Davranışsal bir iddia kurulamıyor (kapanışta yeniden adlandırılmış
    /// proje yok), o yüzden süzgecin <b>kendisi</b> sınanıyor:
    /// <c>loganalyzer</c> bir ürün derlemesi ve <c>LogAnalyzer.</c> öneki onu
    /// <b>reddederdi</b>.
    /// </para>
    /// </summary>
    [Fact]
    public void Urun_suzgeci_onekin_reddedecegi_adi_kabul_ediyor()
    {
        var renamed = ProductDiscovery.Projects.Where(static p => p.Renamed).ToArray();

        Assert.NotEmpty(renamed);

        foreach (var project in renamed)
        {
            // Önekin reddedeceği bir ad olduğu ÖNCE doğrulanıyor: konvansiyona
            // uyan bir ada bakan test, önek kusurunu hiç ölçmezdi.
            Assert.False(
                project.AssemblyName.StartsWith("LogAnalyzer.", StringComparison.Ordinal),
                $"`{project.AssemblyName}` zaten önekli — bu test önek kusurunu ölçemez.");

            Assert.True(
                ProductDiscovery.IsProduct(project.AssemblyName),
                $"`{project.Project}` projesinin derleme adı `{project.AssemblyName}` ürün sayılmıyor. " +
                "Süzgeç `LogAnalyzer.` önekine geri dönmüş olabilir — önek bir KONVANSİYON, " +
                "derleyicinin ürettiği ad değil.");
        }
    }

    /// <summary>
    /// <b>Yeniden adlandırılmış bir proje keşfe önekle değil GERÇEK adıyla
    /// giriyor.</b>
    ///
    /// <para>
    /// <c>LogAnalyzer.Cli</c>'nin derleme adı <c>loganalyzer</c>. <c>LogAnalyzer.</c> önekine
    /// bakan bir keşif onu <b>hiç görmüyor</b>, ve önek bir <b>konvansiyon</b>
    /// — derleyicinin ürettiği ad değil.
    /// </para>
    ///
    /// <para>
    /// <b>Bugün kurbanı olmadığı ayrıca ölçüldü</b> ve bu yazılmak zorunda:
    /// CLI'yi referanslayan ürün projesi yok, yani öneki düzeltmek kapanışın
    /// kümesini değiştirmiyor (16 → 16). Kapatılan şey <b>gizli</b> bir kusur:
    /// yeniden adlandırılmış bir proje bir gün kapanışa girerse sessizce
    /// atlanırdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Yeniden_adlandirilmis_proje_gercek_adiyla_kesfediliyor()
    {
        var renamed = ProductDiscovery.Projects.Where(static p => p.Renamed).ToArray();

        // İddia adımı: yeniden adlandırma diye bir şey GERÇEKTEN varsa bu test
        // bir şey ölçüyor. Kalmadığı gün burası düşüyor ve testin neyi
        // koruduğu yeniden okunuyor — sessizce boş geçmiyor.
        Assert.NotEmpty(renamed);

        foreach (var project in renamed)
        {
            var discovered = ProductDiscovery.AllProducts.Assemblies
                .Select(static a => a.GetName().Name);

            Assert.Contains(project.AssemblyName, discovered, StringComparer.Ordinal);

            Assert.DoesNotContain(project.Project, discovered, StringComparer.Ordinal);
        }
    }
}
