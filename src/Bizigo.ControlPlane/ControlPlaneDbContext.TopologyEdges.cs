using Microsoft.EntityFrameworkCore;

namespace Bizigo.ControlPlane;

public partial class ControlPlaneDbContext
{
    public DbSet<TopologyDeclaredEdgeEntity> TopologyDeclaredEdges => Set<TopologyDeclaredEdgeEntity>();
    public DbSet<TopologyDeclaredEdgeHistoryEntity> TopologyDeclaredEdgeHistory => Set<TopologyDeclaredEdgeHistoryEntity>();
}
