using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddTopologyRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "topology_bindings",
                schema: "bizigo",
                columns: table => new
                {
                    revision = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    binding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    service_namespace = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    service_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    service_node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    instance_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    target_node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    from_nano = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    to_nano = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topology_bindings", x => x.revision);
                });

            migrationBuilder.CreateTable(
                name: "topology_node_history",
                schema: "bizigo",
                columns: table => new
                {
                    revision = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    owner_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    node_version = table.Column<long>(type: "bigint", nullable: false),
                    source_history_revision = table.Column<long>(type: "bigint", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    from_nano = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    to_nano = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topology_node_history", x => x.revision);
                });

            migrationBuilder.CreateTable(
                name: "topology_nodes",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    source_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    owner_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topology_nodes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "topology_read_state",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    published_sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topology_read_state", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_topology_bindings_binding_id_from_nano",
                schema: "bizigo",
                table: "topology_bindings",
                columns: new[] { "binding_id", "from_nano" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_topology_bindings_source_id_service_namespace_service_name_",
                schema: "bizigo",
                table: "topology_bindings",
                columns: new[] { "source_id", "service_namespace", "service_name", "instance_id", "from_nano" });

            migrationBuilder.CreateIndex(
                name: "ix_topology_node_history_node_id",
                schema: "bizigo",
                table: "topology_node_history",
                column: "node_id",
                unique: true,
                filter: "to_nano IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_topology_node_history_node_id_from_nano",
                schema: "bizigo",
                table: "topology_node_history",
                columns: new[] { "node_id", "from_nano" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_topology_nodes_owner_group_id",
                schema: "bizigo",
                table: "topology_nodes",
                columns: new[] { "owner_group", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_topology_nodes_source_id",
                schema: "bizigo",
                table: "topology_nodes",
                column: "source_id",
                unique: true,
                filter: "source_id IS NOT NULL");

            // Existing inventory becomes authoritative only from this migration.
            // CreatedAt is not evidence of historical service or topology ownership.
            migrationBuilder.Sql("""
                INSERT INTO bizigo.topology_nodes
                    (id, kind, source_id, display_name, owner_group, version, enabled, deleted)
                SELECT 'source:' || gen_random_uuid()::text, 1, source_id,
                       COALESCE(hostname, source_id), owner_group, 1, enabled, false
                FROM bizigo.sources;

                INSERT INTO bizigo.topology_node_history
                    (node_id, owner_group, display_name, node_version, source_history_revision,
                     enabled, from_nano, to_nano)
                SELECT n.id, n.owner_group, n.display_name, n.version, h.revision, n.enabled,
                       floor(extract(epoch FROM transaction_timestamp()) * 1000000) * 1000, NULL
                FROM bizigo.topology_nodes n
                LEFT JOIN bizigo.source_ownership_history h
                  ON h.source_id = n.source_id AND h.effective_to_nano IS NULL;

                INSERT INTO bizigo.topology_read_state (id, epoch, published_sequence)
                VALUES (1, 0, 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Topology identity and ownership history require an explicit archival downgrade; automatic deletion is refused.");
        }
    }
}
