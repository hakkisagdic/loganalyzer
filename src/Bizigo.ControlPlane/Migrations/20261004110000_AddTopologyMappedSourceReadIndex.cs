using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Bizigo.ControlPlane.Migrations;

[DbContext(typeof(ControlPlaneDbContext))]
[Migration("20261004110000_AddTopologyMappedSourceReadIndex")]
public sealed class AddTopologyMappedSourceReadIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateIndex(
        name: "ix_topology_edge_hist_from_relation_revision",
        schema: "bizigo",
        table: "topology_edge_declared_history",
        columns: ["from_node_id", "relation", "revision"]);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "Historical mapped-source reads require their bounded keyset index; downgrade needs an explicit readiness stop.");
}
