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
/// <h3><c>OwnerGroup</c> artık BİLDİRİMİN SÜZGECİ — ve o alan bunun için taşınıyordu</h3>
///
/// <para>
/// M07'de bu alan haberde <b>vardı ve kullanılmıyordu</b>; gerekçesi yazılıydı:
/// <i>"süzgeç kurulduğu gün eklenecek bir alan değil, zaten taşınan bir alan
/// olacak."</i> M20 süzgeci kurdu ve haberin şekli <b>değişmedi</b> — alan
/// yerinde duruyordu.
/// </para>
///
/// <para>
/// Süzgecin gerekliliği <b>ölçüldü</b>, varsayılmadı: süzgeçsiz hâlde abone,
/// bildirimleri sayıp üçe bölerek <b>göremediği gruptaki koşum sayısını tam
/// olarak</b> buluyordu (<c>McpSubscriptionSideChannelTests</c>). Yani kanal
/// ihmal edilebilir değildi — kesirli bir bit değil, <b>tam kardinalite</b>.
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
        //
        // GRUP GEÇİYOR: yayın kanalı bildirimi yalnızca o grubu görebilen
        // aboneliklere gönderiyor.
        return updates.PublishAsync(RcaRunsResource.Uri, change.OwnerGroup, cancellationToken);
    }
}
