using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddRcaReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rca_reports",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bundle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    scenario_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    scenario_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    produced_sentence_count = table.Column<int>(type: "integer", nullable: false),
                    dropped_sentence_count = table.Column<int>(type: "integer", nullable: false),
                    fabricated_citation_sentence_count = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rca_reports", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rca_reports_bundle_id_created_at",
                schema: "bizigo",
                table: "rca_reports",
                columns: new[] { "bundle_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_rca_reports_scenario_id_created_at",
                schema: "bizigo",
                table: "rca_reports",
                columns: new[] { "scenario_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rca_reports",
                schema: "bizigo");
        }
    }
}
