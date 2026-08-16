using System.Collections.Concurrent;

namespace Bizigo.Parsing.Engine;

public sealed record QuarantineEntry(string ParserKey, DateTimeOffset Since, int Timeouts, string Reason);

/// <summary>
/// ReDoS savunmasının üçüncü kademesi (F1 §4.1): sürekli zaman aşımı veren parser
/// devre dışı bırakılır ve sahibi uyarılır.
///
/// <para>
/// Tek bir zaman aşımı karantina sebebi değil — uzun bir satır sınırı zorlamış
/// olabilir. Sayaç <see cref="Window"/> içinde <see cref="Threshold"/>'a ulaşırsa
/// karar verilir. Sayaç başarılı ayrıştırmada sıfırlanmaz, <b>pencere</b> ile
/// eskir; aksi halde "on satırda bir kilitleyen" bir pattern sonsuza dek yaşar.
/// </para>
/// </summary>
public sealed class ParserQuarantine
{
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _timeouts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, QuarantineEntry> _quarantined = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public ParserQuarantine(int threshold = 5, TimeSpan? window = null, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
        Threshold = threshold;
        Window = window ?? TimeSpan.FromMinutes(5);
        _time = timeProvider ?? TimeProvider.System;
    }

    public int Threshold { get; }

    public TimeSpan Window { get; }

    public IReadOnlyCollection<QuarantineEntry> Entries => _quarantined.Values.ToArray();

    public bool IsQuarantined(string parserKey) => _quarantined.ContainsKey(parserKey);

    /// <summary>Zaman aşımı bildirir. Karantinaya alındıysa <c>true</c> döner.</summary>
    public bool ReportTimeout(string parserKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parserKey);

        var now = _time.GetUtcNow();
        var list = _timeouts.GetOrAdd(parserKey, static _ => []);

        lock (list)
        {
            list.RemoveAll(timestamp => now - timestamp > Window);
            list.Add(now);

            if (list.Count < Threshold)
            {
                return false;
            }

            _quarantined[parserKey] = new QuarantineEntry(
                parserKey,
                now,
                list.Count,
                $"{Window.TotalMinutes:0} dakika içinde {list.Count} zaman aşımı.");

            return true;
        }
    }

    /// <summary>Sahibi düzelttikten sonra elle serbest bırakma.</summary>
    public bool Release(string parserKey)
    {
        _timeouts.TryRemove(parserKey, out _);
        return _quarantined.TryRemove(parserKey, out _);
    }
}
