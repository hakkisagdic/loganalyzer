using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LogAnalyzer.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddSigmaGatedFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "gated_fingerprint",
                schema: "loganalyzer",
                table: "alert_rules",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "gated_fingerprint",
                schema: "loganalyzer",
                table: "alert_rules");
        }
    }
}
