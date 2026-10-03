using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.ControlPlane;

[Table("topology_edges_declared")]
public sealed class TopologyDeclaredEdgeEntity
{
    [Key] public Guid Id { get; set; }
    [MaxLength(64)] public required string FromNodeId { get; set; }
    [MaxLength(64)] public required string ToNodeId { get; set; }
    [MaxLength(32)] public required string Relation { get; set; }
    [MaxLength(64)] public required string FromOwnerGroup { get; set; }
    [MaxLength(64)] public required string ToOwnerGroup { get; set; }
    public bool Directed { get; set; } = true;
    [MaxLength(16)] public string Provenance { get; set; } = "declared";
    public decimal Confidence { get; set; } = 1m;
    public long Version { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    [MaxLength(256)] public required string CreatedBy { get; set; }
    [MaxLength(256)] public required string UpdatedBy { get; set; }
}

[Table("topology_edge_declared_history")]
public sealed class TopologyDeclaredEdgeHistoryEntity
{
    [Key] public long Revision { get; set; }
    public Guid EdgeId { get; set; }
    [MaxLength(64)] public required string FromNodeId { get; set; }
    [MaxLength(64)] public required string ToNodeId { get; set; }
    [MaxLength(32)] public required string Relation { get; set; }
    [MaxLength(64)] public required string FromOwnerGroup { get; set; }
    [MaxLength(64)] public required string ToOwnerGroup { get; set; }
    public long EdgeVersion { get; set; }
    public bool Directed { get; set; } = true;
    [MaxLength(16)] public string Provenance { get; set; } = "declared";
    public decimal Confidence { get; set; } = 1m;
    public DateTimeOffset? DeletedAt { get; set; }
    public decimal FromNano { get; set; }
    public decimal? ToNano { get; set; }
}

internal static class TopologyEdgeModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<TopologyDeclaredEdgeEntity>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.Confidence).HasPrecision(3, 2);
            e.HasIndex(x => new { x.FromNodeId, x.ToNodeId, x.Relation }).IsUnique().HasFilter("deleted_at IS NULL");
            e.HasIndex(x => new { x.FromOwnerGroup, x.ToOwnerGroup, x.Id });
        });
        model.Entity<TopologyDeclaredEdgeHistoryEntity>(e =>
        {
            e.HasOne<TopologyDeclaredEdgeEntity>().WithMany().HasForeignKey(x => x.EdgeId)
                .HasConstraintName("fk_topology_edge_declared_history_edge");
            e.Property(x => x.Confidence).HasPrecision(3, 2);
            e.Property(x => x.FromNano).HasPrecision(20, 0);
            e.Property(x => x.ToNano).HasPrecision(20, 0);
            e.HasIndex(x => new { x.EdgeId, x.FromNano }).IsUnique();
            e.HasIndex(x => x.EdgeId).IsUnique().HasFilter("to_nano IS NULL");
            // Historical Contains keyset: one bounded next revision per parent.
            e.HasIndex(x => new { x.FromNodeId, x.Relation, x.Revision })
                .HasDatabaseName("ix_topology_edge_hist_from_relation_revision");
        });
    }
}
