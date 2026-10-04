using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.UnitTests;

public sealed class TopologySourceTargetPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly AccessScope ScopeA = AccessScope.ForGroups("mapping-A", ["A"]);

    [Fact]
    public async Task Full_target_pages_need_separate_terminal_and_preserve_service_before_instance()
    {
        var factory = new MemoryFactory();
        await SeedAsync(factory, includeService: true, includeInstance: true);
        var revision = new MutableRevision();
        var reader = new TopologySourceTargetPageReader(factory, new TopologyPublicationFence(revision));

        var first = await reader.ReadPageAsync(["source"], 1000, ScopeA, 1, null, Ct);
        var service = Assert.Single(first.Items);
        Assert.Equal("service", service.Target?.NodeId);
        Assert.Equal("00000000-0000-0000-0000-000000000001",
            Assert.Single(service.Target!.MappingEdgeIds));
        Assert.Null(service.FinalStatus);
        Assert.NotNull(first.Cursor);

        var second = await reader.ReadPageAsync(["source"], 1000, ScopeA, 1, first.Cursor, Ct);
        var instance = Assert.Single(second.Items);
        Assert.Equal("instance", instance.Target?.NodeId);
        Assert.Equal(2, instance.Target?.MappingEdgeIds.Count);
        Assert.Null(instance.FinalStatus);
        Assert.NotNull(second.Cursor);

        var terminal = await reader.ReadPageAsync(["source"], 1000, ScopeA, 1, second.Cursor, Ct);
        var complete = Assert.Single(terminal.Items);
        Assert.Null(complete.Target);
        Assert.Equal(TopologySourceTargetStatus.Complete, complete.FinalStatus);
        Assert.Null(terminal.Cursor);
    }

    [Fact]
    public async Task Root_only_is_complete_but_hidden_contains_is_not_false_complete()
    {
        var roots = new MemoryFactory();
        await SeedAsync(roots, includeService: false, includeInstance: false);
        var rootReader = new TopologySourceTargetPageReader(roots,
            new TopologyPublicationFence(new MutableRevision()));
        var root = await rootReader.ReadPageAsync(["source"], 1000, ScopeA, 1, null, Ct);
        Assert.Equal(TopologySourceTargetStatus.Complete, Assert.Single(root.Items).FinalStatus);
        Assert.Null(root.Cursor);

        var hiddenFactory = new MemoryFactory();
        await SeedAsync(hiddenFactory, includeService: true, includeInstance: false, serviceOwner: "B");
        var hiddenReader = new TopologySourceTargetPageReader(hiddenFactory,
            new TopologyPublicationFence(new MutableRevision()));
        var hidden = await hiddenReader.ReadPageAsync(["source"], 1000, ScopeA, 1, null, Ct);
        var item = Assert.Single(hidden.Items);
        Assert.Equal(TopologySourceTargetStatus.Hidden, item.FinalStatus);
        Assert.Null(item.Target);
        Assert.Equal("root", item.SourceNodeId);
    }

    [Fact]
    public async Task Cursor_binds_scope_asof_and_both_publication_revisions()
    {
        var factory = new MemoryFactory();
        await SeedAsync(factory, includeService: true, includeInstance: false);
        var revision = new MutableRevision();
        var reader = new TopologySourceTargetPageReader(factory, new TopologyPublicationFence(revision));
        var first = await reader.ReadPageAsync(["source"], 1000, ScopeA, 1, null, Ct);
        Assert.NotNull(first.Cursor);
        await Assert.ThrowsAsync<TopologyCursorException>(() => reader.ReadPageAsync(["source"],
            1001, ScopeA, 1, first.Cursor, Ct));
        await Assert.ThrowsAsync<TopologyCursorException>(() => reader.ReadPageAsync(["source"],
            1000, AccessScope.ForGroups("mapping-B", ["B"]), 1, first.Cursor, Ct));
        revision.Current = new(2, 3) { RepairStamp = new(1, "mapping-test-certificate") };
        await Assert.ThrowsAsync<TopologyRestartRequiredException>(() => reader.ReadPageAsync(["source"],
            1000, ScopeA, 1, first.Cursor, Ct));
    }

    [Fact]
    public async Task Raw_cap_after_provisional_target_terminates_truncated_not_complete()
    {
        var factory = new MemoryFactory();
        await SeedAsync(factory, includeService: true, includeInstance: true);
        var reader = new TopologySourceTargetPageReader(factory,
            new TopologyPublicationFence(new MutableRevision()), maxRawEdgesPerSource: 1);
        var first = await reader.ReadPageAsync(["source"], 1000, ScopeA, 1, null, Ct);
        Assert.Equal("service", Assert.Single(first.Items).Target?.NodeId);
        var next = await reader.ReadPageAsync(["source"], 1000, ScopeA, 1, first.Cursor, Ct);
        var terminal = Assert.Single(next.Items);
        Assert.Null(terminal.Target);
        Assert.Equal(TopologySourceTargetStatus.Truncated, terminal.FinalStatus);
        Assert.Null(next.Cursor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diamond_streams_every_raw_chain_before_terminal_for_canonical_consumer(
        bool reverseFirstEdgeOrder)
    {
        var factory = new MemoryFactory();
        await SeedDiamondAsync(factory, reverseFirstEdgeOrder);
        var reader = new TopologySourceTargetPageReader(factory,
            new TopologyPublicationFence(new MutableRevision()));
        var raw = new List<TopologySourceTargetChunk>();
        string? cursor = null;
        do
        {
            var page = await reader.ReadPageAsync(["source"], 1000, ScopeA, 1, cursor, Ct);
            raw.AddRange(page.Items);
            cursor = page.Cursor;
        } while (cursor is not null);

        Assert.Equal(5, raw.Count); // service A/B, instance via each, then terminal
        Assert.Equal(TopologySourceTargetStatus.Complete, raw[^1].FinalStatus);
        var instances = raw.Where(static item => item.Target?.NodeId == "instance")
            .Select(static item => item.Target!).ToArray();
        Assert.Equal(2, instances.Length);
        Assert.All(instances, target =>
        {
            Assert.Equal("root", target.MappingNodeIds[0]);
            Assert.Equal("instance", target.MappingNodeIds[^1]);
            Assert.Equal(target.MappingNodeIds.Count - 1, target.MappingEdgeIds.Count);
        });
        var viaA = Assert.Single(instances, target => target.MappingNodeIds[1] == "service-a");
        Assert.Equal(["root", "service-a", "instance"], viaA.MappingNodeIds);
        Assert.Equal([EdgeId(reverseFirstEdgeOrder ? 2 : 1), EdgeId(3)], viaA.MappingEdgeIds);
        var viaB = Assert.Single(instances, target => target.MappingNodeIds[1] == "service-b");
        Assert.Equal(["root", "service-b", "instance"], viaB.MappingNodeIds);
        Assert.Equal([EdgeId(reverseFirstEdgeOrder ? 1 : 2), EdgeId(4)], viaB.MappingEdgeIds);
        var canonical = instances.OrderBy(target => string.Join("/", target.MappingNodeIds),
            StringComparer.Ordinal).ThenBy(target => string.Join("/", target.MappingEdgeIds),
            StringComparer.Ordinal).First();
        Assert.Equal("service-a", canonical.MappingNodeIds[1]);
    }

    private static async Task SeedAsync(MemoryFactory factory, bool includeService,
        bool includeInstance, string serviceOwner = "A")
    {
        await using var db = factory.CreateDbContext();
        db.TopologyNodes.Add(new() { Id = "root", Kind = TopologyNodeKind.Source,
            SourceId = "source", OwnerGroup = "A", DisplayName = "root" });
        db.TopologyNodeHistory.Add(new() { Revision = 1, NodeId = "root", OwnerGroup = "A",
            DisplayName = "root", Enabled = true, FromNano = 0 });
        if (includeService)
        {
            db.TopologyNodes.Add(new() { Id = "service", Kind = TopologyNodeKind.Service,
                OwnerGroup = serviceOwner, DisplayName = "service" });
            db.TopologyNodeHistory.Add(new() { Revision = 2, NodeId = "service",
                OwnerGroup = serviceOwner, DisplayName = "service", Enabled = true, FromNano = 0 });
            db.TopologyDeclaredEdgeHistory.Add(Edge(1, "root", "service", "A", serviceOwner));
        }
        if (includeInstance)
        {
            db.TopologyNodes.Add(new() { Id = "instance", Kind = TopologyNodeKind.ServiceInstance,
                OwnerGroup = "A", DisplayName = "instance" });
            db.TopologyNodeHistory.Add(new() { Revision = 3, NodeId = "instance", OwnerGroup = "A",
                DisplayName = "instance", Enabled = true, FromNano = 0 });
            db.TopologyDeclaredEdgeHistory.Add(Edge(2, "service", "instance", serviceOwner, "A"));
        }
        await db.SaveChangesAsync(Ct);
    }

    private static TopologyDeclaredEdgeHistoryEntity Edge(long revision, string from,
        string to, string fromOwner, string toOwner) => new()
    {
        Revision = revision, EdgeId = Guid.Parse(EdgeId(revision)),
        FromNodeId = from, ToNodeId = to, Relation = "contains", Provenance = "declared",
        FromOwnerGroup = fromOwner, ToOwnerGroup = toOwner, Directed = true,
        Confidence = 1m, EdgeVersion = 1, FromNano = 0,
    };

    private static string EdgeId(long revision) =>
        $"00000000-0000-0000-0000-{revision:000000000000}";

    private static async Task SeedDiamondAsync(MemoryFactory factory, bool reverse)
    {
        await using var db = factory.CreateDbContext();
        var nodes = new[]
        {
            (Id: "root", Kind: TopologyNodeKind.Source),
            (Id: "service-a", Kind: TopologyNodeKind.Service),
            (Id: "service-b", Kind: TopologyNodeKind.Service),
            (Id: "instance", Kind: TopologyNodeKind.ServiceInstance),
        };
        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            db.TopologyNodes.Add(new() { Id = node.Id, Kind = node.Kind,
                SourceId = index == 0 ? "source" : null, OwnerGroup = "A", DisplayName = node.Id });
            db.TopologyNodeHistory.Add(new() { Revision = index + 1, NodeId = node.Id,
                OwnerGroup = "A", DisplayName = node.Id, Enabled = true, FromNano = 0 });
        }
        db.TopologyDeclaredEdgeHistory.Add(Edge(1, "root", reverse ? "service-b" : "service-a", "A", "A"));
        db.TopologyDeclaredEdgeHistory.Add(Edge(2, "root", reverse ? "service-a" : "service-b", "A", "A"));
        db.TopologyDeclaredEdgeHistory.Add(Edge(3, "service-a", "instance", "A", "A"));
        db.TopologyDeclaredEdgeHistory.Add(Edge(4, "service-b", "instance", "A", "A"));
        await db.SaveChangesAsync(Ct);
    }

    private sealed class MutableRevision : ITopologyPublicationRevisionSource
    {
        public TopologyPublicationRevision Current { get; set; } = new(1, 3)
        {
            RepairStamp = new(1, "mapping-test-certificate"),
        };
        public Task<TopologyPublicationRevision> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Current);
    }

    private sealed class MemoryFactory : IDbContextFactory<ControlPlaneDbContext>
    {
        private readonly DbContextOptions<ControlPlaneDbContext> _options =
            new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseInMemoryDatabase("topology-map-" + Guid.NewGuid()).Options;
        public ControlPlaneDbContext CreateDbContext() => new(_options);
    }
}
