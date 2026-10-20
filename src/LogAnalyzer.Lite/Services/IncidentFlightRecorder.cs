using System.Collections.Concurrent;
using System.Text.Json;
using LogAnalyzer.Lite.Models;

namespace LogAnalyzer.Lite.Services;

/// <summary>
/// Olay halka tamponu ve disk spool'u.
///
/// <para>
/// <b>Spool tek bir arka plan döngüsünden yazılıyor</b> ve kapanışta son bir
/// kez boşaltılıyor (<see cref="StopAsync"/>). Eski hâli her olay için ayrı
/// bir <c>Task.Run</c> açıyordu; kapanış sırasında yarım kalan yazımın ya da
/// son olayın kaybolmadığını söyleyen hiçbir şey yoktu.
/// </para>
///
/// <para>
/// <b>Boş depoya örnek olay eklenmiyor.</b> Eski hâli depo boşsa
/// <c>TRK-INIT-*</c> adında sahte bir olay yazıyordu; birim yanlış
/// bağlandığında "1 olay" görünüyor ve sessizce boş başlamayı
/// maskeliyordu. Boş depo artık boş görünüyor ve açılışta kaç olay
/// yüklendiği loglanıyor.
/// </para>
/// </summary>
internal sealed class IncidentFlightRecorder : IHostedService, IDisposable
{
    public const string StorageFileName = "incidents-flight-recorder.json";

    private const int MaxIncidents = 1000;

    private static readonly JsonSerializerOptions StorageJson = new() { WriteIndented = true };

    private readonly AlertWebhookNotifier _notifier;
    private readonly LiteStorage _storage;
    private readonly ILogger<IncidentFlightRecorder> _logger;
    private readonly ConcurrentQueue<IncidentRecord> _incidents = new();
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly object _diskLock = new();

    // En fazla bir bekleyen yazım: iki olay arka arkaya gelirse tek yazım yeter.
    private readonly SemaphoreSlim _saveSignal = new(0, 1);
    private readonly CancellationTokenSource _stopping = new();
    private Task _saveLoop = Task.CompletedTask;
    private int _stopRequested;

    private long _totalIngested;
    private long _successfulRequests;
    private long _errorCount;
    private long _purgedCount;
    private double _totalDurationMs;
    private DateTime? _lastIncidentTime;

    public IncidentFlightRecorder(AlertWebhookNotifier notifier, LiteStorage storage, ILogger<IncidentFlightRecorder> logger)
    {
        _notifier = notifier;
        _storage = storage;
        _logger = logger;

        // Diskten mevcut kaydedilmiş olayları geri yükle (Persistence / Spool recovery)
        LoadFromDisk();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _saveLoop = Task.Run(() => SaveLoopAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Döngüyü durdurur ve son boşaltmayı yapar. <b>İdempotent ve
    /// <see cref="Dispose"/>'dan sonra da güvenli</b>: barındırıcı bu yöntemi
    /// birden fazla kez çağırabiliyor ve sıra garanti değil (ölçüldü:
    /// <c>WebApplicationFactory</c> kapanışında <c>RunAsync</c>'in kendi
    /// durdur+at yolu fabrikanın <c>StopAsync</c>'iyle yarışıyor). İptal yalnızca
    /// bir kez yapılıyor; son boşaltma her çağrıda — yazım kilitli ve aynı
    /// içeriği yazıyor.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (TryRequestStop())
        {
            try
            {
                await _saveLoop.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Döngü iptalle bitiyor; son boşaltma aşağıda.
            }
        }

        SaveToDisk();
    }

    public void Dispose()
    {
        // StopAsync hiç çağrılmadıysa döngü asılı kalmasın.
        TryRequestStop();
        _stopping.Dispose();
        _saveSignal.Dispose();
    }

    /// <returns>Durdurmayı bu çağrı istediyse <c>true</c>.</returns>
    private bool TryRequestStop()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            return false;
        }

        try
        {
            _stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Eşzamanlı bir Dispose kaynağı çoktan bıraktı; döngü de onunla bitti.
        }

        return true;
    }

    public IngestResponse Ingest(IngestRequest request)
    {
        Interlocked.Increment(ref _totalIngested);
        var duration = request.DurationMs ?? 0;
        lock (_diskLock)
        {
            _totalDurationMs += duration;
        }

        var trackingId = string.IsNullOrWhiteSpace(request.TrackingId)
            ? "TRK-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant()
            : request.TrackingId;

        var isError = (request.HttpStatus.HasValue && request.HttpStatus.Value >= 400)
                      || string.Equals(request.Level, "Error", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(request.Level, "Fatal", StringComparison.OrdinalIgnoreCase)
                      || !string.IsNullOrWhiteSpace(request.ErrorMessage);

        if (isError)
        {
            Interlocked.Increment(ref _errorCount);
            _lastIncidentTime = DateTime.UtcNow;

            var incident = new IncidentRecord
            {
                TrackingId = trackingId,
                Timestamp = DateTime.UtcNow,
                Level = request.Level ?? (request.HttpStatus >= 500 ? "Fatal" : "Error"),
                Source = request.Source ?? "UnknownService",
                Endpoint = request.Endpoint ?? "/",
                HttpMethod = request.HttpMethod ?? "POST",
                HttpStatus = request.HttpStatus ?? 500,
                DurationMs = duration,
                ClientIp = request.ClientIp,
                ErrorMessage = request.ErrorMessage ?? $"HTTP Error {request.HttpStatus}",
                StackTrace = request.StackTrace,
                RequestPayload = request.RequestPayload,
                ResponsePayload = request.ResponsePayload,
                Metadata = request.Metadata ?? new Dictionary<string, string>()
            };

            RecordIncident(incident);

            return new IngestResponse
            {
                Success = true,
                TrackingId = trackingId,
                IsIncident = true,
                Message = "Incident captured in flight recorder."
            };
        }

        // Başarılı istek: Sadece telemetri metriğinde sayaç artırılır
        Interlocked.Increment(ref _successfulRequests);
        return new IngestResponse
        {
            Success = true,
            TrackingId = trackingId,
            IsIncident = false,
            Message = "Request counted in telemetry metrics. No disk bloat."
        };
    }

    private void RecordIncident(IncidentRecord incident)
    {
        _incidents.Enqueue(incident);

        // Ring buffer limit
        while (_incidents.Count > MaxIncidents && _incidents.TryDequeue(out _))
        {
            Interlocked.Increment(ref _purgedCount);
        }

        // Arka planda diske spool et (tek döngü, birleştirilmiş yazım)
        ScheduleSave();

        // Slack / Discord Webhook alarm bildirimi
        _notifier.NotifyIncident(incident);
    }

    public IReadOnlyList<IncidentRecord> GetIncidents(int limit = 50, string? search = null, string? level = null, string? source = null)
    {
        var query = _incidents.ToArray().AsEnumerable();

        if (!string.IsNullOrWhiteSpace(level))
        {
            query = query.Where(i => string.Equals(i.Level, level, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            query = query.Where(i => i.Source.Contains(source, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(i =>
                i.TrackingId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                i.Endpoint.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (i.ErrorMessage != null && i.ErrorMessage.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        return query.OrderByDescending(i => i.Timestamp).Take(Math.Clamp(limit, 0, MaxIncidents)).ToList();
    }

    public IncidentRecord? GetIncident(string idOrTrackingId)
    {
        return _incidents.FirstOrDefault(i =>
            string.Equals(i.Id, idOrTrackingId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.TrackingId, idOrTrackingId, StringComparison.OrdinalIgnoreCase));
    }

    public FlightRecorderStats GetStats()
    {
        var total = Interlocked.Read(ref _totalIngested);
        var avgDuration = total > 0 ? (_totalDurationMs / total) : 0;

        return new FlightRecorderStats
        {
            TotalIngested = total,
            SuccessfulRequests = Interlocked.Read(ref _successfulRequests),
            ErrorCount = Interlocked.Read(ref _errorCount),
            ActiveIncidents = _incidents.Count,
            PurgedCount = Interlocked.Read(ref _purgedCount),
            AvgDurationMs = Math.Round(avgDuration, 2),
            UptimeSeconds = (long)(DateTime.UtcNow - _startTime).TotalSeconds,
            LastIncidentTime = _lastIncidentTime,
            Mode = "Lite (Incident Flight Recorder - Spool Active)"
        };
    }

    public PurgeResponse Purge(PurgeRequest request)
    {
        var cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, request.KeepLastHours));
        var initialCount = _incidents.Count;

        var remaining = _incidents.Where(i => i.Timestamp >= cutoff).ToList();

        while (_incidents.TryDequeue(out _)) { }

        foreach (var item in remaining)
        {
            _incidents.Enqueue(item);
        }

        var purged = initialCount - _incidents.Count;
        Interlocked.Add(ref _purgedCount, purged);

        if (request.ResetStats)
        {
            Interlocked.Exchange(ref _successfulRequests, 0);
            Interlocked.Exchange(ref _errorCount, _incidents.Count);
        }

        SaveToDisk();

        return new PurgeResponse
        {
            PurgedIncidents = purged,
            RemainingIncidents = _incidents.Count,
            Message = $"Purge complete. Removed {purged} older incidents; retained {remaining.Count} within SLA window."
        };
    }

    private void ScheduleSave()
    {
        try
        {
            _saveSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Zaten bekleyen bir yazım var; o yazım bu olayı da kapsayacak.
        }
        catch (ObjectDisposedException)
        {
            // Kapanış sonrası gelen olay; son boşaltma çoktan yapıldı.
        }
    }

    private async Task SaveLoopAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            await _saveSignal.WaitAsync(stopping);
            SaveToDisk();
        }
    }

    private void SaveToDisk()
    {
        try
        {
            lock (_diskLock)
            {
                var list = _incidents.ToArray();
                _storage.WriteAtomic(StorageFileName, JsonSerializer.Serialize(list, StorageJson));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Sunucuyu düşürmüyor ama artık sessiz de değil.
            _logger.LogError(error, "Incident spool could not be written to {File}.", StorageFileName);
        }
    }

    private void LoadFromDisk()
    {
        string? json;

        try
        {
            json = _storage.ReadText(StorageFileName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Incident spool '{StorageFileName}' cannot be read.", error);
        }

        if (json is null)
        {
            _logger.LogInformation("No incident spool found in the data directory; starting empty.");
            return;
        }

        List<IncidentRecord>? items;

        try
        {
            items = JsonSerializer.Deserialize<List<IncidentRecord>>(json);
        }
        catch (JsonException)
        {
            // Bozuk dosyanın üstüne yazmak veri kaybı; kenara alınıyor.
            var quarantined = _storage.Quarantine(StorageFileName);
            _logger.LogError("Incident spool could not be parsed; moved aside as {File}. Starting empty.", quarantined);
            return;
        }

        if (items is null)
        {
            return;
        }

        foreach (var item in items)
        {
            _incidents.Enqueue(item);
        }

        _errorCount = items.Count;
        _lastIncidentTime = items.OrderByDescending(x => x.Timestamp).FirstOrDefault()?.Timestamp;
        _logger.LogInformation("Loaded {Count} incidents from the incident spool.", items.Count);
    }
}
