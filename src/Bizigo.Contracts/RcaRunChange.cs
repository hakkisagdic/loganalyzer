namespace Bizigo.Contracts;

/// <summary>
/// Bir RCA koşumunun durumunun değiştiği — <b>MCP'yi bilmeyen</b> bir haber.
///
/// <para>
/// <b>Neden <c>Bizigo.Contracts</c>'ta.</b> Yayın noktası <c>RcaAdmission</c>
/// (<c>Bizigo.Rca</c>) ve dinleyici MCP kaynak kanalı (<c>Bizigo.Mcp.Product</c>).
/// İkisi arasında referans <b>yok</b> ve olmaması bilinçli: <c>Bizigo.Rca</c>
/// bir model yüzeyini bilmemeli — bilse, RCA çekirdeği bir protokol sürümüne
/// bağlanırdı. Ortak dil burada duruyor.
/// </para>
///
/// <para>
/// <b>Taşıdığı üç alanın her biri bir karar.</b> <see cref="OwnerGroup"/>
/// olmadan dinleyici bildirimi kime göndereceğini bilemez; <see cref="State"/>
/// olmadan <i>"ne değişti"</i> sorusu ikinci bir sorgu ister;
/// <see cref="RunId"/> olmadan iki eşzamanlı koşum ayırt edilemez.
/// </para>
///
/// <para>
/// <b>Taşımadığı şey de karar:</b> koşumun ayrıntısı (<c>StateDetail</c>) burada
/// <b>yok</b>. O alan bir istisna metni olabiliyor, yani bir sızıntı kanalı; ve
/// bildirim zaten içerik taşımıyor — istemci belgeyi <b>okuyarak</b> öğreniyor
/// ve o okuma kapsam kapısından geçiyor.
/// </para>
/// </summary>
/// <param name="RunId">Değişen koşum.</param>
/// <param name="OwnerGroup">Koşumun sahibi grup (K17).</param>
/// <param name="State">Yeni durumun tel adı.</param>
public sealed record RcaRunChange(Guid RunId, string OwnerGroup, string State);

/// <summary>
/// RCA koşumu durum değiştirdiğinde haber almak isteyen taraf.
///
/// <para>
/// <b>Kayıt isteğe bağlı ve bu şart.</b> <c>RcaAdmission</c> dinleyici
/// koleksiyonunu boş da alabiliyor: MCP yüzeyi kurulmamış bir süreçte (CLI'nin
/// çoğu komutu, arka plan işçileri) hiçbir dinleyici yok ve olmaması bir arıza
/// değil. Zorunlu tek bir dinleyici, RCA çekirdeğini MCP'nin kurulmasına
/// bağlardı.
/// </para>
///
/// <para>
/// <b>Bir dinleyicinin hatası koşumu düşürmemeli.</b> Yayın noktası koşumun
/// durumunu <b>yazdıktan sonra</b> geliyor; bir bildirim kanalının kırılması,
/// veritabanına yazılmış bir gerçeği geri almaz. Çağıranın bu istisnayı
/// yutması gerekiyor ve gerekçesi orada yazılı.
/// </para>
/// </summary>
public interface IRcaRunChangeListener
{
    /// <summary>Koşum durumu değişti.</summary>
    ValueTask RunChangedAsync(RcaRunChange change, CancellationToken cancellationToken);
}
