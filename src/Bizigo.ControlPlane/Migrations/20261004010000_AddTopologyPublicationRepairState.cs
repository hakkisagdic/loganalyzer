using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Bizigo.ControlPlane.Migrations;

/// <summary>
/// 0014's PG authority is deliberately separate from the ClickHouse DDL
/// ledger. The absence of a Ready certificate never means an empty graph.
/// </summary>
[DbContext(typeof(ControlPlaneDbContext))]
[Migration("20261004010000_AddTopologyPublicationRepairState")]
public sealed class AddTopologyPublicationRepairState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE bizigo.topology_repair_state (
            id smallint PRIMARY KEY CHECK (id = 1),
            pg_database_identity uuid NOT NULL,
            phase varchar(16) NOT NULL CHECK (phase IN ('Uninitialized', 'Repairing', 'Ready')),
            generation bigint NOT NULL CHECK (generation >= 0),
            copy_attempt_id uuid,
            clickhouse_database_uuid uuid,
            old_canonical_identity_json jsonb,
            allowed_copy_identity_json jsonb,
            automatic_empty_init boolean NOT NULL DEFAULT false,
            certificate_digest varchar(64) CHECK (certificate_digest ~ '^[0-9a-f]{64}$'),
            certificate_json jsonb,
            receipt_prefix_sequence bigint NOT NULL DEFAULT 0 CHECK (receipt_prefix_sequence >= 0),
            receipt_prefix_sha256 varchar(64) CHECK (receipt_prefix_sha256 ~ '^[0-9a-f]{64}$'),
            updated_at timestamptz NOT NULL DEFAULT now(),
            CHECK ((phase = 'Ready') = (certificate_digest IS NOT NULL AND certificate_json IS NOT NULL)),
            CHECK (phase <> 'Repairing' OR (copy_attempt_id IS NOT NULL
                AND clickhouse_database_uuid IS NOT NULL
                AND old_canonical_identity_json IS NOT NULL
                AND allowed_copy_identity_json IS NOT NULL)),
            CHECK (NOT automatic_empty_init OR generation = 1)
        );
        INSERT INTO bizigo.topology_repair_state
            (id, pg_database_identity, phase, generation)
        VALUES (1, gen_random_uuid(), 'Uninitialized', 0);

        CREATE TABLE bizigo.topology_repair_attestations (
            generation bigint PRIMARY KEY CHECK (generation > 0),
            pg_database_identity uuid NOT NULL,
            clickhouse_database_uuid uuid NOT NULL,
            old_canonical_identity_json jsonb NOT NULL,
            operator_subject varchar(256) NOT NULL CHECK (length(btrim(operator_subject)) > 0),
            drain_statement text NOT NULL CHECK (length(btrim(drain_statement)) > 0),
            attested_at timestamptz NOT NULL DEFAULT now(),
            valid_until timestamptz NOT NULL,
            revoked_at timestamptz,
            CHECK (valid_until > attested_at)
        );
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Publication repair state cannot be downgraded without explicit operator reconciliation.");
}
