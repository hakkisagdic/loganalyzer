using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Bizigo.ControlPlane.Migrations;

/// <summary>
/// Immutable conversion authority for a future, independently attested legacy
/// upgrade. Creating these tables does not certify a legacy publication.
/// </summary>
[DbContext(typeof(ControlPlaneDbContext))]
[Migration("20261004130000_AddTopologyLegacyMembership")]
public sealed class AddTopologyLegacyMembership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE bizigo.topology_repair_state
          ADD COLUMN sidecar_revision bigint NOT NULL DEFAULT 0 CHECK (sidecar_revision >= 0),
          ADD COLUMN certificate_member_set_id uuid;

        CREATE TABLE bizigo.topology_legacy_conversion_sidecars (
            original_publication_key varchar(64) NOT NULL CHECK (original_publication_key ~ '^[0-9a-f]{64}$'),
            original_sequence bigint NOT NULL CHECK (original_sequence > 0),
            conversion_digest varchar(64) NOT NULL CHECK (conversion_digest ~ '^[0-9a-f]{64}$'),
            original_payload_sha256 varchar(64) NOT NULL CHECK (original_payload_sha256 ~ '^[0-9a-f]{64}$'),
            original_rowset_sha256 varchar(64) NOT NULL CHECK (original_rowset_sha256 ~ '^[0-9a-f]{64}$'),
            upgraded_payload_sha256 varchar(64) NOT NULL CHECK (upgraded_payload_sha256 ~ '^[0-9a-f]{64}$'),
            upgraded_rowset_sha256 varchar(64) NOT NULL CHECK (upgraded_rowset_sha256 ~ '^[0-9a-f]{64}$'),
            original_catalog_sha256 varchar(64) NOT NULL CHECK (original_catalog_sha256 ~ '^[0-9a-f]{64}$'),
            created_generation bigint NOT NULL CHECK (created_generation > 0),
            pg_database_identity uuid NOT NULL,
            clickhouse_database_uuid uuid NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (original_publication_key, original_sequence),
            UNIQUE (original_sequence),
            UNIQUE (original_publication_key, original_sequence, conversion_digest)
        );

        CREATE TABLE bizigo.topology_repair_member_sets (
            member_set_id uuid PRIMARY KEY,
            generation bigint NOT NULL CHECK (generation > 0),
            copy_attempt_id uuid NOT NULL,
            pg_database_identity uuid NOT NULL,
            clickhouse_database_uuid uuid NOT NULL,
            sampled_sidecar_revision bigint NOT NULL CHECK (sampled_sidecar_revision >= 0),
            receipt_prefix_sequence bigint NOT NULL CHECK (receipt_prefix_sequence >= 0),
            receipt_prefix_sha256 varchar(64) NOT NULL CHECK (receipt_prefix_sha256 ~ '^[0-9a-f]{64}$'),
            pending_publication_key varchar(64) CHECK (pending_publication_key ~ '^[0-9a-f]{64}$'),
            pending_sequence bigint CHECK (pending_sequence > 0),
            pending_conversion_digest varchar(64) CHECK (pending_conversion_digest ~ '^[0-9a-f]{64}$'),
            pending_payload_sha256 varchar(64) CHECK (pending_payload_sha256 ~ '^[0-9a-f]{64}$'),
            member_count integer NOT NULL CHECK (member_count BETWEEN 0 AND 4096),
            canonical_byte_length integer NOT NULL CHECK (canonical_byte_length BETWEEN 38 AND 1048576),
            canonical_sha256 varchar(64) NOT NULL CHECK (canonical_sha256 ~ '^[0-9a-f]{64}$'),
            sealed_at timestamptz,
            created_at timestamptz NOT NULL DEFAULT now(),
            CHECK ((pending_publication_key IS NULL) = (pending_sequence IS NULL)),
            CHECK (pending_conversion_digest IS NULL OR pending_publication_key IS NOT NULL),
            CHECK (pending_payload_sha256 IS NULL OR pending_publication_key IS NOT NULL)
        );

        CREATE TABLE bizigo.topology_repair_members (
            member_set_id uuid NOT NULL REFERENCES bizigo.topology_repair_member_sets(member_set_id),
            original_sequence bigint NOT NULL CHECK (original_sequence > 0),
            original_publication_key varchar(64) NOT NULL CHECK (original_publication_key ~ '^[0-9a-f]{64}$'),
            conversion_digest varchar(64) NOT NULL CHECK (conversion_digest ~ '^[0-9a-f]{64}$'),
            PRIMARY KEY (member_set_id, original_sequence, original_publication_key),
            FOREIGN KEY (original_publication_key, original_sequence, conversion_digest)
                REFERENCES bizigo.topology_legacy_conversion_sidecars
                    (original_publication_key, original_sequence, conversion_digest)
        );
        CREATE INDEX topology_repair_members_exact_lookup
          ON bizigo.topology_repair_members
             (original_publication_key, original_sequence, conversion_digest, member_set_id);

        CREATE FUNCTION bizigo.topology_repair_le32(value integer) RETURNS bytea
        LANGUAGE plpgsql IMMUTABLE STRICT AS $$
        DECLARE source bytea := int4send(value); output bytea := decode('00000000', 'hex'); i integer;
        BEGIN
            FOR i IN 0..3 LOOP output := set_byte(output, i, get_byte(source, 3-i)); END LOOP;
            RETURN output;
        END $$;
        CREATE FUNCTION bizigo.topology_repair_le64(value bigint) RETURNS bytea
        LANGUAGE plpgsql IMMUTABLE STRICT AS $$
        DECLARE source bytea := int8send(value); output bytea := decode('0000000000000000', 'hex'); i integer;
        BEGIN
            FOR i IN 0..7 LOOP output := set_byte(output, i, get_byte(source, 7-i)); END LOOP;
            RETURN output;
        END $$;

        CREATE FUNCTION bizigo.topology_repair_member_bytes(target uuid) RETURNS bytea
        LANGUAGE plpgsql STABLE AS $$
        DECLARE result bytea; item record; item_count integer := 0;
        BEGIN
            result := bizigo.topology_repair_le32(26)
                   || convert_to('bizigo-topology-members-v2', 'UTF8')
                   || bizigo.topology_repair_le32(2);
            SELECT count(*) INTO item_count FROM bizigo.topology_repair_members WHERE member_set_id = target;
            IF item_count > 4096 THEN RAISE EXCEPTION 'topology member cap exceeded'; END IF;
            result := result || bizigo.topology_repair_le32(item_count);
            FOR item IN SELECT original_sequence, original_publication_key, conversion_digest
                FROM bizigo.topology_repair_members WHERE member_set_id = target
                ORDER BY original_sequence, original_publication_key COLLATE "C"
            LOOP
                result := result || bizigo.topology_repair_le64(item.original_sequence)
                    || bizigo.topology_repair_le32(64) || convert_to(item.original_publication_key, 'UTF8')
                    || bizigo.topology_repair_le32(64) || convert_to(item.conversion_digest, 'UTF8');
                IF octet_length(result) > 1048576 THEN RAISE EXCEPTION 'topology membership byte cap exceeded'; END IF;
            END LOOP;
            RETURN result;
        END $$;

        CREATE FUNCTION bizigo.topology_repair_sidecar_before_insert() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE state record; previous record;
        BEGIN
            PERFORM pg_advisory_xact_lock(735032);
            SELECT * INTO state FROM bizigo.topology_repair_state WHERE id = 1 FOR UPDATE;
            IF state.phase <> 'Repairing' OR state.generation <> NEW.created_generation
               OR state.pg_database_identity <> NEW.pg_database_identity
               OR state.clickhouse_database_uuid <> NEW.clickhouse_database_uuid
            THEN RAISE EXCEPTION 'topology sidecar admission is not in the active repair generation'; END IF;
            SELECT * INTO previous FROM bizigo.topology_legacy_conversion_sidecars
                WHERE original_publication_key = NEW.original_publication_key
                  AND original_sequence = NEW.original_sequence;
            IF previous.original_publication_key IS NOT NULL
               AND (to_jsonb(previous) - 'created_at') <> (to_jsonb(NEW) - 'created_at')
            THEN RAISE EXCEPTION 'divergent topology conversion sidecar retry'; END IF;
            RETURN NEW;
        END $$;
        CREATE FUNCTION bizigo.topology_repair_sidecar_after_insert() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            UPDATE bizigo.topology_repair_state SET sidecar_revision = sidecar_revision + 1,
                updated_at = now() WHERE id = 1;
            RETURN NEW;
        END $$;
        CREATE FUNCTION bizigo.topology_repair_immutable() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'topology repair authority is immutable'; END $$;
        CREATE TRIGGER topology_sidecar_admit BEFORE INSERT ON bizigo.topology_legacy_conversion_sidecars
          FOR EACH ROW EXECUTE FUNCTION bizigo.topology_repair_sidecar_before_insert();
        CREATE TRIGGER topology_sidecar_revision AFTER INSERT ON bizigo.topology_legacy_conversion_sidecars
          FOR EACH ROW EXECUTE FUNCTION bizigo.topology_repair_sidecar_after_insert();
        CREATE TRIGGER topology_sidecar_immutable BEFORE UPDATE OR DELETE
          ON bizigo.topology_legacy_conversion_sidecars
          FOR EACH ROW EXECUTE FUNCTION bizigo.topology_repair_immutable();

        CREATE FUNCTION bizigo.topology_repair_set_before_insert() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE state record;
        BEGIN
            PERFORM pg_advisory_xact_lock(735032);
            SELECT * INTO state FROM bizigo.topology_repair_state WHERE id = 1 FOR UPDATE;
            IF NEW.sealed_at IS NOT NULL OR state.phase <> 'Repairing'
               OR state.generation <> NEW.generation OR state.copy_attempt_id <> NEW.copy_attempt_id
               OR state.pg_database_identity <> NEW.pg_database_identity
               OR state.clickhouse_database_uuid <> NEW.clickhouse_database_uuid
               OR state.sidecar_revision <> NEW.sampled_sidecar_revision
            THEN RAISE EXCEPTION 'topology member set is not bound to active repair state'; END IF;
            RETURN NEW;
        END $$;
        CREATE FUNCTION bizigo.topology_repair_set_before_update() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE state record; actual bytea;
        BEGIN
            IF OLD.sealed_at IS NOT NULL OR NEW.sealed_at IS NULL
               OR (to_jsonb(NEW) - 'sealed_at') <> (to_jsonb(OLD) - 'sealed_at')
            THEN RAISE EXCEPTION 'topology member set is immutable'; END IF;
            IF NOT pg_try_advisory_xact_lock(735032)
            THEN RAISE EXCEPTION 'topology repair publication lock is busy'; END IF;
            SELECT * INTO state FROM bizigo.topology_repair_state WHERE id = 1 FOR UPDATE;
            IF state.phase <> 'Repairing' OR state.generation <> NEW.generation
               OR state.copy_attempt_id <> NEW.copy_attempt_id
               OR state.pg_database_identity <> NEW.pg_database_identity
               OR state.clickhouse_database_uuid <> NEW.clickhouse_database_uuid
               OR state.sidecar_revision <> NEW.sampled_sidecar_revision
            THEN RAISE EXCEPTION 'topology member set seal is stale'; END IF;
            actual := bizigo.topology_repair_member_bytes(NEW.member_set_id);
            IF octet_length(actual) <> NEW.canonical_byte_length
               OR encode(sha256(actual), 'hex') <> NEW.canonical_sha256
               OR (SELECT count(*) FROM bizigo.topology_repair_members
                   WHERE member_set_id = NEW.member_set_id) <> NEW.member_count
            THEN RAISE EXCEPTION 'topology member set is incomplete or divergent'; END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER topology_set_admit BEFORE INSERT ON bizigo.topology_repair_member_sets
          FOR EACH ROW EXECUTE FUNCTION bizigo.topology_repair_set_before_insert();
        CREATE TRIGGER topology_set_seal BEFORE UPDATE ON bizigo.topology_repair_member_sets
          FOR EACH ROW EXECUTE FUNCTION bizigo.topology_repair_set_before_update();
        CREATE TRIGGER topology_set_no_delete BEFORE DELETE ON bizigo.topology_repair_member_sets
          FOR EACH ROW EXECUTE FUNCTION bizigo.topology_repair_immutable();

        CREATE FUNCTION bizigo.topology_repair_member_before_insert() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE state record; header record;
        BEGIN
            PERFORM pg_advisory_xact_lock(735032);
            SELECT * INTO state FROM bizigo.topology_repair_state WHERE id = 1 FOR UPDATE;
            SELECT * INTO header FROM bizigo.topology_repair_member_sets
                WHERE member_set_id = NEW.member_set_id FOR UPDATE;
            IF header.member_set_id IS NULL OR header.sealed_at IS NOT NULL
               OR state.phase <> 'Repairing' OR state.generation <> header.generation
               OR state.copy_attempt_id <> header.copy_attempt_id
               OR state.sidecar_revision <> header.sampled_sidecar_revision
            THEN RAISE EXCEPTION 'topology member append is not in an unsealed active set'; END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER topology_member_admit BEFORE INSERT ON bizigo.topology_repair_members
          FOR EACH ROW EXECUTE FUNCTION bizigo.topology_repair_member_before_insert();
        CREATE TRIGGER topology_member_immutable BEFORE UPDATE OR DELETE ON bizigo.topology_repair_members
          FOR EACH ROW EXECUTE FUNCTION bizigo.topology_repair_immutable();

        CREATE FUNCTION bizigo.topology_repair_format2_ready_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE header record; actual bytea;
        BEGIN
            IF NEW.phase <> 'Ready' OR (OLD.phase = 'Ready'
                AND NEW.certificate_json IS NOT DISTINCT FROM OLD.certificate_json
                AND NEW.certificate_member_set_id IS NOT DISTINCT FROM OLD.certificate_member_set_id)
            THEN RETURN NEW; END IF;
            IF NEW.certificate_json ->> 'FormatVersion' IS DISTINCT FROM '2'
            THEN RETURN NEW; END IF; -- Frozen format-1 certificates retain their old gate.
            SELECT * INTO header FROM bizigo.topology_repair_member_sets
                WHERE member_set_id = NEW.certificate_member_set_id FOR UPDATE;
            IF header.member_set_id IS NULL OR header.sealed_at IS NULL
               OR NEW.sidecar_revision <> OLD.sidecar_revision
               OR header.generation <> NEW.generation
               OR header.copy_attempt_id <> NEW.copy_attempt_id
               OR header.pg_database_identity <> NEW.pg_database_identity
               OR header.clickhouse_database_uuid <> NEW.clickhouse_database_uuid
               OR header.sampled_sidecar_revision <> NEW.sidecar_revision
               OR header.receipt_prefix_sequence <> NEW.receipt_prefix_sequence
               OR header.receipt_prefix_sha256 <> NEW.receipt_prefix_sha256
               OR header.member_count <> 0
               OR NEW.certificate_json ->> 'MemberSetId' <> header.member_set_id::text
               OR (NEW.certificate_json ->> 'SidecarRevision')::bigint <> NEW.sidecar_revision
               OR (NEW.certificate_json ->> 'MemberCount')::integer <> header.member_count
               OR NEW.certificate_json ->> 'MembershipSha256' <> header.canonical_sha256
            THEN RAISE EXCEPTION 'topology format-2 certificate has no sealed authority'; END IF;
            actual := bizigo.topology_repair_member_bytes(header.member_set_id);
            IF octet_length(actual) <> header.canonical_byte_length
               OR encode(sha256(actual), 'hex') <> header.canonical_sha256
            THEN RAISE EXCEPTION 'topology format-2 member set changed'; END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER topology_format2_ready_guard BEFORE UPDATE ON bizigo.topology_repair_state
          FOR EACH ROW EXECUTE FUNCTION bizigo.topology_repair_format2_ready_guard();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Sealed topology repair membership cannot be downgraded.");
}
