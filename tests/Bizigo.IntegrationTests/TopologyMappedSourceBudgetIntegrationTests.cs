using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

public sealed partial class TopologyMappedSourceGroupOracleIntegrationTests
{
    /// <summary>
    /// R08 real PG/CH counterpart to the in-memory budget matrix. Source A has
    /// two raw Contains chains (root→A1 and root→A2), with A1→I1 as a third
    /// visited edge. Source B has root→B1/B2. The admitted typed I1→B1 proof
    /// uses only one candidate pair, but every authorized raw mapping chain
    /// and every cross-pair orientation must still consume the shared budget.
    /// </summary>
    [Fact]
    public async Task R08_Real_mapped_service_instance_raw_chains_charge_exact_node_edge_page_budget()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(Stack, Ct);
        var seed = await SeedAsync(fixture, admitInstance: true, projectDependency: true);
        var fence = new TopologyPublicationFence(new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage),
            new TopologyObservedRepairReadiness(fixture.Factory, fixture.Storage)));
        await using (var db = await fixture.Factory.CreateDbContextAsync(Ct))
        {
            var roots = await db.TopologyNodes.AsNoTracking()
                .Where(node => node.SourceId == seed.SourceA || node.SourceId == seed.SourceB)
                .ToDictionaryAsync(node => node.SourceId!, Ct);
            var rawA = await db.TopologyDeclaredEdgeHistory.AsNoTracking()
                .Where(edge => edge.FromNodeId == roots[seed.SourceA].Id && edge.Relation == "contains"
                    && edge.ToNano == null).Select(edge => edge.ToNodeId).ToArrayAsync(Ct);
            var rawB = await db.TopologyDeclaredEdgeHistory.AsNoTracking()
                .Where(edge => edge.FromNodeId == roots[seed.SourceB].Id && edge.Relation == "contains"
                    && edge.ToNano == null).Select(edge => edge.ToNodeId).ToArrayAsync(Ct);
            Assert.Equal(new[] { seed.A1, seed.A2 }.Order(StringComparer.Ordinal),
                rawA.Order(StringComparer.Ordinal));
            Assert.Equal(new[] { seed.B1, seed.B2 }.Order(StringComparer.Ordinal),
                rawB.Order(StringComparer.Ordinal));
            Assert.Equal(seed.I1, Assert.Single(await db.TopologyDeclaredEdgeHistory.AsNoTracking()
                .Where(edge => edge.FromNodeId == seed.A1 && edge.Relation == "contains"
                    && edge.ToNano == null).Select(edge => edge.ToNodeId).ToArrayAsync(Ct)));
        }

        var before = await AuditCountsAsync(fixture, seed.Scope.Subject);
        var baseline = await new TopologyGraphPathProvider(seed.Query, MappedGraphBudget, fence).GatherAsync(
            seed.Window, seed.Scope, GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Gathered, baseline.Status);
        var item = Assert.Single(baseline.Items);
        using (var proof = JsonDocument.Parse(item.Payload["proof_edges"]))
        {
            var edge = Assert.Single(proof.RootElement.EnumerateArray().ToArray());
            Assert.Equal(seed.I1, edge.GetProperty("from_node").GetString());
            Assert.Equal(seed.B1, edge.GetProperty("to_node").GetString());
            Assert.Equal("observed", edge.GetProperty("provenance").GetString());
            Assert.Equal("depends_on", edge.GetProperty("relation").GetString());
        }
        AssertWitnesses(item.Payload["source_witnesses"],
            new(seed.SourceA, seed.I1, [seed.ContainsA1, seed.ContainsI1], [], []),
            new(seed.SourceB, seed.B1, [seed.ContainsB1], [], []));
        var after = await AuditCountsAsync(fixture, seed.Scope.Subject);
        Assert.Equal(1, after.Mapping - before.Mapping); // seven chunks, one real PG page
        Assert.Equal(24, after.Path - before.Path); // (A root+A1+A2+I1)×(B root+B1+B2)×2
        Assert.Equal(1, after.Detail - before.Detail); // only I1→B1 is a proof

        // Exact budget: seven distinct Source/Service/Instance nodes, five
        // visited Contains IDs plus the observed dependency, and 1+24+1 pages.
        // A smaller limit must be incomplete; the exact limit and +1 pass.
        foreach (var (dimension, exact) in new[] { ("node", 7), ("edge", 6), ("page", 26) })
        foreach (var delta in new[] { -1, 0, 1 })
        {
            var budget = new TopologyProviderBudget(
                dimension == "node" ? exact + delta : 1000,
                dimension == "edge" ? exact + delta : 4000,
                dimension == "page" ? exact + delta : 100,
                1024 * 1024);
            var result = await new TopologyGraphPathProvider(seed.Query, budget, fence).GatherAsync(
                seed.Window, seed.Scope, GatherBudget.Default, Ct);
            Assert.Equal(delta < 0 ? EvidenceStatus.Unavailable : EvidenceStatus.Gathered, result.Status);
            Assert.Equal(delta < 0, result.Truncated);
            Assert.Equal(delta < 0 ? "NotComparable" : "Evaluated", result.Telemetry!.Evaluation);
            if (delta < 0) Assert.Empty(result.Items);
            else Assert.Equal(item.Id, Assert.Single(result.Items).Id);
        }
    }

    [Fact]
    public async Task R08_Real_grouped_ancestor_mapped_witness_charges_exact_node_edge_page_budget()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(Stack, Ct);
        var seed = await SeedAsync(fixture, admitInstance: true, projectDependency: true);
        var fence = new TopologyPublicationFence(new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage),
            new TopologyObservedRepairReadiness(fixture.Factory, fixture.Storage)));
        var ancestor = await NodeAsync(fixture, seed.Scope, "budget-strict-ancestor");
        var toInstance = await EdgeAsync(fixture, seed.Scope, ancestor, seed.I1);
        var toService = await EdgeAsync(fixture, seed.Scope, ancestor, seed.B1);
        await using (var db = await fixture.Factory.CreateDbContextAsync(Ct))
        {
            var cutoff = TopologyIdentity.Nano(seed.Window.To) - 1m;
            var declaredIds = new[] { Guid.Parse(toInstance), Guid.Parse(toService) };
            Assert.Equal(2, await db.TopologyDeclaredEdgeHistory.AsNoTracking().CountAsync(edge =>
                declaredIds.Contains(edge.EdgeId) && edge.FromNano <= cutoff
                && (edge.ToNano == null || cutoff < edge.ToNano), Ct));
        }
        var before = await AuditCountsAsync(fixture, seed.Scope.Subject);
        var baseline = await new TopologyCommonAncestorProvider(seed.Query, MappedGraphBudget, fence)
            .GatherAsync(seed.Window, seed.Scope, GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Gathered, baseline.Status);
        var item = Assert.Single(baseline.Items);
        Assert.Equal(ancestor, item.Payload["ancestor_node_id"]);
        using (var proof = JsonDocument.Parse(item.Payload["proof_edges"]))
        {
            var ids = proof.RootElement.EnumerateArray()
                .Select(edge => edge.GetProperty("id").GetString()!).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { toInstance, toService }.Order(StringComparer.Ordinal), ids);
            Assert.All(proof.RootElement.EnumerateArray().ToArray(), edge =>
                Assert.Equal("declared", edge.GetProperty("provenance").GetString()));
        }
        AssertWitnesses(item.Payload["source_witnesses"],
            new(seed.SourceA, seed.I1, [seed.ContainsA1, seed.ContainsI1],
                [ancestor, seed.I1], [toInstance]),
            new(seed.SourceB, seed.B1, [seed.ContainsB1],
                [ancestor, seed.B1], [toService]));
        var after = await AuditCountsAsync(fixture, seed.Scope.Subject);
        Assert.Equal(1, after.Mapping - before.Mapping);
        Assert.Equal(1, after.Ancestor - before.Ancestor);
        Assert.Equal(2, after.Detail - before.Detail);

        // Mapping charged seven nodes/five Contains edges. The selected strict
        // witness adds ancestor X and two declared dependency edges; the
        // observed I1→B1 edge exists but is not a chosen proof edge.
        foreach (var (dimension, exact) in new[] { ("node", 8), ("edge", 7), ("page", 4) })
        foreach (var delta in new[] { -1, 0, 1 })
        {
            var budget = new TopologyProviderBudget(
                dimension == "node" ? exact + delta : 1000,
                dimension == "edge" ? exact + delta : 4000,
                dimension == "page" ? exact + delta : 100,
                1024 * 1024);
            var result = await new TopologyCommonAncestorProvider(seed.Query, budget, fence).GatherAsync(
                seed.Window, seed.Scope, GatherBudget.Default, Ct);
            Assert.Equal(delta < 0 ? EvidenceStatus.Unavailable : EvidenceStatus.Gathered, result.Status);
            Assert.Equal(delta < 0, result.Truncated);
            Assert.Equal(delta < 0 ? "NotComparable" : "Evaluated", result.Telemetry!.Evaluation);
            if (delta < 0) Assert.Empty(result.Items);
            else Assert.Equal(item.Id, Assert.Single(result.Items).Id);
        }
    }

    private static async Task<(int Mapping, int Path, int Ancestor, int Detail)> AuditCountsAsync(
        TelemetryDbFixture fixture, string subject)
    {
        await using var db = await fixture.Factory.CreateDbContextAsync(Ct);
        var actions = await db.AuditLog.AsNoTracking().Where(row => row.Subject == subject)
            .Select(row => row.Action).ToArrayAsync(Ct);
        return (actions.Count(action => action == "topology.source-targets"),
            actions.Count(action => action == "topology.path"),
            actions.Count(action => action == "topology.ancestors"),
            actions.Count(action => action == "topology.edges.detail"));
    }
}
