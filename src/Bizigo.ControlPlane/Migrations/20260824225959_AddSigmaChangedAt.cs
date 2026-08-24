using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddSigmaChangedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sigma_changed_at",
                schema: "bizigo",
                table: "alert_rules",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "sigma_changed_at",
                schema: "bizigo",
                table: "alert_rules");
        }
    }
}
