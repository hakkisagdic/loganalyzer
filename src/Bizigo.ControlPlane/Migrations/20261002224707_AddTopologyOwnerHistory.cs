using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddTopologyOwnerHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "topology_owner_history",
                schema: "bizigo",
                columns: table => new
                {
                    revision = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    old_owner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    new_owner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    node_version = table.Column<long>(type: "bigint", nullable: false),
                    changed_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topology_owner_history", x => x.revision);
                });

            migrationBuilder.CreateIndex(
                name: "ix_topology_owner_history_changed_at",
                schema: "bizigo",
                table: "topology_owner_history",
                column: "changed_at");

            migrationBuilder.CreateIndex(
                name: "ix_topology_owner_history_node_id_revision",
                schema: "bizigo",
                table: "topology_owner_history",
                columns: new[] { "node_id", "revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Topology owner-transfer history requires an explicit archival downgrade; automatic deletion is refused.");
        }
    }
}
