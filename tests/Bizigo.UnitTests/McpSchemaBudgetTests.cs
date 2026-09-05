using System.Globalization;
using System.Text.Json;
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
    /// <b>Araç başına</b> belirteç tavanı — toplam ondan türüyor.
    ///
    /// <para>
    /// <b>M02'de yapı değişti ve sebebi ölçülmüş bir eğilim.</b> Sabit önce bir
    /// TOPLAM tavandı (400) ve yedi araç eklenince 2.400'e çekilmesi gerekti.
    /// Ama eğilim şunu söylüyor: MCP planı ~15 araç öngörüyor, araç başına ~280
    /// belirteçle toplam <b>~4.500</b>'e çıkıyor. Toplam tavanla her araç
    /// ailesi sabiti yeniden düzenlerdi — ve o noktada kapı bir <b>kayıt</b>
    /// olmaktan çıkıp <b>güncellenmesi rutinleşen bir sabite</b> dönerdi. Bu
    /// deponun defalarca adını koyduğu şey; elle tutulan sayı er ya da geç
    /// bekçiyi kör ediyor.
    /// </para>
    ///
    /// <para>
    /// <b>Araç başına tavan bunu yapısal olarak kaldırıyor:</b> yeni bir araç
    /// eklemek sabiti düzenlemeyi <b>gerektirmiyor</b>, ve kapı hâlâ gerçek bir
    /// şey ölçüyor — <i>"bir aracın bütçesi şunu aşamaz"</i>. Disiplin de
    /// maliyetin gerçekten olduğu yere biniyor: <c>description</c> metinleri.
    /// M02'de şema açıklamaları kırpılınca yük <b>2.484 → 2.248</b>'e indi (%10).
    /// </para>
    ///
    /// <para>
    /// <b>600 nereden geliyor.</b> Ölçülen dağılım: ortalama ~280, en ucuz
    /// <c>server.info</c> 194, en pahalı <c>fields.coverage</c> 481. Tavan en
    /// pahalı araca <b>%25 pay</b> bırakıyor. Daha dar bir tavan (örn. 500)
    /// gürültüyle kırmızı yanar ve rutin olarak yükseltilirdi — yani kaldırmaya
    /// çalıştığımız hâle geri dönerdi. 600'ü aşan bir araç fazla iş yapıyor
    /// demektir ve bir konuşmayı hak eder.
    /// </para>
    /// </summary>
    private const int ToolTokenCeiling = 600;

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
        await using var services = McpTestServices.Production();
        var options = BizigoMcpServer.CreateOptions(surface, typeof(global::Program).Assembly, services);

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
                    JsonSerializer.Serialize(tool.ProtocolTool, McpJsonUtilities.DefaultOptions))))
            .OrderByDescending(static entry => entry.Tokens)
            .ToArray();

        var report = string.Join(
            "\n",
            [
                $"MCP bağlam maliyeti — yüzey `{McpSurfaces.WireName(surface)}`",
                $"  tools/list toplam : {total.ToString(CultureInfo.InvariantCulture)} belirteç "
                    + $"({payload.Length.ToString(CultureInfo.InvariantCulture)} karakter)",
                $"  araç sayısı       : {perTool.Length.ToString(CultureInfo.InvariantCulture)}",
                "  araç başına:",
                .. perTool.Select(static entry =>
                    $"    {entry.Name,-24} {entry.Tokens.ToString(CultureInfo.InvariantCulture),5} belirteç"),
            ]);

        TestContext.Current.TestOutputHelper?.WriteLine(report);

        var over = perTool.Where(entry => entry.Tokens > ToolTokenCeiling).ToArray();

        Assert.True(
            over.Length == 0,
            "Araç bütçesini aşan araç(lar): "
            + string.Join(", ", over.Select(static e =>
                $"{e.Name} ({e.Tokens.ToString(CultureInfo.InvariantCulture)})"))
            + $"; araç başına tavan {ToolTokenCeiling.ToString(CultureInfo.InvariantCulture)}.\n\n{report}\n\n"
            + "Bu bir performans hatası DEĞİL, bir karar noktası. En pahalı kalem genelde şema "
            + "değil `description` metnidir; önce onu kırpın. Tavanı yükseltmek son çare — "
            + "yükseltilen bir tavan bir sonraki araçta yine yükseltilir ve kapı bir kayıt "
            + "olmaktan çıkar.");

        // Toplam TÜRETİLİYOR, elle yazılmıyor: yeni araç eklemek bu satırı
        // değiştirmeyi gerektirmiyor ve bütçe yine de gerçek bir şey ölçüyor.
        var derivedCeiling = perTool.Length * ToolTokenCeiling;

        Assert.True(
            total <= derivedCeiling,
            $"`tools/list` yükü {total.ToString(CultureInfo.InvariantCulture)} belirteç; "
            + $"türetilmiş tavan {derivedCeiling.ToString(CultureInfo.InvariantCulture)} "
            + $"({perTool.Length.ToString(CultureInfo.InvariantCulture)} araç × "
            + $"{ToolTokenCeiling.ToString(CultureInfo.InvariantCulture)}).\n\n{report}\n\n"
            + "Araçların hiçbiri tek başına tavanı aşmıyorsa ama toplam aşıyorsa, fark "
            + "araçlarda değil ZARFTA: `tools/list` yanıtının kendi yükü büyümüş demektir.");
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
