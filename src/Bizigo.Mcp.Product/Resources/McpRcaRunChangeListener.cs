using Bizigo.Contracts;

namespace Bizigo.Mcp.Product.Resources;

/// <summary>
/// RCA koşumu değişti → <c>bizigo://rca-runs</c> güncellendi.
///
/// <para>
/// <b>Köprünün tamamı bu sınıf, ve tek satır olması kasıtlı.</b> RCA çekirdeği
/// MCP'yi bilmiyor (<see cref="IRcaRunChangeListener"/> <c>Bizigo.Contracts</c>'ta),
/// MCP çekirdeği RCA'yı bilmiyor (<see cref="McpResourceUpdates"/> yalnızca bir
/// adres alıyor). İkisini burada birleştirmek, ikisinden birinin diğerine
/// referans vermesinden ucuz: bu dosya silinse iki taraf da derlenmeye devam
/// ediyor.
/// </para>
///
/// <h3><c>OwnerGroup</c> okunuyor ama BİLDİRİME girmiyor — ölçülmüş bir sınır</h3>
///
/// <para>
/// <see cref="RcaRunChange.OwnerGroup"/> haberde <b>var</b> ve burada
/// kullanılmıyor. Sebebi bir eksiklik değil, yayın kanalının bugünkü şekli:
/// bildirim <b>oturum geneline</b> gidiyor ve abone başına kimlik bu katmanda
/// bilinmiyor (gerekçe ve doğru çözüm <see cref="McpResourceUpdates"/>
/// belgesinde). Alan haberde <b>bilerek duruyor</b>: süzgeç kurulduğu gün
/// eklenecek bir alan değil, <b>zaten taşınan</b> bir alan olacak — ve o gün
/// haberin şeklini değiştirmek gerekmeyecek.
/// </para>
///
/// <para>
/// Bunun ölçülebilir sonucu şu: bir abone <b>göremeyeceği</b> bir grubun koşumu
/// değiştiğinde de bildirim alıyor. Belgeyi okuyunca hiçbir şey görmüyor (kapsam
/// kapısı <c>BizigoMcpResource.ReadAsync</c>'te), yani <b>veri sızmıyor</b>; ama
/// bildirimin zamanlaması <i>"bir yerde bir şey oldu"</i> sinyali veriyor.
/// </para>
/// </summary>
public sealed class McpRcaRunChangeListener(McpResourceUpdates updates) : IRcaRunChangeListener
{
    /// <inheritdoc/>
    public ValueTask RunChangedAsync(RcaRunChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        // Adres SABİT ve bu doğru: değişen şey belgenin İÇERİĞİ, adresi değil.
        // Koşum kimliğini adrese koymak, her koşum için ayrı bir abonelik
        // gerektirirdi ve kimlik tetiklenmeden önce bilinmiyor.
        return updates.PublishAsync(RcaRunsResource.Uri, cancellationToken);
    }
}
