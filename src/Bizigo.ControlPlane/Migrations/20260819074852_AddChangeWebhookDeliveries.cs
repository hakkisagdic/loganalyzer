using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddChangeWebhookDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "change_webhook_deliveries",
                schema: "bizigo",
                columns: table => new
                {
                    delivery_key = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    endpoint_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    owner_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    change_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_webhook_deliveries", x => x.delivery_key);
                });

            migrationBuilder.CreateIndex(
                name: "ix_change_webhook_deliveries_endpoint_id_received_at",
                schema: "bizigo",
                table: "change_webhook_deliveries",
                columns: new[] { "endpoint_id", "received_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_webhook_deliveries",
                schema: "bizigo");
        }
    }
}
