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
    /// <c>tools/list</c> yükünün belirteç tavanı.
    ///
    /// <para>
    /// Tavanın işlevi bir performans hedefi değil <b>görünürlük</b>: araç
    /// eklemek bu sabiti de değiştirmeyi gerektiriyor, yani bağlam bütçesinin
    /// büyümesi sessiz olamıyor. Kalıp <c>ProducesContractTests.ExpectedExemptCount</c>'tan.
    /// </para>
    /// </summary>
    private const int ToolListTokenCeiling = 400;

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
        await using var services = McpTestServices.Empty();
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

        Assert.True(
            total <= ToolListTokenCeiling,
            $"`tools/list` yükü {total.ToString(CultureInfo.InvariantCulture)} belirtece çıktı; "
            + $"tavan {ToolListTokenCeiling.ToString(CultureInfo.InvariantCulture)}.\n\n{report}\n\n"
            + "Bu bir performans hatası DEĞİL, bir karar noktası: bağlam bütçesi büyüdü. "
            + "Tavanı yükseltmek serbest — ama görünür olsun diye buradan geçiyor. "
            + "Yükseltirken araç açıklamalarının uzunluğuna da bakın: en pahalı kalem genelde "
            + "şema değil, `description` metnidir.");
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
