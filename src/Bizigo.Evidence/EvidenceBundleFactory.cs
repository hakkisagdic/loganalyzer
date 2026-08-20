using Bizigo.Contracts;
using Bizigo.Query;
using Microsoft.Extensions.Logging;

namespace Bizigo.Evidence;

/// <summary>
/// Toplama koşusunu <b>saklanabilir</b> bir pakete çevirir (T36).
///
/// <para>
/// <see cref="EvidenceCollector"/>'dan ayrı duruyor ve ayrım anlamlı: toplayıcı
/// sağlayıcıları koşturuyor ve hiçbirini tanımıyor (T34'ün taşıyıcı iddiası).
/// Bu sınıf ise pakete <b>koşunun kendisi hakkında</b> iki şey ekliyor —
/// kimlik/zaman ve pencerenin zaman güvenilirliği. İkisini toplayıcıya koymak,
/// onu sağlayıcılardan habersiz olmaktan çıkarır ve F5'te değişmesi gereken yer
/// hâline getirirdi.
/// </para>
/// </summary>
public sealed class EvidenceBundleFactory(
    EvidenceCollector collector,
    IScopedQuery query,
    ILogger<EvidenceBundleFactory> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<EvidenceBundle> BuildAsync(
        RcaWindow window,
        AccessScope scope,
        GatherBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(scope);

        var report = await collector.GatherAsync(
            window, scope, budget ?? GatherBudget.Default, cancellationToken);

        var trust = await MeasureTrustAsync(window, scope, cancellationToken);

        return new EvidenceBundle
        {
            Id = Guid.CreateVersion7(_time.GetUtcNow()),
            GatheredAt = _time.GetUtcNow(),
            Window = window,
            Scope = new BundleScope([.. scope.OwnerGroups.OrderBy(g => g, StringComparer.Ordinal)], scope.IsUnrestricted),
            Slices = report.Slices,
            Trust = trust,
        };
    }

    /// <summary>
    /// Penceredeki toplam olay ve zamanı <c>parsed</c> olmayanların sayısı.
    ///
    /// <para>
    /// <b>Üçüncü bir sorgu yüzeyi yazılmıyor:</b> <c>time_source</c> zaten olay
    /// sorgusunun filtrelenebilir alanı ve sayım <c>CountEventsAsync</c>'ten
    /// geçiyor — yani kapsam kapısı (K17) burada da tek kapı. Kendi SQL'ini yazan
    /// bir yoklama, kapsamı ikinci kez tanımlamak olurdu.
    /// </para>
    ///
    /// <para>
    /// Hata yutulmuyor ama paketi de düşürmüyor: ölçüm başarısızsa
    /// <see cref="WindowTrust.Unmeasured"/> dönüyor ve rapor "bilinmiyor" diyor.
    /// Sıfır dönmek, ölçülmemiş bir şeye "sorun yok" demek olurdu.
    /// </para>
    /// </summary>
    private async Task<WindowTrust> MeasureTrustAsync(
        RcaWindow window,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        var baseQuery = new EventQuery
        {
            From = window.From,
            To = window.To,
            OwnerGroups = window.OwnerGroups,
            SourceIds = window.SourceIds,
            Limit = 1,
        };

        try
        {
            var total = await query.CountEventsAsync(baseQuery, scope, cancellationToken);

            var unreliable = await query.CountEventsAsync(
                baseQuery with
                {
                    Filters = [new FieldFilter("time_source", FilterOperator.NotEquals, [TimeSources.Parsed])],
                },
                scope,
                cancellationToken);

            return new WindowTrust(total, unreliable);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Pencere zaman güvenilirliği ölçülemedi; rapor 'bilinmiyor' diyecek.");
            return WindowTrust.Unmeasured;
        }
    }
}
