using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

public sealed class TraceEvidenceBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly AccessScope Scope = AccessScope.ForGroups("trace-budget", ["A"]);
    private static RcaWindow Window => new()
    {
        From = DateTimeOffset.UnixEpoch.AddMinutes(10), To = DateTimeOffset.UnixEpoch.AddMinutes(20),
        BaselineFrom = DateTimeOffset.UnixEpoch, BaselineTo = DateTimeOffset.UnixEpoch.AddMinutes(10),
    };
    private static TelemetryRecord Span(int id) => MetricEvidenceProviderTests.Point("0", (ulong)id) with
    {
        Signal = TelemetrySignal.Traces, Metric = null, LogicalId = "span/" + id,
        TraceId = new string('1', 32), SpanId = id.ToString("x16", CultureInfo.InvariantCulture),
        ServiceName = "service-" + id, Status = 2,
        Span = JsonSerializer.SerializeToElement(new
        {
            parentSpanId = id == 1 ? "" : (id - 1).ToString("x16", CultureInfo.InvariantCulture), links = Array.Empty<object>(),
        }),
    };
    private static IEvidenceProvider Provider(bool errors, RecordingScopedQuery q, TelemetryEvidenceOptions options) => errors
        ? new TraceErrorPropagationProvider(q, Options.Create(options))
        : new TraceServiceDependencyProvider(q, Options.Create(options));

    [Theory]
    [InlineData(true, "span", -1)] [InlineData(true, "span", 0)] [InlineData(true, "span", 1)]
    [InlineData(false, "span", -1)] [InlineData(false, "span", 0)] [InlineData(false, "span", 1)]
    [InlineData(true, "relation", -1)] [InlineData(true, "relation", 0)] [InlineData(true, "relation", 1)]
    [InlineData(false, "relation", -1)] [InlineData(false, "relation", 0)] [InlineData(false, "relation", 1)]
    [InlineData(true, "page", -1)] [InlineData(true, "page", 0)] [InlineData(true, "page", 1)]
    [InlineData(false, "page", -1)] [InlineData(false, "page", 0)] [InlineData(false, "page", 1)]
    [InlineData(true, "byte", -1)] [InlineData(true, "byte", 0)] [InlineData(true, "byte", 1)]
    [InlineData(false, "byte", -1)] [InlineData(false, "byte", 0)] [InlineData(false, "byte", 1)]
    public async Task Trace_provider_budget_limit_minus_exact_plus_one(bool errors, string dimension, int delta)
    {
        // Fixed input; capacity = exact required budget -1, exactly, +1.
        var rows = Enumerable.Range(1, dimension == "page" ? 6 : dimension == "relation" ? 4 : 3).Select(Span).ToArray();
        var serializedBytes = rows.Sum(r => JsonSerializer.SerializeToUtf8Bytes(r, RawSignalCodec.Json).Length);
        var options = new TelemetryEvidenceOptions();
        switch (dimension)
        {
            case "span": options.MaxRecords = rows.Length + delta; break;
            case "relation": options.MaxRelations = rows.Length - 1 + delta; break;
            case "page": options.MaxPages = 3 + delta; break;
            case "byte": options.MaxBytes = serializedBytes + delta; break;
        }
        var calls = 0;
        var q = new RecordingScopedQuery { TelemetrySearch = (request, scope, token) =>
        {
            Assert.Equal(Scope, scope); Assert.Equal(TelemetrySignal.Traces, request.Signal);
            Assert.Equal(Math.Min(100, options.MaxRecords), request.Limit); token.ThrowIfCancellationRequested();
            Assert.Equal(calls == 0 ? null : "page-" + calls, request.Cursor);
            var offset = calls++ * (dimension == "page" ? 2 : 100);
            var page = rows.Skip(offset).Take(Math.Min(request.Limit, dimension == "page" ? 2 : 100)).ToArray();
            // For a span-limited read, the first page is full and has continuation.
            // Return the remainder on the next request to prove truncation honesty.
            if (dimension == "span" && calls > 1) page = rows.Skip(options.MaxRecords).ToArray();
            var more = dimension == "page" ? calls < 3 : dimension == "span" && delta < 0 && calls == 1;
            return Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, page, Partial: more, Cursor: more ? "page-" + calls : null));
        } };
        var result = await Provider(errors, q, options).GatherAsync(Window, Scope, GatherBudget.Default, Ct);
        var partial = delta < 0;
        Assert.Equal(partial, result.Truncated);
        Assert.Equal(EvidenceStatus.Gathered, result.Status); // Known prefix remains evidence.
        Assert.Equal(partial ? "NotComparable" : "Evaluated", result.Telemetry!.Evaluation);
        var decision = Assert.Single(result.Telemetry.Decisions);
        Assert.Equal(partial ? "Partial" : "Complete", decision.State);
        Assert.Equal(partial ? "BudgetExceeded" : "", decision.Reason);
        var edgeCount = dimension switch
        {
            "span" => Math.Min(rows.Length, options.MaxRecords) - 1,
            "relation" => Math.Min(rows.Length - 1, options.MaxRelations),
            "page" => Math.Min(3, options.MaxPages) * 2 - 1,
            _ => rows.Length - 1 - (partial ? 1 : 0),
        };
        using var edges = JsonDocument.Parse(decision.Values["error_edges"]!);
        Assert.Equal(edgeCount, edges.RootElement.GetArrayLength());
        Assert.Equal(errors ? 1 : edgeCount, result.Items.Count);
        Assert.Equal(dimension == "page" ? Math.Min(3, options.MaxPages) : dimension == "span" && partial ? 2 : 1, calls);
    }

    [Theory]
    [InlineData(true, false)] [InlineData(false, false)]
    [InlineData(true, true)] [InlineData(false, true)]
    public async Task Trace_provider_timeout_is_failed_and_caller_cancel_never_completes_report(bool errors, bool callerCancel)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var q = new RecordingScopedQuery { TelemetrySearch = async (_, _, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { if (token.IsCancellationRequested) observed.TrySetResult(); }
            throw new InvalidOperationException("Infinite query unexpectedly completed");
        } };
        var provider = Provider(errors, q, new() { TimeoutSeconds = 1 });
        var collector = new EvidenceCollector([provider], NullLogger<EvidenceCollector>.Instance);
        var run = collector.GatherAsync(Window, Scope, GatherBudget.Default, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        var watch = Stopwatch.StartNew();
        if (callerCancel)
        {
            caller.Cancel();
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(1), Ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(2), Ct));
            Assert.False(run.IsCompletedSuccessfully);
        }
        else
        {
            var report = await run.WaitAsync(TimeSpan.FromSeconds(2), Ct);
            var slice = Assert.Single(report.Slices, s => s.ProviderId == provider.Id);
            Assert.Equal(EvidenceStatus.Failed, slice.Status); Assert.True(slice.Truncated);
            Assert.Contains("Süre tavanı", slice.Detail, StringComparison.Ordinal);
            Assert.Empty(slice.Items); Assert.True(report.IsPartial); Assert.False(caller.IsCancellationRequested);
            Assert.True(observed.Task.IsCompletedSuccessfully);
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }
}
