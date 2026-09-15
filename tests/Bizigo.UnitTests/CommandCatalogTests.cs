using System.Text.RegularExpressions;

using Bizigo.Commands;
using Bizigo.Commands.Mcp;
using Bizigo.Mcp;

using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>M02'nin bitti tanımı</b>: komut çekirdeğindeki her komut ya MCP aracı ya
/// gerekçeli muafiyet; sayı sabitle tutuluyor.
///
/// <para>
/// <b>Kapı TEK YÖNLÜ ve bu bir eksiklik değil.</b> Tuttuğu şey <i>"her komut ya
/// araç ya muaf"</i>; tersi — <i>"her araç bir komuttan doğar"</i> — bir şart
/// DEĞİL ve olmamalı: M03/M04/M05'in araçlarının hiçbirinin CLI komutu yok.
/// Simetriyi bir eksiklik sanıp "tamamlamak", kapıyı ilk yeni araç ailesinde
/// kıracak bir şart eklemek olur.
/// </para>
/// </summary>
public sealed partial class CommandCatalogTests
{
    /// <summary>
    /// <c>Program.cs</c>'te bir komuta eylem bağlayan çağrı. Yaprak komut
    /// sayısı bundan <b>sayılıyor</b>, elle yazılmıyor.
    /// </summary>
    [GeneratedRegex(@"\.SetAction\(", RegexOptions.ExplicitCapture)]
    private static partial Regex LeafCommand();

    /// <summary><c>new Command("&lt;ad&gt;"</c> — CLI'daki yaprak/grup adları.</summary>
    [GeneratedRegex("""new Command\(\s*"(?<name>[a-z-]+)"\s*,""", RegexOptions.ExplicitCapture)]
    private static partial Regex CommandName();

    private static string ProgramSource() =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root, "src", "Bizigo.Cli", "Program.cs"));

    // ---------------------------------------------------------- kapsama ---

    /// <summary>
    /// <b>Katalog CLI ile aynı sayıda komut taşıyor</b> — ve sayı iki taraftan
    /// da <b>sayılıyor</b>.
    ///
    /// <para>
    /// Sabit yazılmadı ve gerekçesi ölçüldü: bu dal M01'in üstünde duruyor ve
    /// <c>mcp serve</c> orada doğdu; ana ağaçta <c>SetAction</c> sayısı <b>10</b>,
    /// burada <b>12</b>. Bugünkü ağaca göre çivilenmiş bir sabit, M01 merge
    /// olduğu gün kapıyı kırmızı yakar ve sebebi <b>yanlış dalda</b> aranırdı.
    /// </para>
    ///
    /// <para>
    /// Kalan tek elle yazılan sayı muafiyet sayısı (<see cref="ExpectedExemptCount"/>)
    /// ve o <b>M01'den bağımsız</b>: muafiyet listesi bu ticket'ın kendi kararı.
    /// </para>
    /// </summary>
    [Fact]
    public void Katalog_CLI_yaprak_komutlariyla_ayni_sayida()
    {
        var leaves = LeafCommand().Matches(ProgramSource()).Count;

        Assert.True(leaves > 0, "`Program.cs`'te hiç `SetAction` bulunamadı — desen bozulmuş olabilir.");

        Assert.True(
            leaves == CommandCatalog.All.Count,
            $"CLI {leaves} yaprak komut taşıyor, katalog {CommandCatalog.All.Count} satır. " +
            "Bir komut eklendi ve kataloğa yazılmadı (ya da tersi); parite iddiası bu eşitlik.");
    }

    /// <summary>
    /// Katalogdaki her komutun CLI'da bir karşılığı var — <b>adıyla</b>.
    ///
    /// <para>
    /// Sayı eşitliği tek başına iki hatanın birbirini götürmesine açık: bir
    /// komut silinip başkası eklenirse sayı aynı kalır. Ad kontrolü o boşluğu
    /// kapatıyor.
    /// </para>
    ///
    /// <para>
    /// <b>SINIRI: yalnızca YAPRAK adı sınanıyor, tam yol değil.</b>
    /// <c>parser lint</c> ile (varsayımsal) <c>sigma lint</c> aynı yaprağı
    /// taşısaydı bu test ikisini ayırt edemezdi — biri katalogdan düşse bile
    /// diğerinin adı eşleşmeyi sağlardı. Bugün çakışma <b>yok</b> (ölçüldü);
    /// tam yol eşleştirmesi <c>Program.cs</c>'in ağaç yapısını kaynaktan
    /// çıkarmayı gerektiriyor ve <b>yapılmadı</b>.
    /// </para>
    ///
    /// <para>
    /// Sınırın burada yazılı olması bir kapsam iddiası değil kapsamın
    /// <b>kendisi</b>: yazılmasaydı bir sonraki okuyan bu testi tam yol
    /// eşleştirmesi sanardı.
    /// </para>
    /// </summary>
    [Fact]
    public void Katalogdaki_her_komut_CLI_da_var()
    {
        var declared = CommandName().Matches(ProgramSource())
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = CommandCatalog.All
            .Select(c => c.CliPath.Split(' ')[^1])
            .Where(leaf => !declared.Contains(leaf))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Katalogda olup CLI'da bulunamayan komut: " + string.Join(", ", missing));
    }

    // ------------------------------------------------------- araç / muaf ---

    /// <summary>
    /// <b>Kapının kendisi.</b> Araç olarak işaretlenmiş her komut gerçekten
    /// ilan ediliyor, ve ilan edilen her komut aracı katalogda araç olarak
    /// işaretli.
    ///
    /// <para>
    /// <b>Küme KEŞFEDİLİYOR</b> (<see cref="McpToolDiscovery"/>), elle
    /// yazılmıyor. Elle yazılan bir liste bu depoda bekçiyi <b>dört kez</b> kör
    /// etti; ayrıca M01'in uyum kapısı ayrı bir liste taşıyor ve iki liste
    /// aynı kaynaktan beslenmezse <b>ikisi de kendi içinde tutarlı kalarak</b>
    /// ayrışabilirdi — S04'ün "baseline'ın iki gösterimi" deseni.
    /// </para>
    /// </summary>
    [Fact]
    public void Araclar_katalogla_birebir()
    {
        using var services = new ServiceCollection().AddBizigoCommandTools().BuildServiceProvider();

        var discovered = McpToolDiscovery
            .Instantiate(
                McpToolDiscovery
                    .ToolTypes([typeof(CommandTool).Assembly])
                    .Where(static t => typeof(CommandTool).IsAssignableFrom(t)),
                McpSurface.Product,
                services)
            .Select(static tool => tool.ToolName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(discovered);

        var expected = CommandCatalog.Tools.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        Assert.True(
            discovered.SetEquals(expected),
            "Katalog ile ilan edilen araçlar ayrıştı.\n" +
            "  Araç var, katalogda `Tool` değil: " + string.Join(", ", discovered.Except(expected)) + "\n" +
            "  Katalogda `Tool`, aracı yok: " + string.Join(", ", expected.Except(discovered)));
    }

    /// <summary>
    /// <b>Muafiyet sayısı sabitle çivili</b> — muafiyet eklemek <b>iki ayrı
    /// bilinçli hareket</b>: kataloğa gerekçeyi yazmak ve bu sabiti
    /// değiştirmek. Tek başına birincisi bir kaçış kapısı, tek başına ikincisi
    /// gerekçesiz bir sayı. Kalıp T43'ün <c>constraints_waived</c>'ı ve T44'ün
    /// 2→1 düşüşüyle aynı.
    /// </summary>
    [Fact]
    public void Muaf_sayisi_sabitle_tutuluyor()
    {
        Assert.True(
            CommandCatalog.Exempt.Count == ExpectedExemptCount,
            $"Katalogda {CommandCatalog.Exempt.Count} muafiyet var, beklenen {ExpectedExemptCount}. " +
            $"Değişiklik bilinçliyse `{nameof(ExpectedExemptCount)}`'u da güncelleyin.");
    }

    /// <summary>
    /// <b>Gerekçesiz muafiyet yok.</b> Muafiyet <i>"araç yapmaya üşendik"</i>
    /// değil <b>"araç olması yanlış olur"</b> demek; gerekçesiz bir muafiyet
    /// ikisini ayırt edilemez yapardı.
    /// </summary>
    [Fact]
    public void Her_muafiyetin_gerekcesi_dolu()
    {
        foreach (var command in CommandCatalog.Exempt)
        {
            var reason = Assert.IsType<CommandExposure.Exempt>(command.Exposure).Reason;

            Assert.True(
                reason.Trim().Length >= 40,
                $"`{command.Name}` muafiyetinin gerekçesi bir cümle bile değil: '{reason}'");
        }
    }

    /// <summary>Araç + muaf = komutların tamamı; üçüncü bir hâl yok (§8).</summary>
    [Fact]
    public void Ucuncu_bir_hal_yok()
    {
        Assert.Equal(
            CommandCatalog.All.Count,
            CommandCatalog.Tools.Count + CommandCatalog.Exempt.Count);
    }

    /// <summary>
    /// Muaf komutların sayısı. <b>M01'den bağımsız</b> — bu ticket'ın kendi
    /// kararı, dolayısıyla dal birleşmeleri bu sayıyı kaydırmıyor.
    /// </summary>
    // M16: `rca quota` eklendi. Muafiyetin gerekçesi kataloğun kendisinde ve
    // ölçülmüş: modelin cevabı `rca.trigger`'ın reddiyle ZATEN geliyor, yani
    // araç eklemek yeni bir yetenek değil yalnızca bağlam bütçesi olurdu (§9).
    private const int ExpectedExemptCount = 6;
}
