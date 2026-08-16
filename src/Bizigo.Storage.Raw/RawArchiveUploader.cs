using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bizigo.Storage.Raw;

public sealed record UploadReport(int SegmentsProcessed, int ObjectsWritten, int SegmentsDeleted);

/// <summary>
/// WAL segmentlerini ham arşive taşır ve manifest'i yazar (F1 §7.0, §7.1).
///
/// <para>
/// Sıralama, RustFS'in veri kaybetmesini <b>varsayarak</b> kuruldu:
/// yükle → <b>geri oku</b> → sha256 karşılaştır → manifest'e <c>verified_at</c>
/// yaz → segmenti saklama süresi dolunca sil. Doğrulanmamış bir segment asla
/// silinmez; yazma başarılı görünüp içerik bozulmuşsa yerel kopya hâlâ elde.
/// </para>
///
/// <para>
/// Kesinti dayanıklılığı: manifest yazılmadan çökülürse nesne öksüz kalır ve
/// segment yeniden yüklenir. Mükerrer nesne yer kaplar ama veri kaybettirmez —
/// bilinçli tercih, tersi (manifest'i önce yazmak) kayıp nesneyi var gösterirdi.
/// </para>
/// </summary>
public sealed class RawArchiveUploader(
    IRawSegmentSource segments,
    IRawObjectStore store,
    IDbContextFactory<ControlPlaneDbContext> factory,
    SourceDirectory sources,
    IRawRefSink refSink,
    IOptions<RawStoreOptions> options,
    ILogger<RawArchiveUploader> logger,
    TimeProvider? timeProvider = null)
{
    private readonly RawStoreOptions _options = options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<UploadReport> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await sources.RefreshAsync(cancellationToken);

        var pending = segments.ListPending();
        var processed = 0;
        var written = 0;

        foreach (var segment in pending)
        {
            written += await UploadSegmentAsync(segment, cancellationToken);
            processed++;
        }

        var deleted = await DeleteExpiredSegmentsAsync(pending, cancellationToken);
        return new UploadReport(processed, written, deleted);
    }

    private async Task<int> UploadSegmentAsync(PendingSegment segment, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var alreadyUploaded = await db.RawManifest
            .AsNoTracking()
            .AnyAsync(m => m.WalSegment == segment.Id, cancellationToken);

        if (alreadyUploaded)
        {
            return 0;
        }

        var builders = new Dictionary<RawObjectKey, RawObjectBuilder>();
        var written = 0;

        foreach (var line in segments.ReadLines(segment.Id))
        {
            RawRecord record;
            try
            {
                record = RawRecordCodec.Read(line.Span);
            }
            catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException)
            {
                // Tek bozuk satır yüzünden segmentin tamamını arşivsiz bırakmayız.
                logger.LogWarning(ex, "Segment {Segment} içinde çözülemeyen satır atlandı.", segment.Id);
                continue;
            }

            var resolved = sources.Resolve(record.SourceKey);
            var timestamp = record.ObservedAt ?? record.ReceivedAt;
            var key = new RawObjectKey(
                resolved.OwnerGroup,
                new DateTimeOffset(timestamp.UtcDateTime.Date.AddHours(timestamp.UtcDateTime.Hour), TimeSpan.Zero),
                resolved.SourceClass,
                Guid.CreateVersion7(timestamp).ToString("N"));

            // Aynı gruba giden kayıtlar tek nesnede toplansın diye anahtarın
            // kimlik bileşeni dışındaki kısmı gruplama anahtarı.
            var groupKey = key with { Id = string.Empty };

            if (!builders.TryGetValue(groupKey, out var builder))
            {
                builder = new RawObjectBuilder();
                builders[groupKey] = builder;
            }

            // Çözülen kimlikler satıra geri yazılıyor: arşivdeki satır kendi
            // başına anlamlı olmalı, manifest'e bakmadan hangi gruba ait olduğu
            // okunabilmeli.
            var enriched = record with { OwnerGroup = resolved.OwnerGroup, SourceId = resolved.SourceId };
            builder.Add(record.EventId, timestamp, RawRecordCodec.ToLine(enriched));

            if (builder.UncompressedBytes >= _options.TargetObjectBytes)
            {
                await FlushAsync(groupKey, builder, segment, db, cancellationToken);
                builders[groupKey] = new RawObjectBuilder();
                written++;
            }
        }

        foreach (var (groupKey, builder) in builders)
        {
            if (builder.IsEmpty)
            {
                continue;
            }

            await FlushAsync(groupKey, builder, segment, db, cancellationToken);
            written++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return written;
    }

    private async Task FlushAsync(
        RawObjectKey groupKey,
        RawObjectBuilder builder,
        PendingSegment segment,
        ControlPlaneDbContext db,
        CancellationToken cancellationToken)
    {
        var built = builder.Build(_options.CompressionLevel);
        var key = (groupKey with { Id = Guid.CreateVersion7(built.TsFrom).ToString("N") }).Value;

        await store.PutAsync(key, built.Compressed, cancellationToken);

        // GERİ OKUMA: "yazdım" ile "yazıldı" aynı şey değil. RustFS 1.0-rc olduğu
        // için bu adım atlanmaz — doğrulanmadan segment silinemez.
        var readBack = await store.GetAsync(key, cancellationToken);
        var verified = readBack is not null
            && string.Equals(Sha256Of(readBack), built.Sha256, StringComparison.Ordinal);

        if (!verified)
        {
            logger.LogError(
                "Ham nesne {Key} geri okumada doğrulanamadı — segment {Segment} silinmeyecek.",
                key,
                segment.Id);
        }

        var now = _time.GetUtcNow();
        db.RawManifest.Add(new RawManifestEntity
        {
            ObjectKey = key,
            OwnerGroup = groupKey.OwnerGroup,
            Sha256 = built.Sha256,
            ByteSize = built.ByteSize,
            EventCount = built.EventCount,
            TsFrom = built.TsFrom,
            TsTo = built.TsTo,
            UploadedAt = now,
            VerifiedAt = verified ? now : null,
            State = verified ? RawObjectState.Verified : RawObjectState.ChecksumMismatch,
            WalSegment = segment.Id,
        });

        await refSink.RecordAsync(key, built.Refs, cancellationToken);
    }

    /// <summary>
    /// Saklama süresi dolmuş ve <b>tamamı doğrulanmış</b> segmentleri siler
    /// (F1 §7.0 koruma #3).
    /// </summary>
    private async Task<int> DeleteExpiredSegmentsAsync(
        IReadOnlyList<PendingSegment> pending,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var cutoff = _time.GetUtcNow() - _options.SegmentRetention;
        var deleted = 0;

        foreach (var segment in pending)
        {
            var rows = await db.RawManifest
                .AsNoTracking()
                .Where(m => m.WalSegment == segment.Id)
                .Select(m => new { m.VerifiedAt })
                .ToListAsync(cancellationToken);

            // Hiç satır yoksa segment henüz yüklenmemiş demektir — silinmez.
            if (rows.Count == 0)
            {
                continue;
            }

            if (rows.Any(r => r.VerifiedAt is null || r.VerifiedAt > cutoff))
            {
                continue;
            }

            segments.Delete(segment.Id);
            deleted++;
        }

        return deleted;
    }

    private static string Sha256Of(byte[] content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
}
