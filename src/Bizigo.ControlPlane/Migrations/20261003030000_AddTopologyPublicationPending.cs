using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Bizigo.ControlPlane.Migrations;

[DbContext(typeof(ControlPlaneDbContext))]
[Migration("20261003030000_AddTopologyPublicationPending")]
public sealed class AddTopologyPublicationPending : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE bizigo.topology_publication_pending (
            id smallint PRIMARY KEY CHECK (id = 1),
            publication_key varchar(64) NOT NULL CHECK (publication_key ~ '^[0-9a-f]{64}$'),
            publication_sequence bigint NOT NULL CHECK (publication_sequence > 0),
            created_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE bizigo.topology_publication_receipts (
            publication_key varchar(64) PRIMARY KEY CHECK (publication_key ~ '^[0-9a-f]{64}$'),
            publication_sequence bigint NOT NULL UNIQUE CHECK (publication_sequence > 0),
            published_at timestamptz NOT NULL DEFAULT now()
        );
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Pending topology publication requires explicit reconciliation before downgrade.");
}
