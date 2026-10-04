using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Bizigo.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.ControlPlane;

[Table("topology_nodes")]
public sealed class TopologyNodeEntity
{
    [Key, MaxLength(64)] public required string Id { get; set; }
    public TopologyNodeKind Kind { get; set; }
    [MaxLength(128)] public string? SourceId { get; set; }
    [MaxLength(256)] public required string DisplayName { get; set; }
    [MaxLength(64)] public required string OwnerGroup { get; set; }
    public long Version { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Deleted { get; set; }
}

[Table("topology_node_history")]
public sealed class TopologyNodeHistoryEntity
{
    [Key] public long Revision { get; set; }
    [MaxLength(64)] public required string NodeId { get; set; }
    [MaxLength(64)] public required string OwnerGroup { get; set; }
    [MaxLength(256)] public required string DisplayName { get; set; }
    public long NodeVersion { get; set; }
    public long? SourceHistoryRevision { get; set; }
    public bool Enabled { get; set; }
    public decimal FromNano { get; set; }
    public decimal? ToNano { get; set; }
}

[Table("topology_owner_history")]
public sealed class TopologyOwnerHistoryEntity
{
    [Key] public long Revision { get; set; }
    [MaxLength(64)] public required string NodeId { get; set; }
    [MaxLength(64)] public required string OldOwner { get; set; }
    [MaxLength(64)] public required string NewOwner { get; set; }
    public long NodeVersion { get; set; }
    [MaxLength(256)] public required string ChangedBy { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}

[Table("topology_bindings")]
public sealed class TopologyBindingEntity
{
    [Key] public long Revision { get; set; }
    public Guid BindingId { get; set; }
    public long SourceHistoryRevision { get; set; }
    [MaxLength(128)] public required string SourceId { get; set; }
    [MaxLength(256)] public required string ServiceNamespace { get; set; }
    [MaxLength(256)] public required string ServiceName { get; set; }
    [MaxLength(64)] public required string ServiceNodeId { get; set; }
    // Empty is the service binding; nonempty is an instance binding under that service.
    [MaxLength(256)] public required string InstanceId { get; set; }
    [MaxLength(64)] public required string TargetNodeId { get; set; }
    public decimal FromNano { get; set; }
    public decimal? ToNano { get; set; }
}

[Table("topology_read_state")]
public sealed class TopologyReadStateEntity
{
    [Key] public int Id { get; set; } = 1;
    public long Epoch { get; set; }
    public long PublishedSequence { get; set; }
}

internal static class TopologyModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<TopologyNodeEntity>(e =>
        {
            e.HasIndex(x => x.SourceId).IsUnique().HasFilter("source_id IS NOT NULL");
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.OwnerGroup, x.Id });
        });
        model.Entity<TopologyNodeHistoryEntity>(e =>
        {
            e.Property(x => x.FromNano).HasPrecision(20, 0);
            e.Property(x => x.ToNano).HasPrecision(20, 0);
            e.HasIndex(x => new { x.NodeId, x.FromNano }).IsUnique();
            e.HasIndex(x => x.NodeId).IsUnique().HasFilter("to_nano IS NULL");
            e.HasIndex(x => new { x.OwnerGroup, x.FromNano })
                .HasDatabaseName("ix_topology_node_hist_owner_clock");
        });
        model.Entity<TopologyOwnerHistoryEntity>(e =>
        {
            e.HasIndex(x => new { x.NodeId, x.Revision }).IsUnique();
            e.HasIndex(x => x.ChangedAt);
        });
        model.Entity<TopologyBindingEntity>(e =>
        {
            e.Property(x => x.FromNano).HasPrecision(20, 0);
            e.Property(x => x.ToNano).HasPrecision(20, 0);
            e.HasIndex(x => new { x.SourceId, x.ServiceNamespace, x.ServiceName, x.InstanceId, x.FromNano });
            e.HasIndex(x => new { x.BindingId, x.FromNano }).IsUnique();
        });
        model.Entity<TopologyReadStateEntity>().Property(x => x.Epoch).IsConcurrencyToken();
    }
}
