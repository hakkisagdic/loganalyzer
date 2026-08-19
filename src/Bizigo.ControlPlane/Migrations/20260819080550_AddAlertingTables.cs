using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertingTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_maintenance_windows",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_maintenance_windows", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "alert_rule_channels",
                schema: "bizigo",
                columns: table => new
                {
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_rule_channels", x => new { x.rule_id, x.channel_id });
                });

            migrationBuilder.CreateTable(
                name: "alert_rules",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    rule_type = table.Column<int>(type: "integer", nullable: false),
                    owner_subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    owner_groups = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    search_json = table.Column<string>(type: "text", nullable: false),
                    window_seconds = table.Column<int>(type: "integer", nullable: false),
                    interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    threshold = table.Column<double>(type: "double precision", nullable: false),
                    comparison = table.Column<int>(type: "integer", nullable: false),
                    silence_seconds = table.Column<int>(type: "integer", nullable: false),
                    repeat_interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_fired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_state = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "alert_triggers",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: false),
                    threshold = table.Column<double>(type: "double precision", nullable: false),
                    source_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    owner_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    summary = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_triggers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_channels",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    channel_type = table.Column<int>(type: "integer", nullable: false),
                    owner_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    config_json = table.Column<string>(type: "text", nullable: false),
                    secret_cipher = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_channels", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_deliveries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alert_maintenance_windows_owner_group_starts_at_ends_at",
                schema: "bizigo",
                table: "alert_maintenance_windows",
                columns: new[] { "owner_group", "starts_at", "ends_at" });

            migrationBuilder.CreateIndex(
                name: "ix_alert_maintenance_windows_rule_id",
                schema: "bizigo",
                table: "alert_maintenance_windows",
                column: "rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_rule_channels_channel_id",
                schema: "bizigo",
                table: "alert_rule_channels",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_enabled_next_run_at",
                schema: "bizigo",
                table: "alert_rules",
                columns: new[] { "enabled", "next_run_at" });

            migrationBuilder.CreateIndex(
                name: "ix_alert_triggers_fired_at",
                schema: "bizigo",
                table: "alert_triggers",
                column: "fired_at");

            migrationBuilder.CreateIndex(
                name: "ix_alert_triggers_rule_id_fired_at",
                schema: "bizigo",
                table: "alert_triggers",
                columns: new[] { "rule_id", "fired_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_channels_name_owner_group",
                schema: "bizigo",
                table: "notification_channels",
                columns: new[] { "name", "owner_group" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_channels_owner_group",
                schema: "bizigo",
                table: "notification_channels",
                column: "owner_group");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_state_next_attempt_at",
                schema: "bizigo",
                table: "notification_deliveries",
                columns: new[] { "state", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_trigger_id",
                schema: "bizigo",
                table: "notification_deliveries",
                column: "trigger_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_maintenance_windows",
                schema: "bizigo");

            migrationBuilder.DropTable(
                name: "alert_rule_channels",
                schema: "bizigo");

            migrationBuilder.DropTable(
                name: "alert_rules",
                schema: "bizigo");

            migrationBuilder.DropTable(
                name: "alert_triggers",
                schema: "bizigo");

            migrationBuilder.DropTable(
                name: "notification_channels",
                schema: "bizigo");

            migrationBuilder.DropTable(
                name: "notification_deliveries",
                schema: "bizigo");
        }
    }
}
