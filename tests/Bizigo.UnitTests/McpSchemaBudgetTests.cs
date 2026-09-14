using System.Globalization;
using System.Text.Json;
using Bizigo.Api;
using Bizigo.Contracts.Security;
using Bizigo.Mcp;
using Microsoft.ML.Tokenizers;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Araç şemalarının bağlam maliyeti — bir sayı olarak.</b>
///
/// <para>
/// Teknik planın §9'u bunu bilmediğini yazıyordu: <i>"On beş aracın şeması her
/// bağlamda taşınıyor; bu bir bağlam bütçesi kalemi ve bu belgede sayısı
/// yok."</i> Sayı yoksa M04/M05 araç eklerken maliyeti göremez — ve
/// göremediği bir şeyi tartamaz.
/// </para>
///
/// <para>
/// <b>Neden tahmin değil ölçüm.</b> Akla gelen ilk çözüm <i>"≈4 karakter = 1
/// belirteç"</i> idi ve elendi: §6 ölçülmemiş bir şeyin çalıştığının
/// varsayılmasını yasaklıyor, ve <b>ölçülmüş görünen bir tahmin</b> o
/// yasağın en kötü hâli. Burada gerçek bir BPE sözlüğü koşuyor
/// (<c>o200k_base</c>) ve sözlük pakete gömülü: <b>ağ yok</b>.
/// </para>
///
/// <para>
/// <b>Sayının anlamı.</b> Ölçülen şey <c>tools/list</c> yanıtının tamamı —
/// yani modelin her konuşmada taşıdığı yük. İki rakam basılıyor: <b>toplam</b>
/// ve <b>araç başına</b>. İkincisi asıl değerli olan: yeni bir araç eklemenin
/// fiyatı.
/// </para>
///
/// <para>
/// <b>Bugünkü sayı küçük ve bu bir kusur değil.</b> M01 tek bir araç ilan
/// ediyor (<c>server.info</c>), yani ölçülen şey büyük ölçüde <i>zarf</i>
/// maliyeti + bir aracın marjinal maliyeti. On beş araç için bir sayı
/// <b>uydurmuyoruz</b>: o sayı M04/M05'in ölçümü, bizim tahminimiz değil.
/// </para>
/// </summary>
public sealed class McpSchemaBudgetTests
{
    /// <summary>
    /// <b>ARAÇ BAŞINA</b> belirteç tavanı. Toplam tavan bundan <b>türetiliyor</b>
    /// (<c>araç sayısı × bu sayı</c>), elle yazılmıyor.
    ///
    /// <para>
    /// <b>İlk hâli tek bir toplam tavandı (<c>400</c>) ve M04'te değiştirildi.</b>
    /// Sebep bir sayı sorunu değil bir <b>koordinasyon</b> sorunuydu: toplam
    /// tavan, araç ekleyen <b>her</b> ticket'ı aynı satıra dokunmaya zorluyor.
    /// M04 ve M02 aynı turda beş ve yedi araç ekliyordu; ikisi de bu satırı
    /// düzenleyecekti ve §9 tam olarak bunu yasaklıyor — <i>aynı satırı iki
    /// ajana sildirme</i>. Kalıp T48'in hamlesinin aynısı: elle tutulan sayıyı
    /// <b>türetilen</b> bir şeye çevirmek.
    /// </para>
    ///
    /// <para>
    /// <b>Ve soruyu da düzeltiyor.</b> Toplam tavan <i>"kaç araç var"</i> ile
    /// <i>"araçlar ne kadar pahalı"</i> sorularını tek sayıda karıştırıyordu:
    /// on ucuz araç ile üç şişkin araç aynı sayıyı üretebilir. Araç başına tavan
    /// yalnızca ikinci soruyu soruyor, ve cevaplanabilir olan o.
    /// </para>
    ///
    /// <para>
    /// <b>Sayının türetildiği ölçüm</b> (o200k_base, <c>tools/list</c> teldeki hâli):
    /// </para>
    /// <list type="bullet">
    /// <item><c>server.info</c> — <b>194</b>. Alt sınıra yakın: iki boş şema, iki cümle.</item>
    /// <item><c>catalog.parsers</c> — 284 · <c>inventory.list</c> — 325.</item>
    /// <item><c>alerts.triggers</c> — 415 · <c>alerts.rules</c> — 418.</item>
    /// <item>
    /// <c>logs.search</c> — <b>578</b>, bugünün en pahalısı: on bir alanlı satır
    /// şeması, sekiz girdi alanı, üç cümlelik açıklama.
    /// </item>
    /// </list>
    ///
    /// <para>
    /// Tavan <b>700</b>: bugünün en pahalısının üstünde ~120 belirteç pay. O pay
    /// bir aracın <i>bir alan grubu daha</i> kazanmasına yetiyor, <i>iki katına
    /// çıkmasına</i> yetmiyor. Yükseltmek serbest — <b>sessizce</b> yükselmek değil.
    /// </para>
    /// </summary>
    private const int PerToolTokenCeiling = 700;

    /// <summary>
    /// Ölçülmüş <b>taban</b>: bir aracın olabileceği en ucuz hâl
    /// (<c>server.info</c>, 194).
    ///
    /// <para>
    /// Burada durmasının tek sebebi <b>ölçüm aracının kendisini ölçmek</b> (§6):
    /// sayıcı bir gün sessizce küçük sayılar dönerse araç başına tavan her zaman
    /// sağlanır ve kapı <b>hiçbir şey ifade etmez</b>. En ucuz aracın bu tabanın
    /// altına düşmesi, bütçenin iyileşmesi değil <b>ölçümün bozulması</b> demek.
    /// </para>
    /// </summary>
    private const int CheapestToolFloor = 150;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// <c>o200k_base</c> — bugünün Anthropic/OpenAI sınıfı modellerinin
    /// kullandığı BPE. Sözlük <c>Microsoft.ML.Tokenizers.Data.O200kBase</c>
    /// paketine gömülü, yani koşum ağ istemiyor.
    /// </summary>
    private static readonly TiktokenTokenizer Tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

    [Theory]
    [MemberData(nameof(McpComplianceTests.Surfaces), MemberType = typeof(McpComplianceTests))]
    public async Task Arac_semalarinin_baglam_maliyeti(McpSurface surface)
    {
        // Araçların bağımlılıkları çözülebilir olmalı — `Empty()` ile
        // kurulan sağlayıcıda M04'ün okuma araçları hiç örneklenmiyor ve
        // bütçe tavanın çok altında, SESSİZCE yeşil kalıyordu.
        await using var services = McpTestServices.ForDiscoveredTools();
        var options = BizigoMcpServer.CreateOptions(
            surface,
            McpBoundaryDeclaration.Declare(DataBoundary.Internal, "şema bütçesi: birim testi"),
            McpEndpoints.ToolAssemblies,
            services);

        await using var session = await McpTestSession.StartAsync(options, services, cancellationToken: Ct);

        var tools = await session.Client.ListToolsAsync(cancellationToken: Ct);

        Assert.NotEmpty(tools);

        // Teldeki hâl: istemcinin gerçekten aldığı gövde. Araç nesnelerini
        // kendi ayarlarımızla serileştirmek BAŞKA bir sayı üretirdi ve o sayı
        // hiçbir yerde taşınmıyor olurdu.
        var payload = JsonSerializer.Serialize(
            new ListToolsResult { Tools = [.. tools.Select(static t => t.ProtocolTool)] },
            McpJsonUtilities.DefaultOptions);

        var total = Tokenizer.CountTokens(payload);

        var perTool = tools
            .Select(tool => (
                tool.Name,
                Tokens: Tokenizer.CountTokens(
                    JsonSerializer.Serialize(tool.ProtocolTool, McpJsonUtilities.DefaultOptions)),

                // `description` AYRICA sayılıyor. "En pahalı kalem şema değil
                // açıklama" bu depoda bir kez ölçülmüş bir cümle; ayrı bir kolon
                // olmadan bir sonraki kişi onu folklor olarak okuyup tahmin
                // etmek zorunda kalıyor (§6: ölçülmüş görünen bir tahmin, en
                // kötü hâl).
                Description: Tokenizer.CountTokens(tool.ProtocolTool.Description ?? string.Empty)))
            .OrderByDescending(static entry => entry.Tokens)
            .ToArray();

        // TOPLAM TAVAN TÜRETİLİYOR, elle yazılmıyor: araç eklemek bu dosyayı
        // düzenlemeyi gerektirmiyor ve iki ticket aynı satırda buluşmuyor (§9).
        var totalCeiling = perTool.Length * PerToolTokenCeiling;

        var report = string.Join(
            "\n",
            [
                $"MCP bağlam maliyeti — yüzey `{McpSurfaces.WireName(surface)}`",
                $"  tools/list toplam : {total.ToString(CultureInfo.InvariantCulture)} belirteç "
                    + $"({payload.Length.ToString(CultureInfo.InvariantCulture)} karakter)",
                $"  araç sayısı       : {perTool.Length.ToString(CultureInfo.InvariantCulture)}",
                $"  türetilen tavan   : {totalCeiling.ToString(CultureInfo.InvariantCulture)} "
                    + $"({perTool.Length.ToString(CultureInfo.InvariantCulture)} × "
                    + $"{PerToolTokenCeiling.ToString(CultureInfo.InvariantCulture)})",
                "  araç başına (parantez içi: `description` payı):",
                .. perTool.Select(static entry =>
                    $"    {entry.Name,-24} {entry.Tokens.ToString(CultureInfo.InvariantCulture),5} belirteç"
                    + $"  ({entry.Description.ToString(CultureInfo.InvariantCulture)})"),
            ]);

        TestContext.Current.TestOutputHelper?.WriteLine(report);

        var overBudget = perTool.Where(static entry => entry.Tokens > PerToolTokenCeiling).ToArray();

        Assert.True(
            overBudget.Length == 0,
            "Şu araç(lar) araç başına bütçeyi aştı:\n  "
            + string.Join(
                "\n  ",
                overBudget.Select(static entry =>
                    $"{entry.Name}: {entry.Tokens.ToString(CultureInfo.InvariantCulture)} belirteç"))
            + $"\n\nTavan araç başına {PerToolTokenCeiling.ToString(CultureInfo.InvariantCulture)}.\n\n{report}\n\n"
            + "Bu bir performans hatası DEĞİL, bir karar noktası: bir aracın bağlam maliyeti "
            + "büyüdü ve her konuşmada taşınıyor. İlk bakılacak yer `description` — ölçüm "
            + "yukarıda, parantez içinde. Sonra çıktı şemasındaki alanlar: modelin karar "
            + "veremediği bir alan her çağrıda bedava değil.");

        Assert.True(
            total <= totalCeiling,
            $"`tools/list` yükü {total.ToString(CultureInfo.InvariantCulture)} belirteç; "
            + $"türetilen tavan {totalCeiling.ToString(CultureInfo.InvariantCulture)}.\n\n{report}\n\n"
            + "Araç başına tavanlar sağlanıyorsa buranın düşmesi ZARFIN büyüdüğü anlamına gelir "
            + "(protokol zarfı, `annotations`, SDK'nın eklediği alanlar) — araçların değil.");

        // ÖLÇÜM ARACININ KENDİSİ: en ucuz araç bilinen tabanın altına düşerse
        // sayıcı bozulmuş, bütçe iyileşmiş değil (§6).
        Assert.True(
            perTool[^1].Tokens >= CheapestToolFloor,
            $"En ucuz araç (`{perTool[^1].Name}`) {perTool[^1].Tokens.ToString(CultureInfo.InvariantCulture)} "
            + $"belirteç ölçüldü; ölçülmüş taban {CheapestToolFloor.ToString(CultureInfo.InvariantCulture)}.\n\n"
            + "Bütçenin iyileşmesi DEĞİL, sayıcının bozulması daha olası: sessizce küçük sayılar "
            + "dönen bir sayıcı yukarıdaki tavanları her zaman sağlar ve kapı hiçbir şey ifade etmez.");
    }

    /// <summary>
    /// <b>Ölçüm aracının kendisi ölçülüyor.</b>
    ///
    /// <para>
    /// §6: yeşil bir sonuç, ölçümün <i>yapılmadığı</i> anlamına da gelebiliyor.
    /// Belirteç sayıcı sessizce sıfır dönerse yukarıdaki tavan her zaman
    /// sağlanır ve bütçe bekçisi <b>hiçbir şey ifade etmez</b>. Bu test sayıcının
    /// gerçekten saydığını sabitliyor: bilinen bir metin, bilinen bir aralık.
    /// </para>
    /// </summary>
    [Fact]
    public void Belirtec_sayici_gercekten_sayiyor()
    {
        // 26 harf + boşluklar. BPE'de tam sayı model sürümüne bağlı, o yüzden
        // bir aralık: sıfır olmadığını ve karakter sayısını aşmadığını
        // ölçüyoruz — ikisi de sayıcının bozulma biçimleri.
        const string sample = "the quick brown fox jumps over the lazy dog";

        var tokens = Tokenizer.CountTokens(sample);

        Assert.InRange(tokens, 5, sample.Length);
    }
}
