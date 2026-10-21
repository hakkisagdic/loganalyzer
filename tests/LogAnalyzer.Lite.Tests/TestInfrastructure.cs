using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using LogAnalyzer.Lite.Configuration;
using LogAnalyzer.Lite.Security;
using LogAnalyzer.Lite.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LogAnalyzer.Lite.Tests;

/// <summary>
/// Gerçek Lite uygulaması — gerçek ara katman zinciri, gerçek DI — ama veri
/// dizini geçici, webhook çağrıları sahte birincil işleyiciye gidiyor ve
/// loglar yakalanıyor.
///
/// <para>
/// Ayarlar ortam değişkeni ADLARIYLA veriliyor (<c>LITE_INGEST_TOKEN</c> …):
/// üretimin okuduğu anahtarlar bunlar. Süreç ortamına dokunulmuyor; testler
/// paralel koşabiliyor.
/// </para>
/// </summary>
internal sealed class LiteAppFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> _settings;
    private readonly bool _realWebhookHandler;

    public LiteAppFactory(
        string dataDirectory,
        IReadOnlyDictionary<string, string?>? settings = null,
        bool realWebhookHandler = false)
    {
        DataDirectory = dataDirectory;
        _settings = settings ?? new Dictionary<string, string?>();
        _realWebhookHandler = realWebhookHandler;
    }

    public string DataDirectory { get; }

    public FakeWebhookHandler Webhooks { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(LiteSettings.DataDirectoryKey, DataDirectory);

        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        if (!_realWebhookHandler)
        {
            builder.ConfigureTestServices(services => services
                .AddHttpClient(AlertWebhookNotifier.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Webhooks));
        }
    }
}

/// <summary>Testin kendi geçici veri dizini. Dizin uygulamaya YARATILMAMIŞ verilir.</summary>
internal sealed class TempDataDirectory : IDisposable
{
    public TempDataDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "lite-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        DataPath = Path.Combine(Root, "data");
    }

    public string Root { get; }

    public string DataPath { get; }

    public string File(string name) => Path.Combine(DataPath, name);

    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows() && Directory.Exists(DataPath))
            {
                // Yazılamaz dizin testi izinleri daraltıyor; temizlik için geri aç.
                System.IO.File.SetUnixFileMode(DataPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Geçici dizin; işletim sistemi toplar.
        }
    }
}

internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, string Body);

/// <summary>Webhook istemcisinin yerine geçen birincil işleyici. Ağ yok.</summary>
internal sealed class FakeWebhookHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();
    private readonly object _gate = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
        static _ => new HttpResponseMessage(HttpStatusCode.OK);

    public IReadOnlyList<CapturedRequest> Requests => [.. _requests];

    /// <summary>
    /// <paramref name="count"/> istek gelene kadar bekler. Geçme koşulu olayın
    /// kendisi; süre yalnızca asılı kalmaya karşı bir üst sınır.
    /// </summary>
    public async Task WaitForRequestsAsync(int count, CancellationToken cancellationToken)
    {
        TaskCompletionSource signal;

        lock (_gate)
        {
            if (_requests.Count >= count)
            {
                return;
            }

            signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, signal));
        }

        await signal.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

        lock (_gate)
        {
            _requests.Enqueue(new CapturedRequest(request.Method, request.RequestUri!, body));

            foreach (var waiter in _waiters.Where(w => _requests.Count >= w.Count).ToList())
            {
                waiter.Signal.TrySetResult();
                _waiters.Remove(waiter);
            }
        }

        return Respond(request);
    }
}

internal sealed record LogEntry(string Category, LogLevel Level, string Message);

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public string AllText => string.Join('\n', _entries.Select(e => e.Message));

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<LogEntry> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue(new LogEntry(category, logLevel, formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception)));
    }
}

/// <summary>Yönetim isteği kurucusu — arayüzün gönderdiğinin aynısı.</summary>
internal static class Admin
{
    public static HttpRequestMessage Post(
        string path,
        object? body = null,
        bool withHeader = true,
        string? origin = null,
        string? secFetchSite = null,
        string? headerValue = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        if (withHeader)
        {
            request.Headers.Add(LiteRequestGuardMiddleware.RequestedWithHeader, headerValue ?? LiteRequestGuardMiddleware.RequestedWithValue);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (secFetchSite is not null)
        {
            request.Headers.Add("Sec-Fetch-Site", secFetchSite);
        }

        return request;
    }
}

internal static class Ingest
{
    public const string Header = LiteRequestGuardMiddleware.IngestTokenHeader;

    public static HttpRequestMessage Error(string trackingId, string? token = null, string errorMessage = "Upstream timeout")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest")
        {
            Content = JsonContent.Create(new
            {
                trackingId,
                level = "Error",
                source = "Gateway.Test",
                endpoint = "/api/v1/test",
                httpMethod = "POST",
                httpStatus = 502,
                durationMs = 12.5,
                errorMessage,
            }),
        };

        if (token is not null)
        {
            request.Headers.Add(Header, token);
        }

        return request;
    }
}

/// <summary>Deponun kökü — derleme çıktısından yukarı yürüyerek.</summary>
internal static class RepositoryRoot
{
    public static string Path { get; } = Find();

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "LogAnalyzer.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (LogAnalyzer.sln) not found above " + AppContext.BaseDirectory);
    }

    public static string File(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
}
