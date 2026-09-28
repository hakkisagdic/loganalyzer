using Bizigo.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.ControlPlane;

public partial class ControlPlaneDbContext
{
    public DbSet<TopologyNodeEntity> TopologyNodes => Set<TopologyNodeEntity>();
    public DbSet<TopologyNodeHistoryEntity> TopologyNodeHistory => Set<TopologyNodeHistoryEntity>();
    public DbSet<TopologyBindingEntity> TopologyBindings => Set<TopologyBindingEntity>();
    public DbSet<TopologyReadStateEntity> TopologyReadState => Set<TopologyReadStateEntity>();

    // Runs inside the source/history transaction and its advisory lock. No
    // standalone sync worker can commit a node with a different owner revision.
    private async Task SynchronizeSourceTopologyAsync(string[] sourceIds, decimal boundary, CancellationToken token)
    {
        var histories = await SourceOwnershipHistory.Where(h => sourceIds.Contains(h.SourceId) && h.EffectiveToNano == null)
            .ToArrayAsync(token);
        var nodes = await TopologyNodes.Where(n => n.SourceId != null && sourceIds.Contains(n.SourceId)).ToArrayAsync(token);
        foreach (var source in histories)
        {
            var node = nodes.SingleOrDefault(n => n.SourceId == source.SourceId);
            if (node is null)
            {
                node = new()
                {
                    Id = TopologyIdentity.Node(TopologyNodeKind.Source, Guid.NewGuid()), Kind = TopologyNodeKind.Source,
                    SourceId = source.SourceId, OwnerGroup = source.OwnerGroup, DisplayName = source.Hostname ?? source.SourceId,
                };
                TopologyNodes.Add(node);
            }
            var prior = await TopologyNodeHistory.Where(h => h.NodeId == node.Id && h.ToNano == null).SingleOrDefaultAsync(token);
            var effective = prior is null ? boundary : Math.Max(boundary, prior.FromNano + 1000);
            if (prior is not null)
            {
                if (Database.IsNpgsql())
                {
                    await Database.ExecuteSqlInterpolatedAsync($"UPDATE bizigo.topology_node_history SET to_nano = {effective} WHERE revision = {prior.Revision}", token);
                    Entry(prior).State = EntityState.Detached;
                }
                else prior.ToNano = effective;
            }
            node.OwnerGroup = source.OwnerGroup;
            node.DisplayName = source.Hostname ?? source.SourceId;
            node.Enabled = source.Enabled && !node.Deleted;
            node.Version++;
            TopologyNodeHistory.Add(new()
            {
                NodeId = node.Id, OwnerGroup = node.OwnerGroup, DisplayName = node.DisplayName, NodeVersion = node.Version,
                Enabled = node.Enabled, SourceHistoryRevision = source.Revision, FromNano = effective,
            });
        }
        await AdvanceTopologyEpochAsync(token);
    }

    internal async Task AdvanceTopologyEpochAsync(CancellationToken token)
    {
        if (Database.IsNpgsql())
        {
            await Database.ExecuteSqlRawAsync("""
                INSERT INTO bizigo.topology_read_state (id, epoch, published_sequence) VALUES (1,1,0)
                ON CONFLICT (id) DO UPDATE SET epoch = bizigo.topology_read_state.epoch + 1
                """, token);
        }
        else
        {
            var state = await TopologyReadState.SingleOrDefaultAsync(s => s.Id == 1, token);
            if (state is null) TopologyReadState.Add(new() { Epoch = 1 });
            else state.Epoch++;
        }
    }
}
