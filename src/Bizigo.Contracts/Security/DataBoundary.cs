namespace Bizigo.Contracts.Security;

/// <summary>
/// Bir veri tüketicisinin <b>hangi tarafta</b> durduğu — K6'nın çizdiği tek
/// eksen.
///
/// <para>
/// <b>Eksen "yerel / uzak" DEĞİL.</b> K6 kararının kendi metni uzak bir GPU
/// kümesini açıkça kapsıyor: <i>"Yerel (Ollama/vLLM) + uzak GPU cluster …
/// Log verisi kurum dışına çıkmaz."</i> Yani uzaklık yasak değil; yasak olan
/// <b>kurum dışına çıkmak</b>. Kurum içindeki bir GPU kümesi ile aynı makinedeki
/// Ollama, bu eksende <b>aynı</b> yerde duruyor.
/// </para>
///
/// <para>
/// RCA §2.1 aynı ayrımı açılış cümlesinde yapıyor: K6 <b>ağ sınırını</b>
/// çiziyor, prompt'un <b>içeriği</b> ayrı bir soru. Bu enum ağ sınırının
/// tarafı; içerik düzeyi <c>PromptContentLevel</c>.
/// </para>
///
/// <h3>Neden <c>Bizigo.Contracts</c>'ta ve neden tek</h3>
///
/// <para>
/// İlk hâli <c>Bizigo.Rca.Models.ModelDataBoundary</c> idi ve tek tüketicisi
/// vardı: RCA'nın dışarı bağlandığı model ucu. M06 ikinci bir tüketici getirdi
/// — MCP sunucusunun kendisi de aynı sınırı beyan etmek zorunda. İkinci bir
/// enum yazmak <c>CLAUDE.md</c> §9'un yasakladığı şey olurdu, ve §6'nın kaydı
/// bedelini söylüyor: <i>"iki gösterim doğdu, birim paketi sessiz kaldı"</i>.
/// K6 tek bir eksen çiziyor; o eksenin tek bir tipi var.
/// </para>
///
/// <para>
/// Yeri <c>Bizigo.Contracts</c> çünkü burası <b>yaprak</b> — hiçbir projeye
/// referansı yok. <c>Bizigo.Mcp</c>'nin buraya bağlanması ucuz;
/// <c>Bizigo.Rca</c>'ya bağlanması ise <c>ControlPlane</c>,
/// <c>ScenarioPlugin</c> ve <c>Extensions.Http</c>'yi <c>Bizigo.Cli</c>'ye
/// sürüklerdi — <c>Bizigo.Mcp.csproj</c>'un ASP.NET için ölçtüğü kusurun
/// aynısı.
/// </para>
///
/// <h3>Bu enum bir GARANTİ GÜCÜ taşımıyor — kapılar taşıyor</h3>
///
/// <para>
/// <b>Bu paragrafın burada olması şart.</b> Tip paylaşılınca doğan tek gerçek
/// tehlike bu: <see cref="Internal"/> değerini okuyan biri <i>"demek ki
/// doğrulandı"</i> sanabilir. Doğrulama <b>kapının</b> işi ve kapıdan kapıya
/// değişiyor:
/// </para>
///
/// <list type="bullet">
/// <item><b><c>ModelBoundaryGate</c> (T42) — doğruluyor.</b> Uç <i>egress</i>:
/// biz ona bağlanıyoruz, adresi biliniyor, ve <see cref="Internal"/> beyanı
/// çözülen her adresin yönlendirilemez olmasıyla sınanıyor.</item>
/// <item><b><c>BizigoMcpServer</c> (M06) — doğrulamıyor, BEYAN ALIYOR.</b> MCP
/// yüzeyi <i>ingress</i>: istemci bize bağlanıyor ve kim olduğu bağlanana kadar
/// bilinmiyor; stdio'da hiç adres yok. Doğrulanacak bir şey olmadığı için
/// beyan <c>McpBoundaryDeclaration</c> içinde dolaşıyor ve o tip beyanın
/// <b>nasıl kurulduğunu</b> yazmadan kurulamıyor.</item>
/// </list>
///
/// <para>
/// Yani ayrım silinmedi, <b>tipten okunur hâle getirildi</b>. Enum'a
/// "doğrulanmış" diye bir değer eklemek yanlış olurdu: doğrulama beyanın bir
/// değeri değil, kapının bir davranışı.
/// </para>
/// </summary>
public enum DataBoundary
{
    /// <summary>
    /// Beyan edilmedi. <b>Varsayılan bu ve hiçbir kapı kabul etmiyor.</b>
    ///
    /// <para>
    /// Sıfırın "iç ağ" sayılması bu deponun en pahalı hata sınıfı olurdu:
    /// yapılandırmayı yazan kişi alanı hiç görmemiş olur, ürün çalışır, ve
    /// kurumun en büyük sözü <b>hiç kimse karar vermeden</b> boşa çıkar.
    /// Ölçülmemiş bir sınır çalışıyor sayılmaz.
    /// </para>
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Tüketici kurum sınırının içinde.
    ///
    /// <para>
    /// <b>Bu değerin kendisi bir doğrulama iddiası değil</b> — sınıf
    /// belgesindeki ayrım. <c>ModelBoundaryGate</c> onu adres sınıfına karşı
    /// doğruluyor; MCP kapısının doğrulayacak bir adresi yok ve beyanı
    /// gerekçesiyle birlikte alıyor.
    /// </para>
    /// </summary>
    Internal = 1,

    /// <summary>
    /// Tüketici kurum sınırının dışında. <b>Log verisi oraya gitmiyor</b> —
    /// <c>summary</c> dahil.
    ///
    /// <para>
    /// Gerekçe: özet de müşteri verisi. <i>"edge-rtr-07 14:02'de sustu"</i>
    /// cümlesi host adı, sahiplik grubu ve topoloji taşıyor. K6 "ham log
    /// çıkmaz" demiyor, <b>"log verisi çıkmaz"</b> diyor — ve özet o verinin
    /// türevi. Kapıyı düzey eksenine kurmak, bu cümleyi düzeylerden birine
    /// istisna yazmak olurdu.
    /// </para>
    ///
    /// <para>
    /// <b>"Reddedilir" demiyor, "log verisi gitmez" diyor</b> — ve fark M06'da
    /// ortaya çıktı. Ürün verisi <i>döndürmeyen</i> bir yüzey (<c>bizigo-sim</c>)
    /// kurum dışı bir istemciye açılabilir; K6 ihlal edilmiyor çünkü ortada log
    /// verisi yok. Reddi enum'a yazmak, ürün verisi taşımayan yüzeyleri de
    /// aynı kurala tabi kılardı.
    /// </para>
    /// </summary>
    External = 2,
}
