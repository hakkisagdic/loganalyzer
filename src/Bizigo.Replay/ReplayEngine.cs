using System.Diagnostics;
using System.Globalization;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Normalization;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Grok;
using Bizigo.Storage.ClickHouse;
using Bizigo.Storage.Raw;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bizigo.Replay;

/// <summary>
/// Parser düzeltildiğinde geçmişi yeniden işler (F1 §7.2, K12).
///
/// <code>
/// ham nesneleri seç → sabitlenmiş parser sürümüyle yeniden ayrıştır
///                   → gölge tabloya yaz → ALTER TABLE REPLACE PARTITION
/// </code>
///
/// <para>
/// <b>Aynı akış hem <c>--dry-run</c> hem gerçek çalıştırma için koşuyor.</b> Tek
/// fark son adımın atlanması. İki ayrı yol yazmak, raporun gerçeği öngörmediği
/// bir gün getirirdi — ve o gün kimse fark etmezdi, çünkü raporu kontrol etmenin
/// yolu raporun kendisi.
/// </para>
/// </summary>
public sealed class ReplayEngine(
    IDbContextFactory<ControlPlaneDbContext> factory,
    IRawObjectStore store,
    ParserCatalog catalog,
    Dispatcher dispatcher,
    SourceDirectory sources,
    EventNormalizer normalizer,
    EventWriter writer,
    ReplayStore replayStore,
    MaskCatalog masks,
    ILogger<ReplayEngine> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Rapordaki örnek fark sayısı.</summary>
    public int SampleLimit { get; init; } = 20;

    public Task<ReplayReport> DryRunAsync(ReplayPlan plan, CancellationToken cancellationToken = default) =>
        RunAsync(plan, apply: false, cancellationToken);

    public Task<ReplayReport> ApplyAsync(ReplayPlan plan, CancellationToken cancellationToken = default) =>
        RunAsync(plan, apply: true, cancellationToken);

    private async Task<ReplayReport> RunAsync(ReplayPlan plan, bool apply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var watch = Stopwatch.StartNew();
        await sources.RefreshAsync(cancellationToken);

        var found = await replayStore.ListPartitionsAsync(plan.From, plan.To, cancellationToken);

        // Açık bölüm (bugünün bölümü) varsayılan olarak DIŞARIDA. Gerekçe
        // `ReplayPlan.AllowOpenPartition`'da: `REPLACE PARTITION` atomik ama
        // anlık görüntüden sonra gelen satırı korumuyor, yani hâlâ yazılan bir
        // bölümü replay etmek canlı veriyi sessizce siliyor.
        var (partitions, skippedOpen) = SplitOpenPartition(plan, found);

        if (skippedOpen.Count > 0)
        {
            logger.LogWarning(
                "Replay {Count} açık bölümü atladı: {Partitions}. " +
                "Bu bölümlere hâlâ yazılıyor; dâhil etmek için AllowOpenPartition kullanın.",
                skippedOpen.Count,
                string.Join(", ", skippedOpen));
        }
        var (objects, missing) = await ResolveObjectsAsync(plan, cancellationToken);

        if (missing.Count > 0 && !plan.ContinueOnMissingObjects)
        {
            // SESSİZCE KISA DÖNMÜYORUZ. Manifest'in varlık sebebi bu: eksik nesne
            // olmadan replay "7 gün yerine 5 gün" döner ve kimse fark etmez.
            logger.LogError(
                "Replay durduruldu: {Count} nesne manifest'te var ama arşivde yok. " +
                "Devam etmek için ContinueOnMissingObjects kullanın.",
                missing.Count);

            return new ReplayReport
            {
                Plan = plan,
                Partitions = partitions.Select(p => p.Partition).ToArray(),
                MissingObjects = missing,
                Duration = watch.Elapsed,
                Applied = false,
                SkippedOpenPartitions = skippedOpen,
            };
        }

        var rebuilt = await RebuildAsync(plan, objects, cancellationToken);
        var existing = await LoadExistingAsync(partitions, cancellationToken);

        var comparison = ReplayDiff.Compare(rebuilt, existing, SampleLimit);

        if (!apply)
        {
            return comparison.ToReport(
                plan, partitions, missing, watch.Elapsed, applied: false, copied: 0, skippedOpen);
        }

        var copied = await ApplyPartitionsAsync(plan, partitions, rebuilt, existing, cancellationToken);

        logger.LogInformation(
            "Replay uygulandı: {Partitions} bölüm, {Changed} satır değişti, {Fixed} satır failed→ok.",
            partitions.Count,
            comparison.Changed,
            comparison.FailedToOk);

        return comparison.ToReport(
            plan, partitions, missing, watch.Elapsed, applied: true, copied, skippedOpen);
    }

    /// <summary>
    /// Aralığı kapsayan arşiv nesneleri. Manifest'te olup depoda bulunmayanlar
    /// ayrı listeleniyor.
    /// </summary>
    private async Task<(IReadOnlyList<string> Objects, IReadOnlyList<string> Missing)> ResolveObjectsAsync(
        ReplayPlan plan,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var query = db.RawManifest
            .AsNoTracking()
            .Where(m => m.TsTo >= plan.From && m.TsFrom < plan.To);

        if (plan.OwnerGroups.Count > 0)
        {
            query = query.Where(m => plan.OwnerGroups.Contains(m.OwnerGroup));
        }

        var keys = await query
            .OrderBy(m => m.TsFrom)
            .Select(m => m.ObjectKey)
            .ToListAsync(cancellationToken);

        var present = new List<string>(keys.Count);
        var missing = new List<string>();

        foreach (var key in keys)
        {
            if (await store.HeadAsync(key, cancellationToken) is null)
            {
                missing.Add(key);
            }
            else
            {
                present.Add(key);
            }
        }

        return (present, missing);
    }

    /// <summary>
    /// Ham kayıtları <b>sabitlenmiş</b> parser sürümüyle yeniden ayrıştırır.
    ///
    /// <para>
    /// Sürüm sabitlemesi replay'in tekrarlanabilirliğinin tamamı: "en güncel
    /// parser" ile koşmak, aynı komutun iki ay sonra farklı sonuç vermesi demek.
    /// </para>
    /// </summary>
    private async Task<Dictionary<Guid, LogEvent>> RebuildAsync(
        ReplayPlan plan,
        IReadOnlyList<string> objects,
        CancellationToken cancellationToken)
    {
        var reader = new RawReader(store);
        var scope = AccessScope.System("replay");
        var rebuilt = new Dictionary<Guid, LogEvent>();
        var generation = (uint)_time.GetUtcNow().ToUnixTimeSeconds();

        foreach (var objectKey in objects)
        {
            var records = await reader.ReadObjectAsync(objectKey, scope, cancellationToken);

            foreach (var record in records)
            {
                var timestamp = record.ObservedAt ?? record.ReceivedAt;
                if (timestamp < plan.From || timestamp >= plan.To)
                {
                    continue;
                }

                var resolved = sources.Resolve(record.SourceKey);

                if (!Matches(plan, resolved.OwnerGroup, resolved.SourceId))
                {
                    continue;
                }

                var decoded = System.Text.Encoding.UTF8.GetString(record.Body.Span);
                var result = string.IsNullOrEmpty(plan.ParserId)
                    ? dispatcher.Dispatch(decoded, resolved.ParserId).Result
                    : ParseWithPinned(plan, decoded);

                // İmza replay'de de yeniden üretiliyor — sıcak yoldakiyle aynı
                // fonksiyondan (K35). Boş bırakmak, her replay'in `signature_hash`
                // kolonunu sıfırlaması demek olurdu: RCA'nın en güçlü iki sinyali
                // düzeltilmiş bir parser'ın **yan etkisi** olarak silinirdi ve
                // rapor bunu "değişiklik yok" diye gösterirdi.
                //
                // `template_id`'nin aksine bu değer yeniden üretilebilir: sidecar
                // gerekmiyor, sözlük ve satır yetiyor.
                var signature = masks.Compute(decoded);

                var normalized = normalizer.Normalize(new ParsedEvent(
                    record with { OwnerGroup = resolved.OwnerGroup, SourceId = resolved.SourceId },
                    decoded,
                    record.EncodingDeclared is { Length: > 0 } enc ? enc : "utf-8",
                    resolved,
                    result,
                    DispatchTier.InventoryBound,
                    SignatureHash: signature.Hash));

                // `parse_generation` hangi satırın kaçıncı kuşaktan geldiğini
                // denetlenebilir kılıyor — replay sonrası "bu satır eski mi yeni
                // mi" sorusunun tek cevabı.
                rebuilt[record.EventId] = normalized with { ParseGeneration = generation };
            }
        }

        return rebuilt;
    }

    private Parsing.Engine.ParseResult ParseWithPinned(ReplayPlan plan, string body)
    {
        var snapshot = catalog.Current;

        if (!snapshot.ByParserId.TryGetValue(plan.ParserId, out var parser))
        {
            throw new InvalidOperationException(
                $"Sabitlenmiş parser katalogda yok: '{plan.ParserId}'.");
        }

        if (plan.ParserVersion.Length > 0
            && !string.Equals(parser.Version, plan.ParserVersion, StringComparison.Ordinal))
        {
            // Sürüm uyuşmuyorsa DURUYORUZ. Yaklaşık doğru bir sürümle koşmak,
            // replay'in tekrarlanabilir olduğu iddiasını sessizce çürütürdü.
            throw new InvalidOperationException(
                $"'{plan.ParserId}' katalogda {parser.Version} sürümünde, " +
                $"istenen {plan.ParserVersion}.");
        }

        return parser.Parse(body);
    }

    private static bool Matches(ReplayPlan plan, string ownerGroup, string sourceId) =>
        (plan.OwnerGroups.Count == 0 || plan.OwnerGroups.Contains(ownerGroup, StringComparer.Ordinal))
        && (plan.SourceIds.Count == 0 || plan.SourceIds.Contains(sourceId, StringComparer.Ordinal));

    private async Task<Dictionary<Guid, LogEvent>> LoadExistingAsync(
        IReadOnlyList<PartitionInfo> partitions,
        CancellationToken cancellationToken)
    {
        var existing = new Dictionary<Guid, LogEvent>();

        foreach (var partition in partitions)
        {
            foreach (var row in await replayStore.ReadPartitionAsync(partition.Partition, cancellationToken))
            {
                existing[row.EventId] = row;
            }
        }

        return existing;
    }

    /// <summary>
    /// Bölümleri gölge tabloya yazıp değiştirir.
    ///
    /// <para>
    /// <b>Filtre dışı satırlar değiştirilmeden kopyalanıyor.</b>
    /// <c>REPLACE PARTITION</c> bölümün tamamını değiştirdiği için kopyalanmayan
    /// satır <b>kaybolur</b> — filtreli replay'in en kolay gözden kaçan tuzağı bu.
    /// Bedeli bölüm başına tam yeniden yazım; replay nadir bir işlem olduğu için
    /// kabul edilebilir, ama süresi raporda görünüyor.
    /// </para>
    /// </summary>
    private async Task<int> ApplyPartitionsAsync(
        ReplayPlan plan,
        IReadOnlyList<PartitionInfo> partitions,
        Dictionary<Guid, LogEvent> rebuilt,
        Dictionary<Guid, LogEvent> existing,
        CancellationToken cancellationToken)
    {
        var copied = 0;

        foreach (var partition in partitions)
        {
            var shadow = ShadowNameFor(partition.Partition);

            // Aynı replay iki kez koşabilmeli: eski gölge kalıntısı düşürülüyor.
            await replayStore.DropShadowAsync(shadow, cancellationToken);
            await replayStore.CreateShadowAsync(shadow, cancellationToken);

            try
            {
                var rows = new List<LogEvent>();

                foreach (var (eventId, row) in existing)
                {
                    if (PartitionOf(row.Timestamp) != partition.Partition)
                    {
                        continue;
                    }

                    if (rebuilt.TryGetValue(eventId, out var replacement))
                    {
                        rows.Add(replacement);
                    }
                    else
                    {
                        rows.Add(row);
                        copied++;
                    }
                }

                // Arşivde olup tabloda olmayanlar: ilk işlemede kaybedilmiş satırlar.
                foreach (var (eventId, row) in rebuilt)
                {
                    if (!existing.ContainsKey(eventId) && PartitionOf(row.Timestamp) == partition.Partition)
                    {
                        rows.Add(row);
                    }
                }

                if (rows.Count > 0)
                {
                    await writer.WriteEventsToAsync(shadow, rows, cancellationToken);
                }

                await replayStore.ReplacePartitionAsync(shadow, partition.Partition, cancellationToken);
            }
            finally
            {
                await replayStore.DropShadowAsync(shadow, CancellationToken.None);
            }
        }

        return copied;
    }

    /// <summary>
    /// Bölümleri "replay edilebilir" ve "hâlâ yazılıyor" diye ayırır.
    ///
    /// <para>
    /// <b>Saf, saatten besleniyor ve <c>public</c></b> — üçü de bilerek. Bu
    /// karar veri kaybını önlüyor ve onu ancak konteyner kaldıran bir testle
    /// sınayabilmek, F1'in bedelini ölçtüğü hatanın aynısı olurdu.
    /// <c>LogsEndpoint.ReadBodyAsync</c> aynı gerekçeyle dışarı açık.
    /// </para>
    /// </summary>
    public static (IReadOnlyList<PartitionInfo> Replayable, IReadOnlyList<string> SkippedOpen)
        SplitOpen(ReplayPlan plan, IReadOnlyList<PartitionInfo> partitions, DateTimeOffset now)
    {
        if (plan.AllowOpenPartition)
        {
            return (partitions, []);
        }

        var open = PartitionOf(now);

        // Bugünün bölümü ve (saat farkı yüzünden) sonrası açık sayılıyor:
        // ikisine de yazılabilir.
        var skipped = partitions
            .Where(p => string.CompareOrdinal(p.Partition, open) >= 0)
            .Select(p => p.Partition)
            .ToArray();

        return skipped.Length == 0
            ? (partitions, [])
            : ([.. partitions.Where(p => string.CompareOrdinal(p.Partition, open) < 0)], skipped);
    }

    private (IReadOnlyList<PartitionInfo>, IReadOnlyList<string>) SplitOpenPartition(
        ReplayPlan plan,
        IReadOnlyList<PartitionInfo> partitions) =>
        SplitOpen(plan, partitions, _time.GetUtcNow());

    private static string PartitionOf(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string ShadowNameFor(string partition) => "events_replay_" + partition;

}
