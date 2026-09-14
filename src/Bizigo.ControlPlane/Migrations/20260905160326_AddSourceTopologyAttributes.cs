using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceTopologyAttributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "firmware",
                schema: "bizigo",
                table: "sources",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "upstream",
                schema: "bizigo",
                table: "sources",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "vlan",
                schema: "bizigo",
                table: "sources",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "firmware",
                schema: "bizigo",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "upstream",
                schema: "bizigo",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "vlan",
                schema: "bizigo",
                table: "sources");
        }
    }
}
