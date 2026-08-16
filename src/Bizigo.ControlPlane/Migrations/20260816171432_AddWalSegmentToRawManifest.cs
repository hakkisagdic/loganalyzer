using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddWalSegmentToRawManifest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "wal_segment",
                schema: "bizigo",
                table: "raw_manifest",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_raw_manifest_wal_segment",
                schema: "bizigo",
                table: "raw_manifest",
                column: "wal_segment");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_raw_manifest_wal_segment",
                schema: "bizigo",
                table: "raw_manifest");

            migrationBuilder.DropColumn(
                name: "wal_segment",
                schema: "bizigo",
                table: "raw_manifest");
        }
    }
}
