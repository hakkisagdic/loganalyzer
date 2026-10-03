using System.Text.Json;
using Bizigo.Contracts;

namespace Bizigo.UnitTests;

public sealed class TopologyVisibilityTests
{
    private static readonly TopologyVisibilityNode A = new("node-A", "A", "visible-A");
    private static readonly TopologyVisibilityNode B = new("secret-node-B", "B", "secret-name-B");
    private static readonly TopologyVisibilityEdge BToA = new("secret-edge", B.Id, A.Id, "B", "A",
        "secret-trace", "secret-span");

    [Fact]
    public void Hidden_endpoint_only_contributes_an_aggregate_external_neighbour()
    {
        var result = TopologyVisibility.Project(AccessScope.ForGroups("reader-A", ["A"]), [A, B], [BToA]);

        var node = Assert.Single(result.Nodes);
        Assert.Equal(A.Id, node.Id);
        Assert.Equal(1, node.ExternalNeighborCount);
        Assert.Empty(result.Edges);

        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(B.Id, json, StringComparison.Ordinal);
        Assert.DoesNotContain(B.DisplayName, json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-trace", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-span", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-edge", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Cross_owner_edge_requires_both_endpoint_permissions()
    {
        foreach (var groups in new[] { new[] { "A" }, new[] { "B" }, new[] { "C" } })
            Assert.Empty(TopologyVisibility.Project(AccessScope.ForGroups("reader", groups), [A, B], [BToA]).Edges);

        var allowed = TopologyVisibility.Project(AccessScope.ForGroups("reader-AB", ["A", "B"]), [A, B], [BToA]);
        Assert.Equal(2, allowed.Nodes.Count);
        Assert.Same(BToA, Assert.Single(allowed.Edges));
        Assert.All(allowed.Nodes, node => Assert.Equal(0, node.ExternalNeighborCount));
    }

    [Fact]
    public void External_neighbour_count_is_distinct_and_reveals_no_edge_multiplicity()
    {
        var duplicateProof = BToA with { Id = "other-secret-edge", TraceId = "other-secret-trace" };
        var result = TopologyVisibility.Project(AccessScope.ForGroups("reader-A", ["A"]), [A, B], [BToA, duplicateProof]);

        Assert.Equal(1, Assert.Single(result.Nodes).ExternalNeighborCount);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void Conflicting_endpoint_owner_snapshot_fails_closed()
    {
        var corrupt = BToA with { FromOwnerGroup = "A" };
        Assert.Throws<InvalidDataException>(() =>
            TopologyVisibility.Project(AccessScope.ForGroups("reader-A", ["A"]), [A, B], [corrupt]));
    }

    [Fact]
    public void Unassigned_nodes_require_explicit_system_scope()
    {
        var unassigned = new TopologyVisibilityNode("unassigned-node", OwnerGroups.Unassigned, "unknown");
        Assert.Empty(TopologyVisibility.Project(
            AccessScope.ForGroups("ordinary", [OwnerGroups.Unassigned]), [unassigned], []).Nodes);
        Assert.Single(TopologyVisibility.Project(AccessScope.System("system"), [unassigned], []).Nodes);
    }
}
