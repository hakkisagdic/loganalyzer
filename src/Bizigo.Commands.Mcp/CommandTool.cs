using Bizigo.Mcp;

namespace Bizigo.Commands.Mcp;

/// <summary>
/// Komut çekirdeğinden doğan araçların ortak tabanı.
///
/// <para>
/// <b>Tek işi var ve o iş bir eşleme:</b> çekirdeğin
/// <see cref="CommandFailureKind"/>'ını MCP'nin hata koduna çevirmek. Her araç
/// kendi <c>switch</c>'ini yazsaydı yedi kopya olurdu ve biri eksik kalınca
/// aynı sebep iki araçta iki farklı kod verirdi — istemci tarafında ayırt
/// edilemeyen bir tutarsızlık.
/// </para>
///
/// <para>
/// <b>Kataloğa bağlılık burada kuruluyor.</b> Araç adı, başlığı ve açıklaması
/// <see cref="CommandCatalog"/>'dan okunuyor, elle yazılmıyor: iki yerde
/// yazılsalardı katalog "bu komut araç" der, araç başka bir adla ilan edilir,
/// ve bekçi ikisini eşleştiremezdi. Bu depoda aynı şekil S04'te ölçüldü —
/// baseline'ın iki gösterimi, ikisi de kendi içinde tutarlı.
/// </para>
/// </summary>
/// <remarks>
/// <b>Yapıcı bir zamanlar <c>McpSurface</c> da alıyordu ve o parametre M08 ile
/// KALKTI.</b> Kayda değer, çünkü gerekçesi ölçülmüştü ve ölçüm doğruydu —
/// yalnızca ölçülen şey bir kusurdu: <c>Instantiate</c> <c>surface</c>'i
/// koşulsuz veriyordu, <c>ActivatorUtilities</c> fazladan argümanı reddediyordu
/// ve yedi aracın yedisi kurulamıyordu (21 test düştü). M08 argümanı
/// <b>koşullu</b> yaptı; parametrenin varlık sebebi kalmadı.
///
/// <para>
/// Bırakılsaydı gereksiz bir tören kalıbı yerleşirdi ve M03/M04/M05 onu
/// kopyalardı — ölçülmüş ama artık geçersiz bir gerekçenin taşıyıcısı olarak.
/// </para>
/// </remarks>
/// <param name="commandName">
/// <see cref="CommandCatalog"/>'daki çekirdek adı. Katalogda yoksa
/// <b>kurulum patlıyor</b>: sessizce geçen bir araç, kapsama bekçisine
/// görünmeden ilan edilirdi.
/// </param>
public abstract class CommandTool(string commandName) : BizigoMcpTool
{
    private readonly CommandDescriptor descriptor =
        CommandCatalog.All.SingleOrDefault(c => c.Name == commandName)
        ?? throw new InvalidOperationException(
            $"`{commandName}` komut kataloğunda yok. Araç adı katalogdan okunur; " +
            "elle yazılan bir ad, kapsama bekçisinin eşleştiremeyeceği bir araç üretir.");

    /// <inheritdoc/>
    public sealed override string ToolName => descriptor.Name;

    /// <inheritdoc/>
    public sealed override string ToolTitle => descriptor.Title;

    /// <inheritdoc/>
    public sealed override string ToolDescription => descriptor.Summary + DescriptionSuffix;

    /// <summary>
    /// Aracın kataloğa <b>ek olarak</b> söylemesi gereken şey.
    ///
    /// <para>
    /// <see cref="ToolDescription"/> mühürlü kalıyor — kataloğa bağlılık
    /// kapının kendisi ve bir araç kendi adını/özetini yeniden yazamamalı. Ama
    /// bazı araçların CLI'dan <b>ayrıştığı</b> bir yer var ve modelin onu
    /// görmesi gerekiyor; o cümle koda gömülü kalsaydı aracı kullanan model
    /// ayrışmayı hiç bilmezdi.
    /// </para>
    ///
    /// <para>
    /// <b>Bedeli bağlam bütçesi</b> ve bu bilerek: her ek cümle
    /// <c>tools/list</c>'te taşınıyor ve araç başına tavan onu ölçüyor. Yani
    /// buraya yazmak ücretsiz değil — bir karar.
    /// </para>
    /// </summary>
    protected virtual string DescriptionSuffix => string.Empty;

    /// <summary>
    /// Ürün yüzeyi. Komut çekirdeği <c>bizigo</c>'nun komutlarını taşıyor;
    /// <c>bizigo-sim</c>'in kendi araçları M03'ün.
    /// </summary>
    public sealed override McpSurface Surface => McpSurface.Product;

    /// <summary>
    /// Yedi aracın yedisi de <b>okuma</b>. Yazan komutlar araç değil,
    /// <b>gerekçeli muafiyet</b> — ve gerekçeleri katalogda duruyor.
    /// </summary>
    public sealed override bool IsReadOnly => true;

    /// <summary>
    /// <b>Komut araçları çağıranın kimliğini İSTEMİYOR</b> — ve bu M08'in
    /// kapısında gerekçeli bir muafiyet olarak duruyor.
    ///
    /// <para>
    /// Ölçüt tek soru: <b>kapsam filtresinin uygulanacağı bir satır var mı?</b>
    /// Yedi aracın yedisi de <b>yerel dosya</b> okuyor — parser YAML'ları,
    /// eşleme tabloları, Sigma manifesti, altın örnekler. Hiçbiri ürün
    /// verisine dokunmuyor, dolayısıyla <c>owner_group</c> ile daraltılacak
    /// bir şey yok.
    /// </para>
    ///
    /// <para>
    /// <b>Muafiyet bir kolaylık değil bir SINIR.</b> <c>fields.coverage</c>
    /// bunun kanıtı: komutun ClickHouse yarısı ürün verisi okuyor ve o yarı
    /// araçtan <b>çıkarıldı</b> — muafiyeti korumak için değil, tersine:
    /// muafiyet ancak o yarı yokken doğru. Ürün verisine uzanan bir araç
    /// kimlik istemek zorunda ve o yol M04'ün.
    /// </para>
    ///
    /// <para>
    /// §8 gereği bu ezme <b>tek başına yetmiyor</b>: <c>McpIdentityTests</c>'in
    /// gerekçeli listesine ve sabit sayısına da girmek gerekiyor — muafiyet iki
    /// ayrı bilinçli hareket.
    /// </para>
    /// </summary>
    public sealed override bool RequiresCallerIdentity => false;

    /// <summary>
    /// Çekirdeğin sebebini MCP hata koduna çeviren <b>tek</b> yer.
    /// </summary>
    protected static McpToolResult Failure(CommandFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var code = failure.Kind switch
        {
            CommandFailureKind.InvalidArgument => McpToolError.InvalidArgument,
            CommandFailureKind.NotFound => McpToolError.NotFound,
            CommandFailureKind.Unavailable => McpToolError.Unavailable,

            // Kapalı küme büyürse burası PATLIYOR, sessizce bir koda düşmüyor:
            // tanınmayan bir sebebi `unavailable` saymak, istemciye yanlış bir
            // şey söylemenin en sessiz yolu olurdu.
            _ => throw new ArgumentOutOfRangeException(
                nameof(failure), failure.Kind, "Sebebin MCP karşılığı yazılmamış."),
        };

        return McpToolResult.Failure(new McpToolError(code, failure.Message));
    }
}
