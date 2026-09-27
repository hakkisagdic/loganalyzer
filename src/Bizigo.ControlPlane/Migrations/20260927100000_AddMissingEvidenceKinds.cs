using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Bizigo.ControlPlane.Migrations;

[DbContext(typeof(ControlPlaneDbContext))]
[Migration("20260927100000_AddMissingEvidenceKinds")]
public sealed class AddMissingEvidenceKinds : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string[]>(
            name: "missing_evidence_kinds", schema: "bizigo", table: "golden_reviews", type: "text[]", nullable: true);
        migrationBuilder.AlterColumn<long>(name: "out_of_scope_count", schema: "bizigo", table: "evidence_bundles",
            type: "bigint", nullable: true, oldClrType: typeof(long), oldType: "bigint");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Review answers require a backup and explicit operator downgrade; automatic deletion is disabled.");
}
