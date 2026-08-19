using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddChangeConnectors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "change_connector_runs",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    connector_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<byte>(type: "smallint", nullable: false),
                    changes_written = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_connector_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "change_connectors",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    connector_type = table.Column<byte>(type: "smallint", nullable: false),
                    owner_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    config_json = table.Column<string>(type: "text", nullable: false),
                    credential_cipher = table.Column<string>(type: "text", nullable: false),
                    interval_seconds = table.Column<int>(type: "integer", nullable: true),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_state = table.Column<byte>(type: "smallint", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_connectors", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_change_connector_runs_connector_id_started_at",
                schema: "bizigo",
                table: "change_connector_runs",
                columns: new[] { "connector_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_change_connector_runs_started_at",
                schema: "bizigo",
                table: "change_connector_runs",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_change_connectors_enabled_next_run_at",
                schema: "bizigo",
                table: "change_connectors",
                columns: new[] { "enabled", "next_run_at" });

            migrationBuilder.CreateIndex(
                name: "ix_change_connectors_owner_group",
                schema: "bizigo",
                table: "change_connectors",
                column: "owner_group");

            migrationBuilder.CreateIndex(
                name: "ix_change_connectors_slug",
                schema: "bizigo",
                table: "change_connectors",
                column: "slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_connector_runs",
                schema: "bizigo");

            migrationBuilder.DropTable(
                name: "change_connectors",
                schema: "bizigo");
        }
    }
}
