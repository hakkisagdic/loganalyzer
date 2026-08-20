using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Evidence;

/// <summary>İnceleme yazılamadı — çağıran 400 dönmeli.</summary>
public sealed class ReviewRejectedException(string reason) : InvalidOperationException(reason);

/// <param name="Total">Kapsam altındaki inceleme sayısı.</param>
/// <param name="Correct">Doğru bulunan rapor sayısı.</param>
/// <param name="Unknown">"Bilmiyorum" sayısı — orana <b>girmiyor</b>.</param>
public sealed record GoldenSetQuality(long Total, long Correct, long Unknown)
{
    /// <summary>
    /// Karar verilmiş incelemeler: <c>Total - Unknown</c>. Doğruluk oranının
    /// paydası bu.
    /// </summary>
    public long Decided => Total - Unknown;

    /// <summary>
    /// Doğruluk oranı. Payda sıfırsa <see langword="null"/> — sıfır <b>değil</b>.
    ///
    /// <para>
    /// Ayrım bu üründe pahalı bir hata sınıfının önüne geçiyor: "%0 doğru" ile
    /// "henüz karar verilmiş inceleme yok" aynı sayıyla gösterilirse ekran,
    /// ölçülmemiş bir şeyi kötü ölçülmüş gibi gösterir.
    /// </para>
    /// </summary>
    public double? Accuracy => Decided > 0 ? (double)Correct / Decided : null;

    /// <summary>
    /// "Bilmiyorum" oranı — <b>kendisi bir gösterge</b>. Yüksekse ya kanıt
    /// paketi yetersiz ya soru yanlış soruluyor.
    /// </summary>
    public double? UnknownRatio => Total > 0 ? (double)Unknown / Total : null;
}

/// <param name="BundleId">Zorunlu — paketsiz inceleme F4'te ölçülemez.</param>
/// <param name="TriggerId">Alarm tetikliyse dolu, kullanıcı tetikliyse boş.</param>
/// <param name="OwnerGroup">
/// Kaydın yazılacağı grup. Alarm tetikli incelemede <b>yok sayılıyor</b> —
/// grup tetiklenmeden geliyor. Kullanıcı tetiklide, kapsamı birden çok grup
/// olan kişi bunu vermek zorunda.
/// </param>
public sealed record ReviewInput(
    Guid BundleId,
    Guid? TriggerId,
    ReviewVerdict Verdict,
    ContradictingEvidenceVerdict ContradictingEvidence,
    string Note,
    string? OwnerGroup = null);

/// <summary>
/// Altın kümenin deposu ve <b>kapsam kapısı</b> (T38).
///
/// <para>
/// <b>Kapsam neden burada:</b> inceleme ClickHouse'ta değil kontrol
/// düzleminde, dolayısıyla <see cref="Bizigo.Query.IScopedQuery"/> onu
/// kendiliğinden korumuyor — <c>AlertRuleService</c> ile aynı durum ve aynı
/// çözüm. K17'nin dersi "kapsamı ikinci bir yere koyma" değil, "kapsamı
/// <i>dağıtma</i>": incelemenin kapsamı bu sınıfta, sadece bu sınıfta
/// doğrulanıyor.
/// </para>
///
/// <para>
/// Filtre <c>golden_reviews.owner_group</c> kolonundan geçiyor, pakete
/// <c>JOIN</c> atıp JSON açmaktan değil. Paketin kapsamı yalnızca gövdesindeki
/// <c>BundleScope</c>'ta duruyor ve orası <b>sorgulanamaz</b>.
/// </para>
/// </summary>
public sealed class GoldenReviewStore(
    IDbContextFactory<ControlPlaneDbContext> factory,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// İncelemeyi yazar.
    /// </summary>
    /// <exception cref="ReviewRejectedException">
    /// Paket yok, ya da kapsam tek bir gruba çözülemiyor.
    /// </exception>
    public async Task<GoldenReviewEntity> AddAsync(
        ReviewInput input,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // Paket bağı zorunlu ve VAR OLMAK zorunda. Var olmayan bir kimliği
        // kabul etmek, F4'ün karşılaştırmasını sessizce eksik kümeye indirger:
        // kayıt sayılır, karşılaştırmaya giremez.
        var bundleExists = await db.EvidenceBundles
            .AsNoTracking()
            .AnyAsync(b => b.Id == input.BundleId, cancellationToken);

        if (!bundleExists)
        {
            throw new ReviewRejectedException(
                "İnceleme bir kanıt paketine bağlanmak zorunda; verilen paket bulunamadı.");
        }

        var entity = new GoldenReviewEntity
        {
            BundleId = input.BundleId,
            TriggerId = input.TriggerId,
            OwnerGroup = await ResolveGroupAsync(db, input, scope, cancellationToken),
            Verdict = input.Verdict,
            ContradictingEvidence = input.ContradictingEvidence,
            Note = input.Note ?? string.Empty,
            ReviewerSubject = scope.Subject,
            ReviewedAt = _time.GetUtcNow(),
        };

        db.GoldenReviews.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        return entity;
    }

    /// <summary>
    /// Kalite göstergesi (T38 kabul kriteri).
    ///
    /// <para>
    /// Sayım veritabanında yapılıyor, satırlar çekilip bellekte değil: gösterge
    /// altın küme büyüdükçe pahalılaşırsa ilk kaldırılacak şey gösterge olur.
    /// </para>
    ///
    /// <para>
    /// Boş kümede de <b>bir sonuç dönüyor</b> — sıfırlarla. Boş dönmek ekranın
    /// göstergeyi gizlemesine izin verirdi ve gizlenen bir sıfır, "henüz
    /// ölçülmedi" ile "ölçüldü, sıfır" arasındaki farkı siler.
    /// </para>
    /// </summary>
    public async Task<GoldenSetQuality> QualityAsync(
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var query = Visible(db, scope);

        var total = await query.LongCountAsync(cancellationToken);
        var correct = await query.LongCountAsync(r => r.Verdict == ReviewVerdict.Correct, cancellationToken);
        var unknown = await query.LongCountAsync(r => r.Verdict == ReviewVerdict.Unknown, cancellationToken);

        return new GoldenSetQuality(total, correct, unknown);
    }

    /// <summary>Bir paketin kapsam altındaki incelemeleri, en yeniden eskiye.</summary>
    public async Task<IReadOnlyList<GoldenReviewEntity>> ForBundleAsync(
        Guid bundleId,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        return await Visible(db, scope)
            .Where(r => r.BundleId == bundleId)
            .OrderByDescending(r => r.ReviewedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Kapsam filtresi — <b>tek yer</b>.
    ///
    /// <para>
    /// Boş kapsam <see cref="Enumerable.Empty{T}"/>'ye değil, hiçbir satırın
    /// eşleşemeyeceği bir sorguya çevriliyor: çağıran yine <c>IQueryable</c>
    /// alıyor ve "filtre yok"a düşen bir yol kalmıyor.
    /// </para>
    /// </summary>
    private static IQueryable<GoldenReviewEntity> Visible(ControlPlaneDbContext db, AccessScope scope)
    {
        var query = db.GoldenReviews.AsNoTracking();

        if (scope.IsUnrestricted)
        {
            return query;
        }

        if (scope.OwnerGroups.Count == 0)
        {
            return query.Where(_ => false);
        }

        var groups = scope.OwnerGroups.ToArray();
        return query.Where(r => groups.Contains(r.OwnerGroup));
    }

    /// <summary>
    /// İncelemenin yazılacağı grup.
    ///
    /// <para>
    /// <b>Grup incelenen şeyden geliyor, inceleyenden değil.</b> Alarm tetikli
    /// incelemede tetiklenmenin kendi <c>OwnerGroup</c>'u kullanılıyor: alarm
    /// hangi ekibin verisinde çıktıysa incelemesi o ekibin göstergesine
    /// yazılmalı. Kapsamı geniş bir kişinin başka bir ekibin alarmını kapatıp
    /// kaydı <i>kendi</i> grubuna yazması, göstergeyi sessizce yanlış ekibe
    /// mal ederdi.
    /// </para>
    ///
    /// <para>
    /// Tetiklenmenin grubu ayrıca <b>kapsam içinde olmak zorunda</b> — aksi
    /// hâlde bir kullanıcı görmediği bir alarmı kapatabilirdi.
    /// </para>
    ///
    /// <para>
    /// Kullanıcı tetikli incelemede alarm yok, dolayısıyla grup açıkça
    /// veriliyor ya da kapsam tek bir gruba çözülüyor. Sistemin çok gruplu bir
    /// kapsamdan kendi başına seçmesi, kaydı yanlış ekibe yazmanın sessiz
    /// yoluydu.
    /// </para>
    /// </summary>
    private static async Task<string> ResolveGroupAsync(
        ControlPlaneDbContext db,
        ReviewInput input,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        if (input.TriggerId is { } triggerId)
        {
            var group = await db.AlertTriggers
                .AsNoTracking()
                .Where(t => t.Id == triggerId)
                .Select(t => t.OwnerGroup)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new ReviewRejectedException("Verilen alarm tetiklenmesi bulunamadı.");

            return scope.Allows(group)
                ? group
                : throw new ReviewRejectedException("Bu alarm kapsamınızın dışında.");
        }

        if (!string.IsNullOrWhiteSpace(input.OwnerGroup))
        {
            var requested = input.OwnerGroup.Trim();

            return scope.Allows(requested)
                ? requested
                : throw new ReviewRejectedException("İstenen grup kapsamınızın dışında.");
        }

        if (scope.OwnerGroups.Count == 1)
        {
            return scope.OwnerGroups.First();
        }

        throw new ReviewRejectedException(
            scope.OwnerGroups.Count == 0
                ? "İnceleme yazmak için bir kapsam grubu gerekiyor."
                : "Kapsamınız birden çok grup içeriyor; incelemenin yazılacağı grup belirtilmeli.");
    }
}
