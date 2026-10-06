using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddAnomalyPoliciesAndRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "anomaly_policies",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    owner_group = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    signal = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    event_window_seconds = table.Column<int>(type: "integer", nullable: false),
                    baseline_window_seconds = table.Column<int>(type: "integer", nullable: false),
                    sensitivity = table.Column<double>(type: "double precision", nullable: false),
                    min_samples = table.Column<int>(type: "integer", nullable: false),
                    zero_baseline_min_absolute = table.Column<double>(type: "double precision", nullable: true),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    cadence_seconds = table.Column<int>(type: "integer", nullable: false),
                    last_evaluated_window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_anomaly_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "anomaly_runs",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    policy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    owner_group = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    observed_value = table.Column<double>(type: "double precision", nullable: true),
                    baseline_value = table.Column<double>(type: "double precision", nullable: true),
                    deviation = table.Column<double>(type: "double precision", nullable: true),
                    rca_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    evaluated_policy_version = table.Column<int>(type: "integer", nullable: false),
                    worker_job_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_anomaly_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_policies_owner_group_state",
                schema: "bizigo",
                table: "anomaly_policies",
                columns: new[] { "owner_group", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_policies_signal",
                schema: "bizigo",
                table: "anomaly_policies",
                column: "signal");

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_runs_owner_group_created_at",
                schema: "bizigo",
                table: "anomaly_runs",
                columns: new[] { "owner_group", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_runs_policy_id_owner_group_window_start",
                schema: "bizigo",
                table: "anomaly_runs",
                columns: new[] { "policy_id", "owner_group", "window_start" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_runs_status_created_at",
                schema: "bizigo",
                table: "anomaly_runs",
                columns: new[] { "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "anomaly_policies",
                schema: "bizigo");

            migrationBuilder.DropTable(
                name: "anomaly_runs",
                schema: "bizigo");
        }
    }
}
