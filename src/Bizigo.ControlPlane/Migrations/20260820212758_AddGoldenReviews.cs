using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddGoldenReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "closed_at",
                schema: "bizigo",
                table: "alert_triggers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "closed_by_subject",
                schema: "bizigo",
                table: "alert_triggers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "review_id",
                schema: "bizigo",
                table: "alert_triggers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "state",
                schema: "bizigo",
                table: "alert_triggers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "golden_reviews",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bundle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    verdict = table.Column<int>(type: "integer", nullable: false),
                    contradicting_evidence = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    reviewer_subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_golden_reviews", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alert_triggers_state_fired_at",
                schema: "bizigo",
                table: "alert_triggers",
                columns: new[] { "state", "fired_at" });

            migrationBuilder.CreateIndex(
                name: "ix_golden_reviews_bundle_id",
                schema: "bizigo",
                table: "golden_reviews",
                column: "bundle_id");

            migrationBuilder.CreateIndex(
                name: "ix_golden_reviews_owner_group_reviewed_at",
                schema: "bizigo",
                table: "golden_reviews",
                columns: new[] { "owner_group", "reviewed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_golden_reviews_trigger_id",
                schema: "bizigo",
                table: "golden_reviews",
                column: "trigger_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "golden_reviews",
                schema: "bizigo");

            migrationBuilder.DropIndex(
                name: "ix_alert_triggers_state_fired_at",
                schema: "bizigo",
                table: "alert_triggers");

            migrationBuilder.DropColumn(
                name: "closed_at",
                schema: "bizigo",
                table: "alert_triggers");

            migrationBuilder.DropColumn(
                name: "closed_by_subject",
                schema: "bizigo",
                table: "alert_triggers");

            migrationBuilder.DropColumn(
                name: "review_id",
                schema: "bizigo",
                table: "alert_triggers");

            migrationBuilder.DropColumn(
                name: "state",
                schema: "bizigo",
                table: "alert_triggers");
        }
    }
}
