using Bizigo.Contracts.Security;

namespace Bizigo.Mcp;

/// <summary>
/// <b>Log metninin MCP'ye girebildiği tek taşıyıcı — ve içine yalnızca
/// redaksiyon kapısından geçmiş metin giriyor.</b>
///
/// <para>
/// MCP bir <b>model yüzeyi</b>: bir araç çağrısının dönüş değeri doğrudan
/// modelin bağlamına giriyor. Bu, depodaki iki kapıyı dolaşabilecek bir yol
/// açıyor — T41'in <see cref="RedactedPrompt"/>'u (yapıcısı <c>private</c>,
/// yalnızca <see cref="RedactedPrompt.Redact"/>'ten çıkıyor) ve T42'nin ağ
/// sınırı (<see cref="DataBoundary"/>). Araç arayüzü <c>string</c> döndürseydi
/// kapı bir <i>çağrı alışkanlığına</i> inerdi: "buraya redaksiyon uygulamayı
/// unutma". Bu depoda hatırlamaya dayanan mekanizmanın kaç kez kaybettiği
/// ölçüldü.
/// </para>
///
/// <h3>M01 bir menteşe bıraktı; M06 onu kapıya çevirdi</h3>
///
/// <para>
/// M01'in hâlinde fabrikanın parametresi <c>string</c> idi ve gerekçesi
/// yazılıydı: kapıyı o gün takmak, tüketicisi olmayan bir tip yazmak olurdu
/// (<c>CLAUDE.md</c> §8). M06 tek bir şeyi değiştirdi — <b>parametre tipini</b>.
/// Bugün log metnini modele göndermek isteyen her yol elinde bir
/// <see cref="RedactedPrompt"/> tutmak zorunda, ve o tip yalnızca kapıdan
/// çıkıyor. Kapıyı atlayan çağrı <b>derlenmiyor</b>; her araca tek tek bakmak
/// gerekmiyor.
/// </para>
///
/// <h3>M01'in yazdığı zayıflık ve nasıl kapandığı</h3>
///
/// <para>
/// M01 dürüstlük gereği şunu yazmıştı: fabrika <c>internal</c>, yani
/// <c>Bizigo.Mcp</c> derlemesinin <b>içinden</b> herhangi bir <c>string</c> ile
/// çağrılabilir; araçların dışarıda yaşaması bir <b>yerleşim</b> güvencesi, tip
/// güvencesi değil. Tip değişimi bu deliği kapatıyor ama <b>kalıcı</b>
/// kılmıyor: bu dosyanın içinde ikinci bir fabrika, bir <c>internal</c> yapıcı
/// ya da bir <c>string</c> aşırı yüklemesi açmak hâlâ mümkün ve o hareket
/// sessiz olurdu.
/// </para>
///
/// <para>
/// Kalıcılığı tutan şey bir yorum değil: <c>McpRedactionGateTests</c> hem
/// yansımayla (bu tipi üretebilen her üye bir <see cref="RedactedPrompt"/>
/// istiyor mu) hem IL ile (bu tipi <c>newobj</c> ile kuran tek metot hangisi)
/// sınıyor. Kapının kaldırılabilir olup olmadığı <b>ölçülüyor</b>.
/// </para>
/// </summary>
public sealed class McpLogText
{
    private McpLogText(string text) => Text = text;

    /// <summary>Modelin bağlamına girecek metin — maskelenmiş hâli.</summary>
    public string Text { get; }

    /// <summary>
    /// Log metnini MCP sonucuna koyulabilir hâle getirir.
    ///
    /// <para>
    /// <b>Parametre tipi kapının kendisi.</b> Bir <see cref="RedactedPrompt"/>
    /// yalnızca <see cref="RedactedPrompt.Redact"/>'ten çıkabildiği için,
    /// buraya ulaşan her metin redaksiyondan geçmiş oluyor —
    /// <b>derleyicinin</b> tuttuğu bir şart, bir çağrı alışkanlığı değil.
    /// </para>
    ///
    /// <para>
    /// <c>internal</c> kalması bilinçli: araçlar <c>Bizigo.Mcp</c>'nin dışında
    /// yaşıyor ve log metnini sonuca koymanın yolu
    /// <see cref="McpToolResult.WithLogText(RedactedPrompt[])"/> — yani araç
    /// tarafı bu fabrikayı hiç görmüyor, gördüğü tek şey redaksiyon kapısı.
    /// </para>
    /// </summary>
    internal static McpLogText FromRedacted(RedactedPrompt redacted) =>
        new((redacted ?? throw new ArgumentNullException(nameof(redacted))).Text);
}
