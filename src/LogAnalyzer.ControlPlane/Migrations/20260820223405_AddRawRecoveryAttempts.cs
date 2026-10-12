using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LogAnalyzer.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddRawRecoveryAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "recovery_attempts",
                schema: "loganalyzer",
                table: "raw_manifest",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "recovery_attempts",
                schema: "loganalyzer",
                table: "raw_manifest");
        }
    }
}
