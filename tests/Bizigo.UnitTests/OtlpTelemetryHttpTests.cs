using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using Bizigo.Api;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Pipeline;
using Bizigo.Ingest.Wal;
using Bizigo.Storage.Raw;
using Google.Protobuf;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Collector.Metrics.V1;

namespace Bizigo.UnitTests;

/// <summary>Production endpoint and durable gateway, exercised through real HTTP semantics.</summary>
public sealed class OtlpTelemetryHttpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("9007199254740991", "9007199254740991")]
    [InlineData("9007199254740993", "9007199254740993")]
    [InlineData("\"9007199254740993\"", "9007199254740993")]
    [InlineData("90071992547409930e-1", "9007199254740993")]
    [InlineData("9.007199254740993e15", "9007199254740993")]
    [InlineData("9223372036854775807", "9223372036854775807")]
    [InlineData("\"9223372036854775807\"", "9223372036854775807")]
    [InlineData("-9223372036854775808", "-9223372036854775808")]
    [InlineData("\"-9223372036854775808\"", "-9223372036854775808")]
    public async Task Numeric_integer_precision_survives_HTTP_archive_and_replay(string wire, string expected)
    {
        await using var host = await Host.StartAsync();
        var raw = Encoding.UTF8.GetBytes("""
            {"resourceMetrics":[{"scopeMetrics":[{"metrics":[{"name":"exact",
            "gauge":{"dataPoints":[{"asInt":__WIRE__,"timeUnixNano":18446744073709551615,
            "startTimeUnixNano":"18446744073709551614",
            "attributes":[{"key":"integer","value":{"intValue":__WIRE__}}]}]}}]}]}]}
            """.Replace("__WIRE__", wire, StringComparison.Ordinal));
        using var response = await host.SendAsync("/v1/metrics", raw, true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var work = await host.Ingest.Reader.ReadAsync(Ct);
        Assert.Equal(raw, work.Envelope.Payload);
        await host.Ingest.ProcessAsync(work, Ct);
        var path = Path.Combine(host.Ingest.Root, "processed", work.Envelope.EnvelopeId.ToString("N") + ".json");
        var before = await File.ReadAllTextAsync(path, Ct);
        var point = JsonNode.Parse(before)!["leaves"]![0]!["metric"]!["gauge"]!["dataPoints"]![0]!;
        Assert.Equal(expected, point["asInt"]!.GetValue<string>());
        Assert.Equal(expected, point["attributes"]![0]!["value"]!["intValue"]!.GetValue<string>());
        Assert.Equal("18446744073709551615", point["timeUnixNano"]!.GetValue<string>());
        Assert.Equal("18446744073709551614", point["startTimeUnixNano"]!.GetValue<string>());
        await host.Ingest.ReplayArchiveAsync(Ct);
        Assert.Equal(before, await File.ReadAllTextAsync(path, Ct));
    }

    [Theory]
    [InlineData("asInt", "9223372036854775808")]
    [InlineData("asInt", "-9223372036854775809")]
    [InlineData("asInt", "1.00000000000000000000000000000001")]
    [InlineData("asInt", "1e-1000")]
    [InlineData("asInt", "1e1000")]
    [InlineData("timeUnixNano", "18446744073709551616")]
    [InlineData("timeUnixNano", "-1")]
    public async Task Fractional_and_out_of_range_integer_tokens_reject_without_admission(string field, string value)
    {
        await using var host = await Host.StartAsync();
        var raw = Encoding.UTF8.GetBytes("""
            {"resourceMetrics":[{"scopeMetrics":[{"metrics":[{"name":"exact","gauge":{"dataPoints":[{"__FIELD__":__VALUE__}]}}]}]}]}
            """.Replace("__FIELD__", field, StringComparison.Ordinal).Replace("__VALUE__", value, StringComparison.Ordinal));
        using var response = await host.SendAsync("/v1/metrics", raw, true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Ingest.Wal.TotalBytes);
        Assert.Equal(0, host.Ingest.AcceptedBatches);
    }

    [Theory]
    [InlineData("{\"resourceMetrics\":[],\"resourceMetrics\":[]}")]
    [InlineData("{\"future\":1,\"future\":2,\"resourceMetrics\":[]}")]
    [InlineData("{\"resourceMetrics\":[{\"scopeMetrics\":[],\"scopeMetrics\":[]}]}")]
    [InlineData("{\"future\":{\"x\":1,\"x\":2},\"resourceMetrics\":[]}")]
    public async Task Duplicate_known_and_unknown_JSON_keys_return_bounded_protocol_error(string body)
    {
        await using var host = await Host.StartAsync();
        using var response = await host.SendAsync("/v1/metrics", Encoding.UTF8.GetBytes(body), true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["message"]!.GetValue<string>();
        Assert.InRange(error.Length, 1, 128);
        Assert.DoesNotContain("System.", error, StringComparison.Ordinal);
        Assert.Equal(0, host.Ingest.Wal.TotalBytes);
        using var valid = await host.SendAsync("/v1/metrics", "{\"future\":{\"x\":1},\"resourceMetrics\":[]}"u8.ToArray(), true);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stored_gzip_and_identity_share_exact_expanded_HTTP_limit(bool gzip)
    {
        const int limit = 1048576;
        await using var host = await Host.StartAsync(limit: limit);
        using var exact = await host.SendAsync("/v1/traces", Encoding.UTF8.GetBytes("{}" + new string(' ', limit - 2)),
            true, gzip, compression: CompressionLevel.NoCompression);
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        using var excess = await host.SendAsync("/v1/traces", Encoding.UTF8.GetBytes("{}" + new string(' ', limit - 1)),
            true, gzip, compression: CompressionLevel.NoCompression);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, excess.StatusCode);
        Assert.Equal(0, host.Ingest.Wal.TotalBytes);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Metrics_traces_proto_json_gzip_identity_matrix(bool traces, bool json, bool gzip)
    {
        await using var host = await Host.StartAsync();
        IMessage fixture = traces ? OtlpTelemetryDecoderTests.Traces() : OtlpTelemetryDecoderTests.Metrics();
        var raw = OtlpTelemetryDecoderTests.Wire(fixture, json);
        using var response = await host.SendAsync(traces ? "/v1/traces" : "/v1/metrics", raw, json, gzip);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(json ? "application/json" : "application/x-protobuf", response.Content.Headers.ContentType!.MediaType);
        var work = await host.Ingest.Reader.ReadAsync(Ct);
        Assert.Equal(raw, work.Envelope.Payload);
        Assert.Equal(Bizigo.Contracts.RawSignalEnvelope.Hash(raw), work.Envelope.PayloadSha256);
        await host.Ingest.ProcessAsync(work, Ct);
        Assert.Single(host.Ingest.Archive.Manifests());
    }

    [Fact]
    public async Task Real_http_ack_waits_for_fsync_and_queue_reservation()
    {
        var flush = new Gate();
        await using var host = await Host.StartAsync(flush);
        var request = host.SendAsync("/v1/metrics", SignalDurabilityTests.Payload(), json: true);
        await Task.WhenAny(flush.Entered.Task, request).WaitAsync(Ct);
        Assert.False(request.IsCompleted);
        Assert.Equal(0, host.Ingest.AcceptedBatches);
        Assert.False(host.Ingest.Reader.TryRead(out _));
        using var second = await host.SendAsync("/v1/metrics", SignalDurabilityTests.Payload(), json: true);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(9), second.Headers.RetryAfter!.Delta);
        flush.Release.TrySetResult();
        using var response = await request;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("rejectedDataPoints", body, StringComparison.Ordinal);
        Assert.Equal(1, host.Ingest.AcceptedLeaves);
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("reader", 403)]
    [InlineData("ingest", 200)]
    public async Task Production_policy_requires_ingest_role(string? role, int expected)
    {
        await using var host = await Host.StartAsync();
        using var response = await host.SendAsync("/v1/metrics", "{}"u8.ToArray(), true, role: role);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(0, host.Ingest.Wal.TotalBytes);
    }

    [Theory]
    [InlineData("not json", "application/json", null, 400)]
    [InlineData("{}", "text/plain", null, 415)]
    [InlineData("{}", null, null, 415)]
    [InlineData("{}", "application/json", "br", 400)]
    [InlineData("{}", "application/json", "gzip", 400)]
    [InlineData("{\"resourceMetrics\":{}}", "application/json", null, 400)]
    public async Task Bad_wire_is_rejected_without_admission(string body, string? type, string? encoding, int expected)
    {
        await using var host = await Host.StartAsync();
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/metrics");
        message.Headers.Add("X-Fixture-Role", "ingest");
        message.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        if (type is not null) message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        if (encoding is not null) message.Content.Headers.ContentEncoding.Add(encoding);
        using var response = await host.Client.SendAsync(message, Ct);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(0, host.Ingest.Wal.TotalBytes);
        Assert.Equal(0, host.Ingest.AcceptedBatches);
    }

    [Fact]
    public async Task Empty_and_all_rejected_exports_have_correct_success_shapes()
    {
        await using var host = await Host.StartAsync();
        using var empty = await host.SendAsync("/v1/metrics", "{}"u8.ToArray(), true);
        Assert.Equal("{}", await empty.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, host.Ingest.Wal.TotalBytes);
        var request = OtlpTelemetryDecoderTests.Metrics();
        foreach (var resource in request.ResourceMetrics)
        foreach (var scope in resource.ScopeMetrics)
        foreach (var metric in scope.Metrics) metric.Name = "";
        using var response = await host.SendAsync("/v1/metrics", request.ToByteArray(), false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parsed = ExportMetricsServiceResponse.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(10, parsed.PartialSuccess.RejectedDataPoints);
        Assert.NotEmpty(parsed.PartialSuccess.ErrorMessage);
        Assert.Equal(0, host.Ingest.AcceptedLeaves);
        Assert.Equal(1, host.Ingest.AcceptedBatches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decompressed_limit_rejects_without_wal(bool gzip)
    {
        await using var host = await Host.StartAsync(limit: 64);
        using var response = await host.SendAsync("/v1/metrics", new byte[1000], true, gzip);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, host.Ingest.Wal.TotalBytes);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    [InlineData(503)]
    [InlineData(500)]
    public async Task Protobuf_errors_are_bounded_otlp_status_messages(int expected)
    {
        await using var host = await Host.StartAsync(expected == 500 ? new BrokenFlush() : null,
            limit: expected == 413 ? 64 : 100000);
        byte[] body = expected is 400 or 413 ? new byte[1000] : OtlpTelemetryDecoderTests.Metrics().ToByteArray();
        if (expected == 503)
        {
            using var admitted = await host.SendAsync("/v1/metrics", body, false);
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        }
        using var response = await host.SendAsync("/v1/metrics", body, false);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType!.MediaType);
        using var parsed = new CodedInputStream(await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(18U, parsed.ReadTag());
        var message = parsed.ReadString();
        Assert.InRange(message.Length, 1, 128);
        Assert.True(parsed.IsAtEnd);
        Assert.DoesNotContain("System.", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_body_limit_and_normalized_media_type_are_supported(bool gzip)
    {
        await using var host = await Host.StartAsync(limit: 64);
        using var exact = await host.SendAsync("/v1/traces", Encoding.UTF8.GetBytes("{}" + new string(' ', 62)), true, gzip);
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        using var excess = await host.SendAsync("/v1/traces", Encoding.UTF8.GetBytes("{}" + new string(' ', 63)), true, gzip);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, excess.StatusCode);
        using var normalized = new HttpRequestMessage(HttpMethod.Post, "/v1/traces");
        normalized.Headers.Add("X-Fixture-Role", "ingest");
        normalized.Content = new ByteArrayContent("{}"u8.ToArray());
        normalized.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("APPLICATION/JSON; charset=utf-8");
        using var response = await host.Client.SendAsync(normalized, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(0, host.Ingest.AcceptedBatches);
    }

    private sealed class BrokenFlush : IWalDurability
    {
        public ValueTask FlushAsync(FileStream stream, CancellationToken cancellationToken) => throw new IOException("injected");
    }

    private sealed class Gate : IWalDurability
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask FlushAsync(FileStream stream, CancellationToken cancellationToken)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); stream.Flush(true); }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "signal-http-" + Guid.NewGuid().ToString("N"));
        private readonly InMemoryControlPlaneFactory factory = new();
        private WebApplication app = null!;
        public HttpClient Client { get; private set; } = null!;
        public SignalIngest Ingest { get; private set; } = null!;
        public static async Task<Host> StartAsync(IWalDurability? durability = null, int limit = 100000)
        {
            var host = new Host();
            host.Ingest = new(new(), new SourceDirectory(host.factory), new InMemoryObjectStore(),
                Options.Create(new SignalOptions { Directory = host.root, ChannelCapacity = 1 }),
                Options.Create(new WalOptions { RetryAfterSeconds = 9, FlushToDisk = false }),
                Options.Create(new RawStoreOptions()), NullLogger<WriteAheadLog>.Instance, durability: durability);
            await host.Ingest.RecoverAsync(Ct);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(host.Ingest);
            builder.Services.Configure<IngestOptions>(o => o.MaxRequestBytes = limit);
            builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, RoleHeader>("fixture", _ => { });
            builder.Services.AddAuthorization(o => o.AddPolicy(BizigoAuthPolicies.Ingest, p => p.RequireAuthenticatedUser().RequireRole("ingest")));
            host.app = builder.Build();
            host.app.UseAuthentication(); host.app.UseAuthorization(); host.app.MapOtlpTelemetry();
            await host.app.StartAsync(Ct);
            host.Client = host.app.GetTestClient();
            return host;
        }
        public async Task<HttpResponseMessage> SendAsync(string path, byte[] raw, bool json, bool gzip = false, string? role = "ingest",
            CompressionLevel compression = CompressionLevel.Fastest)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, path);
            if (role is not null) message.Headers.Add("X-Fixture-Role", role);
            if (gzip)
            {
                using var compressed = new MemoryStream();
                using (var zip = new GZipStream(compressed, compression, true)) zip.Write(raw);
                raw = compressed.ToArray();
            }
            message.Content = new ByteArrayContent(raw);
            message.Content.Headers.ContentType = new(json ? "application/json" : "application/x-protobuf");
            if (gzip) message.Content.Headers.ContentEncoding.Add("gzip");
            return await Client.SendAsync(message, Ct);
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await app.DisposeAsync(); Ingest.Dispose(); factory.Dispose();
            Directory.Delete(root, true);
        }
    }
    private sealed class RoleHeader(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Fixture-Role"].ToString();
            return Task.FromResult(role.Length == 0 ? AuthenticateResult.NoResult() : AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "fixture")), Scheme.Name)));
        }
    }
}
