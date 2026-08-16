using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "bizigo");

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    scope = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    details = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    row_count = table.Column<long>(type: "bigint", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    succeeded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "idp_group_mapping",
                schema: "bizigo",
                columns: table => new
                {
                    idp_group = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    owner_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    note = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idp_group_mapping", x => x.idp_group);
                });

            migrationBuilder.CreateTable(
                name: "parsers",
                schema: "bizigo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parser_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    vendor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    product = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    yaml = table.Column<string>(type: "text", nullable: false),
                    passing_tests = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    owner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    quarantined = table.Column<bool>(type: "boolean", nullable: false),
                    quarantine_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parsers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "raw_manifest",
                schema: "bizigo",
                columns: table => new
                {
                    object_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    owner_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    byte_size = table.Column<long>(type: "bigint", nullable: false),
                    event_count = table.Column<int>(type: "integer", nullable: false),
                    ts_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ts_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_scrubbed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    state = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_raw_manifest", x => x.object_key);
                });

            migrationBuilder.CreateTable(
                name: "sources",
                schema: "bizigo",
                columns: table => new
                {
                    source_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    peer_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    hostname = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    owner_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    vendor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    product = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    parser_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    encoding = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_class = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sources", x => x.source_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_at",
                schema: "bizigo",
                table: "audit_log",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_subject_at",
                schema: "bizigo",
                table: "audit_log",
                columns: new[] { "subject", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_parsers_parser_id_version",
                schema: "bizigo",
                table: "parsers",
                columns: new[] { "parser_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_parsers_state",
                schema: "bizigo",
                table: "parsers",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_raw_manifest_owner_group_ts_from",
                schema: "bizigo",
                table: "raw_manifest",
                columns: new[] { "owner_group", "ts_from" });

            migrationBuilder.CreateIndex(
                name: "ix_raw_manifest_state",
                schema: "bizigo",
                table: "raw_manifest",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_raw_manifest_verified_at",
                schema: "bizigo",
                table: "raw_manifest",
                column: "verified_at");

            migrationBuilder.CreateIndex(
                name: "ix_sources_hostname",
                schema: "bizigo",
                table: "sources",
                column: "hostname");

            migrationBuilder.CreateIndex(
                name: "ix_sources_owner_group",
                schema: "bizigo",
                table: "sources",
                column: "owner_group");

            migrationBuilder.CreateIndex(
                name: "ix_sources_peer_address",
                schema: "bizigo",
                table: "sources",
                column: "peer_address",
                unique: true,
                filter: "peer_address IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "bizigo");

            migrationBuilder.DropTable(
                name: "idp_group_mapping",
                schema: "bizigo");

            migrationBuilder.DropTable(
                name: "parsers",
                schema: "bizigo");

            migrationBuilder.DropTable(
                name: "raw_manifest",
                schema: "bizigo");

            migrationBuilder.DropTable(
                name: "sources",
                schema: "bizigo");
        }
    }
}
