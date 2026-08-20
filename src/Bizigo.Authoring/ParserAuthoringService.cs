using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bizigo.Authoring;

/// <param name="Ok">İşlem gerçekleşti mi.</param>
/// <param name="Draft">Sonuçtaki kayıt; başarısızsa <see langword="null"/>.</param>
/// <param name="Verdict">Yayın kapısının kararı; yalnızca yayın denemelerinde dolu.</param>
/// <param name="Error">Kullanıcıya gösterilecek sebep.</param>
public sealed record AuthoringResult(
    bool Ok,
    ParserEntity? Draft,
    PublishVerdict? Verdict,
    string Error);

/// <summary>
/// Parser taslaklarının yaşam döngüsü: taslak → incelemede → yayında, ve geri
/// alma (T18, K33).
///
/// <para>
/// <b>Durum makinesi kapalı:</b> her geçiş burada ve yalnızca burada yapılıyor.
/// Uçların doğrudan <c>State</c> yazması, "yayın kapısını atlayan bir yol var mı"
/// sorusunu her uç için ayrı ayrı sormak demek olurdu.
/// </para>
///
/// <para>
/// Yayın <b>tek bir sürümü</b> etkin bırakıyor: aynı <c>parser_id</c> için
/// önceki yayın <see cref="ParserState.Retired"/>'a düşüyor. Geri alma da bunun
/// tersi — yeni bir kayıt yaratmıyor, eski sürümü yeniden etkinleştiriyor, çünkü
/// geri alınan sürümün kapılardan geçtiği zaten biliniyor.
/// </para>
/// </summary>
public sealed class ParserAuthoringService(
    IDbContextFactory<ControlPlaneDbContext> factory,
    ParserPublishGate gate,
    ILogger<ParserAuthoringService> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Taslak oluşturur ya da mevcut taslağı günceller. Kapı çalışmıyor.</summary>
    public async Task<AuthoringResult> SaveDraftAsync(
        Guid? id,
        string yaml,
        string owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        ParserEntity draft;

        if (id is { } existing)
        {
            var found = await db.Parsers.FirstOrDefaultAsync(p => p.Id == existing, cancellationToken);
            if (found is null)
            {
                return new AuthoringResult(false, null, null, "Taslak bulunamadı.");
            }

            // Yayınlanmış bir kaydın üstüne yazmak, geçmişi silmek olurdu.
            if (found.State != ParserState.Draft)
            {
                return new AuthoringResult(false, null, null,
                    $"Yalnızca taslak düzenlenebilir; kayıt '{found.State}' durumunda.");
            }

            draft = found;
        }
        else
        {
            draft = new ParserEntity { ParserId = string.Empty, Version = string.Empty, Yaml = yaml, Owner = owner };
            db.Parsers.Add(draft);
        }

        draft.Yaml = yaml;
        draft.Owner = owner;
        draft.UpdatedAt = _time.GetUtcNow();

        // Kimlik ve sürüm YAML'ın kendisinden geliyor; kullanıcının ayrıca
        // yazması iki gerçek kaynak doğururdu. Çözülemezse boş kalıyor ve
        // yayın kapısı zaten reddedecek.
        var verdict = gate.Inspect(yaml);
        draft.ParserId = verdict.ParserId;
        draft.Version = verdict.Version;

        await db.SaveChangesAsync(cancellationToken);
        return new AuthoringResult(true, draft, verdict, string.Empty);
    }

    public async Task<AuthoringResult> SubmitForReviewAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var draft = await db.Parsers.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (draft is null)
        {
            return new AuthoringResult(false, null, null, "Taslak bulunamadı.");
        }

        if (draft.State != ParserState.Draft)
        {
            return new AuthoringResult(false, null, null,
                $"Yalnızca taslak incelemeye gönderilebilir; kayıt '{draft.State}' durumunda.");
        }

        // Kapı burada da koşuyor: incelemeye bozuk bir taslak göndermek,
        // inceleyenin zamanını harcamak demek.
        var verdict = gate.Inspect(draft.Yaml, draft.ParserId);
        if (!verdict.Ok)
        {
            return new AuthoringResult(false, draft, verdict, "Taslak yayın kapılarından geçmiyor.");
        }

        draft.State = ParserState.InReview;
        draft.PassingTests = verdict.PassingTests;
        draft.UpdatedAt = _time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        return new AuthoringResult(true, draft, verdict, string.Empty);
    }

    public async Task<AuthoringResult> ReturnToDraftAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var entity = await db.Parsers.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (entity is null)
        {
            return new AuthoringResult(false, null, null, "Kayıt bulunamadı.");
        }

        if (entity.State != ParserState.InReview)
        {
            return new AuthoringResult(false, null, null,
                $"Yalnızca incelemedeki kayıt taslağa döndürülebilir; kayıt '{entity.State}' durumunda.");
        }

        entity.State = ParserState.Draft;
        entity.UpdatedAt = _time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        return new AuthoringResult(true, entity, null, string.Empty);
    }

    /// <summary>
    /// İncelemedeki kaydı yayınlar.
    ///
    /// <para>
    /// Kapı <b>yayın anında yeniden</b> koşuyor. İncelemeye gönderildiğinde de
    /// koşmuştu, ama aradan geçen sürede pattern kütüphanesi ya da eşleme
    /// tabloları değişmiş olabilir — o zaman geçen bir taslak şimdi geçmeyebilir
    /// ve bunu yayından sonra öğrenmek istemiyoruz.
    /// </para>
    /// </summary>
    public async Task<AuthoringResult> PublishAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var candidate = await db.Parsers.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (candidate is null)
        {
            return new AuthoringResult(false, null, null, "Kayıt bulunamadı.");
        }

        if (candidate.State != ParserState.InReview)
        {
            return new AuthoringResult(false, null, null,
                $"Yalnızca incelemedeki kayıt yayınlanabilir; kayıt '{candidate.State}' durumunda.");
        }

        var verdict = gate.Inspect(candidate.Yaml, candidate.ParserId);
        if (!verdict.Ok)
        {
            return new AuthoringResult(false, candidate, verdict, "Kayıt yayın kapılarından geçmiyor.");
        }

        var previous = await db.Parsers
            .Where(p => p.ParserId == candidate.ParserId
                && p.State == ParserState.Published
                && p.Id != candidate.Id)
            .ToListAsync(cancellationToken);

        foreach (var old in previous)
        {
            old.State = ParserState.Retired;
        }

        candidate.State = ParserState.Published;
        candidate.PassingTests = verdict.PassingTests;
        candidate.PublishedAt = _time.GetUtcNow();
        candidate.UpdatedAt = _time.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Parser yayınlandı: {ParserId}@{Version} ({Tests} test geçti, {Retired} eski sürüm emekliye ayrıldı).",
            candidate.ParserId,
            candidate.Version,
            verdict.PassingTests,
            previous.Count);

        return new AuthoringResult(true, candidate, verdict, string.Empty);
    }

    /// <summary>
    /// Yayındaki sürümü emekliye ayırıp bir öncekini geri getirir.
    ///
    /// <para>
    /// Kapı <b>koşmuyor</b> — bilinçli. Geri alınan sürüm zaten yayınlanmıştı,
    /// yani kapılardan geçmişti. Geri alma bir acil durum işlemi; o anda pattern
    /// kütüphanesindeki bir değişiklik yüzünden reddedilmesi, kullanıcıyı bozuk
    /// sürümle baş başa bırakırdı.
    /// </para>
    /// </summary>
    public async Task<AuthoringResult> RollbackAsync(
        string parserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parserId);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var current = await db.Parsers
            .FirstOrDefaultAsync(p => p.ParserId == parserId && p.State == ParserState.Published, cancellationToken);

        if (current is null)
        {
            return new AuthoringResult(false, null, null, $"'{parserId}' için yayında sürüm yok.");
        }

        var previous = await db.Parsers
            .Where(p => p.ParserId == parserId && p.State == ParserState.Retired && p.PublishedAt != null)
            .OrderByDescending(p => p.PublishedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (previous is null)
        {
            return new AuthoringResult(false, null, null,
                $"'{parserId}' için dönülecek önceki sürüm yok.");
        }

        current.State = ParserState.Retired;
        current.UpdatedAt = _time.GetUtcNow();
        previous.State = ParserState.Published;
        previous.PublishedAt = _time.GetUtcNow();
        previous.UpdatedAt = _time.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Parser geri alındı: {ParserId} {From} → {To}.",
            parserId,
            current.Version,
            previous.Version);

        return new AuthoringResult(true, previous, null, string.Empty);
    }
}
