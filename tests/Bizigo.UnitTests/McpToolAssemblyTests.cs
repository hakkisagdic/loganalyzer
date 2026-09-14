using System.Reflection;
using Bizigo.Api;
using Bizigo.Cli;
using Bizigo.Mcp;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Araç taşıyan her derleme, üretimin beyan ettiği listede mi.</b>
///
/// <para>
/// <b>Neden bu bekçi var — ölçüldü.</b> M01 araç keşfini kompozisyon
/// kökünden <c>GetReferencedAssemblies()</c> ile yapıyordu. Sessizce eksik
/// çalışıyordu: <b>derleyici, kodunda hiçbir tipine dokunulmayan bir
/// <c>ProjectReference</c>'ı meta veriden buduyor</b>, ve araç taşıyan bir
/// derleme — tam da araç sınıflarından başka bir şey içermediği için — kökün
/// referans listesinde hiç görünmüyordu.
/// </para>
///
/// <para>
/// Kusurun biçimi, kapatmak için var olduğu deliğin aynısıydı: M04 beş araç
/// yazdı, referansları ekledi, çözüm <b>0 uyarıyla</b> derlendi, uyum kapısı
/// <b>yeşil</b> kaldı — ve sunucu hâlâ tek araç ilan ediyordu. M03 aynı
/// mekanizmayı kendi kolunda bağımsız olarak ölçtü. Üç ajan üç ayrı yerden
/// aynı şeye çarptı.
/// </para>
///
/// <para>
/// <b>İmza düzeltildi</b> — araç derlemeleri artık <c>typeof(X).Assembly</c>
/// ile <b>açıkça</b> veriliyor, yani budama tanım gereği olmuyor. Geriye tek
/// bir risk kaldı: <b>çağıranın unutması</b>. Ve çalışma zamanında
/// <i>"unutuldu"</i> ile <i>"gerçekten araç yok"</i> ayırt edilemiyor — ikisi
/// de sıfır araç demek. Bu yüzden onu bir istisna değil <b>bir bekçi</b>
/// tutuyor.
/// </para>
///
/// <para>
/// <b>Türetilen taraf ile beyan edilen taraf.</b> Denetlenen küme derlenmiş
/// çıktıdan <b>taranarak</b> çıkıyor; elle olan taraf üretimin beyanı
/// (<c>McpEndpoints.ToolAssemblies</c>, <c>McpCommandHandlers.ToolAssemblies</c>).
/// Kalıp <c>McpComplianceTests.Sunucunun_ilan_ettigi_araclar</c> ile aynı ve
/// ölçüt de aynı: elle liste kapının <b>gözü</b> değil <b>beyanı</b>.
/// </para>
/// </summary>
public sealed class McpToolAssemblyTests
{
    /// <summary>
    /// Beyan edilmesi <b>gerekmeyen</b> derlemeler.
    ///
    /// <para>
    /// <c>Bizigo.Mcp</c> — çekirdek, her zaman örtük ekleniyor
    /// (<c>BizigoMcpServer.WithCore</c>); beyan listesine yazmak, unutulabilir
    /// olmayan bir şeyi unutulabilir yapardı.
    /// </para>
    ///
    /// <para>
    /// Test derlemeleri — kendi araçlarını taşıyorlar (<c>TestOnlyTool</c>,
    /// <c>NeverEndingTool</c>, <c>ScopeEchoTool</c>) ve bunlar üretimde ilan
    /// edilmemeli. Ayrımı <b>ad</b> üzerinden yapmak yerine <b>test SDK'sı
    /// referansı</b> üzerinden yapmak daha doğru olurdu, ama ad kuralı bu
    /// depoda <c>CiCoverageTests</c>'in de kullandığı ölçüt ve iki yerde iki
    /// ölçüt tutmak §9'un uyardığı ayrışmayı doğurur.
    /// </para>
    /// </summary>
    private static bool IsExempt(string assemblyName) =>
        assemblyName is "Bizigo.Mcp"
        || assemblyName.EndsWith("Tests", StringComparison.Ordinal);

    /// <summary>
    /// Üretimin beyan ettiği araç derlemeleri — <b>iki host birden</b>.
    ///
    /// <para>
    /// İkisi ayrı listeler ve öyle kalmalı: stdio'da simülatör yüzeyi de açık,
    /// HTTP'de değil. Ama <b>bu bekçi ikisinin birleşimine</b> bakıyor — bir
    /// aracın yalnızca CLI'dan sunulması meşru, hiçbir yerden sunulmaması
    /// değil.
    /// </para>
    /// </summary>
    private static IReadOnlySet<string> Declared() =>
        McpEndpoints.ToolAssemblies
            .Concat(McpCommandHandlers.ToolAssemblies)
            .Select(static assembly => assembly.GetName().Name!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Derlenmiş çıktıdaki <c>Bizigo*.dll</c>'lerden araç taşıyanlar.
    ///
    /// <para>
    /// <b>Yüklenemeyen bir derleme atlanmıyor, testi düşürüyor.</b> Sessizce
    /// atlamak bu bekçiyi tam da kapatmaya çalıştığı biçimde kör ederdi: araç
    /// taşıyan bir derleme yüklenemediğinde "araç yok" ile "bakılamadı" aynı
    /// çıktıyı verirdi.
    /// </para>
    /// </summary>
    private static (IReadOnlySet<string> Carriers, IReadOnlyList<string> Unreadable) Scan()
    {
        var carriers = new HashSet<string>(StringComparer.Ordinal);
        var unreadable = new List<string>();

        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "Bizigo*.dll"))
        {
            Assembly assembly;

            try
            {
                assembly = Assembly.LoadFrom(path);
            }
            catch (Exception error) when (error is BadImageFormatException or FileLoadException)
            {
                unreadable.Add($"{Path.GetFileName(path)}: {error.Message}");

                continue;
            }

            var name = assembly.GetName().Name!;

            if (IsExempt(name))
            {
                continue;
            }

            if (McpToolDiscovery.ToolTypes([assembly]).Count > 0)
            {
                carriers.Add(name);
            }
        }

        return (carriers, unreadable);
    }

    /// <summary>
    /// Araç taşıyan her derleme beyan edilmiş mi.
    /// </summary>
    [Fact]
    public void Arac_tasiyan_her_derleme_beyan_edilmis()
    {
        var (carriers, unreadable) = Scan();

        Assert.True(
            unreadable.Count == 0,
            "Bu derlemeler okunamadı, dolayısıyla araç taşıyıp taşımadıkları BİLİNMİYOR:\n  "
            + string.Join("\n  ", unreadable));

        var declared = Declared();

        var missing = carriers.Where(name => !declared.Contains(name)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0,
            "Bu derlemeler MCP aracı taşıyor ama hiçbir host onları beyan etmiyor:\n  "
            + string.Join("\n  ", missing)
            + "\n\nAraçları yazılmış, referansları eklenmiş, çözüm derleniyor — ve sunucu "
            + "onları HİÇ ilan etmiyor. `McpEndpoints.ToolAssemblies` ya da "
            + "`McpCommandHandlers.ToolAssemblies` listesine `typeof(X).Assembly` olarak ekleyin. "
            + "Dizgi ya da `Assembly.Load` yazmayın: bir tip adı yazmak referansı GERÇEK yapıyor "
            + "ve derleyicinin budamasını imkânsız kılıyor — düzeltilen kusur tam olarak buydu.");
    }

    /// <summary>
    /// <b>Beyan edilen her derleme gerçekten araç taşıyor mu.</b>
    ///
    /// <para>
    /// Ters yön, ve kaybı ayrı: araç taşımayan bir derlemeyi beyan etmek
    /// zararsız görünüyor ama listeyi <b>anlamsızlaştırıyor</b> — bir gün
    /// listedeki adların hangisinin gerçekten iş yaptığı sorulduğunda cevap
    /// "bak ve gör" olur, ve o cevap listeyi bakımsız bırakır.
    /// </para>
    ///
    /// <para>
    /// Bugün iki liste de <b>boş</b>, yani bu test bugün hiçbir şey
    /// söylemiyor. Bilerek şimdi yazıldı: M03/M04/M05 listeyi doldurduğunda
    /// kendiliğinden dişleniyor, ve "hat bitince ekleriz" demek bu depoda
    /// eklenmeyen kapıların yoluydu.
    /// </para>
    /// </summary>
    [Fact]
    public void Beyan_edilen_her_derleme_arac_tasiyor()
    {
        var idle = McpEndpoints.ToolAssemblies
            .Concat(McpCommandHandlers.ToolAssemblies)
            .Where(static assembly => McpToolDiscovery.ToolTypes([assembly]).Count == 0)
            .Select(static assembly => assembly.GetName().Name!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            idle.Length == 0,
            "Bu derlemeler beyan listesinde ama hiç MCP aracı taşımıyor:\n  "
            + string.Join("\n  ", idle)
            + "\n\nListeden çıkarın ya da aracını yazın.");
    }

    /// <summary>
    /// <b>Çekirdek beyan listesinde OLMAMALI.</b>
    ///
    /// <para>
    /// <c>Bizigo.Mcp</c> örtük olarak ekleniyor. Bir de listeye yazılırsa
    /// araçları <b>iki kez</b> örneklenir ve <c>tools/list</c> aynı adı iki kez
    /// ilan eder — istemci tarafında tanımsız davranış.
    /// <c>BizigoMcpServer.WithCore</c> tekilleştiriyor, yani bugün patlamaz;
    /// bu test o tekilleştirmeye <b>güvenmek zorunda kalmamak</b> için var.
    /// </para>
    /// </summary>
    [Fact]
    public void Cekirdek_beyan_listesinde_degil()
    {
        Assert.DoesNotContain(typeof(BizigoMcpServer).Assembly, McpEndpoints.ToolAssemblies);
        Assert.DoesNotContain(typeof(BizigoMcpServer).Assembly, McpCommandHandlers.ToolAssemblies);
    }

    /// <summary>
    /// <b>Taramanın kendi sınavı.</b> Tarama gerçekten araç bulabiliyor mu?
    ///
    /// <para>
    /// Yukarıdaki iki test bugün <b>boş kümelerle</b> yeşil — ve boş kümeyle
    /// yeşil yanan bir bekçi, bozuk bir taramayla da yeşil yanar. Bu test
    /// taramanın çalıştığını sabitliyor: test derlemesi araç taşıyor ve
    /// <c>ToolTypes</c> onu buluyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Tarama_arac_bulabiliyor() =>
        Assert.NotEmpty(McpToolDiscovery.ToolTypes([typeof(McpToolAssemblyTests).Assembly]));
}
