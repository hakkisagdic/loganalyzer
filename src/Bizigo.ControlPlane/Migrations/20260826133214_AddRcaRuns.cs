using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddRcaRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rca_runs",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    root_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    trigger_identity = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    owner_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    window_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    debounce_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    lineage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    accepted = table.Column<bool>(type: "boolean", nullable: false),
                    rejection = table.Column<int>(type: "integer", nullable: false),
                    rejection_detail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    evidence_bundle_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rca_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rca_runs_debounce_key_accepted_requested_at",
                schema: "bizigo",
                table: "rca_runs",
                columns: new[] { "debounce_key", "accepted", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_rca_runs_idempotency_key",
                schema: "bizigo",
                table: "rca_runs",
                column: "idempotency_key",
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_rca_runs_parent_run_id",
                schema: "bizigo",
                table: "rca_runs",
                column: "parent_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_rca_runs_rejection_requested_at",
                schema: "bizigo",
                table: "rca_runs",
                columns: new[] { "rejection", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_rca_runs_root_run_id_depth",
                schema: "bizigo",
                table: "rca_runs",
                columns: new[] { "root_run_id", "depth" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rca_runs",
                schema: "bizigo");
        }
    }
}
