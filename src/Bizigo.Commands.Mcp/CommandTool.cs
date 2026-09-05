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
/// <param name="requestedSurface">
/// Keşfin sorduğu yüzey. <b>Bilerek yok sayılıyor</b> — komut araçlarının
/// yüzeyi sabit (<see cref="McpSurface.Product"/>) ve
/// <see cref="McpToolDiscovery.Instantiate"/> ürettiği aracı
/// <c>tool.Surface == surface</c> ile zaten eliyor.
///
/// <para>
/// <b>Yine de parametre olarak duruyor ve sebebi mekanik:</b>
/// <c>ActivatorUtilities.CreateInstance(services, type, surface)</c> fazladan
/// argümanı <b>reddediyor</b> — yapıcısı onu almayan bir araç
/// <i>"suitable constructor could not be located"</i> ile patlıyor. Ölçüldü:
/// yüzeysiz yapıcıyla yedi aracın yedisi de kurulamadı ve uyum kapısının 21
/// testi düştü.
/// </para>
/// </param>
/// <param name="commandName">
/// <see cref="CommandCatalog"/>'daki çekirdek adı. Katalogda yoksa
/// <b>kurulum patlıyor</b>: sessizce geçen bir araç, kapsama bekçisine
/// görünmeden ilan edilirdi.
/// </param>
public abstract class CommandTool(McpSurface requestedSurface, string commandName) : BizigoMcpTool
{
    /// <summary>
    /// Keşfin sorduğu yüzey — <b>ilan edilen</b> yüzey değil. İkisi ayrı ve
    /// <see cref="Surface"/> sabit; bu alan yalnızca yapıcı imzasının
    /// gereğinin görünür kalması için duruyor.
    /// </summary>
    protected McpSurface RequestedSurface => requestedSurface;

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
    public sealed override string ToolDescription => descriptor.Summary;

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
