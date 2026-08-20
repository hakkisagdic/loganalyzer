using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddSigmaRuleStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_alert_rules_enabled_next_run_at",
                schema: "bizigo",
                table: "alert_rules");

            migrationBuilder.AddColumn<string>(
                name: "gated_reason",
                schema: "bizigo",
                table: "alert_rules",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "sigma_output_sha",
                schema: "bizigo",
                table: "alert_rules",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "sigma_rule_id",
                schema: "bizigo",
                table: "alert_rules",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "sigma_source_sha",
                schema: "bizigo",
                table: "alert_rules",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "source",
                schema: "bizigo",
                table: "alert_rules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "status",
                schema: "bizigo",
                table: "alert_rules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_sigma_rule_id",
                schema: "bizigo",
                table: "alert_rules",
                column: "sigma_rule_id");

            // ⚠️ VERİ TAŞIMA — `enabled` düşürülmeden ÖNCE.
            //
            // EF'in ürettiği göç `enabled`'ı düşürüp `status`'ü varsayılan 0
            // (`Enabled`) ile ekliyordu. Yani **her pasif kural sessizce
            // açılırdı**: hata yok, sayaç yok, belirti yok — kullanıcının
            // kapattığı kural bir sabah kendiliğinden bildirim göndermeye
            // başlar.
            //
            // `Gated` bu göçte üretilmiyor: eski şemada karşılığı yok ve
            // uydurmak, bir yetenek sınırı iddiasını veriden değil koddan
            // türetmek olurdu.
            migrationBuilder.Sql(
                """
                UPDATE bizigo.alert_rules
                   SET status = CASE WHEN enabled THEN 0 ELSE 1 END;
                """);

            migrationBuilder.DropColumn(
                name: "enabled",
                schema: "bizigo",
                table: "alert_rules");

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_status_next_run_at",
                schema: "bizigo",
                table: "alert_rules",
                columns: new[] { "status", "next_run_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_alert_rules_sigma_rule_id",
                schema: "bizigo",
                table: "alert_rules");

            migrationBuilder.DropIndex(
                name: "ix_alert_rules_status_next_run_at",
                schema: "bizigo",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "gated_reason",
                schema: "bizigo",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "sigma_output_sha",
                schema: "bizigo",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "sigma_rule_id",
                schema: "bizigo",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "sigma_source_sha",
                schema: "bizigo",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "source",
                schema: "bizigo",
                table: "alert_rules");

            // Ters yönde de veri taşınıyor: `Gated` geri dönüşte `enabled=false`
            // oluyor, çünkü eski şemada "koşamaz" diye bir durum yok ve `true`
            // yazmak koşamayan bir kuralı açık göstermek olurdu.
            migrationBuilder.Sql(
                """
                UPDATE bizigo.alert_rules
                   SET enabled = (status = 0);
                """);

            migrationBuilder.DropColumn(
                name: "status",
                schema: "bizigo",
                table: "alert_rules");

            migrationBuilder.AddColumn<bool>(
                name: "enabled",
                schema: "bizigo",
                table: "alert_rules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_enabled_next_run_at",
                schema: "bizigo",
                table: "alert_rules",
                columns: new[] { "enabled", "next_run_at" });
        }
    }
}
