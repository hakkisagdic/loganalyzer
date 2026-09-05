using Bizigo.Contracts.Security;

namespace Bizigo.Mcp;

/// <summary>
/// <b>Bir MCP yüzeyinin K6 beyanı</b> — ve beyanın <i>nasıl kurulduğu</i>
/// (M06).
///
/// <para>
/// Çıplak bir <see cref="DataBoundary"/> taşımıyoruz ve sebebi tipin kendi
/// belgesinde yazılı: T42'nin kapısı <see cref="DataBoundary.Internal"/>
/// beyanını <b>adres sınıfına karşı doğruluyor</b>, MCP kapısının ise
/// doğrulayacak bir şeyi yok. Uç <i>egress</i> — biz ona bağlanıyoruz, adresi
/// biliniyor. MCP yüzeyi <i>ingress</i> — istemci bize bağlanıyor, kim olduğu
/// bağlanana kadar bilinmiyor, ve stdio'da hiç adres yok.
/// </para>
///
/// <para>
/// Aynı enum değerinin iki farklı garanti gücü taşıması bu deponun §7 sınıfı
/// olurdu: okuyan <c>Internal</c> görüp <i>"demek ki doğrulandı"</i> sanardı.
/// Bu tip ayrımı <b>silmiyor, taşınabilir hâle getiriyor</b>:
/// <see cref="Basis"/> boş olamıyor, yani bu üründe <c>Internal</c> hiçbir
/// yerde <b>gerekçesiz</b> görünmüyor.
/// </para>
///
/// <para>
/// <b>Yapıcı <c>private</c>, kalıp T41'in <see cref="RedactedPrompt"/>'undan:</b>
/// bir <see cref="McpBoundaryDeclaration"/> örneği yalnızca
/// <see cref="Declare"/>'den çıkabiliyor, dolayısıyla <b>geçersiz bir beyan
/// hiç var olamıyor</b>. <see cref="DataBoundary.Unspecified"/> reddi bir
/// çalışma anı kontrolü değil, tipin varoluş şartı.
/// </para>
/// </summary>
public sealed class McpBoundaryDeclaration
{
    private McpBoundaryDeclaration(DataBoundary boundary, string basis)
    {
        Boundary = boundary;
        Basis = basis;
    }

    /// <summary>Yüzeyin istemcisinin hangi tarafta olduğu.</summary>
    public DataBoundary Boundary { get; }

    /// <summary>
    /// Beyanın <b>nasıl kurulduğu</b> — boş olamaz.
    ///
    /// <para>
    /// Bunu yapılandırmayı yazan kişi değil, <b>beyanı kuran kod</b> yazıyor
    /// ("CLI seçeneği <c>--data-boundary</c>", "yapılandırma
    /// <c>Mcp:DataBoundary</c>"). Operatöre her seferinde bir gerekçe metni
    /// yazdırmak T42'nin <c>BoundaryOverrideReason</c>'ının işi ve o bir
    /// <i>muafiyet</i>; buradaki alan bir muafiyet değil, beyanın kaynağı.
    /// Kaynağı mekanik olarak yazmak, yazılmasını unutulamaz kılıyor.
    /// </para>
    /// </summary>
    public string Basis { get; }

    /// <summary>
    /// Beyanı kurar. <b>Üç hâl reddediliyor</b> ve üçü de bir istisna:
    /// <see cref="DataBoundary.Unspecified"/>, tanımsız enum değeri, ve boş
    /// <paramref name="basis"/>.
    ///
    /// <para>
    /// Reddin istisna olması (bir <c>bool</c> ya da <c>null</c> değil) bilinçli:
    /// beyansız bir MCP sunucusunun <b>ayağa kalkmaması</b> gerekiyor. Sessizce
    /// bir varsayılana düşmek, kurumun en büyük sözünü kimse karar vermeden
    /// boşa çıkarırdı — <see cref="DataBoundary.Unspecified"/> belgesindeki
    /// gerekçe.
    /// </para>
    /// </summary>
    /// <param name="boundary">Beyan edilen taraf.</param>
    /// <param name="basis">Beyanın kaynağı; boş olamaz.</param>
    public static McpBoundaryDeclaration Declare(DataBoundary boundary, string basis)
    {
        if (string.IsNullOrWhiteSpace(basis))
        {
            throw new ArgumentException(
                "MCP sınır beyanının kaynağı (`Basis`) boş olamaz. `Internal` gerekçesiz "
                + "göründüğü an, onu okuyan bir sonraki kişi T42'nin ADRES DOĞRULAMASINDAN "
                + "geçtiğini sanar — oysa bu yüzeyde doğrulanacak bir adres yok.",
                nameof(basis));
        }

        if (boundary == DataBoundary.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(boundary),
                boundary,
                "MCP ağ sınırı beyan edilmedi. `Unspecified` bir varsayılan değil bir rettir: "
                + "beyansız bir yüzey 'iç ağ' sayılsaydı, K6 (`log verisi kurum dışına "
                + "çıkmaz`) hiç kimse karar vermeden boşa çıkardı. `internal` ya da "
                + "`external` yazılmalı.");
        }

        if (boundary is not (DataBoundary.Internal or DataBoundary.External))
        {
            throw new ArgumentOutOfRangeException(
                nameof(boundary), boundary, $"Bilinmeyen `DataBoundary` değeri: {boundary}.");
        }

        return new McpBoundaryDeclaration(boundary, basis.Trim());
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Boundary} ({Basis})";
}

/// <summary>
/// <b>K6'nın MCP yüzeyindeki kapısı</b> (M06 — bitti tanımı 6).
///
/// <para>
/// T42'nin <c>ModelBoundaryGate</c>'i ile <b>aynı soruyu</b> soruyor ve
/// <b>farklı bir mekanizmayla</b> cevaplıyor. Orada bir adres çözülüp sınıfına
/// bakılabiliyor; burada bakılacak bir adres yok, dolayısıyla kapının
/// tutabildiği tek şey <i>beyanın kendisiyle yüzeyin taşıdığı verinin
/// tutarlılığı</i>.
/// </para>
///
/// <h3>İki kural</h3>
/// <list type="number">
/// <item><b>Beyan zorunlu.</b> <see cref="McpBoundaryDeclaration"/> kurulamadan
/// bu kapıya gelinemiyor — <see cref="DataBoundary.Unspecified"/> reddi tipin
/// varoluş şartı, kapının bir dalı değil.</item>
/// <item><b>Kurum dışı beyan ÜRÜN yüzeyinde reddediliyor.</b>
/// <c>bizigo</c> log içeriği döndürüyor; onu kurum dışı bir istemciye açmak
/// K6'nın birebir ihlali. <c>bizigo-sim</c> geçiyor — ürün verisi
/// döndürmüyor.</item>
/// </list>
///
/// <h3>Bu kapının TUTAMADIKLARI — yazılı olması şart</h3>
///
/// <list type="bullet">
/// <item>
/// <b>Yerel bir istemcinin metni nereye ilettiğini görmüyor</b> — ve bu, bu
/// kapının en önemli kör noktası. stdio taşımasında istemci aynı makinede bir
/// süreç, ama en olası gerçek istemci (Claude Desktop gibi bir masaüstü MCP
/// istemcisi) aldığı her şeyi <b>buluta</b> gönderiyor. Yani <i>"istemci aynı
/// makinede, demek ki iç ağ"</i> çıkarımı mekanik olarak doğru ve K6 açısından
/// <b>tam ters</b>. Bu yüzden stdio'nun sınırı koda çivilenmedi: operatör
/// <c>--data-boundary</c> ile <b>beyan etmek</b> zorunda.
/// </item>
/// <item>
/// <b>Yalan söyleyen bir yöneticiyi tutmuyor</b>, ve tutamaz — T42'nin aynı
/// beyanı. Ürün tek kurum / tek tenant (K10, K16); beyanı yazan kişi
/// <b>kurumun kendisi</b>. Kapının işi kararı imkânsız kılmak değil,
/// <b>kazara</b> olmasını imkânsız kılmak ve bilinçli olanı görünür kılmak.
/// </item>
/// <item>
/// <b>HTTP taşımasında dinlenen adresi doğrulamıyor.</b> Kestrel'in nereye
/// bağlandığı <c>Bizigo.Api</c>'nin yapılandırması ve bu kapının göremediği
/// yer. Kurum dışına açılmış bir dinleyici + <c>internal</c> beyanı, yukarıdaki
/// "yalan söyleyen yönetici" hâlinin kazara olanı.
/// </item>
/// </list>
/// </summary>
public static class McpBoundaryGate
{
    /// <summary>
    /// Beyanı yüzeye karşı sınar; geçmezse <see cref="InvalidOperationException"/>
    /// fırlatıyor.
    ///
    /// <para>
    /// <b>Fırlatmak bir tercih:</b> reddedilen bir beyanla sunucunun ayağa
    /// kalkması, kapının hiç olmamasıyla aynı sonucu verirdi.
    /// </para>
    /// </summary>
    /// <param name="declaration">Yüzeyin K6 beyanı.</param>
    /// <param name="surface">Hangi yüzey sunulacak.</param>
    public static void Require(McpBoundaryDeclaration declaration, McpSurface surface)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        if (declaration.Boundary != DataBoundary.External)
        {
            return;
        }

        // `bizigo-sim` GEÇİYOR ve bu bir boşluk değil bir karar: iki yüzeyin
        // ayrı olmasının SEBEBİ tam bu risk ayrımı (bkz. `McpSurface`).
        // Simülatör kontrolü ürün verisi döndürmüyor, dolayısıyla kurum dışı
        // bir istemciye açılması K6'yı ihlal etmiyor.
        //
        // ÖLÇÜM NOTU: bu dal bugün ürün yolunda ULAŞILAMAZ. `BizigoMcpSetup`
        // yalnızca `Product` kuruyor ve `bizigo-sim`'in HTTP yüzeyi M03'ün
        // kararı (bugünkü yönü stdio-only). Yani `External` + `Simulator`
        // hâlini bugün yalnızca birim testi görüyor; M03 gerçek bir yol
        // açtığında orada da ölçülmeli.
        if (surface is McpSurface.Simulator)
        {
            return;
        }

        throw new InvalidOperationException(
            $"MCP yüzeyi '{McpSurfaces.WireName(surface)}' `external` beyan edilmiş "
            + $"({declaration.Basis}). K6 gereği kurum dışına log verisi çıkmıyor ve bu "
            + "HİÇBİR içerik düzeyi için esnemiyor — `summary` dahil, çünkü özet de o "
            + "verinin türevi. Kurum dışı bir istemciye açılabilecek tek yüzey "
            + $"'{McpSurfaces.SimulatorName}': o yüzey ürün verisi değil simülatör durumu "
            + "döndürüyor.");
    }
}
