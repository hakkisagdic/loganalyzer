using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Evidence;

/// <summary>
/// Raporun insan değerlendirmesi (T37, RCA §7).
///
/// <para>
/// Ekrandaki üç düğmenin (<i>doğru / kısmen / yanlış</i>) gittiği yer.
/// Yazmayan bir düğme, kullanıcıya katkı verdiğini <b>sandıran</b> bir
/// düğmedir — RCA belgesinin 2. riski o zaman basılmayan değil
/// <b>basılan</b> düğmelerle gerçekleşir.
/// </para>
/// </summary>
public sealed class EvidenceReviewStore(
    IDbContextFactory<ControlPlaneDbContext> factory,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Kabul edilen durumlar — RCA §4.2 <c>review.state</c>.</summary>
    public static IReadOnlySet<string> States { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "correct", "partially", "wrong" };

    /// <summary>
    /// İnceleme kaydeder. <b>Üzerine yazmıyor, ekliyor.</b>
    ///
    /// <para>
    /// Aynı paket birden çok kez incelenebilir: ilk bakan "kısmen" der, kök neden
    /// sonradan anlaşılınca ikinci kayıt düşer. İncelemenin <b>değişmesi</b>
    /// kalite ölçümü için bir veri; üzerine yazmak onu silerdi.
    /// </para>
    /// </summary>
    public async Task<EvidenceReview> SaveAsync(
        Guid bundleId,
        string state,
        string reviewer,
        string actualRootCause,
        string note,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        if (!States.Contains(state))
        {
            // Bilinmeyen bir durum sessizce kabul edilseydi altın küme
            // sayılamayan bir değer taşırdı ve T38 onu ancak sayarken fark
            // ederdi — o da fark ederse.
            throw new ArgumentException(
                $"'{state}' geçerli bir inceleme durumu değil. Kabul edilenler: "
                + string.Join(", ", States.Order(StringComparer.Ordinal)),
                nameof(state));
        }

        var entity = new EvidenceReviewEntity
        {
            Id = Guid.CreateVersion7(_time.GetUtcNow()),
            BundleId = bundleId,
            ReviewedAt = _time.GetUtcNow(),
            State = state,
            Reviewer = reviewer ?? string.Empty,
            ActualRootCause = actualRootCause ?? string.Empty,
            Note = note ?? string.Empty,
        };

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.EvidenceReviews.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        return Map(entity);
    }

    /// <summary>
    /// Paketin <b>son</b> incelemesi — ekranın açılışta sorduğu tek soru.
    /// Hiç incelenmemişse <see langword="null"/>, ki ekran "henüz incelenmedi"
    /// ile "yanlış diye işaretlenmiş"i karıştırmasın.
    /// </summary>
    public async Task<EvidenceReview?> GetLatestAsync(
        Guid bundleId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var entity = await db.EvidenceReviews
            .AsNoTracking()
            .Where(r => r.BundleId == bundleId)
            .OrderByDescending(r => r.ReviewedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return entity is null ? null : Map(entity);
    }

    private static EvidenceReview Map(EvidenceReviewEntity e) =>
        new(e.Id, e.BundleId, e.ReviewedAt, e.State, e.Reviewer, e.ActualRootCause, e.Note);
}

/// <param name="ActualRootCause">
/// Altın kümenin asıl değerli yarısı: "yanlış" demek modeli düzeltmiyor,
/// doğrusunun ne olduğu düzeltiyor.
/// </param>
public sealed record EvidenceReview(
    Guid Id,
    Guid BundleId,
    DateTimeOffset ReviewedAt,
    string State,
    string Reviewer,
    string ActualRootCause,
    string Note);
