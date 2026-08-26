using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddRcaRunState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "counts_against_quota",
                schema: "bizigo",
                table: "rca_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "finished_at",
                schema: "bizigo",
                table: "rca_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "started_at",
                schema: "bizigo",
                table: "rca_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "state",
                schema: "bizigo",
                table: "rca_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "state_detail",
                schema: "bizigo",
                table: "rca_runs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "counts_against_quota",
                schema: "bizigo",
                table: "rca_runs");

            migrationBuilder.DropColumn(
                name: "finished_at",
                schema: "bizigo",
                table: "rca_runs");

            migrationBuilder.DropColumn(
                name: "started_at",
                schema: "bizigo",
                table: "rca_runs");

            migrationBuilder.DropColumn(
                name: "state",
                schema: "bizigo",
                table: "rca_runs");

            migrationBuilder.DropColumn(
                name: "state_detail",
                schema: "bizigo",
                table: "rca_runs");
        }
    }
}
