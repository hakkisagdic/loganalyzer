using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bizigo.Storage.Raw;

public sealed record UploadReport(int SegmentsProcessed, int ObjectsWritten, int SegmentsDeleted);

/// <param name="Attempted">Kurtarılmaya çalışılan nesne sayısı.</param>
/// <param name="Recovered">Yeniden yazılıp doğrulanan nesne sayısı.</param>
/// <param name="Unrecoverable">
/// Kurtarılamadığı <b>kesinleşen</b> nesneler: kaynak segment yok, yeniden
/// kurulan içerik manifest'le uyuşmuyor, ya da deneme üst sınırı doldu.
/// </param>
public sealed record RecoveryReport(int Attempted, int Recovered, int Unrecoverable);

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

        var written = 0;

        foreach (var (groupKey, built) in BuildObjects(segment.Id))
        {
            await StoreAsync(groupKey, built, segment.Id, db, cancellationToken);
            written++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return written;
    }

    /// <summary>
    /// Segmentteki satırları arşiv nesnelerine dönüştürür — <b>depoya yazmadan</b>.
    ///
    /// <para>
    /// Yükleme ile kurtarma (T40) bu tek yolu paylaşıyor. İkinci bir kurulum
    /// yolu yazmak §9'un yasakladığı kopya olurdu ve ayrışma tam olarak
    /// kurtarmanın yakalayamayacağı yerde ortaya çıkardı: yeniden kurulan nesne
    /// farklı bir sha256 üretir, kurtarma "manifest yanlış" diye durur ve gerçek
    /// sebep başka bir dosyada aranırdı.
    /// </para>
    ///
    /// <para>
    /// <b>Belirlenimci olmak zorunda:</b> aynı segment aynı satırları aynı
    /// sırayla aynı gruplara koyup aynı yerlerde bölmeli. Kurtarmanın nesneyi
    /// sha256'sından tanıması buna dayanıyor.
    /// </para>
    /// </summary>
    private IEnumerable<(RawObjectKey GroupKey, BuiltRawObject Built)> BuildObjects(string segmentId)
    {
        var builders = new Dictionary<RawObjectKey, RawObjectBuilder>();

        foreach (var line in segments.ReadLines(segmentId))
        {
            RawRecord record;
            try
            {
                record = RawRecordCodec.Read(line.Span);
            }
            catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException)
            {
                // Tek bozuk satır yüzünden segmentin tamamını arşivsiz bırakmayız.
                logger.LogWarning(ex, "Segment {Segment} içinde çözülemeyen satır atlandı.", segmentId);
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
                yield return (groupKey, builder.Build(_options.CompressionLevel));
                builders[groupKey] = new RawObjectBuilder();
            }
        }

        foreach (var (groupKey, builder) in builders)
        {
            if (builder.IsEmpty)
            {
                continue;
            }

            yield return (groupKey, builder.Build(_options.CompressionLevel));
        }
    }

    /// <summary>
    /// Kurulmuş nesneyi depoya yazar, geri okuyup doğrular ve manifest satırını
    /// ekler.
    /// </summary>
    private async Task StoreAsync(
        RawObjectKey groupKey,
        BuiltRawObject built,
        string segmentId,
        ControlPlaneDbContext db,
        CancellationToken cancellationToken)
    {
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
                segmentId);
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
            WalSegment = segmentId,
        });

        await refSink.RecordAsync(key, built.Refs, cancellationToken);
    }

    /// <summary>
    /// Kayıp ya da bozuk nesneleri yerel WAL segmentinden geri yükler (T40).
    ///
    /// <para>
    /// <b>Neden tespitten tetikleniyor, kendi takviminden değil:</b> bağlayıcı
    /// kısıt saklama penceresi. Kurtarma ayrı bir zamanlamayla koşsaydı tespit
    /// ile kurtarma arasına ikinci bir gecikme girerdi ve 48 saatlik bütçe iki
    /// bağımsız periyot arasında bölünürdü — kimsenin toplamını tutmadığı bir
    /// bütçe. Scrub bir nesneyi <c>Missing</c> işaretlediği turda kurtarma da
    /// koşuyor, yani pencerenin tamamı tespite kalıyor.
    /// </para>
    ///
    /// <para>
    /// <b>Nesne sha256'sından tanınıyor.</b> Segment birden çok nesne üretmiş
    /// olabilir ve manifest hangisinin hangisi olduğunu ayrıca söylemiyor;
    /// yeniden kurulan adaylardan sha256'sı tutan, tanım gereği aranan nesne.
    /// Tutan aday yoksa <b>hiçbir şey yazılmıyor</b>: manifest'in kaydı doğru
    /// kabul ediliyor, sapan taraf kurtarmadır ve yanlış içerikle üzerine
    /// yazmak kaybı sessiz bir bozulmaya çevirirdi.
    /// </para>
    /// </summary>
    public async Task<RecoveryReport> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await sources.RefreshAsync(cancellationToken);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var broken = await db.RawManifest
            .Where(m => m.State == RawObjectState.Missing || m.State == RawObjectState.ChecksumMismatch)
            .Where(m => m.RecoveryAttempts < _options.MaxRecoveryAttempts)
            .OrderBy(m => m.RecoveryAttempts)
            .Take(_options.ScrubSampleSize)
            .ToListAsync(cancellationToken);

        var recovered = 0;
        var unrecoverable = 0;

        // Segment başına tek kurulum: aynı segmentten iki nesne kaybolduysa
        // satırları iki kez okumanın anlamı yok ve kurulum en pahalı adım.
        foreach (var group in broken.GroupBy(m => m.WalSegment, StringComparer.Ordinal))
        {
            var candidates = TryBuild(group.Key);

            foreach (var entry in group)
            {
                entry.RecoveryAttempts++;

                // `FirstOrDefault` değer demeti döndürdüğü için eşleşme yokken
                // `null` değil VARSAYILAN demet geliyor; ayrımı `Built` üzerinden
                // yapmak zorundayız. Yalnızca `match is null` yazmak, eşleşmeyen
                // durumda boş bir demeti geçerli sanıp NullReference'a düşürüyordu.
                var match = candidates?.FirstOrDefault(c =>
                    string.Equals(c.Built.Sha256, entry.Sha256, StringComparison.Ordinal));

                if (match?.Built is null)
                {
                    if (Exhausted(entry))
                    {
                        unrecoverable++;
                    }

                    continue;
                }

                await store.PutAsync(entry.ObjectKey, match.Value.Built.Compressed, cancellationToken);

                var readBack = await store.GetAsync(entry.ObjectKey, cancellationToken);
                var verified = readBack is not null
                    && string.Equals(Sha256Of(readBack), entry.Sha256, StringComparison.Ordinal);

                if (!verified)
                {
                    logger.LogError(
                        "Kurtarılan nesne {Key} geri okumada doğrulanamadı.", entry.ObjectKey);

                    if (Exhausted(entry))
                    {
                        unrecoverable++;
                    }

                    continue;
                }

                entry.State = RawObjectState.Verified;
                entry.VerifiedAt = _time.GetUtcNow();
                recovered++;

                logger.LogInformation(
                    "Ham nesne {Key} segment {Segment}'ten geri yüklendi.",
                    entry.ObjectKey,
                    group.Key);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new RecoveryReport(broken.Count, recovered, unrecoverable);
    }

    /// <summary>
    /// Deneme hakkı bittiyse durumu kesinleştirir.
    ///
    /// <para>
    /// Sessizce yeniden denemeye devam etmek, bozuk bir S3 yapılandırmasında
    /// sonsuz yeniden yazma döngüsü demekti — ve operatör için "hâlâ deniyor"
    /// ile "artık denemiyor" arasındaki fark, bakması gereken tek şey.
    /// </para>
    /// </summary>
    private bool Exhausted(RawManifestEntity entry)
    {
        if (entry.RecoveryAttempts < _options.MaxRecoveryAttempts)
        {
            return false;
        }

        entry.State = RawObjectState.Unrecoverable;

        logger.LogError(
            "Ham nesne {Key} kurtarılamadı ({Attempts} deneme). Kaynak segment: {Segment}.",
            entry.ObjectKey,
            entry.RecoveryAttempts,
            string.IsNullOrEmpty(entry.WalSegment) ? "(kayıt yok)" : entry.WalSegment);

        return true;
    }

    /// <summary>
    /// Segmenti yeniden kurmayı dener. Segment artık yoksa <see langword="null"/>.
    /// </summary>
    private List<(RawObjectKey GroupKey, BuiltRawObject Built)>? TryBuild(string segmentId)
    {
        if (string.IsNullOrEmpty(segmentId))
        {
            return null;
        }

        try
        {
            var built = BuildObjects(segmentId).ToList();
            return built.Count > 0 ? built : null;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Segment {Segment} okunamadı; kurtarma bu tur atlanıyor.", segmentId);
            return null;
        }
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
                .Select(m => new { m.VerifiedAt, m.State })
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

            // DURUM da bakılıyor, yalnızca damga değil (T40).
            //
            // Önce doğrulanıp sonra kaybolan bir nesnenin `VerifiedAt`'i dolu
            // kalıyor; yalnızca damgaya bakan kapı o satırı "doğrulanmış ve
            // süresi dolmuş" sayıp segmenti siliyordu. Yani kaybın TESPİT
            // EDİLMİŞ olması kurtarma kaynağını korumuyordu ve kurtarma
            // mekanizması kendi kaynağını sildirebilirdi.
            if (rows.Any(r => r.State is RawObjectState.Missing or RawObjectState.ChecksumMismatch))
            {
                logger.LogWarning(
                    "Segment {Segment} silinmiyor: bağlı nesnelerden biri kayıp ya da bozuk, " +
                    "kurtarmanın kaynağı bu segment.",
                    segment.Id);
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
