using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LogAnalyzer.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddActualRootCauseToGoldenReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "actual_root_cause",
                schema: "loganalyzer",
                table: "golden_reviews",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "actual_root_cause",
                schema: "loganalyzer",
                table: "golden_reviews");
        }
    }
}
