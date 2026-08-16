using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bizigo.Storage.Raw;

public sealed record ScrubReport(int Checked, int Mismatched, int MissingObjects);

/// <summary>
/// Periyodik sağlama denetimi (F1 §7.0 koruma #5).
///
/// <para>
/// Örneklenmiş nesneler indirilip sha256'ları manifest'e karşı doğrulanıyor.
/// Amaç sessiz bozulmayı <b>olduğu gün</b> yakalamak — replay denendiği gün
/// değil. RustFS 1.0-rc'de bu, "riski taşınabilir kılan" beş korumadan biri.
/// </para>
///
/// <para>
/// En eski doğrulanandan başlanıyor: her tur farklı nesnelere bakar ve arşiv
/// zamanla baştan sona taranmış olur. Rastgele örnekleme aynı nesneyi tekrar
/// tekrar seçip bazılarına hiç bakmayabilirdi.
/// </para>
/// </summary>
public sealed class RawArchiveScrubber(
    IRawObjectStore store,
    IDbContextFactory<ControlPlaneDbContext> factory,
    IOptions<RawStoreOptions> options,
    ILogger<RawArchiveScrubber> logger,
    TimeProvider? timeProvider = null)
{
    private readonly RawStoreOptions _options = options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<ScrubReport> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var sample = await db.RawManifest
            .Where(m => m.State != RawObjectState.Missing)
            .OrderBy(m => m.LastScrubbedAt ?? DateTimeOffset.MinValue)
            .Take(_options.ScrubSampleSize)
            .ToListAsync(cancellationToken);

        var mismatched = 0;
        var missing = 0;
        var now = _time.GetUtcNow();

        foreach (var entry in sample)
        {
            var content = await store.GetAsync(entry.ObjectKey, cancellationToken);

            if (content is null)
            {
                entry.State = RawObjectState.Missing;
                missing++;

                // Kayıp nesne sessizce geçilmez: replay o aralığı eksik dönecek
                // ve manifest olmasa bu fark edilmezdi.
                logger.LogError(
                    "Ham nesne kayıp: {Key} ({Events} olay, {From:o} - {To:o}). " +
                    "Replay bu aralığı eksik dönecek.",
                    entry.ObjectKey,
                    entry.EventCount,
                    entry.TsFrom,
                    entry.TsTo);
            }
            else if (!string.Equals(Sha256Of(content), entry.Sha256, StringComparison.Ordinal))
            {
                entry.State = RawObjectState.ChecksumMismatch;
                mismatched++;

                logger.LogError(
                    "Ham nesne bozulmuş: {Key} sha256 manifest ile uyuşmuyor.",
                    entry.ObjectKey);
            }
            else
            {
                entry.State = RawObjectState.Verified;
            }

            entry.LastScrubbedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ScrubReport(sample.Count, mismatched, missing);
    }

    private static string Sha256Of(byte[] content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
}
