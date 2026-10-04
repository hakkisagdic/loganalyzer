using System.Net;
using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>Planner-run PG/CH oracles; no substitute in-memory publication store.</summary>
[Collection(DevStackCollection.Name)]
public sealed class TopologyConflictIsolationIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Integration")]
    public async Task Published_B_conflict_leaves_A_graph_RCA_and_audit_unchanged()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var before = await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock), seed.ScopeA, Ct);
        var healthy = Assert.Single(before.Items, edge => edge.Provenance == TopologyProvenance.Observed);
        var beforePath = await seed.Query.GetTopologyPathAsync(new(seed.AService1, seed.AService2, seed.ReadClock),
            seed.ScopeA, Ct);
        Assert.Equal(TopologyGraphResultStatus.Found, beforePath.Status);

        await seed.Writer.WriteAsync([Reenvelope(seed.BParent) with
        { Topology = seed.BParent.Topology! with { ServiceNodeId = seed.BAlternate } }], Ct);
        var after = await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock), seed.ScopeA, Ct);
        Assert.Equal(before.Items.Select(static edge => edge.Id), after.Items.Select(static edge => edge.Id));
        Assert.Equal(healthy.Id, (await seed.Query.GetTopologyEdgeAsync(healthy.Id, seed.ReadClock, seed.ScopeA, Ct))!.Edge.Id);
        Assert.Equal(beforePath.EdgeIds,
            (await seed.Query.GetTopologyPathAsync(new(seed.AService1, seed.AService2, seed.ReadClock), seed.ScopeA, Ct)).EdgeIds);
        await Assert.ThrowsAsync<TopologyConflictException>(() => seed.Query.SearchTopologyEdgesAsync(
            new(seed.ReadClock), seed.ScopeB, Ct));
        await Assert.ThrowsAsync<TopologyConflictException>(() => seed.Query.GetTopologyPathAsync(
            new(seed.BService1, seed.BService2, seed.ReadClock), seed.ScopeB, Ct));
        Assert.Empty((await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock,
            Provenance: TopologyProvenance.Observed,
            FromUnixNano: seed.ReadClock - 10 * TopologyExpiry.NanosecondsPerDay,
            ToUnixNano: seed.ReadClock - 9 * TopologyExpiry.NanosecondsPerDay), seed.ScopeB, Ct)).Items);

        var slice = await new TopologyGraphPathProvider(seed.Query).GatherAsync(seed.Window, seed.ScopeA,
            GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Gathered, slice.Status);
        Assert.Single(slice.Items);
        await using var db = await fixture.Factory.CreateDbContextAsync(Ct);
        var audits = await db.AuditLog.Where(row => row.Subject == seed.ScopeA.Subject || row.Subject == seed.ScopeB.Subject)
            .ToArrayAsync(Ct);
        Assert.Contains(audits, row => row.Subject == seed.ScopeA.Subject && row.Action == "topology.edges.list" && row.Succeeded);
        Assert.Contains(audits, row => row.Subject == seed.ScopeB.Subject && row.Action == "topology.edges.list" && !row.Succeeded);
        Assert.Contains(audits, row => row.Subject == seed.ScopeA.Subject && row.Action == "topology.path" && row.Succeeded);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Orphan_conflict_without_published_proof_is_scoped_and_windowed()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var orphanTrace = Guid.NewGuid().ToString("N");
        var start = checked((ulong)(seed.ReadClock - 2_000_000_000m));
        var first = Span(Guid.NewGuid(), orphanTrace, "1111111111111111", "", "orphan-1",
            seed.BSource, seed.ScopeB.OwnerGroups.Single(), seed.BService1, start);
        var second = Reenvelope(first) with
        { Topology = first.Topology! with { ServiceNodeId = seed.BAlternate } };
        await seed.Writer.WriteAsync([first, second], Ct);
        Assert.Equal("0", (await fixture.SqlAsync("SELECT count() FROM topology_edges_observed WHERE trace_logical_id = '"
            + orphanTrace + "'")).Trim());
        Assert.Single((await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock,
            Provenance: TopologyProvenance.Observed), seed.ScopeA, Ct)).Items);
        await Assert.ThrowsAsync<TopologyConflictException>(() => seed.Query.GetTopologyNeighborhoodAsync(
            new(seed.BService1, seed.ReadClock), seed.ScopeB, Ct));
        await Assert.ThrowsAsync<TopologyConflictException>(() => seed.Query.SearchTopologyEdgesAsync(
            new(seed.ReadClock, Provenance: TopologyProvenance.Observed), seed.ScopeB, Ct));
        Assert.Empty((await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock,
            Provenance: TopologyProvenance.Observed,
            FromUnixNano: seed.ReadClock - 10 * TopologyExpiry.NanosecondsPerDay,
            ToUnixNano: seed.ReadClock - 9 * TopologyExpiry.NanosecondsPerDay), seed.ScopeB, Ct)).Items);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task First_publication_child_conflict_blocks_parent_in_child_window()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var trace = Guid.NewGuid().ToString("N");
        var parentTime = checked((ulong)(seed.ReadClock - 2_000_000_000m));
        var childTime = checked((ulong)(seed.ReadClock - 1_000_000_000m));
        var owner = seed.ScopeB.OwnerGroups.Single();
        var parent = Span(Guid.NewGuid(), trace, "aaaaaaaaaaaaaaaa", "", "conflict-parent",
            seed.BSource, owner, seed.BService1, parentTime);
        var child = Span(Guid.NewGuid(), trace, "bbbbbbbbbbbbbbbb", "aaaaaaaaaaaaaaaa",
            "conflict-child", seed.BSource, owner, seed.BService2, childTime);
        var alternate = Reenvelope(child) with
        { Topology = child.Topology! with { ServiceNodeId = seed.BAlternate } };
        await seed.Writer.WriteAsync([parent, child, alternate], Ct);
        var from = seed.ReadClock - 1_500_000_000m;
        var to = seed.ReadClock - 500_000_000m;
        await Assert.ThrowsAsync<TopologyConflictException>(() => seed.Query.GetTopologyNeighborhoodAsync(
            new(seed.BService1, seed.ReadClock, FromUnixNano: from, ToUnixNano: to), seed.ScopeB, Ct));
        var outside = await seed.Query.CountExternalTopologyNeighborsAsync(
            new(seed.BService1, seed.ReadClock, FromUnixNano: from, ToUnixNano: to), seed.ScopeB, Ct);
        Assert.Null(outside.Count);
        Assert.Equal("QueryUnavailable", outside.Reason);
        // The earlier parent is admission context for the child conflict, not
        // an independently conflicted point in its own disjoint time window.
        var parentFrom = seed.ReadClock - 2_500_000_000m;
        var parentTo = seed.ReadClock - 1_500_000_000m;
        var parentOnly = await seed.Query.GetTopologyNeighborhoodAsync(new(seed.BService1, seed.ReadClock,
            FromUnixNano: parentFrom, ToUnixNano: parentTo), seed.ScopeB, Ct);
        Assert.Equal(0, parentOnly.ExternalNeighborCount);
        Assert.Null(parentOnly.ExternalNeighborReason);
        Assert.Empty((await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock,
            Provenance: TopologyProvenance.Observed, FromUnixNano: parentFrom,
            ToUnixNano: parentTo), seed.ScopeB, Ct)).Items);
        Assert.NotEmpty((await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock,
            Provenance: TopologyProvenance.Observed), seed.ScopeA, Ct)).Items);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Expired_conflicted_child_does_not_extend_impact_to_live_parent()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var trace = Guid.NewGuid().ToString("N");
        var childTime = checked((ulong)(seed.ReadClock - 1_000_000_000m));
        var owner = seed.ScopeB.OwnerGroups.Single();
        var parent = Span(Guid.NewGuid(), trace, "abababababababab", "", "expiry-parent",
            seed.BSource, owner, seed.BService1, childTime - 1000)
            with { RetentionDays = 3 };
        var child = Span(Guid.NewGuid(), trace, "cdcdcdcdcdcdcdcd", "abababababababab",
            "expiry-child", seed.BSource, owner, seed.BService2, childTime)
            with { RetentionDays = 1, ObservedRetentionDays = 1 };
        await seed.Writer.WriteAsync([parent, child, Reenvelope(child) with
        { Topology = child.Topology! with { ServiceNodeId = seed.BAlternate } }], Ct);
        var expiredClock = (decimal)childTime + TopologyExpiry.NanosecondsPerDay + 1;
        // TimeProvider advances in 100 ns ticks. The historical as-of remains
        // E+1 ns; the server TTL clock must be at or beyond that instant.
        seed.QueryClock.SetUtcNow(DateTimeOffset.UnixEpoch.AddTicks(
            checked((long)decimal.Ceiling(expiredClock / 100m))));
        var parentOnly = await seed.Query.GetTopologyNeighborhoodAsync(new(seed.BService1,
            expiredClock, FromUnixNano: childTime - 2000, ToUnixNano: childTime + 2000), seed.ScopeB, Ct);
        Assert.Equal(0, parentOnly.ExternalNeighborCount);
        Assert.Null(parentOnly.ExternalNeighborReason);
        Assert.Empty((await seed.Query.SearchTopologyEdgesAsync(new(expiredClock,
            Provenance: TopologyProvenance.Observed,
            FromUnixNano: childTime - 2000, ToUnixNano: childTime + 2000), seed.ScopeB, Ct)).Items);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Pending_parent_resolution_keeps_committed_missing_decision_after_merge()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var registry = new TopologyRegistry(fixture.Factory);
        var owner = seed.ScopeB.OwnerGroups.Single();
        var parentNode = (await registry.CreateAsync(seed.ScopeB, true,
            new(TopologyNodeKind.Service, "pending-parent", owner, true, []), Ct)).Node!.Id;
        var childNode = (await registry.CreateAsync(seed.ScopeB, true,
            new(TopologyNodeKind.Service, "pending-child", owner, true, []), Ct)).Node!.Id;
        var trace = Guid.NewGuid().ToString("N");
        var parentTime = checked((ulong)(seed.ReadClock - 2_000_000_000m));
        var child = Span(Guid.NewGuid(), trace, "eeeeeeeeeeeeeeee", "ffffffffffffffff",
            "pending-child", seed.BSource, owner, childNode, parentTime + 1000);
        var parent = Span(Guid.NewGuid(), trace, "ffffffffffffffff", "",
            "pending-parent", seed.BSource, owner, parentNode, parentTime);
        await seed.Writer.WriteAsync([child], Ct);
        var before = await seed.Query.GetTopologyPathAsync(new(parentNode, childNode, seed.ReadClock), seed.ScopeB, Ct);
        Assert.Equal(TopologyGraphResultStatus.NotVerified, before.Status);
        Assert.Equal("MissingParent", before.Reason);

        var publisher = Publisher(fixture);
        var crashed = new TelemetryWriter(fixture.Storage, fixture.Owners,
            new TopologyObservedProjector(fixture.Storage, publisher.PublishAsync,
                new StopAfterObservedInsert(), publisher.ReadPendingKeyAsync));
        await Assert.ThrowsAsync<IOException>(() => crashed.WriteAsync([parent], Ct));
        Assert.NotNull(await publisher.ReadPendingKeyAsync(Ct));
        await fixture.SqlAsync("OPTIMIZE TABLE topology_parent_resolution FINAL");
        var stillCommitted = await seed.Query.GetTopologyPathAsync(new(parentNode, childNode,
            seed.ReadClock), seed.ScopeB, Ct);
        Assert.Equal(before.Status, stillCommitted.Status);
        Assert.Equal(before.Reason, stillCommitted.Reason);
        var count = await seed.Query.CountExternalTopologyNeighborsAsync(new(childNode, seed.ReadClock), seed.ScopeB, Ct);
        Assert.Null(count.Count);
        Assert.Equal("MissingParent", count.Reason);

        var recovered = new TopologyObservedProjector(fixture.Storage, publisher.PublishAsync,
            readPendingKey: publisher.ReadPendingKeyAsync);
        await recovered.ProjectAsync([parent], Ct);
        Assert.Null(await publisher.ReadPendingKeyAsync(Ct));
        var after = await seed.Query.GetTopologyPathAsync(new(parentNode, childNode, seed.ReadClock), seed.ScopeB, Ct);
        Assert.Equal(TopologyGraphResultStatus.Found, after.Status);
        Assert.Single(after.EdgeIds);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Frozen_v2_conflict_manifest_replays_exact_old_rowset_then_requires_migration()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var publisher = Publisher(fixture);
        var key = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var anchor = new string('a', 64);
        var first = new string('b', 64);
        var second = new string('c', 64);
        // Frozen d32b3be v2 candidate/batch shape. Do not serialize the
        // current TopologyConflictCandidate: its new fields change row bytes.
        var candidate = new
        {
            Fingerprint = first, OwnerGroup = "legacy-owner", SourceId = "legacy-source",
            NodeId = (string?)null, EventTimeNano = 1000UL,
            TraceExpiryNano = 3000m, ObservedExpiryNano = 3000m,
            ParentAnchor = string.Empty, IsConflictedAnchor = true,
        };
        var context = JsonSerializer.Serialize(new[] { candidate }, RawSignalCodec.Json);
        var payload = JsonSerializer.Serialize(new
        {
            PublicationKey = key, Edges = Array.Empty<object>(),
            Conflicts = new[] { new
            {
                Anchor = anchor, FirstFingerprint = first, ConflictingFingerprint = second,
                Candidates = new[] { candidate },
            } },
        }, RawSignalCodec.Json);
        var edgeColumns = new[]
        {
            "owner_group", "child_owner_group", "parent_owner_group", "from_node_id", "to_node_id",
            "relation", "directed", "provenance", "confidence", "edge_id", "trace_logical_id",
            "parent_span_logical_id", "span_logical_id", "parent_semantic_anchor", "child_semantic_anchor",
            "parent_fingerprint", "child_fingerprint", "parent_source_id", "child_source_id",
            "parent_node_binding_revision", "child_node_binding_revision", "parent_owner_history_revision",
            "child_owner_history_revision", "parent_node_history_revision", "child_node_history_revision",
            "parent_event_time_nano", "child_event_time_nano", "first_seen", "last_seen",
            "parent_occurrence_ids", "child_occurrence_ids", "evidence_occurrence_ids",
            "parent_trace_expiry", "child_trace_expiry", "observed_expiry", "expires_nano",
            "fingerprint", "publication_seq", "ttl_at", "ttl_supported",
        };
        var conflictColumns = new[]
        {
            "semantic_anchor", "first_fingerprint", "conflicting_fingerprint",
            "candidate_context_json", "publication_seq",
        };
        var oldRowset = JsonSerializer.SerializeToUtf8Bytes(new
        {
            EdgeColumns = edgeColumns, Edges = Array.Empty<object[]>(),
            ConflictColumns = conflictColumns,
            Conflicts = new[] { new object[] { anchor, first, second, context, 0UL } },
        }, RawSignalCodec.Json);
        var rowsetHash = RawSignalEnvelope.Hash(oldRowset);
        var payloadHash = RawSignalEnvelope.Hash(Encoding.UTF8.GetBytes(payload));
        await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(key,
            (_, _) => throw new IOException("Reserved legacy pending publication."), Ct));
        Assert.Equal(key, await publisher.ReadPendingKeyAsync(Ct));
        await fixture.SqlAsync("INSERT INTO topology_projection_batches "
            + "(publication_key,payload_sha256,rowset_sha256,edge_count,conflict_count,payload_json) VALUES ('"
            + key + "','" + payloadHash + "','" + rowsetHash + "',0,1,'" + payload + "')");

        var restarted = new TopologyObservedProjector(fixture.Storage, publisher.PublishAsync,
            readPendingKey: publisher.ReadPendingKeyAsync);
        await restarted.ProjectAsync([], Ct);
        Assert.Null(await publisher.ReadPendingKeyAsync(Ct));
        Assert.Equal("1", (await fixture.SqlAsync("SELECT count() FROM topology_span_conflicts "
            + "WHERE semantic_anchor = '" + anchor + "'")).Trim());
        var stored = (await fixture.SqlAsync("SELECT candidate_context_json FROM topology_span_conflicts "
            + "WHERE semantic_anchor = '" + anchor + "' FORMAT TSVRaw")).Trim();
        Assert.Equal(context, stored);
        var watermark = (await new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage)).ReadAsync(Ct)).ClickHouseWatermark;
        Assert.False((await new TopologyObservedSnapshotReader(fixture.Storage)
            .CheckReadinessAsync(watermark, Ct)).Usable);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Directed_path_ignores_same_owner_weak_conflict_branch()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var owner = seed.ScopeA.OwnerGroups.Single();
        var registry = new TopologyRegistry(fixture.Factory);
        var x = (await registry.CreateAsync(seed.ScopeA, true,
            new(TopologyNodeKind.Service, "X", owner, true, []), Ct)).Node!.Id;
        var c = (await registry.CreateAsync(seed.ScopeA, true,
            new(TopologyNodeKind.Service, "C", owner, true, []), Ct)).Node!.Id;
        var alternate = (await registry.CreateAsync(seed.ScopeA, true,
            new(TopologyNodeKind.Service, "C-alt", owner, true, []), Ct)).Node!.Id;
        var edges = new TopologyEdgeRegistry(fixture.Factory);
        Assert.Equal(201, (await edges.CreateAsync(seed.ScopeA, true,
            new(x, seed.AService1, "depends_on"), Ct)).Status);
        Assert.Equal(201, (await edges.CreateAsync(seed.ScopeA, true,
            new(x, c, "depends_on"), Ct)).Status);
        var trace = Guid.NewGuid().ToString("N");
        var root = Span(Guid.NewGuid(), trace, "cccccccccccccccc", "", "conflict-root",
            seed.AParent.Owner.SourceId, owner, c, checked((ulong)(seed.ReadClock - 1_000_000_000m)));
        await seed.Writer.WriteAsync([root, Reenvelope(root) with
        { Topology = root.Topology! with { ServiceNodeId = alternate } }], Ct);
        var path = await seed.Query.GetTopologyPathAsync(
            new(seed.AService1, seed.AService2, seed.ReadClock), seed.ScopeA, Ct);
        Assert.Equal(TopologyGraphResultStatus.Found, path.Status);
        Assert.Single(path.EdgeIds);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Negative_binding_conflict_does_not_fail_unrelated_same_owner_proof()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var trace = Guid.NewGuid().ToString("N");
        var root = Span(Guid.NewGuid(), trace, "dddddddddddddddd", "", "negative-root",
            seed.AParent.Owner.SourceId, seed.ScopeA.OwnerGroups.Single(), seed.AService1,
            checked((ulong)(seed.ReadClock - 1_000_000_000m)));
        var negative = root with
        {
            Topology = root.Topology! with
            { ServiceNodeId = null, ServiceBindingRevision = null, NodeHistoryRevision = null, Reason = "Unresolved" },
        };
        await seed.Writer.WriteAsync([negative, Reenvelope(negative) with { Kind = "Server" }], Ct);
        var path = await seed.Query.GetTopologyPathAsync(
            new(seed.AService1, seed.AService2, seed.ReadClock), seed.ScopeA, Ct);
        Assert.Equal(TopologyGraphResultStatus.Found, path.Status);
        var rca = await new TopologyGraphPathProvider(seed.Query).GatherAsync(seed.Window, seed.ScopeA,
            GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Gathered, rca.Status);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Legacy_orphan_marker_requires_explicit_migration_but_declared_only_reads_work()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var publisher = Publisher(fixture);
        var anchor = new string('a', 64);
        await publisher.PublishAsync(new string('e', 64), async (sequence, _) =>
        {
            await fixture.SqlAsync("INSERT INTO topology_span_conflicts "
                + "(semantic_anchor,first_fingerprint,conflicting_fingerprint,publication_seq) VALUES ('"
                + anchor + "','" + new string('b', 64) + "','" + new string('c', 64) + "'," + sequence + ")");
        }, Ct);
        var reader = new TopologyObservedSnapshotReader(fixture.Storage);
        var watermark = (await new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage)).ReadAsync(Ct)).ClickHouseWatermark;
        var observed = await reader.ReadSnapshotAsync(watermark, Ct);
        Assert.Contains(observed.Conflicts, conflict => conflict.Anchor == anchor && conflict.Unattributed);
        var readiness = await reader.CheckReadinessAsync(watermark, Ct);
        Assert.False(readiness.Usable);
        Assert.Equal(1, readiness.UnattributedMarkers);
        Assert.Equal("1", (await fixture.SqlAsync("SELECT count() FROM topology_span_conflicts "
            + "WHERE candidate_context_json = ''")).Trim());
        await Assert.ThrowsAsync<TopologyObservedMigrationRequiredException>(() => seed.Query.SearchTopologyEdgesAsync(
            new(seed.ReadClock), seed.ScopeA, Ct));
        Assert.NotEmpty((await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock,
            Provenance: TopologyProvenance.Declared), seed.ScopeA, Ct)).Items);
        await Assert.ThrowsAsync<TopologyObservedMigrationRequiredException>(() => seed.Query.GetTopologyPathAsync(
            new(seed.AService1, seed.AService2, seed.ReadClock), seed.ScopeA, Ct));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Legacy_preexisting_edge_cannot_invent_other_fingerprint_context()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var anchor = TopologySpanCandidate.FromRecord(seed.AParent)!.Anchor;
        await Publisher(fixture).PublishAsync(new string('f', 64), async (sequence, _) =>
        {
            await fixture.SqlAsync("INSERT INTO topology_span_conflicts "
                + "(semantic_anchor,first_fingerprint,conflicting_fingerprint,publication_seq) VALUES ('"
                + anchor + "','" + new string('b', 64) + "','" + new string('c', 64) + "'," + sequence + ")");
        }, Ct);
        var reader = new TopologyObservedSnapshotReader(fixture.Storage);
        var watermark = (await new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage)).ReadAsync(Ct)).ClickHouseWatermark;
        var snapshot = await reader.ReadSnapshotAsync(watermark, Ct);
        Assert.Contains(snapshot.Rows, row => row.ParentSemanticAnchor == anchor && row.HasPublishedConflict);
        Assert.False((await reader.CheckReadinessAsync(watermark, Ct)).Usable);
        await Assert.ThrowsAsync<TopologyObservedMigrationRequiredException>(() => seed.Query.SearchTopologyEdgesAsync(
            new(seed.ReadClock), seed.ScopeB, Ct)); // unknown alternate owner cannot be guessed
        Assert.NotEmpty((await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock,
            Provenance: TopologyProvenance.Declared), seed.ScopeA, Ct)).Items);
        await Assert.ThrowsAsync<InvalidDataException>(() => seed.Writer.WriteAsync(
            [Reenvelope(seed.AParent) with
            { Topology = seed.AParent.Topology! with { ServiceNodeId = seed.AService2 } }], Ct));
        Assert.False((await reader.CheckReadinessAsync(watermark, Ct)).Usable);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Separate_HTTP_occurrences_keep_semantic_proof_id_with_more_detail_references()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var firstPage = await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock,
            Provenance: TopologyProvenance.Observed), seed.ScopeA, Ct);
        var edgeId = Assert.Single(firstPage.Items).Id;
        var firstDetail = await seed.Query.GetTopologyEdgeAsync(edgeId, seed.ReadClock, seed.ScopeA, Ct);
        Assert.NotNull(firstDetail);
        Assert.Equal(2, firstDetail.Evidence.Count);
        var firstPath = await seed.Query.GetTopologyPathAsync(new(seed.AService1, seed.AService2, seed.ReadClock),
            seed.ScopeA, Ct);
        var firstRca = await new TopologyGraphPathProvider(seed.Query).GatherAsync(seed.Window, seed.ScopeA,
            GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Gathered, firstRca.Status);

        await seed.Writer.WriteAsync([Reenvelope(seed.AParent), Reenvelope(seed.AChild)], Ct);
        var secondPage = await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock,
            Provenance: TopologyProvenance.Observed), seed.ScopeA, Ct);
        Assert.Equal(edgeId, Assert.Single(secondPage.Items).Id);
        var secondDetail = await seed.Query.GetTopologyEdgeAsync(edgeId, seed.ReadClock, seed.ScopeA, Ct);
        Assert.NotNull(secondDetail);
        Assert.Equal(4, secondDetail.Evidence.Count); // raw archives remain separately addressable
        Assert.Equal(firstPath.EdgeIds, (await seed.Query.GetTopologyPathAsync(
            new(seed.AService1, seed.AService2, seed.ReadClock), seed.ScopeA, Ct)).EdgeIds);
        var secondRca = await new TopologyGraphPathProvider(seed.Query).GatherAsync(seed.Window, seed.ScopeA,
            GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Gathered, secondRca.Status);
        var before = Assert.Single(firstRca.Items);
        var after = Assert.Single(secondRca.Items);
        Assert.Equal(before.Id, after.Id); // semantic proof hash excludes raw envelope identity
        using var beforeProof = JsonDocument.Parse(before.Payload["proof_edges"]);
        using var afterProof = JsonDocument.Parse(after.Payload["proof_edges"]);
        Assert.Equal(beforeProof.RootElement.GetArrayLength(), afterProof.RootElement.GetArrayLength());
        Assert.Equal(3, afterProof.RootElement.GetArrayLength());
    }

    private sealed record Seed(IScopedQuery Query, TelemetryWriter Writer, AccessScope ScopeA, AccessScope ScopeB,
        string AService1, string AService2, string BService1, string BService2, string BAlternate,
        string BSource, TelemetryRecord AParent, TelemetryRecord AChild, TelemetryRecord BParent,
        decimal ReadClock, RcaWindow Window, Microsoft.Extensions.Time.Testing.FakeTimeProvider QueryClock);

    private static async Task<Seed> SeedAsync(TelemetryDbFixture fixture)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var ownerA = "conflict-a-" + suffix; var ownerB = "conflict-b-" + suffix;
        var sourceA1 = "conflict-a1-" + suffix; var sourceA2 = "conflict-a2-" + suffix;
        var sourceB = "conflict-b-" + suffix;
        var scopeA = AccessScope.ForGroups("conflict-A-" + suffix, [ownerA]);
        var scopeB = AccessScope.ForGroups("conflict-B-" + suffix, [ownerB]);
        await fixture.SourceAsync(sourceA1, ownerA);
        await fixture.SourceAsync(sourceA2, ownerA);
        await fixture.SourceAsync(sourceB, ownerB);
        await using var db = await fixture.Factory.CreateDbContextAsync(Ct);
        var sourceNodes = await db.TopologyNodes.Where(node => node.SourceId == sourceA1 || node.SourceId == sourceA2)
            .ToDictionaryAsync(node => node.SourceId!, Ct);
        if (string.CompareOrdinal(sourceNodes[sourceA1].Id, sourceNodes[sourceA2].Id) > 0)
            (sourceA1, sourceA2) = (sourceA2, sourceA1);
        var registry = new TopologyRegistry(fixture.Factory);
        var serviceA1 = (await registry.CreateAsync(scopeA, true,
            new(TopologyNodeKind.Service, "A1", ownerA, true, []), Ct)).Node!.Id;
        var serviceA2 = (await registry.CreateAsync(scopeA, true,
            new(TopologyNodeKind.Service, "A2", ownerA, true, []), Ct)).Node!.Id;
        var serviceB1 = (await registry.CreateAsync(scopeB, true,
            new(TopologyNodeKind.Service, "B1", ownerB, true, []), Ct)).Node!.Id;
        var serviceB2 = (await registry.CreateAsync(scopeB, true,
            new(TopologyNodeKind.Service, "B2", ownerB, true, []), Ct)).Node!.Id;
        var alternate = (await registry.CreateAsync(scopeB, true,
            new(TopologyNodeKind.Service, "B-alt", ownerB, true, []), Ct)).Node!.Id;
        var edgeRegistry = new TopologyEdgeRegistry(fixture.Factory);
        Assert.Equal(201, (await edgeRegistry.CreateAsync(scopeA, true,
            new(sourceNodes[sourceA1].Id, serviceA1, "depends_on"), Ct)).Status);
        Assert.Equal(201, (await edgeRegistry.CreateAsync(scopeA, true,
            new(serviceA2, sourceNodes[sourceA2].Id, "depends_on"), Ct)).Status);

        var publisher = Publisher(fixture);
        var writer = new TelemetryWriter(fixture.Storage, fixture.Owners,
            new TopologyObservedProjector(fixture.Storage, publisher.PublishAsync,
                readPendingKey: publisher.ReadPendingKeyAsync));
        var start = fixture.Now;
        var traceA = Guid.NewGuid().ToString("N"); var traceB = Guid.NewGuid().ToString("N");
        var aParent = Span(Guid.NewGuid(), traceA, "1111111111111111", "", "a-parent",
            sourceA1, ownerA, serviceA1, start);
        var aChild = Span(Guid.NewGuid(), traceA, "2222222222222222", "1111111111111111", "a-child",
            sourceA2, ownerA, serviceA2, start + 1000);
        var bParent = Span(Guid.NewGuid(), traceB, "3333333333333333", "", "b-parent",
            sourceB, ownerB, serviceB1, start);
        var bChild = Span(Guid.NewGuid(), traceB, "4444444444444444", "3333333333333333", "b-child",
            sourceB, ownerB, serviceB2, start + 1000);
        await writer.WriteAsync([aParent, aChild, bParent, bChild], Ct);

        var now = fixture.Clock.GetUtcNow();
        await new EventWriter(fixture.Storage).WriteEventsAsync(
            [Degraded(ownerA, sourceA1, now.AddMinutes(1)), Degraded(ownerA, sourceA2, now.AddMinutes(2))], Ct);
        var fence = new TopologyPublicationFence(new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage)));
        var source = new TopologyGraphSnapshotSource(fixture.Factory,
            new TopologyObservedSnapshotReader(fixture.Storage), fence);
        var graph = new TopologyGraphQueryService(source);
        var queryClock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now.AddMinutes(15));
        var query = new ScopedQuery(new(fixture.Storage), new(fixture.Storage), new(fixture.Storage),
            new(fixture.Storage), fixture.Db, new ControlPlaneAuditSink(fixture.Factory), fixture.Reader, graph,
            queryClock, sourceTargetPages: new TopologySourceTargetPageReader(fixture.Factory, fence));
        var window = new RcaWindow
        {
            BaselineFrom = now.AddDays(-7), BaselineTo = now.AddMinutes(-1),
            From = now, To = now.AddMinutes(15), OwnerGroups = [ownerA],
        };
        return new(query, writer, scopeA, scopeB, serviceA1, serviceA2, serviceB1, serviceB2,
            alternate, sourceB, aParent, aChild, bParent, TopologyIdentity.Nano(window.To), window,
            queryClock);
    }

    private static TopologyPublicationCoordinator Publisher(TelemetryDbFixture fixture)
    {
        var watermark = new TopologyPublicationWatermarkReader(fixture.Storage);
        return new(fixture.Factory, watermark, new TopologyPublicationWatermarkWriter(watermark, fixture.Storage));
    }

    private sealed class StopAfterObservedInsert : ITopologyProjectionCheckpoints
    {
        public Task ReachAsync(string stage, CancellationToken cancellationToken)
        {
            Assert.Equal("after-observed-db-before-publish", stage);
            throw new IOException("Simulated crash before PG publication commit.");
        }
    }

    private static TelemetryRecord Reenvelope(TelemetryRecord record)
    {
        var id = Guid.NewGuid();
        return record with { EnvelopeId = id, LogicalId = id.ToString("N") + "/" + record.Owner.LeafKey };
    }

    private static TelemetryRecord Span(Guid envelope, string trace, string span, string parent, string leaf,
        string source, string owner, string node, ulong start)
    {
        var raw = JsonDocument.Parse("{\"traceId\":\"" + trace + "\",\"spanId\":\"" + span
            + "\",\"parentSpanId\":\"" + parent + "\",\"startTimeUnixNano\":\"" + start
            + "\",\"endTimeUnixNano\":\"" + (start + 1) + "\",\"name\":\"op\",\"kind\":2}").RootElement.Clone();
        var binding = new TelemetryOwnerBinding(leaf, source, owner, 7, start, "known");
        var topology = new TopologyLeafBinding(leaf, start, source, owner, 7,
            node, null, 5, null, 9, "display", "Resolved");
        return new(1, envelope, envelope.ToString("N") + "/" + leaf, TelemetrySignal.Traces,
            new string('a', 64), new string('b', 64), binding, start, "op", "svc", "Client", "", 0, false,
            trace, span, 1, new string('c', 64), JsonDocument.Parse("{}").RootElement.Clone(),
            JsonDocument.Parse("{}").RootElement.Clone(), "", "", null, raw)
        { Topology = topology, TopologyBindingsSha256 = new string('d', 64) };
    }

    private static LogEvent Degraded(string owner, string source, DateTimeOffset at) => new()
    {
        EventId = Guid.NewGuid(), Timestamp = at, OwnerGroup = owner, SourceId = source, Host = source,
        Vendor = "conflict", Product = "test", ParserId = "conflict", ParserVersion = "1.0.0",
        ParseStatus = ParseStatus.Ok, SignatureHash = 1, TimeSource = TimeSources.Parsed,
        SeverityNum = 3, SrcIp = IPAddress.IPv6Any, DstIp = IPAddress.IPv6Any,
        Attrs = new Dictionary<string, string>(StringComparer.Ordinal), Body = "degraded",
    };
}
