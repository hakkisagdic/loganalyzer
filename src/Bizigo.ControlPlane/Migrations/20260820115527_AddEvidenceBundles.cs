using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddEvidenceBundles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "evidence_bundles",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    gathered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    window_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    baseline_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    baseline_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    out_of_scope_count = table.Column<long>(type: "bigint", nullable: false),
                    is_partial = table.Column<bool>(type: "boolean", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evidence_bundles", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_evidence_bundles_content_hash",
                schema: "bizigo",
                table: "evidence_bundles",
                column: "content_hash");

            migrationBuilder.CreateIndex(
                name: "ix_evidence_bundles_gathered_at",
                schema: "bizigo",
                table: "evidence_bundles",
                column: "gathered_at");

            migrationBuilder.CreateIndex(
                name: "ix_evidence_bundles_window_from_window_to",
                schema: "bizigo",
                table: "evidence_bundles",
                columns: new[] { "window_from", "window_to" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evidence_bundles",
                schema: "bizigo");
        }
    }
}
