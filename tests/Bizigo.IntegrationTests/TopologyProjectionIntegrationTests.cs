using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.IntegrationTests;

/// <summary>Planner-only real CH storage assertions; no in-memory edge store.</summary>
[Collection(DevStackCollection.Name)]
public sealed class TopologyProjectionIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Trace = "00112233445566778899aabbccddeeff";
    private const string ParentSpan = "0011223344556677";
    private const string ChildSpan = "8899aabbccddeeff";
    private static readonly string ParentNode = TopologyIdentity.Node(TopologyNodeKind.Service,
        Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly string ChildNode = TopologyIdentity.Node(TopologyNodeKind.Service,
        Guid.Parse("55555555-5555-5555-5555-555555555555"));

    [Fact, Trait("Category", "Integration")]
    public async Task Parent_direction_not_links_and_out_of_order_durable_pairing()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var published = new Publication();
        var projector = new TopologyObservedProjector(f.Storage, published.PublishAsync);
        var writer = new TelemetryWriter(f.Storage, f.Owners, projector);
        var parent = Span(Guid.NewGuid(), "parent", ParentSpan, string.Empty, ParentNode, f.Now);
        var child = Span(Guid.NewGuid(), "child", ChildSpan, ParentSpan, ChildNode, f.Now + 1000,
            "\"links\":[{\"traceId\":\"" + Trace + "\",\"spanId\":\"deadbeefdeadbeef\"}]");
        await writer.WriteAsync([child], Ct);
        Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM topology_edges_observed")).Trim());
        var unresolvedReader = new TopologyObservedSnapshotReader(f.Storage);
        var missing = await unresolvedReader.ReadSnapshotAsync(1, Ct);
        Assert.Equal("MissingParent", Assert.Single(missing.ParentResolutions).Reason);
        await writer.WriteAsync([parent], Ct);
        var edge = Assert.Single(TopologyObservation.Reduce([parent, child]).Edges);
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_edges_observed FINAL")).Trim());
        Assert.Equal(ParentNode + "\t" + ChildNode + "\t" + edge.EdgeId,
            (await f.SqlAsync("SELECT from_node_id,to_node_id,edge_id FROM topology_edges_observed FINAL FORMAT TSV")).Trim());
        Assert.Equal(2, published.Keys.Count);
        var resolved = await unresolvedReader.ReadSnapshotAsync(2, Ct);
        Assert.Equal("Resolved", Assert.Single(resolved.ParentResolutions).Reason);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Scoped_observed_reader_reduces_committed_version_before_window_and_owner_filter()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var published = new Publication();
        var writer = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, published.PublishAsync));
        var parent = Span(Guid.NewGuid(), "parent", ParentSpan, string.Empty, ParentNode, f.Now);
        var child = Span(Guid.NewGuid(), "child", ChildSpan, ParentSpan, ChildNode, f.Now + 1000);
        await writer.WriteAsync([parent, child], Ct);
        var observed = Assert.Single(TopologyObservation.Reduce([parent, child]).Edges);
        var scope = new TopologyObservedReadScope(AccessScope.ForGroups("scope-A", ["A"]),
            f.Now, (decimal)f.Now + 2000, (decimal)f.Now + 2000, 64);
        var reader = new TopologyObservedSnapshotReader(f.Storage);
        Assert.Single((await reader.ReadScopedSnapshotAsync(1, scope, Ct)).Rows);

        // The non-TTL authority must select the latest committed version before
        // applying the event window. The synthetic later version is deliberately
        // ledger-only: a filtered-out version needs no physical proof hydration.
        await InsertLifecycleVersionAsync(f, observed.EdgeId, "A", "A", f.Now + 101000, 2);
        Assert.Empty((await reader.ReadScopedSnapshotAsync(2, scope, Ct)).Rows);

        // An unpublished later owner transfer cannot suppress the committed
        // view at watermark 1, even when it has the same edge identity.
        await InsertLifecycleVersionAsync(f, observed.EdgeId, "B", "B", f.Now + 1000, 3);
        Assert.Single((await reader.ReadScopedSnapshotAsync(1, scope, Ct)).Rows);
        Assert.Empty((await reader.ReadScopedSnapshotAsync(3, scope, Ct)).Rows);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Scoped_reader_global_readiness_detects_unrelated_incomplete_v3_context()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var anchor = new string('a', 64);
        var candidate = new TopologyConflictCandidate(new string('b', 64), "B", "source-B",
            null, f.Now - 10_000, (decimal)f.Now + 100_000, (decimal)f.Now + 100_000,
            string.Empty, true)
        {
            ContextVersion = 3,
            Anchor = anchor,
            ResolutionReason = string.Empty,
        };
        var context = JsonSerializer.Serialize(new[] { candidate }, RawSignalCodec.Json);
        var row = JsonSerializer.Serialize(new
        {
            semantic_anchor = anchor,
            first_fingerprint = new string('b', 64),
            conflicting_fingerprint = new string('c', 64),
            candidate_context_json = context,
            publication_seq = 1,
        });
        await f.SqlAsync("INSERT INTO topology_span_conflicts FORMAT JSONEachRow\n" + row);
        var reader = new TopologyObservedSnapshotReader(f.Storage);
        var scope = new TopologyObservedReadScope(AccessScope.ForGroups("scope-A", ["A"]),
            f.Now, (decimal)f.Now + 1000, (decimal)f.Now + 1000, 64);
        var observed = await reader.ReadScopedSnapshotAsync(1, scope, Ct);
        Assert.Empty(observed.Rows);
        Assert.True(Assert.Single(observed.Conflicts).Unattributed);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Scoped_candidate_cap_plus_one_fails_before_a_partial_graph_can_be_used()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var writer = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, new Publication().PublishAsync));
        var parent = Span(Guid.NewGuid(), "parent", ParentSpan, string.Empty, ParentNode, f.Now);
        var child1 = Span(Guid.NewGuid(), "child1", ChildSpan, ParentSpan, ChildNode, f.Now + 1000);
        var child2 = Span(Guid.NewGuid(), "child2", "fedcba9876543210", ParentSpan,
            ChildNode, f.Now + 1500);
        await writer.WriteAsync([parent, child1, child2], Ct);
        var scope = new TopologyObservedReadScope(AccessScope.ForGroups("scope-A", ["A"]),
            f.Now, (decimal)f.Now + 2000, (decimal)f.Now + 2000, 2);
        var plans = new List<TopologySqlPlan>();
        var reader = new TopologyObservedSnapshotReader(f.Storage) { ObserveQuery = plans.Add };
        Assert.Equal(2, (await reader.ReadScopedSnapshotAsync(1, scope, Ct)).Rows.Count);
        await Assert.ThrowsAsync<IOException>(() => reader.ReadScopedSnapshotAsync(1,
            scope with { MaxCandidates = 1 }, Ct));
        Assert.NotEmpty(plans);
        Assert.All(plans, plan =>
        {
            Assert.Contains("max_rows_to_read = 131072", plan.Sql, StringComparison.Ordinal);
            Assert.Contains("read_overflow_mode = 'throw'", plan.Sql, StringComparison.Ordinal);
        });
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Scoped_production_candidate_SQL_excludes_other_owner_and_event_window()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var published = new Publication();
        var writer = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, published.PublishAsync));
        var currentParent = Span(Guid.NewGuid(), "a-parent", ParentSpan, string.Empty, ParentNode, f.Now);
        var currentChild = Span(Guid.NewGuid(), "a-child", ChildSpan, ParentSpan, ChildNode, f.Now + 1000);
        await writer.WriteAsync([currentParent, currentChild], Ct);

        var otherTrace = Guid.NewGuid().ToString("N");
        var otherParent = Span(Guid.NewGuid(), "b-parent", ParentSpan, string.Empty,
            TopologyIdentity.Node(TopologyNodeKind.Service, Guid.NewGuid()), f.Now,
            trace: otherTrace, ownerGroup: "B", sourceId: "SB");
        var otherChild = Span(Guid.NewGuid(), "b-child", ChildSpan, ParentSpan,
            TopologyIdentity.Node(TopologyNodeKind.Service, Guid.NewGuid()), f.Now + 1000,
            trace: otherTrace, ownerGroup: "B", sourceId: "SB");
        await writer.WriteAsync([otherParent, otherChild], Ct);

        var oldTrace = Guid.NewGuid().ToString("N");
        var oldStart = f.Now - 2 * (ulong)TopologyExpiry.NanosecondsPerDay;
        var oldParent = Span(Guid.NewGuid(), "old-parent", ParentSpan, string.Empty,
            ParentNode, oldStart, trace: oldTrace) with { RetentionDays = 90, ObservedRetentionDays = 90 };
        var oldChild = Span(Guid.NewGuid(), "old-child", ChildSpan, ParentSpan,
            ChildNode, oldStart + 1000, trace: oldTrace) with
            { RetentionDays = 90, ObservedRetentionDays = 90 };
        await writer.WriteAsync([oldParent, oldChild], Ct);

        var plans = new List<TopologySqlPlan>();
        var reader = new TopologyObservedSnapshotReader(f.Storage) { ObserveQuery = plans.Add };
        var scope = new TopologyObservedReadScope(AccessScope.ForGroups("scope-A", ["A"]),
            f.Now, (decimal)f.Now + 2000, (decimal)f.Now + 2000, 64);
        Assert.Equal(3, published.Keys.Count);
        var snapshot = await reader.ReadScopedSnapshotAsync(published.Sequences.Last(), scope, Ct);
        Assert.Equal("3", (await f.SqlAsync("SELECT count() FROM topology_edges_observed")).Trim());
        Assert.Equal(TopologyObservation.Reduce([currentParent, currentChild]).Edges.Single().EdgeId,
            Assert.Single(snapshot.Rows).Id);
        var candidates = Assert.Single(plans, static plan => plan.Route == "observed-candidates");
        Assert.Contains("last_seen >= {window_from:Decimal(21,0)}", candidates.Sql,
            StringComparison.Ordinal);
        Assert.Contains("last_seen < {window_to:Decimal(21,0)}", candidates.Sql,
            StringComparison.Ordinal);
        Assert.Contains("parent_owner_group IN ({scope_groups:Array(String)})", candidates.Sql,
            StringComparison.Ordinal);
        var reduced = Assert.Single(plans, static plan => plan.Route == "observed-edges");
        var lifecycle = Assert.Single(plans, static plan => plan.Route == "observed-lifecycle");
        Assert.Contains("FROM topology_edge_lifecycle", lifecycle.Sql, StringComparison.Ordinal);
        Assert.Contains("edge_id IN ({candidate_ids:Array(String)})", lifecycle.Sql,
            StringComparison.Ordinal);
        Assert.Contains("publication_seq <= {watermark:UInt64}", lifecycle.Sql,
            StringComparison.Ordinal);
        Assert.Contains("physical_row_sha256", reduced.Sql, StringComparison.Ordinal);
        Assert.Contains("edge_id IN ({candidate_ids:Array(String)})", reduced.Sql,
            StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Pending_same_key_occurrence_merge_preserves_committed_observed_proof()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var writer = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, new Publication().PublishAsync));
        var parent = Span(Guid.NewGuid(), "parent", ParentSpan, string.Empty, ParentNode, f.Now);
        var child = Span(Guid.NewGuid(), "child", ChildSpan, ParentSpan, ChildNode, f.Now + 1000);
        await writer.WriteAsync([parent, child], Ct);
        var scope = new TopologyObservedReadScope(AccessScope.ForGroups("scope-A", ["A"]),
            f.Now, (decimal)f.Now + 2000, (decimal)f.Now + 2000, 64);
        var reader = new TopologyObservedSnapshotReader(f.Storage);
        var committed = Assert.Single((await reader.ReadScopedSnapshotAsync(1, scope, Ct)).Rows);

        // A retry has inserted a cumulative occurrence vector but PG has not
        // acknowledged publication 2. The CH engine must retain version 1
        // even after an explicit physical merge at the same sorting key.
        await f.SqlAsync("INSERT INTO topology_edges_observed "
            + "SELECT * REPLACE (arrayConcat(parent_occurrence_ids, ['pending-parent']) "
            + "AS parent_occurrence_ids, arrayConcat(child_occurrence_ids, ['pending-child']) "
            + "AS child_occurrence_ids, arrayConcat(evidence_occurrence_ids, "
            + "['pending-parent','pending-child']) AS evidence_occurrence_ids, "
            + "2 AS publication_seq) FROM topology_edges_observed WHERE publication_seq = 1");
        await f.SqlAsync("OPTIMIZE TABLE topology_edges_observed FINAL");
        var stillCommitted = Assert.Single((await reader.ReadScopedSnapshotAsync(1, scope, Ct)).Rows);
        Assert.Equal(committed.Id, stillCommitted.Id);
        Assert.Equal(1UL, stillCommitted.PublicationSequence);
        Assert.Equal(committed.EvidenceOccurrenceIds, stillCommitted.EvidenceOccurrenceIds);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Pending_same_anchor_conflict_merge_preserves_committed_marker()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var anchor = new string('a', 64);
        var first = new TopologyConflictCandidate(new string('b', 64), "A", "SA", ParentNode,
            f.Now + 1000, (decimal)f.Now + 100_000, (decimal)f.Now + 100_000,
            string.Empty, true)
        {
            ContextVersion = 3, Anchor = anchor, ResolutionReason = "Resolved",
        };
        async Task InsertAsync(TopologyConflictCandidate candidate, ulong sequence)
        {
            var row = JsonSerializer.Serialize(new
            {
                semantic_anchor = anchor,
                first_fingerprint = candidate.Fingerprint,
                conflicting_fingerprint = new string('c', 64),
                candidate_context_json = JsonSerializer.Serialize(new[] { candidate }, RawSignalCodec.Json),
                publication_seq = sequence,
            });
            await f.SqlAsync("INSERT INTO topology_span_conflicts FORMAT JSONEachRow\n" + row);
        }
        await InsertAsync(first, 1);
        var scope = new TopologyObservedReadScope(AccessScope.ForGroups("scope-A", ["A"]),
            f.Now, (decimal)f.Now + 2000, (decimal)f.Now + 2000, 64);
        var reader = new TopologyObservedSnapshotReader(f.Storage);
        Assert.Equal(first.Fingerprint,
            Assert.Single(Assert.Single((await reader.ReadScopedSnapshotAsync(1, scope, Ct)).Conflicts).Candidates).Fingerprint);

        // A pending replacement of the same semantic anchor must not erase
        // the committed context before the publication watermark advances.
        await InsertAsync(first with { Fingerprint = new string('d', 64) }, 2);
        await f.SqlAsync("OPTIMIZE TABLE topology_span_conflicts FINAL");
        var committed = Assert.Single((await reader.ReadScopedSnapshotAsync(1, scope, Ct)).Conflicts);
        Assert.Equal(first.Fingerprint, Assert.Single(committed.Candidates).Fingerprint);
        Assert.False(committed.Unattributed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task Same_sequence_divergent_conflict_marker_fails_after_merge(bool changeContext)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var anchor = new string('a', 64);
        var candidate = new TopologyConflictCandidate(new string('b', 64), "A", "SA", ParentNode,
            f.Now + 1000, (decimal)f.Now + 100_000, (decimal)f.Now + 100_000,
            string.Empty, true)
        {
            ContextVersion = 3, Anchor = anchor, ResolutionReason = "Resolved",
        };
        var context = JsonSerializer.Serialize(new[] { candidate }, RawSignalCodec.Json);
        async Task InsertAsync(string secondFingerprint, string capturedContext)
        {
            var row = JsonSerializer.Serialize(new
            {
                semantic_anchor = anchor,
                first_fingerprint = candidate.Fingerprint,
                conflicting_fingerprint = secondFingerprint,
                candidate_context_json = capturedContext,
                publication_seq = 1,
            });
            await f.SqlAsync("INSERT INTO topology_span_conflicts FORMAT JSONEachRow\n" + row);
        }

        await InsertAsync(new string('c', 64), context);
        await InsertAsync(new string('c', 64), context);
        await f.SqlAsync("OPTIMIZE TABLE topology_span_conflicts FINAL");
        Assert.Equal("2", (await f.SqlAsync("SELECT count() FROM topology_span_conflicts "
            + "WHERE semantic_anchor = '" + anchor + "' AND publication_seq = 1")).Trim());
        var reader = new TopologyObservedSnapshotReader(f.Storage);
        Assert.Single(Assert.Single((await reader.ReadSnapshotAsync(1, Ct)).Conflicts).Candidates);

        // Same anchor and sequence but different bytes are not an arbitrary
        // ReplacingMergeTree winner. Both fingerprint and context matter.
        await InsertAsync(changeContext ? new string('c', 64) : new string('d', 64),
            changeContext ? JsonSerializer.Serialize(new[] { candidate with
            { ResolutionReason = "AmbiguousParent" } }, RawSignalCodec.Json) : context);
        await f.SqlAsync("OPTIMIZE TABLE topology_span_conflicts FINAL");
        Assert.Equal("3", (await f.SqlAsync("SELECT count() FROM topology_span_conflicts "
            + "WHERE semantic_anchor = '" + anchor + "' AND publication_seq = 1")).Trim());
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(() =>
            reader.ReadSnapshotAsync(1, Ct));
        var scope = new TopologyObservedReadScope(AccessScope.ForGroups("scope-A", ["A"]),
            f.Now, (decimal)f.Now + 2000, (decimal)f.Now + 2000, 64);
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(() =>
            reader.ReadScopedSnapshotAsync(1, scope, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task Same_sequence_divergent_parent_decision_fails_without_mixing_reason_and_context(
        bool changeContext)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var anchor = new string('a', 64);
        var fingerprint = new string('b', 64);
        var decision = new TopologyParentResolution(anchor, fingerprint, new string('c', 64),
            "MissingParent", "A", "SA", ChildNode, f.Now + 1000, (decimal)f.Now + 100_000);
        var context = JsonSerializer.Serialize(decision, RawSignalCodec.Json);
        async Task InsertAsync(string reason, string capturedContext, ulong sequence = 1)
        {
            var row = JsonSerializer.Serialize(new
            {
                child_anchor = anchor,
                child_fingerprint = fingerprint,
                reason,
                captured_context_json = capturedContext,
                publication_seq = sequence,
            });
            await f.SqlAsync("INSERT INTO topology_parent_resolution FORMAT JSONEachRow\n" + row);
        }

        await InsertAsync("MissingParent", context);
        await InsertAsync("MissingParent", context);
        await f.SqlAsync("OPTIMIZE TABLE topology_parent_resolution FINAL");
        Assert.Equal("2", (await f.SqlAsync("SELECT count() FROM topology_parent_resolution "
            + "WHERE child_anchor = '" + anchor + "' AND publication_seq = 1")).Trim());
        var reader = new TopologyObservedSnapshotReader(f.Storage);
        Assert.Equal("MissingParent", Assert.Single((await reader.ReadSnapshotAsync(1, Ct))
            .ParentResolutions).Reason);
        var scope = new TopologyObservedReadScope(AccessScope.ForGroups("scope-A", ["A"]),
            f.Now, (decimal)f.Now + 2000, (decimal)f.Now + 2000, 64);
        Assert.Equal("MissingParent", Assert.Single((await reader.ReadScopedSnapshotAsync(1, scope, Ct))
            .ParentResolutions).Reason);
        await Assert.ThrowsAsync<IOException>(() => reader.ReadScopedSnapshotAsync(1,
            scope with { MaxCandidates = 1 }, Ct)); // raw rows count before duplicate reduction

        await InsertAsync("Resolved", JsonSerializer.Serialize(decision with { Reason = "Resolved" },
            RawSignalCodec.Json), 2);
        await f.SqlAsync("OPTIMIZE TABLE topology_parent_resolution FINAL");
        Assert.Equal("MissingParent", Assert.Single((await reader.ReadSnapshotAsync(1, Ct))
            .ParentResolutions).Reason); // pending seq2 cannot rewrite watermark1
        Assert.Equal("Resolved", Assert.Single((await reader.ReadSnapshotAsync(2, Ct))
            .ParentResolutions).Reason);

        await InsertAsync(changeContext ? "MissingParent" : "Resolved",
            changeContext ? JsonSerializer.Serialize(decision with { SourceId = "other-source" },
                RawSignalCodec.Json) : context);
        await f.SqlAsync("OPTIMIZE TABLE topology_parent_resolution FINAL");
        Assert.Equal("3", (await f.SqlAsync("SELECT count() FROM topology_parent_resolution "
            + "WHERE child_anchor = '" + anchor + "' AND publication_seq = 1")).Trim());
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(() =>
            reader.ReadSnapshotAsync(1, Ct));
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(() =>
            reader.ReadScopedSnapshotAsync(1, scope, Ct));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Expired_latest_version_cannot_resurrect_older_observed_proof_after_cleanup()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var publisher = new Publication();
        var writer = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, publisher.PublishAsync));
        var parent = Span(Guid.NewGuid(), "parent", ParentSpan, string.Empty, ParentNode, f.Now);
        var child = Span(Guid.NewGuid(), "child", ChildSpan, ParentSpan, ChildNode, f.Now + 1000);
        await writer.WriteAsync([parent, child], Ct);
        var reader = new TopologyObservedSnapshotReader(f.Storage);
        var windowFrom = (decimal)f.Now;
        var windowTo = windowFrom + 2000;
        var first = Assert.Single((await reader.ReadScopedSnapshotAsync(1,
            new(AccessScope.ForGroups("scope-A", ["A"]), windowFrom, windowTo,
                windowTo, 64), Ct)).Rows);

        // Same semantic anchors, new raw occurrences and a shorter captured
        // retention create committed seq2 with the same EdgeId but expiry10d.
        await writer.WriteAsync([
            Reenvelope(parent) with { RetentionDays = 10, ObservedRetentionDays = 10 },
            Reenvelope(child) with { RetentionDays = 10, ObservedRetentionDays = 10 },
        ], Ct);
        Assert.Equal(2, publisher.Sequences.Count);
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_edges_observed WHERE edge_id = '"
            + first.Id + "' AND publication_seq = 1")).Trim());
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_edges_observed WHERE edge_id = '"
            + first.Id + "' AND publication_seq = 2")).Trim());
        var expiryClock = (decimal)child.TimeUnixNano + 10 * TopologyExpiry.NanosecondsPerDay + 1;
        Assert.True(expiryClock < first.ExpiresUnixNano);
        var atExpiredLatest = new TopologyObservedReadScope(AccessScope.ForGroups("scope-A", ["A"]),
            windowFrom, windowTo, expiryClock, 64);
        Assert.Empty((await reader.ReadScopedSnapshotAsync(2, atExpiredLatest, Ct)).Rows);

        // Simulate completed physical TTL cleanup of seq2 while seq1's 90-day
        // row remains. The non-TTL lifecycle authority must still suppress it.
        await f.SqlAsync("ALTER TABLE topology_edges_observed DELETE WHERE edge_id = '"
            + first.Id + "' AND publication_seq = 2 SETTINGS mutations_sync = 2");
        await f.SqlAsync("OPTIMIZE TABLE topology_edges_observed FINAL");
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_edges_observed WHERE edge_id = '"
            + first.Id + "' AND publication_seq = 1")).Trim());
        Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM topology_edges_observed WHERE edge_id = '"
            + first.Id + "' AND publication_seq = 2")).Trim());
        Assert.Empty((await reader.ReadScopedSnapshotAsync(2, atExpiredLatest, Ct)).Rows);
        Assert.Empty((await reader.ReadSnapshotAsync(2, expiryClock, Ct)).Rows);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Missing_unexpired_latest_physical_row_fails_instead_of_using_old_proof()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var publisher = new Publication();
        var writer = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, publisher.PublishAsync));
        var parent = Span(Guid.NewGuid(), "parent", ParentSpan, string.Empty, ParentNode, f.Now);
        var child = Span(Guid.NewGuid(), "child", ChildSpan, ParentSpan, ChildNode, f.Now + 1000);
        await writer.WriteAsync([parent, child], Ct);
        await writer.WriteAsync([Reenvelope(parent), Reenvelope(child)], Ct);
        Assert.Equal(2, publisher.Sequences.Count);
        var scope = new TopologyObservedReadScope(AccessScope.ForGroups("scope-A", ["A"]),
            f.Now, (decimal)f.Now + 2000, (decimal)f.Now + 2000, 64);
        var reader = new TopologyObservedSnapshotReader(f.Storage);
        var latest = Assert.Single((await reader.ReadScopedSnapshotAsync(2, scope, Ct)).Rows);
        Assert.Equal(2UL, latest.PublicationSequence);
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_edges_observed WHERE edge_id = '"
            + latest.Id + "' AND publication_seq = 1")).Trim());

        // The lifecycle says seq2 is still unexpired. If its canonical row is
        // absent, returning seq1 (or Empty) would falsely complete the graph.
        await f.SqlAsync("ALTER TABLE topology_edges_observed DELETE WHERE edge_id = '"
            + latest.Id + "' AND publication_seq = 2 SETTINGS mutations_sync = 2");
        await f.SqlAsync("OPTIMIZE TABLE topology_edges_observed FINAL");
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_edges_observed WHERE edge_id = '"
            + latest.Id + "' AND publication_seq = 1")).Trim());
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(() =>
            reader.ReadScopedSnapshotAsync(2, scope, Ct));
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(() =>
            reader.ReadSnapshotAsync(2, scope.ExpiryReadClockUnixNano, Ct));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Missing_all_physical_candidates_for_unexpired_lifecycle_fails_instead_of_empty()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var publisher = new Publication();
        var writer = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, publisher.PublishAsync));
        var parent = Span(Guid.NewGuid(), "parent", ParentSpan, string.Empty, ParentNode, f.Now);
        var child = Span(Guid.NewGuid(), "child", ChildSpan, ParentSpan, ChildNode, f.Now + 1000);
        await writer.WriteAsync([parent, child], Ct);
        Assert.Single(publisher.Sequences);

        var scope = new TopologyObservedReadScope(AccessScope.ForGroups("scope-A", ["A"]),
            f.Now, (decimal)f.Now + 2000, (decimal)f.Now + 2000, 64);
        var reader = new TopologyObservedSnapshotReader(f.Storage);
        var proof = Assert.Single((await reader.ReadScopedSnapshotAsync(1, scope, Ct)).Rows);
        Assert.True(scope.ExpiryReadClockUnixNano < proof.ExpiresUnixNano);
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_edges_observed WHERE edge_id = '"
            + proof.Id + "'")).Trim());

        // The committed lifecycle still describes a live edge, but physical
        // candidate discovery has no edge row at all. Empty/zero is unsafe.
        await f.SqlAsync("ALTER TABLE topology_edges_observed DELETE WHERE edge_id = '"
            + proof.Id + "' SETTINGS mutations_sync = 2");
        await f.SqlAsync("OPTIMIZE TABLE topology_edges_observed FINAL");
        Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM topology_edges_observed WHERE edge_id = '"
            + proof.Id + "'")).Trim());
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(() =>
            reader.ReadScopedSnapshotAsync(1, scope, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task Same_sequence_lifecycle_duplicate_is_idempotent_but_divergence_unavailable(
        bool changeDigest)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var writer = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, new Publication().PublishAsync));
        var parent = Span(Guid.NewGuid(), "parent", ParentSpan, string.Empty, ParentNode, f.Now);
        var child = Span(Guid.NewGuid(), "child", ChildSpan, ParentSpan, ChildNode, f.Now + 1000);
        await writer.WriteAsync([parent, child], Ct);
        var scope = new TopologyObservedReadScope(AccessScope.ForGroups("scope-A", ["A"]),
            f.Now, (decimal)f.Now + 2000, (decimal)f.Now + 2000, 64);
        var reader = new TopologyObservedSnapshotReader(f.Storage);
        var proof = Assert.Single((await reader.ReadScopedSnapshotAsync(1, scope, Ct)).Rows);

        await f.SqlAsync("INSERT INTO topology_edge_lifecycle SELECT * FROM "
            + "topology_edge_lifecycle WHERE publication_seq = 1");
        await f.SqlAsync("OPTIMIZE TABLE topology_edge_lifecycle FINAL");
        Assert.Equal("2", (await f.SqlAsync("SELECT count() FROM topology_edge_lifecycle "
            + "WHERE edge_id = '" + proof.Id + "' AND publication_seq = 1")).Trim());
        Assert.Equal(proof.Id, Assert.Single((await reader.ReadScopedSnapshotAsync(1, scope, Ct)).Rows).Id);

        var replacement = changeDigest
            ? "'" + new string('e', 64) + "' AS physical_row_sha256"
            : "expires_nano + 1 AS expires_nano";
        await f.SqlAsync("INSERT INTO topology_edge_lifecycle SELECT * REPLACE (" + replacement
            + ") FROM topology_edge_lifecycle WHERE edge_id = '" + proof.Id
            + "' AND publication_seq = 1 LIMIT 1");
        await f.SqlAsync("OPTIMIZE TABLE topology_edge_lifecycle FINAL");
        Assert.Equal("3", (await f.SqlAsync("SELECT count() FROM topology_edge_lifecycle "
            + "WHERE edge_id = '" + proof.Id + "' AND publication_seq = 1")).Trim());
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(() =>
            reader.ReadScopedSnapshotAsync(1, scope, Ct));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Live_lifecycle_requires_exact_canonical_physical_digest()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var writer = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, new Publication().PublishAsync));
        var parent = Span(Guid.NewGuid(), "parent", ParentSpan, string.Empty, ParentNode, f.Now);
        var child = Span(Guid.NewGuid(), "child", ChildSpan, ParentSpan, ChildNode, f.Now + 1000);
        await writer.WriteAsync([parent, child], Ct);
        var scope = new TopologyObservedReadScope(AccessScope.ForGroups("scope-A", ["A"]),
            f.Now, (decimal)f.Now + 2000, (decimal)f.Now + 2000, 64);
        var reader = new TopologyObservedSnapshotReader(f.Storage);
        var proof = Assert.Single((await reader.ReadScopedSnapshotAsync(1, scope, Ct)).Rows);

        // An attested ledger row does not make a corrupted physical proof valid.
        // This mutates only the derived hash, not the immutable v4 manifest.
        await f.SqlAsync("ALTER TABLE topology_edges_observed UPDATE physical_row_sha256 = '"
            + new string('e', 64) + "' WHERE edge_id = '" + proof.Id
            + "' SETTINGS mutations_sync = 2");
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(() =>
            reader.ReadScopedSnapshotAsync(1, scope, Ct));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Unresolved_and_query_failure()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var published = new Publication();
        var projector = new TopologyObservedProjector(f.Storage, published.PublishAsync);
        var writer = new TelemetryWriter(f.Storage, f.Owners, projector);
        var child = Span(Guid.NewGuid(), "missing", ChildSpan, ParentSpan, ChildNode, f.Now);
        await writer.WriteAsync([child], Ct);
        var reader = new TopologyObservedSnapshotReader(f.Storage);
        var first = await reader.ReadSnapshotAsync(1, Ct);
        Assert.Equal("MissingParent", Assert.Single(first.ParentResolutions).Reason);
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_parent_resolution")).Trim());

        var failed = new TopologyObservedProjector(f.Storage, published.PublishAsync,
            readPendingKey: _ => throw new TimeoutException("simulated storage timeout"));
        await Assert.ThrowsAsync<TimeoutException>(() => failed.ProjectAsync([child], Ct));
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_parent_resolution")).Trim());

        var parent = Span(Guid.NewGuid(), "later", ParentSpan, string.Empty, ParentNode, f.Now - 1000);
        await writer.WriteAsync([parent], Ct);
        var final = await reader.ReadSnapshotAsync(2, Ct);
        Assert.Equal("Resolved", Assert.Single(final.ParentResolutions).Reason);
        Assert.Single(final.Rows);

        var alternate = Reenvelope(parent) with
        { Topology = parent.Topology! with { ServiceNodeId = ChildNode } };
        await writer.WriteAsync([alternate], Ct);
        var ambiguous = await reader.ReadSnapshotAsync(3, Ct);
        Assert.Equal("AmbiguousParent", Assert.Single(ambiguous.ParentResolutions).Reason);
        Assert.Single(ambiguous.Conflicts);
        Assert.True(Assert.Single(ambiguous.Rows).HasPublishedConflict);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Occurrence_retry_conflict_matrix()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var published = new Publication();
        var writer = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, published.PublishAsync));
        var parent = Span(Guid.NewGuid(), "p", ParentSpan, string.Empty, ParentNode, f.Now);
        var child = Span(Guid.NewGuid(), "c", ChildSpan, ParentSpan, ChildNode, f.Now + 1000);
        await writer.WriteAsync([parent, child], Ct);
        var first = Assert.Single(TopologyObservation.Reduce([parent, child]).Edges);
        await writer.WriteAsync([parent, child], Ct); // same envelope: one raw logical occurrence
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_edges_observed FINAL")).Trim());
        var againParent = Reenvelope(parent);
        var againChild = Reenvelope(child);
        await writer.WriteAsync([againParent, againChild], Ct);
        // The E0014 sort key retains each publication sequence. Two physical
        // versions still describe one semantic edge/proof identity.
        Assert.Equal("2", (await f.SqlAsync("SELECT count() FROM topology_edges_observed FINAL")).Trim());
        Assert.Equal(first.EdgeId,
            (await f.SqlAsync("SELECT DISTINCT edge_id FROM topology_edges_observed FINAL FORMAT TSV")).Trim());
        Assert.Equal(2, published.Keys.Distinct(StringComparer.Ordinal).Count());
        var scope = new TopologyObservedReadScope(AccessScope.ForGroups("scope-A", ["A"]),
            f.Now, (decimal)f.Now + 2000, (decimal)f.Now + 2000, 64);
        var publicProof = Assert.Single((await new TopologyObservedSnapshotReader(f.Storage)
            .ReadScopedSnapshotAsync(2, scope, Ct)).Rows);
        Assert.Equal(first.EdgeId, publicProof.Id);
        Assert.Equal(2UL, publicProof.PublicationSequence);
        var conflict = Reenvelope(parent) with { Topology = parent.Topology! with { ServiceNodeId = ChildNode } };
        await writer.WriteAsync([conflict], Ct);
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_span_conflicts FINAL")).Trim());
        Assert.Equal("2", (await f.SqlAsync("SELECT count() FROM topology_edges_observed FINAL")).Trim());
        // Physical old edge remains for audit; the committed conflict marker
        // makes the public snapshot reject that anchor before any scope filter.
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Projector_crash_restart_reuses_publication_key_and_sequence()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var published = new Publication();
        var checkpoint = new FailOnceCheckpoint();
        var parent = Span(Guid.NewGuid(), "p", ParentSpan, string.Empty, ParentNode, f.Now);
        var child = Span(Guid.NewGuid(), "c", ChildSpan, ParentSpan, ChildNode, f.Now + 1000);
        var writer = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, published.PublishAsync, checkpoint, published.ReadPendingKeyAsync));
        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync([parent, child], Ct));
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_edges_observed")).Trim());
        Assert.Empty(published.Keys); // insert occurred, but no publication ACK
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_projection_batches")).Trim());
        // New raw HTTP occurrence arrives while A is pending. Recovery must
        // finish the frozen A manifest before publishing cumulative A+B.
        var laterChild = Reenvelope(child);
        var recovered = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, published.PublishAsync,
                readPendingKey: published.ReadPendingKeyAsync));
        await recovered.WriteAsync([laterChild], Ct);
        Assert.Equal("2", (await f.SqlAsync("SELECT count() FROM topology_edges_observed FINAL")).Trim());
        Assert.Equal("1", (await f.SqlAsync("SELECT count(DISTINCT edge_id) "
            + "FROM topology_edges_observed FINAL")).Trim());
        Assert.Equal(2, published.Keys.Count);
        Assert.Equal(1UL, published.Sequences[0]);
        Assert.Equal(1UL, published.Sequences[1]); // callback retried under the same pending key
        Assert.Equal(2UL, published.Sequences[2]); // then the new cumulative batch
        Assert.Equal(published.AttemptedKeys[0], published.AttemptedKeys[1]);
        Assert.NotEqual(published.AttemptedKeys[1], published.AttemptedKeys[2]);
        Assert.Equal("2", (await f.SqlAsync("SELECT count(DISTINCT publication_key) FROM topology_projection_batches")).Trim());
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Pending_manifest_missing_fails_closed_before_current_batch()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var published = new Publication();
        var writer = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, published.PublishAsync,
                readPendingKey: _ => Task.FromResult<string?>(new string('e', 64))));
        var parent = Span(Guid.NewGuid(), "p", ParentSpan, string.Empty, ParentNode, f.Now);
        var child = Span(Guid.NewGuid(), "c", ChildSpan, ParentSpan, ChildNode, f.Now + 1000);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync([parent, child], Ct));
        Assert.Empty(published.AttemptedKeys);
        Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM topology_edges_observed")).Trim());
    }

    private static Task InsertLifecycleVersionAsync(TelemetryDbFixture fixture, string edgeId,
        string childOwner, string parentOwner, ulong lastSeen, ulong sequence) =>
        fixture.SqlAsync("INSERT INTO topology_edge_lifecycle FORMAT JSONEachRow\n"
            + JsonSerializer.Serialize(new
            {
                child_owner_group = childOwner,
                parent_owner_group = parentOwner,
                from_node_id = ParentNode,
                to_node_id = ChildNode,
                first_seen = fixture.Now,
                last_seen = lastSeen,
                parent_event_time_nano = fixture.Now,
                child_event_time_nano = fixture.Now + 1000,
                edge_id = edgeId,
                publication_seq = sequence,
                expires_nano = (decimal)fixture.Now + 7_776_000_000_000_000m,
                physical_row_sha256 = new string('0', 64),
            }, RawSignalCodec.Json));

    private static TelemetryRecord Reenvelope(TelemetryRecord record)
    {
        var id = Guid.NewGuid();
        return record with { EnvelopeId = id, LogicalId = id.ToString("N") + "/" + record.Owner.LeafKey };
    }

    private static TelemetryRecord Span(Guid envelopeId, string leaf, string spanId, string parentSpanId,
        string nodeId, ulong start, string? extraSpan = null, string trace = Trace,
        string ownerGroup = "A", string sourceId = "SA")
    {
        var json = "{\"traceId\":\"" + trace + "\",\"spanId\":\"" + spanId
            + "\",\"parentSpanId\":\"" + parentSpanId + "\",\"startTimeUnixNano\":\""
            + start.ToString(CultureInfo.InvariantCulture) + "\",\"endTimeUnixNano\":\""
            + (start + 1).ToString(CultureInfo.InvariantCulture) + "\",\"name\":\"op\",\"kind\":2"
            + (extraSpan is null ? string.Empty : "," + extraSpan) + "}";
        var owner = new TelemetryOwnerBinding(leaf, sourceId, ownerGroup, 7, start, "known");
        var topology = new TopologyLeafBinding(leaf, start, sourceId, ownerGroup, 7,
            nodeId, null, 5, null, 9, "display", "Resolved");
        return new(1, envelopeId, envelopeId.ToString("N") + "/" + leaf, TelemetrySignal.Traces,
            new string('a', 64), new string('b', 64), owner, start, "op", "svc", "Client", "", 0, false,
            trace, spanId, 1, new string('c', 64), Json("{}"), Json("{}"), "", "", null, Json(json))
        {
            Topology = topology, TopologyBindingsSha256 = new string('d', 64),
        };
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private sealed class FailOnceCheckpoint : ITopologyProjectionCheckpoints
    {
        private bool fail = true;
        public Task ReachAsync(string stage, CancellationToken cancellationToken)
        {
            Assert.Equal("after-observed-db-before-publish", stage);
            if (fail) { fail = false; throw new IOException("Simulated projector crash after CH insert."); }
            return Task.CompletedTask;
        }
    }

    private sealed class Publication
    {
        private string? pendingKey;
        private ulong pendingSequence;
        private ulong lastSequence;
        private readonly Dictionary<string, ulong> receipts = new(StringComparer.Ordinal);
        public List<string> AttemptedKeys { get; } = [];
        public List<ulong> Sequences { get; } = [];
        public List<string> Keys { get; } = [];
        public Task<string?> ReadPendingKeyAsync(CancellationToken token) => Task.FromResult(pendingKey);
        public async Task<ulong> PublishAsync(string key, Func<ulong, CancellationToken, Task> write,
            CancellationToken token)
        {
            if (receipts.TryGetValue(key, out var committed))
            {
                AttemptedKeys.Add(key); Sequences.Add(committed);
                return committed; // real PG coordinator reuses ACK, never re-inserts
            }
            if (pendingKey is null) { pendingKey = key; pendingSequence = lastSequence + 1; }
            else if (pendingKey != key) throw new InvalidOperationException("Different key cannot reuse pending sequence.");
            AttemptedKeys.Add(key); Sequences.Add(pendingSequence);
            await write(pendingSequence, token);
            lastSequence = pendingSequence; pendingKey = null; Keys.Add(key);
            receipts.Add(key, lastSequence);
            return lastSequence;
        }
    }
}
