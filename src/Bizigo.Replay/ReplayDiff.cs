using System.Globalization;
using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.Replay;

/// <summary>
/// Replay'in saf karşılaştırma mantığı.
///
/// <para>
/// Motordan ayrı durması bilinçli: <c>--dry-run</c>'ın tek işi "ne değişecek"
/// sorusunu doğru cevaplamak ve yanlış cevaplarsa özellik <b>zararlı</b> olur —
/// kullanıcı rapora güvenip uygular. Bu mantığın ClickHouse, arşiv ve parser
/// kurmadan sınanabilmesi gerekiyor.
/// </para>
/// </summary>
public static class ReplayDiff
{
    public static ComparisonResult Compare(
        Dictionary<Guid, LogEvent> rebuilt,
        Dictionary<Guid, LogEvent> existing,
        int sampleLimit)
    {
        var result = new ComparisonResult();

        foreach (var (eventId, after) in rebuilt)
        {
            result.RecordsReplayed++;

            if (!existing.TryGetValue(eventId, out var before))
            {
                result.NewRows++;
                continue;
            }

            var changes = DiffOf(before, after);

            if (changes.Count == 0)
            {
                result.Unchanged++;
                continue;
            }

            result.Changed++;

            if (before.ParseStatus == ParseStatus.Failed && after.ParseStatus != ParseStatus.Failed)
            {
                result.FailedToOk++;
            }
            else if (before.ParseStatus != ParseStatus.Failed && after.ParseStatus == ParseStatus.Failed)
            {
                result.OkToFailed++;
            }

            foreach (var change in changes)
            {
                result.ChangesByField[change.Field] = result.ChangesByField.GetValueOrDefault(change.Field) + 1;
            }

            if (result.Samples.Count < sampleLimit)
            {
                result.Samples.Add(new EventDiff(
                    eventId,
                    before.ParseStatus.ToString(),
                    after.ParseStatus.ToString(),
                    changes));
            }
        }

        return result;
    }

    /// <summary>
    /// Karşılaştırma <c>ingested_at</c> ve <c>parse_generation</c>'ı <b>dışarıda
    /// bırakıyor</b>: ikisi de her replay'de değişir ve raporu "her satır değişti"
    /// diye gösterip gerçek farkları görünmez kılardı.
    /// </summary>
    private static List<FieldChange> DiffOf(LogEvent before, LogEvent after)
    {
        var changes = new List<FieldChange>();

        void Check(string field, string? left, string? right)
        {
            if (!string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal))
            {
                changes.Add(new FieldChange(field, left ?? string.Empty, right ?? string.Empty));
            }
        }

        Check("parse_status", before.ParseStatus.ToString(), after.ParseStatus.ToString());
        Check("parser_id", before.ParserId, after.ParserId);
        Check("parser_version", before.ParserVersion, after.ParserVersion);
        Check("host", before.Host, after.Host);
        Check("vendor", before.Vendor, after.Vendor);
        Check("product", before.Product, after.Product);
        Check("proto", before.Proto, after.Proto);
        Check("action", before.Action, after.Action);
        Check("outcome", before.Outcome, after.Outcome);
        Check("user_name", before.UserName, after.UserName);
        Check("src_ip", before.SrcIp?.ToString(), after.SrcIp?.ToString());
        Check("dst_ip", before.DstIp?.ToString(), after.DstIp?.ToString());
        Check("src_port", before.SrcPort.ToString(CultureInfo.InvariantCulture), after.SrcPort.ToString(CultureInfo.InvariantCulture));
        Check("dst_port", before.DstPort.ToString(CultureInfo.InvariantCulture), after.DstPort.ToString(CultureInfo.InvariantCulture));
        Check("severity_num", before.SeverityNum.ToString(CultureInfo.InvariantCulture), after.SeverityNum.ToString(CultureInfo.InvariantCulture));
        Check("ocsf_class_uid", before.OcsfClassUid.ToString(CultureInfo.InvariantCulture), after.OcsfClassUid.ToString(CultureInfo.InvariantCulture));
        Check("ts", before.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), after.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        // İmza değişmesi replay'in **raporlaması gereken** bir fark: maskeleme
        // sözlüğü güncellendiyse ya da kodlama tespiti düzeldiyse aynı ham satır
        // başka bir imzaya düşer, ve o satırlar RCA'nın gözünde "ilk kez görülen"
        // olur. Sessiz kalırsa replay sonrası tek seferlik bir ilk-görülen
        // dalgası doğar ve sebebi hiçbir yerde yazılı olmaz.
        Check(
            "signature_hash",
            before.SignatureHash.ToString(CultureInfo.InvariantCulture),
            after.SignatureHash.ToString(CultureInfo.InvariantCulture));

        // `ingested_at`in aksine bu KARŞILAŞTIRILIYOR: replay'in en değerli
        // kazançlarından biri, önce zamanı çözemeyen bir parser'ın düzeltilip
        // `observed` → `parsed` geçişi yapması. Rapor bunu göstermeli.
        Check("time_source", before.TimeSource, after.TimeSource);

        // `attrs` alan alan karşılaştırılıyor: tek bir "attrs değişti" satırı
        // hangi alanın etkilendiğini gizlerdi.
        foreach (var key in before.Attrs.Keys.Union(after.Attrs.Keys, StringComparer.Ordinal))
        {
            Check("attrs." + key, before.Attrs.GetValueOrDefault(key), after.Attrs.GetValueOrDefault(key));
        }

        return changes;
    }

    public sealed class ComparisonResult
    {
        public int RecordsReplayed { get; set; }
        public int Unchanged { get; set; }
        public int Changed { get; set; }
        public int FailedToOk { get; set; }
        public int OkToFailed { get; set; }
        public int NewRows { get; set; }
        public Dictionary<string, int> ChangesByField { get; } = new(StringComparer.Ordinal);
        public List<EventDiff> Samples { get; } = [];

        public ReplayReport ToReport(
            ReplayPlan plan,
            IReadOnlyList<PartitionInfo> partitions,
            IReadOnlyList<string> missing,
            TimeSpan duration,
            bool applied,
            int copied,
            IReadOnlyList<string>? skippedOpen = null) => new()
            {
                Plan = plan,
                Partitions = partitions.Select(p => p.Partition).ToArray(),
                RecordsReplayed = RecordsReplayed,
                Unchanged = Unchanged,
                Changed = Changed,
                FailedToOk = FailedToOk,
                OkToFailed = OkToFailed,
                NewRows = NewRows,
                CopiedUnchanged = copied,
                MissingObjects = missing,
                ChangesByField = ChangesByField,
                Samples = Samples,
                Duration = duration,
                Applied = applied,
                SkippedOpenPartitions = skippedOpen ?? [],
            };
    }
}
