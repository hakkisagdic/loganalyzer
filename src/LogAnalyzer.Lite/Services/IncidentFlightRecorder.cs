using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LogAnalyzer.Lite.Models;

namespace LogAnalyzer.Lite.Services;

public class IncidentFlightRecorder
{
    private readonly AlertWebhookNotifier? _notifier;
    private readonly ConcurrentQueue<IncidentRecord> _incidents = new();
    private readonly int _maxIncidents;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly string _storageFile;
    private readonly object _diskLock = new();

    private long _totalIngested;
    private long _successfulRequests;
    private long _errorCount;
    private long _purgedCount;
    private double _totalDurationMs;
    private DateTime? _lastIncidentTime;

    public IncidentFlightRecorder(int maxIncidents = 1000, string? storagePath = null, AlertWebhookNotifier? notifier = null)
    {
        _notifier = notifier;
        _maxIncidents = maxIncidents;
        var dir = storagePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        Directory.CreateDirectory(dir);
        _storageFile = Path.Combine(dir, "incidents-flight-recorder.json");

        // Diskten mevcut kaydedilmiş olayları geri yükle (Persistence / Spool recovery)
        LoadFromDisk();

        // Eğer kayıt yoksa örnek başlangıç incident'ı koy
        if (_incidents.IsEmpty)
        {
            RecordIncident(new IncidentRecord
            {
                TrackingId = "TRK-INIT-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                Timestamp = DateTime.UtcNow,
                Level = "Warning",
                Source = "SystemBootstrap",
                Endpoint = "/health",
                HttpMethod = "GET",
                HttpStatus = 200,
                DurationMs = 12.4,
                ErrorMessage = "LogAnalyzer Lite initialized with disk spooling enabled.",
                Metadata = new Dictionary<string, string> { { "profile", "lite" }, { "spool", "active" } }
            });
        }
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
        while (_incidents.Count > _maxIncidents && _incidents.TryDequeue(out _))
        {
            Interlocked.Increment(ref _purgedCount);
        }

        // Arka planda diske spool et (asenkron debounced)
        _ = Task.Run(SaveToDisk);

        // Slack / Discord Webhook alarm bildirimi
        _notifier?.NotifyIncident(incident);
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

        return query.OrderByDescending(i => i.Timestamp).Take(limit).ToList();
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

    private void SaveToDisk()
    {
        try
        {
            lock (_diskLock)
            {
                var list = _incidents.ToArray();
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_storageFile, json);
            }
        }
        catch
        {
            // Disk write failure shouldn't crash the server
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (File.Exists(_storageFile))
            {
                var json = File.ReadAllText(_storageFile);
                var items = JsonSerializer.Deserialize<List<IncidentRecord>>(json);
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        _incidents.Enqueue(item);
                    }
                    _errorCount = items.Count;
                    _lastIncidentTime = items.OrderByDescending(x => x.Timestamp).FirstOrDefault()?.Timestamp;
                }
            }
        }
        catch
        {
            // Ignore load corruption gracefully
        }
    }
}
