using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddTopologyDeclaredEdges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "topology_edges_declared",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    to_node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    relation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    from_owner_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    to_owner_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    directed = table.Column<bool>(type: "boolean", nullable: false),
                    provenance = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    confidence = table.Column<decimal>(type: "numeric(3,2)", precision: 3, scale: 2, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    updated_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topology_edges_declared", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "topology_edge_declared_history",
                schema: "bizigo",
                columns: table => new
                {
                    revision = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    edge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    to_node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    relation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    from_owner_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    to_owner_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    edge_version = table.Column<long>(type: "bigint", nullable: false),
                    directed = table.Column<bool>(type: "boolean", nullable: false),
                    provenance = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    confidence = table.Column<decimal>(type: "numeric(3,2)", precision: 3, scale: 2, nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    from_nano = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    to_nano = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topology_edge_declared_history", x => x.revision);
                    table.ForeignKey(
                        name: "fk_topology_edge_declared_history_edge",
                        column: x => x.edge_id,
                        principalSchema: "bizigo",
                        principalTable: "topology_edges_declared",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_topology_edge_declared_history_edge_id",
                schema: "bizigo",
                table: "topology_edge_declared_history",
                column: "edge_id",
                unique: true,
                filter: "to_nano IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_topology_edge_declared_history_edge_id_from_nano",
                schema: "bizigo",
                table: "topology_edge_declared_history",
                columns: new[] { "edge_id", "from_nano" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_topology_edges_declared_from_node_id_to_node_id_relation",
                schema: "bizigo",
                table: "topology_edges_declared",
                columns: new[] { "from_node_id", "to_node_id", "relation" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_topology_edges_declared_from_owner_group_to_owner_group_id",
                schema: "bizigo",
                table: "topology_edges_declared",
                columns: new[] { "from_owner_group", "to_owner_group", "id" });

            migrationBuilder.Sql("""
                ALTER TABLE bizigo.topology_edges_declared
                    ADD CONSTRAINT ck_topology_edges_declared_relation
                    CHECK (relation IN ('depends_on', 'contains', 'connects_to')),
                    ADD CONSTRAINT ck_topology_edges_declared_provenance
                    CHECK (directed AND provenance = 'declared' AND confidence = 1.00),
                    ADD CONSTRAINT ck_topology_edges_declared_version CHECK (version > 0),
                    ADD CONSTRAINT fk_topology_edges_declared_from_node
                    FOREIGN KEY (from_node_id) REFERENCES bizigo.topology_nodes(id),
                    ADD CONSTRAINT fk_topology_edges_declared_to_node
                    FOREIGN KEY (to_node_id) REFERENCES bizigo.topology_nodes(id);
                ALTER TABLE bizigo.topology_edge_declared_history
                    ADD CONSTRAINT ck_topology_edge_declared_history_interval
                    CHECK (to_nano IS NULL OR from_nano < to_nano),
                    ADD CONSTRAINT ck_topology_edge_declared_history_provenance
                    CHECK (directed AND provenance = 'declared' AND confidence = 1.00);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Declared edge history requires an explicit archival downgrade; automatic deletion is refused.");
    }
}
