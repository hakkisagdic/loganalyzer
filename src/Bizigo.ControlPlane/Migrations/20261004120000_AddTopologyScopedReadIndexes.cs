using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Bizigo.ControlPlane.Migrations;

[DbContext(typeof(ControlPlaneDbContext))]
[Migration("20261004120000_AddTopologyScopedReadIndexes")]
public sealed class AddTopologyScopedReadIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(name: "ix_topology_node_hist_owner_clock", schema: "bizigo",
            table: "topology_node_history", columns: ["owner_group", "from_nano"]);
        migrationBuilder.CreateIndex(name: "ix_topology_edge_hist_from_owner_clock", schema: "bizigo",
            table: "topology_edge_declared_history", columns: ["from_owner_group", "from_nano"]);
        migrationBuilder.CreateIndex(name: "ix_topology_edge_hist_to_owner_clock", schema: "bizigo",
            table: "topology_edge_declared_history", columns: ["to_owner_group", "from_nano"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "Scoped topology reads require historical owner indexes; downgrade needs an explicit readiness stop.");
}
