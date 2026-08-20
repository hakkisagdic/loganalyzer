using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddEvidenceReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "evidence_reviews",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bundle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reviewer = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actual_root_cause = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evidence_reviews", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_evidence_reviews_bundle_id_reviewed_at",
                schema: "bizigo",
                table: "evidence_reviews",
                columns: new[] { "bundle_id", "reviewed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_evidence_reviews_reviewed_at",
                schema: "bizigo",
                table: "evidence_reviews",
                column: "reviewed_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evidence_reviews",
                schema: "bizigo");
        }
    }
}
