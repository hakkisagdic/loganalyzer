using Bizigo.Contracts;
using Bizigo.ControlPlane;

namespace Bizigo.UnitTests;

public sealed partial class TopologyIdentityTests
{
    private static readonly Guid Uuid = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Fact]
    public void Type_qualified_ids_do_not_merge_names()
    {
        var ids = Enum.GetValues<TopologyNodeKind>().Select(k => TopologyIdentity.Node(k, Uuid)).ToArray();
        Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (var kind in Enum.GetValues<TopologyNodeKind>())
            Assert.Equal(kind, TopologyIdentity.Kind(TopologyIdentity.Node(kind, Uuid)));
    }

    [Theory]
    [InlineData("service:00000000000000000000000000000001")]
    [InlineData("SERVICE:00000000-0000-0000-0000-000000000001")]
    [InlineData("unknown:00000000-0000-0000-0000-000000000001")]
    [InlineData("source:00000000-0000-0000-0000-000000000000")]
    public void Invalid_ids_are_rejected(string id) => Assert.Throws<ArgumentException>(() => TopologyIdentity.Kind(id));

    [Theory]
    [InlineData("A", false)]
    [InlineData("B", false)]
    [InlineData("A,B", true)]
    [InlineData("C", false)]
    public void Dual_endpoint_predicate(string groups, bool expected) =>
        Assert.Equal(expected, TopologyIdentity.CanReadEdge(AccessScope.ForGroups("reader", groups.Split(',')), "A", "B"));

    private static TopologyBindingRequest Request(string source = "SA", string owner = "A", string? instance = null, ulong time = 100) =>
        new(new("leaf", source, owner, 1, time, "known"), "n", "checkout", instance);

    internal static HistoricalTopologyBindings.Row Row(string source, string owner, Guid service, string instance = "",
        Guid? target = null, long revision = 1, decimal from = 0, decimal? to = null) => new(new()
        {
            Revision = revision, BindingId = Guid.NewGuid(), SourceId = source, ServiceNamespace = "n", ServiceName = "checkout",
            ServiceNodeId = TopologyIdentity.Node(TopologyNodeKind.Service, service), InstanceId = instance,
            TargetNodeId = TopologyIdentity.Node(instance == "" ? TopologyNodeKind.Service : TopologyNodeKind.ServiceInstance, target ?? service),
            FromNano = from, ToNano = to,
        }, new()
        {
            Revision = revision, NodeId = TopologyIdentity.Node(instance == "" ? TopologyNodeKind.Service : TopologyNodeKind.ServiceInstance, target ?? service),
            DisplayName = "same name", OwnerGroup = owner, Enabled = true, NodeVersion = 1, FromNano = from, ToNano = to,
        });

    [Fact]
    public void Binding_keys_preserve_sources_and_instances()
    {
        var sb = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var i1 = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var i2 = Guid.Parse("00000000-0000-0000-0000-000000000004");
        var rows = new[] { Row("SA", "A", Uuid), Row("SB", "B", sb),
            Row("SA", "A", Uuid, "i1", i1, 3), Row("SA", "A", Uuid, "i2", i2, 4) };
        Assert.Equal(TopologyIdentity.Node(TopologyNodeKind.Service, Uuid), HistoricalTopologyBindings.Resolve(Request(), rows).NodeId);
        Assert.Equal(TopologyIdentity.Node(TopologyNodeKind.Service, sb), HistoricalTopologyBindings.Resolve(Request("SB", "B"), rows).NodeId);
        Assert.Equal(TopologyIdentity.Node(TopologyNodeKind.ServiceInstance, i1), HistoricalTopologyBindings.Resolve(Request(instance: "i1"), rows).NodeId);
        Assert.Equal(TopologyIdentity.Node(TopologyNodeKind.ServiceInstance, i2), HistoricalTopologyBindings.Resolve(Request(instance: "i2"), rows).NodeId);
    }

    [Fact]
    public void Missing_ambiguous_spoofed_instance_never_falls_back()
    {
        var row = Row("SA", "A", Uuid);
        Assert.Equal("MissingBinding", HistoricalTopologyBindings.Resolve(Request(), []).Reason);
        Assert.Equal("AmbiguousBinding", HistoricalTopologyBindings.Resolve(Request(), [row, Row("SA", "A", Guid.NewGuid(), revision: 2)]).Reason);
        Assert.Equal("BindingOwnerMismatch", HistoricalTopologyBindings.Resolve(Request(owner: "B"), [row]).Reason);
        Assert.Equal("MissingInstanceBinding", HistoricalTopologyBindings.Resolve(Request(instance: "foreign"), [row]).Reason);
        Assert.Null(HistoricalTopologyBindings.Resolve(Request(instance: "foreign"), [row]).NodeId);
        Assert.Equal("SourceUnresolved", HistoricalTopologyBindings.Resolve(Request(owner: OwnerGroups.Unassigned), [row]).Reason);
    }

    [Theory]
    [InlineData(99UL, "A")]
    [InlineData(100UL, "B")]
    [InlineData(101UL, "B")]
    public void Half_open_history_uses_event_time(ulong time, string owner)
    {
        var rows = new[] { Row("SA", "A", Uuid, from: 0, to: 100), Row("SA", "B", Uuid, revision: 2, from: 100) };
        var result = HistoricalTopologyBindings.Resolve(Request(owner: owner, time: time), rows);
        Assert.True(result.Resolved); Assert.Equal(owner, result.OwnerGroup);
        Assert.Equal(owner == "A" ? 1L : 2L, result.NodeHistoryRevision);
    }

    [Fact]
    public void Late_binding_does_not_resolve_an_earlier_event()
    {
        var row = Row("SA", "A", Uuid, from: 101);
        Assert.Equal("MissingBinding", HistoricalTopologyBindings.Resolve(Request(time: 100), [row]).Reason);
        Assert.True(HistoricalTopologyBindings.Resolve(Request(time: 101), [row]).Resolved);
    }

    [Fact]
    public void Instance_lookup_uses_service_identity_across_authoritative_alias_rename()
    {
        var service = Row("SA", "A", Uuid);
        service.Binding.ServiceName = "new-name";
        var instance = Row("SA", "A", Uuid, "i1", Guid.NewGuid(), 2);
        var result = HistoricalTopologyBindings.Resolve(Request(instance: "i1") with { ServiceName = "new-name" }, [service, instance]);
        Assert.Equal(instance.Binding.TargetNodeId, result.NodeId);
    }
}
