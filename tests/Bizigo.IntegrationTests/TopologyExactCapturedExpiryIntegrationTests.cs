using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Diagnostics;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Wal;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Bizigo.Storage.Raw;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Proto.Common.V1;

namespace Bizigo.IntegrationTests;

/// <summary>
/// B04 exact captured-retention cases. The event is historical while E is two
/// hours in the future: public detail can prove an explicit old event window
/// today, and direct scoped reads can probe E-1ns/E/E+1ns without wall-clock
/// sleeps. A separate process replay remains a distinct O06/B04 oracle.
/// </summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyExactCapturedExpiryIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const ulong Day = TopologyExpiry.NanosecondsPerDay;

    [Theory]
    [InlineData(30, 90, 90)]
    [InlineData(90, 30, 90)]
    [InlineData(90, 90, 10)]
    public async Task B04_Exact_30_90_10_captured_minimum_physical_row_and_all_scoped_graph_surfaces(
        int parentDays, int childDays, int observedDays)
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var shortDays = Math.Min(parentDays, Math.Min(childDays, observedDays));
        var parentAt = DateTimeOffset.UtcNow.AddHours(2).AddDays(-shortDays);
        var parentStart = TopologyIdentity.Nano(parentAt);
        var childStart = parentStart + 1000;
        var historical = parentAt.AddDays(-2);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var sourceA = "b04-a-" + suffix;
        var sourceB = "b04-b-" + suffix;
        var scopeA = AccessScope.ForGroups("b04-A-" + suffix, ["A"]);
        var scopeAB = AccessScope.ForGroups("b04-AB-" + suffix, ["A", "B"]);
        await fixture.SourceAsync(sourceA, "A", historical);
        await fixture.SourceAsync(sourceB, "B", historical);
        var historicalClock = new FakeTimeProvider(parentAt.AddDays(-1));
        var registry = new TopologyRegistry(fixture.Factory, historicalClock);
        var root = (await registry.CreateAsync(scopeAB, true,
            new(TopologyNodeKind.Service, "b04-root", "A", true, []), Ct)).Node!.Id;
        var parent = (await registry.CreateAsync(scopeAB, true,
            new(TopologyNodeKind.Service, "b04-parent", "A", true,
                [new(sourceA, "n", "parent")]), Ct)).Node!.Id;
        var child = (await registry.CreateAsync(scopeAB, true,
            new(TopologyNodeKind.Service, "b04-child", "B", true,
                [new(sourceB, "n", "child")]), Ct)).Node!.Id;
        await using var history = await fixture.Factory.CreateDbContextAsync(Ct);
        var sourceRoots = await history.TopologyNodes.AsNoTracking()
            .Where(node => node.SourceId == sourceA || node.SourceId == sourceB)
            .ToDictionaryAsync(node => node.SourceId!, Ct);
        var declared = new TopologyEdgeRegistry(fixture.Factory, historicalClock);
        Assert.Equal(201, (await declared.CreateAsync(scopeAB, true,
            new(sourceRoots[sourceA].Id, parent, "contains"), Ct)).Status);
        Assert.Equal(201, (await declared.CreateAsync(scopeAB, true,
            new(sourceRoots[sourceB].Id, child, "contains"), Ct)).Status);
        Assert.Equal(201, (await declared.CreateAsync(
            scopeAB, true, new(root, parent, "depends_on"), Ct)).Status);

        var watermark = new TopologyPublicationWatermarkReader(fixture.Storage);
        var publisher = new TopologyPublicationCoordinator(fixture.Factory, watermark,
            new TopologyPublicationWatermarkWriter(watermark, fixture.Storage));
        var writer = new TelemetryWriter(fixture.Storage, fixture.Owners,
            new TopologyObservedProjector(fixture.Storage, publisher.PublishAsync,
                readPendingKey: publisher.ReadPendingKeyAsync));
        var parentExport = TelemetryDbFixture.Traces(sourceA, checked((ulong)parentStart));
        var childExport = TelemetryDbFixture.Traces(sourceB, checked((ulong)childStart));
        Bind(parentExport.ResourceSpans[0].Resource.Attributes, "parent");
        Bind(childExport.ResourceSpans[0].Resource.Attributes, "child");
        var parentSpan = parentExport.ResourceSpans[0].ScopeSpans[0].Spans[0];
        var childSpan = childExport.ResourceSpans[0].ScopeSpans[0].Spans[0];
        parentSpan.ParentSpanId = ByteString.Empty;
        childSpan.SpanId = ByteString.CopyFrom(Convert.FromHexString("0000000000000002"));
        childSpan.ParentSpanId = parentSpan.SpanId;
        var ingestRoot = "captured-" + suffix;
        var rawParent = await AdmitAsync(fixture, writer, parentExport, ingestRoot,
            parentDays, parentDays);
        var rawChild = await AdmitAsync(fixture, writer, childExport, ingestRoot,
            childDays, observedDays);
        Assert.Equal(parentDays, rawParent.RetentionDays);
        Assert.Equal(childDays, rawChild.RetentionDays);
        Assert.Equal(observedDays, rawChild.ObservedRetentionDays);
        Assert.Equal(parent, Assert.Single(rawParent.TopologyBindings!).NodeId);
        Assert.Equal(child, Assert.Single(rawChild.TopologyBindings!).NodeId);

        var expectedParent = parentStart + (decimal)parentDays * Day;
        var expectedChild = childStart + (decimal)childDays * Day;
        var expectedObserved = childStart + (decimal)observedDays * Day;
        var expiry = Math.Min(expectedParent, Math.Min(expectedChild, expectedObserved));
        var from = parentStart - 1;
        var to = childStart + 1;
        var graph = Graph(fixture);
        // The graph engine accepts an exact decimal-nanosecond expiry clock.
        // ScopedQuery derives its production expiry clock from TimeProvider,
        // whose DateTimeOffset precision is 100ns; keep these distinct.
        var before = await graph.SearchEdgesAsync(new TopologyEdgeQuery(expiry - 1,
            Provenance: TopologyProvenance.Observed, FromUnixNano: from, ToUnixNano: to)
            { ExpiryReadClockUnixNano = expiry - 1 }, scopeAB, Ct);
        var edge = Assert.Single(before.Items);
        Assert.Equal(expiry, edge.EffectiveExpiry!.Value);
        var physical = await fixture.SqlAsync("SELECT toString(parent_trace_expiry), "
            + "toString(child_trace_expiry), toString(observed_expiry), toString(expires_nano) "
            + "FROM topology_edges_observed WHERE edge_id = '" + edge.Id + "' "
            + "ORDER BY publication_seq DESC LIMIT 1");
        Assert.Equal(new[] { expectedParent, expectedChild, expectedObserved, expiry }, physical.Trim()
            .Split('\t').Select(value => decimal.Parse(value, CultureInfo.InvariantCulture)).ToArray());

        var eventAt = DateTimeOffset.FromUnixTimeSeconds((long)(parentStart / 1_000_000_000m));
        await new EventWriter(fixture.Storage).WriteEventsAsync([
            Degraded("A", sourceA, eventAt.AddMinutes(1)),
            Degraded("B", sourceB, eventAt.AddMinutes(2)),
        ], Ct);
        foreach (var clock in new[] { expiry - 1, expiry, expiry + 1 })
        {
            var eligible = clock < expiry;
            var page = await graph.SearchEdgesAsync(new TopologyEdgeQuery(clock,
                Provenance: TopologyProvenance.Observed, FromUnixNano: from, ToUnixNano: to)
                { ExpiryReadClockUnixNano = clock }, scopeAB, Ct);
            Assert.Equal(eligible, page.Items.Any(row => row.Id == edge.Id));
            Assert.Equal(eligible, await graph.GetEdgeAtExpiryAsync(edge.Id, clock, from, to,
                null, clock, scopeAB, null, 200, Ct) is not null);
            var neighbors = await graph.NeighborhoodAsync(new TopologyNeighborhoodQuery(parent, clock,
                FromUnixNano: from, ToUnixNano: to)
                { ExpiryReadClockUnixNano = clock }, scopeAB, Ct);
            Assert.Equal(eligible, neighbors.Neighbors.Any(row => row.EdgeId == edge.Id));
            Assert.Equal(eligible ? TopologyGraphResultStatus.Found : TopologyGraphResultStatus.Unreachable,
                (await graph.PathAsync(new TopologyPathQuery(parent, child, clock,
                    FromUnixNano: from, ToUnixNano: to)
                    { ExpiryReadClockUnixNano = clock }, scopeAB, Ct)).Status);
            var ancestor = await graph.CommonAncestorAsync(new TopologyCommonAncestorQuery([parent, child], clock,
                from, to) { ExpiryReadClockUnixNano = clock }, scopeAB, Ct);
            Assert.Equal(eligible ? TopologyGraphResultStatus.Found : TopologyGraphResultStatus.Unreachable,
                ancestor.Status);
            if (eligible) Assert.Equal(root, ancestor.NodeId);
            var outside = await graph.CountExternalNeighborsAsync(new TopologyNeighborhoodQuery(parent, clock,
                FromUnixNano: from, ToUnixNano: to)
                { ExpiryReadClockUnixNano = clock }, scopeA, Ct);
            Assert.Equal(eligible ? 1 : 0, outside.Count);
            Assert.Null(outside.Reason);
        }
        // Scoped production reads get the exact server-authoritative decimal
        // clock from DI; historical asOf cannot override observed expiry.
        foreach (var clock in new[] { expiry - 1, expiry, expiry + 1 })
        {
            var query = ScopedAt(fixture, fixture.Storage, graph, clock);
            var eligible = clock < expiry;
            var page = await query.SearchTopologyEdgesAsync(new(clock,
                Provenance: TopologyProvenance.Observed, FromUnixNano: from, ToUnixNano: to), scopeAB, Ct);
            Assert.Equal(eligible, page.Items.Any(row => row.Id == edge.Id));
            Assert.Equal(eligible, await WindowDetailAsync(query, edge.Id, clock, from, to, scopeAB) is not null);
            var neighbors = await query.GetTopologyNeighborhoodAsync(new(parent, clock,
                FromUnixNano: from, ToUnixNano: to), scopeAB, Ct);
            Assert.Equal(eligible, neighbors.Neighbors.Any(row => row.EdgeId == edge.Id));
            Assert.Equal(eligible ? TopologyGraphResultStatus.Found : TopologyGraphResultStatus.Unreachable,
                (await query.GetTopologyPathAsync(new(parent, child, clock,
                    FromUnixNano: from, ToUnixNano: to), scopeAB, Ct)).Status);
            var ancestor = await query.GetTopologyCommonAncestorAsync(new([parent, child], clock,
                from, to), scopeAB, Ct);
            Assert.Equal(eligible ? TopologyGraphResultStatus.Found : TopologyGraphResultStatus.Unreachable,
                ancestor.Status);
            if (eligible) Assert.Equal(root, ancestor.NodeId);
            var outside = await query.CountExternalTopologyNeighborsAsync(new(parent, clock,
                FromUnixNano: from, ToUnixNano: to), scopeA, Ct);
            Assert.Equal(eligible ? 1 : 0, outside.Count);
            Assert.Null(outside.Reason);
        }
        var afterExpiry = ScopedAt(fixture, fixture.Storage, graph, expiry + 1);
        Assert.Empty((await afterExpiry.SearchTopologyEdgesAsync(new(expiry - 1,
            Provenance: TopologyProvenance.Observed, FromUnixNano: from, ToUnixNano: to), scopeAB, Ct)).Items);
        Assert.Equal("1", (await fixture.SqlAsync("SELECT count() FROM topology_edges_observed "
            + "WHERE edge_id = '" + edge.Id + "'")).Trim());

        // The trace feed remains physically present at all three read clocks.
        // RCA must use captured edge expiry, not turn feed presence into proof.
        Assert.Equal(TelemetryResultStatus.Data,
            (await afterExpiry.GetTelemetryFeedAsync(TelemetrySignal.Traces, null, scopeAB, Ct)).Status);
        // The RCA event window is fixed and historical. Only the trusted
        // expiry clock moves: DateTimeOffset's 100ns precision must not turn
        // this into an approximate expiry-boundary assertion.
        var rcaWindow = new RcaWindow
        {
            BaselineFrom = eventAt.AddDays(-7), BaselineTo = eventAt.AddMinutes(-2),
            From = eventAt.AddMinutes(-1), To = eventAt.AddMinutes(10), OwnerGroups = ["A", "B"],
        };
        Assert.True(TopologyIdentity.Nano(rcaWindow.To) <= expiry - 1);
        foreach (var clock in new[] { expiry - 1, expiry, expiry + 1 })
        {
            var query = ScopedAt(fixture, fixture.Storage, graph, clock);
            var path = await new TopologyGraphPathProvider(query).GatherAsync(rcaWindow,
                scopeAB, GatherBudget.Default, Ct);
            var ancestorProof = await new TopologyCommonAncestorProvider(query).GatherAsync(rcaWindow,
                scopeAB, GatherBudget.Default, Ct);
            var expected = clock < expiry ? EvidenceStatus.Gathered : EvidenceStatus.Empty;
            Assert.Equal(expected, path.Status);
            Assert.Equal(expected, ancestorProof.Status);
            if (clock < expiry)
            {
                using var pathProof = JsonDocument.Parse(Assert.Single(path.Items).Payload["proof_edges"]);
                var observedPathEdge = Assert.Single(pathProof.RootElement.EnumerateArray().ToArray());
                Assert.Equal(edge.Id, observedPathEdge.GetProperty("id").GetString());
                Assert.Equal("observed", observedPathEdge.GetProperty("provenance").GetString());
                var ancestorItem = Assert.Single(ancestorProof.Items);
                Assert.Equal(root, ancestorItem.Payload["ancestor_node_id"]);
                using var ancestorEdges = JsonDocument.Parse(ancestorItem.Payload["proof_edges"]);
                Assert.Contains(ancestorEdges.RootElement.EnumerateArray().ToArray(), proof =>
                    proof.GetProperty("id").GetString() == edge.Id &&
                    proof.GetProperty("provenance").GetString() == "observed");
            }
            else
            {
                Assert.Empty(path.Items);
                Assert.Empty(ancestorProof.Items);
            }
        }

        // At real server time, the row is still TTL-valid but older than the
        // default 24-hour observed window. V's public from/to contract must
        // make explicit historical detail available without changing asOf.
        await using var api = await TopologyHttpOracleHost.StartAsync(fixture, Ct);
        var asOfNow = TopologyIdentity.Nano(DateTimeOffset.UtcNow);
        var route = "/v1/topology/edges/" + edge.Id + "?asOf=" + Uri.EscapeDataString(Utc(asOfNow))
            + "&from=" + Uri.EscapeDataString(Utc(from)) + "&to=" + Uri.EscapeDataString(Utc(to));
        using var defaultDetail = await GetAsBothAsync(api, "/v1/topology/edges/" + edge.Id);
        Assert.Equal(HttpStatusCode.NotFound, defaultDetail.StatusCode);
        using var first = await GetAsBothAsync(api, route + "&evidencePageSize=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync(Ct));
        Assert.Equal(edge.Id, firstJson.RootElement.GetProperty("edge").GetProperty("id").GetString());
        var firstEvidence = Assert.Single(firstJson.RootElement.GetProperty("evidence").EnumerateArray().ToArray());
        var cursor = firstJson.RootElement.GetProperty("evidence_cursor").GetString();
        Assert.NotNull(cursor);
        using var second = await GetAsBothAsync(api, route + "&evidencePageSize=1&evidenceCursor="
            + Uri.EscapeDataString(cursor));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(edge.Id, secondJson.RootElement.GetProperty("edge").GetProperty("id").GetString());
        var secondEvidence = Assert.Single(secondJson.RootElement.GetProperty("evidence").EnumerateArray().ToArray());
        Assert.Null(secondJson.RootElement.GetProperty("evidence_cursor").GetString());
        var traceId = Convert.ToHexStringLower(parentSpan.TraceId.Span);
        var expectedOccurrences = new Dictionary<string, (string SpanId, decimal EventTime)>(StringComparer.Ordinal)
        {
            [rawParent.EnvelopeId.ToString("N") + "/" + Assert.Single(rawParent.AcceptedKeys)] =
                (Convert.ToHexStringLower(parentSpan.SpanId.Span), parentStart),
            [rawChild.EnvelopeId.ToString("N") + "/" + Assert.Single(rawChild.AcceptedKeys)] =
                (Convert.ToHexStringLower(childSpan.SpanId.Span), childStart),
        };
        var pageEvidence = new[] { firstEvidence, secondEvidence };
        Assert.Equal(expectedOccurrences.Keys.Order(StringComparer.Ordinal), pageEvidence
            .Select(reference => reference.GetProperty("id").GetString()!).Order(StringComparer.Ordinal));
        foreach (var reference in pageEvidence)
        {
            var occurrence = reference.GetProperty("id").GetString()!;
            Assert.Equal(traceId, reference.GetProperty("trace_logical_id").GetString());
            Assert.Equal(expectedOccurrences[occurrence].SpanId,
                reference.GetProperty("span_logical_id").GetString());
            Assert.Equal(expectedOccurrences[occurrence].EventTime,
                decimal.Parse(reference.GetProperty("event_time_unix_nano").GetString()!,
                    CultureInfo.InvariantCulture));
        }
        var changed = route[..route.LastIndexOf("&to=", StringComparison.Ordinal)]
            + "&to=" + Uri.EscapeDataString(Utc(to - 1));
        using var altered = await GetAsBothAsync(api, changed + "&evidencePageSize=1&evidenceCursor="
            + Uri.EscapeDataString(cursor));
        Assert.Equal(HttpStatusCode.BadRequest, altered.StatusCode);
        using var partial = await GetAsBothAsync(api, "/v1/topology/edges/" + edge.Id + "?from="
            + Uri.EscapeDataString(Utc(from)));
        Assert.Equal(HttpStatusCode.BadRequest, partial.StatusCode);
        using var onlyA = await api.GetAsync(route, "A");
        using var onlyB = await api.GetAsync(route, "B");
        Assert.Equal(HttpStatusCode.NotFound, onlyA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, onlyB.StatusCode);

        // The public wire gate and ScopedQuery must consume the same
        // server-owned decimal-nanosecond clock. The asOf parameter is still
        // only a historical state selector, never an expiry override.
        var serverClock = new MutableTopologyExpiryNanoClock(expiry - 1);
        await using (var exactApi = await TopologyHttpOracleHost.StartAsync(fixture, Ct, serverClock))
        {
            foreach (var clock in new[] { expiry - 1, expiry, expiry + 1 })
            {
                serverClock.Set(clock);
                var exactRoute = "/v1/topology/edges/" + edge.Id + "?asOf="
                    + Uri.EscapeDataString(Utc(clock)) + "&from="
                    + Uri.EscapeDataString(Utc(from)) + "&to=" + Uri.EscapeDataString(Utc(to));
                using var response = await GetAsBothAsync(exactApi, exactRoute);
                Assert.Equal(clock < expiry ? HttpStatusCode.OK : HttpStatusCode.NotFound,
                    response.StatusCode);
                if (clock < expiry)
                {
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
                    Assert.Equal(edge.Id, body.RootElement.GetProperty("edge").GetProperty("id").GetString());
                }
            }
            // A historical asOf before E cannot reopen a row after the
            // authoritative server clock has advanced beyond E.
            serverClock.Set(expiry + 1);
            var historicalRoute = "/v1/topology/edges/" + edge.Id + "?asOf="
                + Uri.EscapeDataString(Utc(expiry - 1)) + "&from="
                + Uri.EscapeDataString(Utc(from)) + "&to=" + Uri.EscapeDataString(Utc(to));
            using var historicalDetail = await GetAsBothAsync(exactApi, historicalRoute);
            Assert.Equal(HttpStatusCode.NotFound, historicalDetail.StatusCode);
        }

        await VerifyFreshProcessReplayAsync(fixture, ingestRoot, rawParent, rawChild,
            edge.Id, expiry, from, to, scopeAB);
    }

    private async Task VerifyFreshProcessReplayAsync(TelemetryDbFixture fixture, string ingestRoot,
        RawSignalEnvelope parentEnvelope, RawSignalEnvelope childEnvelope, string originalEdgeId,
        decimal expiry, decimal from, decimal to, AccessScope scope)
    {
        var originalArchive = new SignalArchive(fixture.Objects, Path.Combine(fixture.Root, ingestRoot));
        var originals = originalArchive.Manifests().ToArray();
        Assert.Equal(2, originals.Length);
        foreach (var manifest in originals) await originalArchive.ReadAsync(manifest, Ct);
        var archiveRoot = Path.Combine(fixture.Root, "archive-only-" + Guid.NewGuid().ToString("N"));
        var manifestDirectory = Path.Combine(archiveRoot, "manifests");
        Directory.CreateDirectory(manifestDirectory);
        foreach (var file in Directory.GetFiles(originalArchive.ManifestDirectory, "*.json"))
            File.Copy(file, Path.Combine(manifestDirectory, Path.GetFileName(file)));
        Assert.Equal(2, Directory.GetFiles(manifestDirectory, "*.json").Length);
        Assert.False(Directory.Exists(Path.Combine(archiveRoot, "wal")));
        Assert.False(Directory.Exists(Path.Combine(archiveRoot, "processed")));
        Dictionary<Guid, string> originalClaims;
        await using (var db = await fixture.Factory.CreateDbContextAsync(Ct))
            originalClaims = await db.TelemetryOwnerClaims.AsNoTracking()
                .Where(claim => claim.EnvelopeId == parentEnvelope.EnvelopeId
                    || claim.EnvelopeId == childEnvelope.EnvelopeId)
                .ToDictionaryAsync(claim => claim.EnvelopeId, claim => claim.BindingHash, Ct);
        Assert.Equal(2, originalClaims.Count);

        // The restore target retains authoritative PG history and verified S3
        // objects only. Derived CH projection and PG publication receipts start
        // together at zero. The repair certificate must also be rebound to
        // this distinct CH database; the child cannot consult old WAL or an
        // old Ready certificate for a different physical target.
        using var fresh = await DevStackSetup.ClickHouseAsync(stack, Ct);
        await DevStackSetup.ResetTopologyPublicationForFreshStoreAsync(fixture.Factory, Ct);
        await using (var db = await fixture.Factory.CreateDbContextAsync(Ct))
        {
            Assert.True(await db.TopologyBindings.AnyAsync(Ct));
            var retainedClaims = await db.TelemetryOwnerClaims.AsNoTracking()
                .Where(claim => claim.EnvelopeId == parentEnvelope.EnvelopeId
                    || claim.EnvelopeId == childEnvelope.EnvelopeId)
                .ToDictionaryAsync(claim => claim.EnvelopeId, claim => claim.BindingHash, Ct);
            Assert.Equal(originalClaims.Count, retainedClaims.Count);
            foreach (var (envelopeId, bindingHash) in originalClaims)
                Assert.Equal(bindingHash, retainedClaims[envelopeId]);
        }
        var config = Path.Combine(fixture.Root, "captured-recover.json");
        await File.WriteAllBytesAsync(config, JsonSerializer.SerializeToUtf8Bytes(new
        {
            root = archiveRoot, stage = "after-wal-before-ack", recover = true,
            first = "", second = "", TelemetryRetentionDays = 365, ObservedRetentionDays = 365,
        }), Ct);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = DevStackSetup.RepoPath(""), RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false,
        };
        start.ArgumentList.Add(DevStackSetup.RepoPath(
            "sim/Bizigo.OtlpFixture/bin/Release/net10.0/Bizigo.OtlpFixture.dll"));
        start.ArgumentList.Add("--topology-crash");
        start.ArgumentList.Add(config);
        start.Environment["ConnectionStrings__ControlPlane"] = stack.PostgresConnectionString;
        start.Environment["ConnectionStrings__ClickHouse"] = fresh.Options.ConnectionString;
        start.Environment["RawStore__ServiceUrl"] = fixture.RawOptions.ServiceUrl;
        start.Environment["RawStore__Bucket"] = fixture.RawOptions.Bucket;
        start.Environment["RawStore__AccessKey"] = fixture.RawOptions.AccessKey;
        start.Environment["RawStore__SecretKey"] = fixture.RawOptions.SecretKey;
        using var child = Process.Start(start) ?? throw new IOException("Could not start captured-TTL archive child.");
        var stdout = child.StandardOutput.ReadToEndAsync(Ct);
        var stderr = child.StandardError.ReadToEndAsync(Ct);
        try
        {
            Assert.NotEqual(Environment.ProcessId, child.Id);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                await child.WaitForExitAsync(timeout.Token);
            }
            Assert.True(child.ExitCode == 0, await stderr);
            using var receipt = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(archiveRoot, "recovered.json"), Ct));
            Assert.Equal(child.Id, receipt.RootElement.GetProperty("pid").GetInt32());
            Assert.True(receipt.RootElement.GetProperty("ready").GetBoolean());
            Assert.Equal(365, receipt.RootElement.GetProperty("configuredTelemetryRetentionDays").GetInt32());
            Assert.Equal(365, receipt.RootElement.GetProperty("configuredObservedRetentionDays").GetInt32());
            Assert.Equal(2, receipt.RootElement.GetProperty("verifiedReplayCount").GetInt32());
            Assert.Equal(2, receipt.RootElement.GetProperty("replayCount").GetInt32());
            Assert.Equal(2, receipt.RootElement.GetProperty("verified").GetArrayLength());
            await using (var db = await fixture.Factory.CreateDbContextAsync(Ct))
            {
                var replayClaims = await db.TelemetryOwnerClaims.AsNoTracking()
                    .Where(claim => claim.EnvelopeId == parentEnvelope.EnvelopeId
                        || claim.EnvelopeId == childEnvelope.EnvelopeId)
                    .ToDictionaryAsync(claim => claim.EnvelopeId, claim => claim.BindingHash, Ct);
                Assert.Equal(originalClaims.Count, replayClaims.Count);
                foreach (var (envelopeId, bindingHash) in originalClaims)
                    Assert.Equal(bindingHash, replayClaims[envelopeId]);
            }
            var restoredArchive = new SignalArchive(fixture.Objects, archiveRoot);
            foreach (var original in new[] { parentEnvelope, childEnvelope })
            {
                var restored = await restoredArchive.ReadAsync(restoredArchive.Manifests()
                    .Single(manifest => manifest.EnvelopeId == original.EnvelopeId), Ct);
                Assert.Equal(RawSignalCodec.Encode(original), RawSignalCodec.Encode(restored));
                Assert.Equal(original.TopologyBindingsSha256, restored.TopologyBindingsSha256);
                Assert.Equal(original.OwnerBindingsSha256, restored.OwnerBindingsSha256);
                Assert.Equal(original.RetentionDays, restored.RetentionDays);
                Assert.Equal(original.ObservedRetentionDays, restored.ObservedRetentionDays);
            }
            var wal = Path.Combine(archiveRoot, "wal");
            if (Directory.Exists(wal))
                Assert.All(Directory.GetFiles(wal, "*.log"), file =>
                    Assert.Equal(0L, new FileInfo(file).Length));
            var watermark = new TopologyPublicationWatermarkReader(fresh);
            var graph = new TopologyGraphQueryService(new TopologyGraphSnapshotSource(fixture.Factory,
                new TopologyObservedSnapshotReader(fresh), new TopologyPublicationFence(
                    new TopologyPublicationRevisionSource(fixture.Factory, watermark,
                        new TopologyObservedRepairReadiness(fixture.Factory, fresh)))));
            foreach (var clock in new[] { expiry - 1, expiry, expiry + 1 })
            {
                var page = await graph.SearchEdgesAsync(new TopologyEdgeQuery(clock,
                    Provenance: TopologyProvenance.Observed, FromUnixNano: from, ToUnixNano: to)
                    { ExpiryReadClockUnixNano = clock }, scope, Ct);
                Assert.Equal(clock < expiry, page.Items.Any(edge => edge.Id == originalEdgeId));
                if (clock < expiry) Assert.Equal(originalEdgeId, Assert.Single(page.Items).Id);
                Assert.Equal(clock < expiry,
                    await graph.GetEdgeAtExpiryAsync(originalEdgeId, clock, from, to,
                        null, clock, scope, null, 200, Ct) is not null);
            }
            foreach (var clock in new[] { expiry - 1, expiry, expiry + 1 })
            {
                var scoped = ScopedAt(fixture, fresh, graph, clock);
                var page = await scoped.SearchTopologyEdgesAsync(new(clock,
                    Provenance: TopologyProvenance.Observed, FromUnixNano: from, ToUnixNano: to), scope, Ct);
                Assert.Equal(clock < expiry, page.Items.Any(edge => edge.Id == originalEdgeId));
                Assert.Equal(clock < expiry,
                    await WindowDetailAsync(scoped, originalEdgeId, clock, from, to, scope) is not null);
            }
            var afterExpiry = ScopedAt(fixture, fresh, graph, expiry + 1);
            Assert.Empty((await afterExpiry.SearchTopologyEdgesAsync(new(expiry - 1,
                Provenance: TopologyProvenance.Observed, FromUnixNano: from, ToUnixNano: to), scope, Ct)).Items);
            var persisted = await SqlAsync(fresh, "SELECT toString(expires_nano) FROM topology_edges_observed "
                + "WHERE edge_id = '" + originalEdgeId + "' ORDER BY publication_seq DESC LIMIT 1");
            Assert.Equal(expiry, decimal.Parse(persisted.Trim(), CultureInfo.InvariantCulture));
            Assert.Equal("1", (await SqlAsync(fresh, "SELECT count() FROM topology_edges_observed "
                + "WHERE edge_id = '" + originalEdgeId + "'")).Trim());
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await child.WaitForExitAsync(cleanup.Token);
            _ = await stdout;
            _ = await stderr;
        }
    }

    private static async Task<RawSignalEnvelope> AdmitAsync(TelemetryDbFixture fixture, TelemetryWriter writer,
        OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceRequest request, string root,
        int traceDays, int observedDays)
    {
        var path = Path.Combine(fixture.Root, root);
        using var ingest = new SignalIngest(new(), new(fixture.Factory), fixture.Objects,
            Options.Create(new SignalOptions { Directory = path, ObservedRetentionDays = observedDays }),
            Options.Create(new WalOptions { Directory = path }), Options.Create(fixture.RawOptions),
            NullLogger<WriteAheadLog>.Instance, owners: fixture.Owners, bindings: fixture.Owners,
            sink: writer, retention: new TelemetryRetentionPolicy(traceDays));
        await ingest.RecoverAsync(Ct);
        return await fixture.EmitAsync(ingest, request, TelemetrySignal.Traces);
    }

    private static TopologyGraphQueryService Graph(TelemetryDbFixture fixture)
    {
        var fence = new TopologyPublicationFence(new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage),
            new TopologyObservedRepairReadiness(fixture.Factory, fixture.Storage)));
        return new(new TopologyGraphSnapshotSource(fixture.Factory,
            new TopologyObservedSnapshotReader(fixture.Storage), fence));
    }

    private static IScopedQuery ScopedAt(TelemetryDbFixture fixture, ClickHouseContext storage,
        TopologyGraphQueryService graph, decimal expiryClock)
    {
        var fence = new TopologyPublicationFence(new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(storage),
            new TopologyObservedRepairReadiness(fixture.Factory, storage)));
        return new ScopedQuery(new(storage), new(storage), new(storage), new(storage), fixture.Db,
            new ControlPlaneAuditSink(fixture.Factory), new TelemetryReader(storage, fixture.Clock), graph,
            topologyClock: new FakeTimeProvider(ClockDate(expiryClock)),
            expiryNanoClock: new FixedTopologyExpiryNanoClock(expiryClock),
            sourceTargetPages: new TopologySourceTargetPageReader(fixture.Factory, fence));
    }

    private async Task<string> SqlAsync(ClickHouseContext storage, string sql)
    {
        var database = new DbConnectionStringBuilder
        { ConnectionString = storage.Options.ConnectionString }["Database"].ToString()!;
        using var http = new HttpClient { BaseAddress = new Uri(stack.ClickHouseHttpUrl),
            Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Add("X-ClickHouse-User", "bizigo");
        http.DefaultRequestHeaders.Add("X-ClickHouse-Key", "bizigo");
        http.DefaultRequestHeaders.Add("X-ClickHouse-Database", database);
        using var content = new StringContent(sql, Encoding.UTF8, new MediaTypeHeaderValue("text/plain"));
        using var response = await http.PostAsync("/", content, Ct);
        var result = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, result);
        return result;
    }

    private static async Task<TopologyEdgeDetail?> WindowDetailAsync(IScopedQuery query, string edgeId,
        decimal clock, decimal from, decimal to, AccessScope scope)
    {
        // Q's window-aware scoped overload is on the integration branch and is
        // intentionally required. Reflection lets this source-only test compile
        // against the unmerged E base without inventing a default-window fallback.
        var method = typeof(IScopedQuery).GetMethods().Single(info => info.Name == "GetTopologyEdgeAsync"
            && info.GetParameters().Length == 6 && info.GetParameters()[2].ParameterType == typeof(decimal));
        return await (Task<TopologyEdgeDetail?>)method.Invoke(query,
            [edgeId, clock, from, to, scope, Ct])!;
    }

    private static string Utc(decimal nano)
    {
        var seconds = decimal.Truncate(nano / 1_000_000_000m);
        var fraction = (long)(nano - seconds * 1_000_000_000m);
        return DateTimeOffset.FromUnixTimeSeconds((long)seconds)
            .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "."
            + fraction.ToString("D9", CultureInfo.InvariantCulture) + "Z";
    }

    private static DateTimeOffset ClockDate(decimal nano) =>
        new(DateTime.UnixEpoch.AddTicks(checked((long)(nano / 100m))), TimeSpan.Zero);

    private static async Task<HttpResponseMessage> GetAsBothAsync(TopologyHttpOracleHost api, string route)
    {
        // The shared host exposes only one-group convenience requests. Use its
        // fixture-local signing key and its two already mapped IdP groups for
        // a true AB JWT, without widening the production AccessScope.
        var key = (SymmetricSecurityKey)typeof(TopologyHttpOracleHost)
            .GetField("key", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(api)!;
        var token = new JwtSecurityToken("https://telemetry-fixture.invalid/issuer", "bizigo-api",
            [new Claim("sub", api.Subject("AB")), new Claim("roles", "reader"),
                new Claim("groups", "/" + api.Subject("A")),
                new Claim("groups", "/" + api.Subject("B"))],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            new JwtSecurityTokenHandler().WriteToken(token));
        return await api.Http.SendAsync(request, Ct);
    }

    private static void Bind(Google.Protobuf.Collections.RepeatedField<KeyValue> attributes, string service)
    {
        attributes.Single(attribute => attribute.Key == "service.name").Value.StringValue = service;
        attributes.Add(new KeyValue { Key = "service.namespace", Value = new AnyValue { StringValue = "n" } });
    }

    private static LogEvent Degraded(string owner, string source, DateTimeOffset at) => new()
    {
        EventId = Guid.NewGuid(), Timestamp = at, OwnerGroup = owner, SourceId = source, Host = source,
        Vendor = "b04", Product = "test", ParserId = "b04", ParserVersion = "1.0.0",
        ParseStatus = ParseStatus.Ok, SignatureHash = 1, TimeSource = TimeSources.Parsed,
        SeverityNum = 3, SrcIp = IPAddress.IPv6Any, DstIp = IPAddress.IPv6Any,
        Attrs = new Dictionary<string, string>(StringComparer.Ordinal), Body = "degraded",
    };
}
