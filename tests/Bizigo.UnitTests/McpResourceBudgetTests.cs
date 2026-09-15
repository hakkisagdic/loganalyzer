using System.Text.Json;
using Bizigo.Mcp;
using Microsoft.ML.Tokenizers;
using ModelContextProtocol;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Kaynak ilanının bağlam maliyeti.</b>
///
/// <para>
/// M07'nin ticket'ı bir kazanç iddia ediyor (§6.3): <i>"kaynaklar araç değil,
/// yani <c>tools/list</c> bütçesine girmiyorlar"</i>. Bu doğru ama <b>eksik</b>:
/// kaynaklar kendi listelerini taşıyor (<c>resources/templates/list</c>) ve o
/// liste de modelin bağlamına giriyor. İddianın ölçülmemiş hâli, üç aracı üç
/// kaynağa çevirip <i>"bedava"</i> demek olurdu.
/// </para>
///
/// <para>
/// Gerçek kazanç <b>ne zaman</b> ödendiğinde: araç ilanı <b>her</b> oturumda
/// <c>tools/list</c> ile geliyor, kaynak listesi ise istemci kanalı
/// <b>kullanırsa</b>. Yani maliyet sıfır değil, <b>koşullu</b>.
/// </para>
///
/// <para>
/// Araç tavanına (<c>McpSchemaBudgetTests</c>, 700) <b>dokunulmuyor</b>: bu
/// kanalın kendi tavanı var, çünkü iki liste ayrı yanıtlarda taşınıyor ve tek
/// bir sayıya toplanması hangisinin büyüdüğünü gizlerdi.
/// </para>
/// </summary>
public sealed class McpResourceBudgetTests
{
    /// <summary>
    /// Kaynak başına tavan.
    ///
    /// <para>
    /// <b>ÖLÇÜLEN (SDK'nın kendi serileştiricisiyle, dört kaynak):</b> toplam
    /// <b>594</b> belirteç — <c>evidence-bundle</c> 170, <c>rca-report</c> 148,
    /// <c>parser</c> 143, <c>rca-runs</c> 133. Tavan 200, yani bugünkü en pahalı
    /// kalemin (170) üstünde ama <b>dar</b>: bir açıklamayı iki katına çıkarmak
    /// kırmızı yakıyor.
    /// </para>
    ///
    /// <para>
    /// <b>Abonelik ŞEMA EKLEMEDİ.</b> Dördüncü kaynak listeye <b>133</b> belirteç
    /// ekledi (461 → 594) ve bunun tamamı ilanın kendisi: abonelik yeteneği
    /// yetenek nesnesinde tek bir <c>subscribe</c> bayrağı, ve bildirimler
    /// <c>tools/list</c> ya da kaynak listesine <b>hiç girmiyor</b> — onlar
    /// oturum sırasında akıyor. Yani abonelik ilan maliyeti <b>sıfır</b>;
    /// maliyeti akış tarafında ve ölçüsü <i>koşum başına bildirim</i>
    /// (<c>McpResourceSubscriptionTests.Kosum_basina_uc_bildirim</c>).
    /// </para>
    ///
    /// <para>
    /// <b>İlk ölçüm 500 çıktı ve YANLIŞTI</b> — <c>JsonSerializerDefaults.Web</c>
    /// ile ölçülmüştü ve o seçenekler <c>null</c> alanları da yazıyor. Tel
    /// üzerinde giden şey SDK'nın <c>McpJsonUtilities.DefaultOptions</c>'ı;
    /// aradaki 39 belirteç hiç gönderilmeyen alanlardı. Ölçümün <b>neyi</b>
    /// ölçtüğü, ölçülen sayı kadar önemli.
    /// </para>
    ///
    /// <para>
    /// <b>Araç başına ölçülen 194 ile karşılaştırma yanıltıcı olabilir</b> ve
    /// gerçek ayrım büyüklükte değil <b>ne zaman ödendiğinde</b>: <c>tools/list</c>
    /// HER oturumda geliyor, kaynak listesi ise istemci kanalı <b>kullanırsa</b>.
    /// Üç belgeyi üç araç olarak sunmak ≈580 belirteç ederdi ve <b>her</b>
    /// bağlamda taşınırdı; kaynak olarak 461 ediyor ve <b>koşullu</b>.
    /// </para>
    /// </summary>
    private const int PerResourceCeiling = 200;

    private static readonly TiktokenTokenizer Tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

    /// <summary>
    /// <b>Kaynak ilanının maliyeti ölçülüyor</b> — tahmin edilmiyor.
    ///
    /// <para>
    /// Ölçüt tel üzerindeki hâl: her kaynağın <c>ResourceTemplate</c>'i JSON'a
    /// serileştirilip sayılıyor. Aracın şemasında olduğu gibi burada da en pahalı
    /// kalem <b>açıklama</b>, ve bu M04/M05 için bir tasarım kısıtı: kaynak
    /// eklemek bedava değil, <b>ucuz</b>.
    /// </para>
    /// </summary>
    [Fact]
    public void Kaynak_ilaninin_baglam_maliyeti()
    {
        using var services = McpTestServices.ForDiscoveredTools();

        var resources = BizigoMcpServer.Resources(
            McpSurface.Product,
            McpComplianceTests.DeclaredAssemblies(McpSurface.Product),
            services);

        Assert.NotEmpty(resources);

        var perResource = resources
            .Select(resource => new
            {
                resource.Kind,
                Tokens = Tokenizer.CountTokens(
                    JsonSerializer.Serialize(resource.ProtocolResourceTemplate, McpJsonUtilities.DefaultOptions)),
                Description = Tokenizer.CountTokens(resource.ResourceDescription),
            })
            .OrderByDescending(static entry => entry.Tokens)
            .ToArray();

        var total = perResource.Sum(static entry => entry.Tokens);

        var over = perResource.Where(static entry => entry.Tokens > PerResourceCeiling).ToArray();

        Assert.True(
            over.Length == 0,
            "Kaynak ilanı tavanı aşıldı. Tavan bir performans notu değil: bu metin "
            + "modelin bağlamına giriyor ve orada yediği yer, ürünün cevabına kalmayan "
            + $"yer. Aşanlar: {string.Join(", ", over.Select(static e => $"{e.Kind}={e.Tokens}"))}. "
            + "Açıklamayı kısaltın ya da tavanı BİLİNÇLİ olarak yükseltin.");

        // Sayı raporlanıyor: bir sonraki kişi kaynak eklerken marjinal maliyeti
        // görebilsin. Ölçülen hâl (dört kaynak): toplam 594, en pahalı kalem
        // `evidence-bundle` (170), ve dördünde de en büyük tek kalem AÇIKLAMA
        // (51-62) — yani kaynak eklemenin marjinal maliyeti ~150 belirteç ve
        // onun ~%40'ı yazdığımız cümle.
        Assert.True(
            total > 0,
            $"Toplam {total} belirteç · " + string.Join(
                " · ",
                perResource.Select(static e => $"{e.Kind}={e.Tokens} (açıklama {e.Description})")));
    }

    /// <summary>
    /// <b>Sayıcı gerçekten sayıyor.</b> Araç tarafındaki
    /// <c>Belirtec_sayici_gercekten_sayiyor</c> ile aynı sebeple var: sıfır
    /// döndüren bir sayıcıyla bütün tavanlar sessizce geçerdi.
    /// </summary>
    [Fact]
    public void Belirtec_sayici_gercekten_sayiyor()
    {
        var tokens = Tokenizer.CountTokens("bizigo://evidence-bundle/{id}");

        Assert.InRange(tokens, 5, 40);
    }

}
