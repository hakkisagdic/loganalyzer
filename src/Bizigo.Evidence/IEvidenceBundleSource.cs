using Bizigo.Contracts;

namespace Bizigo.Evidence;

/// <summary>
/// Kanıt paketi üreten şey.
///
/// <para>
/// <b>Neden bir arayüz var:</b> alarm kapatma (T38) paket yoksa üretimi
/// tetikliyor ve bunun <b>tetiklendiğini</b> sınamak gerekiyor. Tek uygulama
/// <see cref="EvidenceBundleFactory"/>; arayüzün işi ikinci bir üretim yolu
/// açmak değil, var olan yolun <b>çağrıldığını</b> gösterebilmek.
/// </para>
///
/// <para>
/// Ayrım önemli çünkü §9 ikinci kopyayı yasaklıyor: T37'nin elle tetiklemesi
/// ve T38'in kapatma tetiklemesi <b>aynı</b> fabrikayı çağırıyor. İki ayrı
/// üretim yolu olsaydı paket biçimleri zamanla ayrışır ve ayrışma tam olarak
/// F4'ün karşılaştırmasında ortaya çıkardı.
/// </para>
/// </summary>
public interface IEvidenceBundleSource
{
    Task<EvidenceBundle> BuildAsync(
        RcaWindow window,
        AccessScope scope,
        GatherBudget? budget = null,
        CancellationToken cancellationToken = default);
}
