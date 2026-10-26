using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using LogAnalyzer.Lite.Models;

namespace LogAnalyzer.Lite.Services;

public class IncidentFlightRecorder
{
    private readonly List<IncidentRecord> _events = new();
    private readonly string _dataDir;
    private readonly string _storageFile;
    private readonly int _maxEvents;
    private readonly int _retentionHours;
    private readonly object _diskLock = new();
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly AlertWebhookNotifier? _notifier;

    private long _totalIngested;
    private long _successfulRequests;
    private long _errorCount;
    private long _inboundCount;
    private long _outboundCount;
    private long _purgedCount;
    private long _diskWriteFailures;
    private double _totalDurationMs;
    private DateTime? _lastIncidentTime;

    private static readonly Regex SecretJsonRegex = new(
        @"""(password|parola|sifre|passphrase|apiKey|api_key|secret|clientSecret|client_secret|webhookSecret|accessToken|refreshToken|token|pfxPassword|donglePin|cloudHsmApiKey|restApiToken|integratorPassword)""\s*:\s*""[^""]*""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SecretHeaderRegex = new(
        @"(?m)^((?:Authorization|Proxy-Authorization|Cookie|Set-Cookie|X-Api-Key|X-Session-Token|X-Support-Service-Token|X-LogAnalyzer-Ingest-Token|X-LogAnalyzer-Admin-Token|[^\r\n:]*?(?:Secret|Password|Token|Api-Key|Pin)))\s*:\s*.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IncidentFlightRecorder(string dataDir, AlertWebhookNotifier? notifier = null, int maxEvents = 10000, int retentionHours = 168)
    {
        _dataDir = dataDir;
        _notifier = notifier;
        _maxEvents = Math.Max(100, maxEvents);
        _retentionHours = Math.Max(1, retentionHours);
        Directory.CreateDirectory(_dataDir);
        _storageFile = Path.Combine(_dataDir, "incidents-flight-recorder.json");

        LoadFromDisk();

        lock (_diskLock)
        {
            if (_events.Count == 0)
            {
                RecordEventInternal(new IncidentRecord
                {
                    TrackingId = "TRK-INIT-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                    Timestamp = DateTime.UtcNow,
                    Level = "Info",
                    IsIncident = false,
                    Direction = "Inbound",
                    Provider = "system",
                    Operation = "Bootstrap",
                    Source = "SystemBootstrap",
                    Endpoint = "/health",
                    HttpMethod = "GET",
                    HttpStatus = 200,
                    DurationMs = 12.4,
                    ErrorMessage = "LogAnalyzer Lite initialized with full inbound/outbound traffic journal and disk spooling enabled.",
                    Metadata = new Dictionary<string, string> { { "profile", "lite" }, { "spool", "active" } }
                }, notify: false);
            }
        }
    }

    public IngestResponse Ingest(IngestRequest request)
    {
        var duration = request.DurationMs ?? 0;
        var status = request.HttpStatus ?? 200;
        var isError = status >= 400
                      || request.IsBusinessFailure
                      || string.Equals(request.Level, "Error", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(request.Level, "Fatal", StringComparison.OrdinalIgnoreCase)
                      || (!string.IsNullOrWhiteSpace(request.ErrorMessage) && !string.Equals(request.Level, "Info", StringComparison.OrdinalIgnoreCase));

        var trackingId = string.IsNullOrWhiteSpace(request.TrackingId)
            ? $"{(isError ? "ERR" : "TRK")}-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}"
            : request.TrackingId!;

        var direction = string.IsNullOrWhiteSpace(request.Direction)
            ? (request.Metadata != null && request.Metadata.TryGetValue("direction", out var mdDir) && !string.IsNullOrWhiteSpace(mdDir) ? mdDir : "Inbound")
            : request.Direction!;

        var level = !string.IsNullOrWhiteSpace(request.Level)
            ? request.Level!
            : (status >= 500 ? "Fatal" : (isError ? "Error" : "Info"));

        var record = new IncidentRecord
        {
            Id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id!,
            TrackingId = trackingId,
            CorrelationId = request.CorrelationId ?? (request.Metadata != null && request.Metadata.TryGetValue("correlationId", out var cid) ? cid : null),
            TraceId = request.TraceId ?? (request.Metadata != null && request.Metadata.TryGetValue("traceId", out var tid) ? tid : null),
            TenantId = request.TenantId ?? (request.Metadata != null && request.Metadata.TryGetValue("tenantId", out var tnt) ? tnt : "default"),
            Direction = direction,
            Provider = request.Provider ?? (request.Metadata != null && request.Metadata.TryGetValue("provider", out var prv) ? prv : null),
            Operation = request.Operation ?? (request.Metadata != null && request.Metadata.TryGetValue("operation", out var op) ? op : null),
            Timestamp = request.Timestamp ?? DateTime.UtcNow,
            Level = level,
            IsIncident = isError,
            IsBusinessFailure = request.IsBusinessFailure,
            Source = request.Source ?? "IntegrationGateway",
            Endpoint = request.Endpoint ?? "/",
            HttpMethod = request.HttpMethod ?? "POST",
            HttpStatus = status,
            DurationMs = duration,
            ClientIp = request.ClientIp,
            ErrorMessage = isError ? (request.ErrorMessage ?? $"HTTP Error {status}") : request.ErrorMessage,
            ExceptionType = request.ExceptionType,
            StackTrace = request.StackTrace,
            RequestHeaders = RedactHeaders(request.RequestHeaders),
            RequestPayload = RedactPayload(request.RequestPayload),
            ResponsePayload = RedactPayload(request.ResponsePayload),
            RequestBytes = request.RequestBytes,
            ResponseBytes = request.ResponseBytes,
            RequestSha256 = request.RequestSha256,
            ResponseSha256 = request.ResponseSha256,
            RequestCaptureComplete = request.RequestCaptureComplete ?? true,
            ResponseCaptureComplete = request.ResponseCaptureComplete ?? true,
            Metadata = request.Metadata ?? new Dictionary<string, string>()
        };

        RecordEventInternal(record, notify: isError);

        return new IngestResponse
        {
            Success = true,
            TrackingId = trackingId,
            IsIncident = isError,
            Message = isError
                ? "Incident captured and persisted in flight recorder."
                : "Event captured and persisted in flight recorder."
        };
    }

    private void RecordEventInternal(IncidentRecord record, bool notify)
    {
        lock (_diskLock)
        {
            var existingIdx = _events.FindIndex(e => string.Equals(e.Id, record.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIdx >= 0)
            {
                _events[existingIdx] = record;
                SaveToDiskLocked();
                return;
            }

            _events.Add(record);
            Interlocked.Increment(ref _totalIngested);
            _totalDurationMs += record.DurationMs;

            if (string.Equals(record.Direction, "Outbound", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _outboundCount);
            }
            else
            {
                Interlocked.Increment(ref _inboundCount);
            }

            if (record.IsIncident)
            {
                Interlocked.Increment(ref _errorCount);
                _lastIncidentTime = record.Timestamp;
            }
            else
            {
                Interlocked.Increment(ref _successfulRequests);
            }

            ApplyRetentionLocked();
            SaveToDiskLocked();
        }

        if (notify && record.IsIncident)
        {
            _notifier?.NotifyIncident(record);
        }
    }

    private void ApplyRetentionLocked()
    {
        var cutoff = DateTime.UtcNow.AddHours(-_retentionHours);
        var expired = _events.RemoveAll(e => e.Timestamp < cutoff);
        if (expired > 0)
        {
            Interlocked.Add(ref _purgedCount, expired);
        }

        while (_events.Count > _maxEvents)
        {
            // Evict oldest non-incident first so incidents survive longer under capacity pressure
            var idx = _events.FindIndex(e => !e.IsIncident);
            if (idx < 0) idx = 0;
            _events.RemoveAt(idx);
            Interlocked.Increment(ref _purgedCount);
        }
    }

    public IReadOnlyList<IncidentRecord> GetIncidents(
        int limit = 50,
        string? search = null,
        string? level = null,
        string? source = null,
        string? direction = null,
        string? tenantId = null,
        bool? incidentsOnly = null)
    {
        List<IncidentRecord> snapshot;
        lock (_diskLock)
        {
            snapshot = _events.ToList();
        }

        var query = snapshot.AsEnumerable();

        if (incidentsOnly == true)
        {
            query = query.Where(i => i.IsIncident || i.HttpStatus >= 400 || string.Equals(i.Level, "Error", StringComparison.OrdinalIgnoreCase) || string.Equals(i.Level, "Fatal", StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(level))
        {
            query = query.Where(i => string.Equals(i.Level, level, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(direction))
        {
            query = query.Where(i => string.Equals(i.Direction, direction, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(tenantId) && !string.Equals(tenantId, "all", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(i => string.Equals(i.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            query = query.Where(i => i.Source.Contains(source, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(i =>
                i.TrackingId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (i.CorrelationId != null && i.CorrelationId.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                i.Endpoint.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (i.Provider != null && i.Provider.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                (i.Operation != null && i.Operation.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                (i.ErrorMessage != null && i.ErrorMessage.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        return query.OrderByDescending(i => i.Timestamp).Take(Math.Clamp(limit, 1, 1000)).ToList();
    }

    public IncidentRecord? GetIncident(string idOrTrackingId)
    {
        if (string.IsNullOrWhiteSpace(idOrTrackingId)) return null;
        lock (_diskLock)
        {
            return _events.FirstOrDefault(i =>
                string.Equals(i.Id, idOrTrackingId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.TrackingId, idOrTrackingId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.CorrelationId, idOrTrackingId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public FlightRecorderStats GetStats()
    {
        lock (_diskLock)
        {
            var total = Interlocked.Read(ref _totalIngested);
            var avgDuration = total > 0 ? (_totalDurationMs / total) : 0;
            var activeIncidents = _events.Count(e => e.IsIncident || e.HttpStatus >= 400);

            return new FlightRecorderStats
            {
                TotalIngested = total,
                SuccessfulRequests = Interlocked.Read(ref _successfulRequests),
                ErrorCount = Interlocked.Read(ref _errorCount),
                ActiveIncidents = activeIncidents,
                StoredEvents = _events.Count,
                InboundCount = Interlocked.Read(ref _inboundCount),
                OutboundCount = Interlocked.Read(ref _outboundCount),
                PurgedCount = Interlocked.Read(ref _purgedCount),
                DiskWriteFailures = Interlocked.Read(ref _diskWriteFailures),
                RetentionHours = _retentionHours,
                MaxEvents = _maxEvents,
                AvgDurationMs = Math.Round(avgDuration, 2),
                UptimeSeconds = (long)(DateTime.UtcNow - _startTime).TotalSeconds,
                LastIncidentTime = _lastIncidentTime,
                Mode = "Lite (Full Traffic & Incident Flight Recorder - Spool Active)"
            };
        }
    }

    public PurgeResponse Purge(PurgeRequest request)
    {
        lock (_diskLock)
        {
            var cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, request.KeepLastHours));
            var initialCount = _events.Count;
            var removed = _events.RemoveAll(i => i.Timestamp < cutoff || !i.IsIncident);
            Interlocked.Add(ref _purgedCount, removed);

            if (request.ResetStats)
            {
                Interlocked.Exchange(ref _successfulRequests, _events.Count(e => !e.IsIncident));
                Interlocked.Exchange(ref _errorCount, _events.Count(e => e.IsIncident));
            }

            SaveToDiskLocked();

            return new PurgeResponse
            {
                PurgedIncidents = removed,
                RemainingIncidents = _events.Count,
                Message = $"Purge complete. Removed {removed} records; retained {_events.Count} within SLA window."
            };
        }
    }

    private void SaveToDiskLocked()
    {
        try
        {
            var json = JsonSerializer.Serialize(_events, new JsonSerializerOptions { WriteIndented = true });
            var tempFile = _storageFile + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, _storageFile, overwrite: true);
        }
        catch
        {
            Interlocked.Increment(ref _diskWriteFailures);
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
                    lock (_diskLock)
                    {
                        foreach (var item in items)
                        {
                            var normalized = item with
                            {
                                IsIncident = item.IsIncident || item.HttpStatus >= 400 || item.IsBusinessFailure || string.Equals(item.Level, "Error", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Level, "Fatal", StringComparison.OrdinalIgnoreCase)
                            };
                            _events.Add(normalized);
                        }
                        _totalIngested = _events.Count;
                        _errorCount = _events.Count(x => x.IsIncident);
                        _successfulRequests = _events.Count - _errorCount;
                        _inboundCount = _events.Count(x => !string.Equals(x.Direction, "Outbound", StringComparison.OrdinalIgnoreCase));
                        _outboundCount = _events.Count(x => string.Equals(x.Direction, "Outbound", StringComparison.OrdinalIgnoreCase));
                        _totalDurationMs = _events.Sum(x => x.DurationMs);
                        _lastIncidentTime = _events.Where(x => x.IsIncident).OrderByDescending(x => x.Timestamp).FirstOrDefault()?.Timestamp;
                    }
                }
            }
        }
        catch
        {
            // Ignore load corruption gracefully
        }
    }

    private static string? RedactHeaders(string? headers)
    {
        if (string.IsNullOrWhiteSpace(headers)) return headers;
        return SecretHeaderRegex.Replace(headers, "$1: ***");
    }

    private static string? RedactPayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return payload;
        var sanitized = SecretJsonRegex.Replace(payload, "\"$1\":\"***\"");
        if (sanitized.Length > 32768)
        {
            sanitized = sanitized[..32768] + "\n... [KIRPILDI: 32KB Limit]";
        }
        return sanitized;
    }
}
