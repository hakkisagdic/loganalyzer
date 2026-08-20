using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Evidence;

/// <param name="Trigger">Kapatılan tetiklenme.</param>
/// <param name="Review">Kapatmayla birlikte kaydedilen inceleme.</param>
/// <param name="BundleGenerated">
/// Paket bu kapatma sırasında mı üretildi. Ekran bunu kullanıcıya söyleyebilsin
/// diye dönüyor — kapatma artık ucuz bir işlem değil ve bunu gizlemek,
/// beklemenin sebebini görünmez yapardı.
/// </param>
public sealed record AlertClosure(
    AlertTriggerEntity Trigger,
    GoldenReviewEntity Review,
    bool BundleGenerated);

/// <summary>
/// Alarm kapatma — ve inceleme zorunluluğunun <b>yapısal</b> hâli (T38).
///
/// <para>
/// <b>Zorunluluk neden burada, ekranda değil:</b> "kullanıcı inceleme adımını
/// atlayamıyor" bir arayüz kuralı olarak yazılsaydı, ekranı atlayan her yol —
/// doğrudan API çağrısı, ileride bir CLI, F4'ün ajanı — zorunluluğu da atlardı.
/// Burada kapatma ile inceleme <b>tek işlem</b>: incelemesiz kapatma diye bir
/// çağrı yok, çünkü metodun imzası onu kabul etmiyor.
/// </para>
///
/// <para>
/// <b>Paket yoksa üretiliyor.</b> İnceleme bir kanıt paketine bağlanmak zorunda
/// (F4 karşılaştırması onsuz ölçemez), ama alarm kapatılırken paket üretilmiş
/// olmayabilir. Üretim <see cref="EvidenceBundleFactory"/> ile yapılıyor —
/// T37'nin elle tetiklemesiyle <b>aynı yol</b>. İkinci bir üretim yolu
/// yazılsaydı iki paket biçimi zamanla ayrışırdı ve ayrışma tam olarak F4'ün
/// karşılaştırmasında ortaya çıkardı.
/// </para>
/// </summary>
public sealed class AlertClosureService(
    IDbContextFactory<ControlPlaneDbContext> factory,
    IEvidenceBundleSource bundles,
    EvidenceBundleStore bundleStore,
    GoldenReviewStore reviews,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Kanıt penceresinin tetiklenme penceresinden ne kadar geriye uzandığı.
    ///
    /// <para>
    /// Taban, olay penceresinin <b>öncesinde</b> olmak zorunda: örtüşen bir
    /// taban "ilk kez görüldü" bulgusunu tanım gereği boşaltır — pencerede
    /// beliren şey tabanda da görünür ve hiçbir şey yeni sayılmaz
    /// (<c>RcaWindow.Validate</c> bunu ayrıca sınıyor).
    /// </para>
    /// </summary>
    public static readonly TimeSpan BaselineSpan = TimeSpan.FromDays(7);

    /// <summary>
    /// Tetiklenmeyi kapatır ve incelemeyi yazar. İkisi bir arada; ayrı ayrı
    /// yapılamaz.
    /// </summary>
    /// <exception cref="ReviewRejectedException">
    /// Tetiklenme yok, kapsam dışı, ya da zaten kapatılmış.
    /// </exception>
    public async Task<AlertClosure> CloseAsync(
        Guid triggerId,
        ReviewVerdict verdict,
        ContradictingEvidenceVerdict contradicting,
        string note,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var trigger = await db.AlertTriggers
            .FirstOrDefaultAsync(t => t.Id == triggerId, cancellationToken)
            ?? throw new ReviewRejectedException("Alarm tetiklenmesi bulunamadı.");

        if (!scope.Allows(trigger.OwnerGroup))
        {
            throw new ReviewRejectedException("Bu alarm kapsamınızın dışında.");
        }

        // Kapatılmış bir alarmı yeniden kapatmak incelemeyi iki kez yazardı ve
        // altın kümede aynı olgu iki kayıt olurdu — doğruluk oranını sessizce
        // eğen bir tekrar.
        if (trigger.State == AlertTriggerState.Closed)
        {
            throw new ReviewRejectedException("Bu alarm zaten kapatılmış.");
        }

        var (bundleId, generated) = await EnsureBundleAsync(db, trigger, scope, cancellationToken);

        var review = await reviews.AddAsync(
            new ReviewInput(bundleId, trigger.Id, verdict, contradicting, note),
            scope,
            cancellationToken);

        trigger.State = AlertTriggerState.Closed;
        trigger.ClosedBySubject = scope.Subject;
        trigger.ClosedAt = _time.GetUtcNow();
        trigger.ReviewId = review.Id;

        await db.SaveChangesAsync(cancellationToken);

        return new AlertClosure(trigger, review, generated);
    }

    /// <summary>
    /// Tetiklenmenin penceresine ait bir paket bulur, yoksa üretir.
    ///
    /// <para>
    /// Arama <b>aynı pencereyle</b> yapılıyor: kapatma sırasında yeni bir paket
    /// üretmek pahalı ve çoğu zaman gereksiz — kullanıcı alarmı zaten rapor
    /// ekranından bakıp kapatıyorsa paket dakikalar önce üretilmiş oluyor.
    /// </para>
    ///
    /// <para>
    /// Eşleşme kapsamı <b>dikkate almıyor</b>, bilerek: aynı pencerenin farklı
    /// kapsamla toplanmış paketi farklı şeyler görür ve ikisi de doğrudur
    /// (<c>BundleScope</c>'un var olma sebebi). Burada aranan şey "bu alarmın
    /// penceresi için elimizde bir paket var mı"; kapsam sorusu incelemenin
    /// kendi kolonunda çözülüyor.
    /// </para>
    /// </summary>
    private async Task<(Guid BundleId, bool Generated)> EnsureBundleAsync(
        ControlPlaneDbContext db,
        AlertTriggerEntity trigger,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        var existing = await db.EvidenceBundles
            .AsNoTracking()
            .Where(b => b.WindowFrom == trigger.WindowFrom && b.WindowTo == trigger.WindowTo)
            .OrderByDescending(b => b.GatheredAt)
            .Select(b => (Guid?)b.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is { } found)
        {
            return (found, false);
        }

        var window = new RcaWindow
        {
            From = trigger.WindowFrom,
            To = trigger.WindowTo,
            BaselineFrom = trigger.WindowFrom - BaselineSpan,
            BaselineTo = trigger.WindowFrom,

            // Alarm hangi kaynakta çıktıysa kanıt oradan başlıyor. Boşsa
            // daraltma yok — sessizlik tipinde kaynak dolu, diğerlerinde
            // olmayabilir.
            SourceIds = string.IsNullOrEmpty(trigger.SourceId) ? [] : [trigger.SourceId],
        };

        var bundle = await bundles.BuildAsync(window, scope, cancellationToken: cancellationToken);
        await bundleStore.SaveAsync(bundle, cancellationToken);

        return (bundle.Id, true);
    }
}
