using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddRcaModelBoundary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "model_boundary",
                schema: "bizigo",
                table: "rca_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "model_boundary_override_reason",
                schema: "bizigo",
                table: "rca_runs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "model_boundary",
                schema: "bizigo",
                table: "rca_runs");

            migrationBuilder.DropColumn(
                name: "model_boundary_override_reason",
                schema: "bizigo",
                table: "rca_runs");
        }
    }
}
