-- P5.2 durable table-store schema. Idempotent for repeated deployment.
--
-- Ownership: TableService (P5.2). This schema is deliberately separate from
-- lobby.*; the table service never reads/writes lobby tables and the lobby only
-- reads the separately owned public projection.
--
-- Application data is accessed through the least-privilege role
-- mahjong_table_runtime (USAGE + DML only). The migration owner retains DDL.
-- Payload bytea columns contain AES-256-GCM ciphertext produced by the
-- application with an external key; no key or connection secret is stored here.

CREATE SCHEMA IF NOT EXISTS table_store;

CREATE TABLE IF NOT EXISTS table_store.schema_migration (
    version integer PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS table_store.table_state (
    room_id text PRIMARY KEY,
    state_version bigint NOT NULL,
    last_event_seq bigint NOT NULL,
    state_fingerprint numeric(20,0) NOT NULL,
    state_hash bytea NOT NULL,
    ruleset_version integer NOT NULL,
    control_version bigint NOT NULL,
    timing_version bigint NOT NULL,
    cheat_version bigint NOT NULL DEFAULT 0,
    cheat_fingerprint numeric(20,0) NOT NULL DEFAULT 0,
    cheat_bound boolean NOT NULL DEFAULT false,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_table_state_versions CHECK (
        state_version >= 0 AND last_event_seq >= 0 AND control_version >= 0 AND
        timing_version >= 0 AND cheat_version >= 0),
    CONSTRAINT ck_table_state_hash CHECK (octet_length(state_hash) = 32),
    CONSTRAINT ck_table_state_ruleset CHECK (ruleset_version >= 1),
    CONSTRAINT ck_table_state_cheat CHECK (
        (cheat_version = 0 AND cheat_fingerprint = 0 AND NOT cheat_bound) OR
        (cheat_version > 0 AND cheat_fingerprint > 0 AND cheat_bound))
);

CREATE TABLE IF NOT EXISTS table_store.table_lease (
    room_id text PRIMARY KEY REFERENCES table_store.table_state (room_id) ON DELETE CASCADE,
    owner_instance text NOT NULL,
    owner_epoch bigint NOT NULL,
    lease_expires_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_table_lease_epoch CHECK (owner_epoch >= 1),
    CONSTRAINT ck_table_lease_owner CHECK (char_length(owner_instance) BETWEEN 1 AND 128)
);

CREATE TABLE IF NOT EXISTS table_store.table_event (
    room_id text NOT NULL REFERENCES table_store.table_state (room_id) ON DELETE CASCADE,
    seq bigint NOT NULL,
    command_version bigint NOT NULL,
    event_ordinal integer NOT NULL,
    event_type integer NOT NULL,
    event_schema integer NOT NULL,
    seat integer NOT NULL,
    event_blob bytea NOT NULL,
    event_blob_sha256 bytea NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (room_id, seq),
    CONSTRAINT uq_table_event_ordinal UNIQUE (room_id, command_version, event_ordinal),
    CONSTRAINT ck_table_event_seq CHECK (seq >= 1 AND command_version >= 1 AND event_ordinal >= 0),
    CONSTRAINT ck_table_event_seat CHECK (seat >= -1 AND seat <= 3),
    CONSTRAINT ck_table_event_schema CHECK (event_schema >= 1),
    CONSTRAINT ck_table_event_hash CHECK (octet_length(event_blob_sha256) = 32)
);

-- Security upgrade for databases created by an earlier P5.2 candidate: private
-- tile identities and free-form details must live only inside the authenticated
-- encrypted event blob, never in plaintext shadow columns.
ALTER TABLE table_store.table_event
    DROP COLUMN IF EXISTS tile,
    DROP COLUMN IF EXISTS detail;

CREATE INDEX IF NOT EXISTS ix_table_event_version
    ON table_store.table_event (room_id, command_version, event_ordinal);

CREATE TABLE IF NOT EXISTS table_store.request_scope (
    scope_id text PRIMARY KEY,
    account_id text NOT NULL,
    role text NOT NULL,
    room_id text NULL,
    issued_at_utc_ms bigint NOT NULL,
    expires_at_utc_ms bigint NOT NULL,
    revoked_at_utc_ms bigint NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_scope_role CHECK (role IN ('PLAYER', 'SPECTATOR', 'LOBBY')),
    CONSTRAINT ck_scope_room CHECK (
        (role = 'LOBBY' AND room_id IS NULL) OR
        (role <> 'LOBBY' AND room_id IS NOT NULL AND char_length(room_id) > 0)),
    CONSTRAINT ck_scope_time CHECK (
        expires_at_utc_ms > issued_at_utc_ms AND
        (revoked_at_utc_ms IS NULL OR revoked_at_utc_ms >= issued_at_utc_ms))
);

CREATE INDEX IF NOT EXISTS ix_request_scope_account
    ON table_store.request_scope (account_id, expires_at_utc_ms);

CREATE TABLE IF NOT EXISTS table_store.request_receipt (
    account_id text NOT NULL,
    scope_id text NOT NULL,
    request_id text NOT NULL,
    room_id text NOT NULL,
    api text NOT NULL,
    payload_sha256 bytea NOT NULL,
    status text NOT NULL,
    reason_code text NULL,
    evaluated_state_version bigint NOT NULL,
    command_version bigint NULL,
    event_seq bigint NULL,
    committed_at_utc_ms bigint NULL,
    canonical_payload_digest bytea NULL,
    response_blob bytea NOT NULL,
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    PRIMARY KEY (account_id, scope_id, request_id),
    CONSTRAINT ck_receipt_status CHECK (status IN ('APPLIED', 'RECORDED', 'REJECTED')),
    CONSTRAINT ck_receipt_hash CHECK (octet_length(payload_sha256) = 32),
    CONSTRAINT ck_receipt_versions CHECK (
        evaluated_state_version >= 0 AND
        (command_version IS NULL OR command_version = evaluated_state_version + 1) AND
        (event_seq IS NULL OR event_seq >= 1)),
    CONSTRAINT ck_receipt_status_fields CHECK (
        (status = 'REJECTED' AND command_version IS NULL AND event_seq IS NULL AND reason_code IS NOT NULL) OR
        (status IN ('APPLIED', 'RECORDED') AND command_version IS NOT NULL AND event_seq IS NOT NULL)),
    CONSTRAINT ck_receipt_committed CHECK (committed_at_utc_ms IS NULL OR committed_at_utc_ms > 0),
    CONSTRAINT ck_receipt_canonical CHECK (
        canonical_payload_digest IS NULL OR octet_length(canonical_payload_digest) = 32),
    CONSTRAINT ck_receipt_expiry CHECK (expires_at > created_at)
);

-- Security/upgrade path for an earlier P5.2 candidate: preserve the complete
-- original Receipt, not a re-derived committed_at or a dropped canonical digest.
ALTER TABLE table_store.request_receipt
    ADD COLUMN IF NOT EXISTS committed_at_utc_ms bigint NULL,
    ADD COLUMN IF NOT EXISTS canonical_payload_digest bytea NULL;

CREATE INDEX IF NOT EXISTS ix_request_receipt_expiry
    ON table_store.request_receipt (expires_at);

CREATE TABLE IF NOT EXISTS table_store.table_checkpoint (
    room_id text NOT NULL REFERENCES table_store.table_state (room_id) ON DELETE CASCADE,
    state_version bigint NOT NULL,
    last_event_seq bigint NOT NULL,
    state_fingerprint numeric(20,0) NOT NULL,
    ruleset_version integer NOT NULL,
    schema_version integer NOT NULL,
    boot_id text NOT NULL,
    created_at_utc_ms bigint NOT NULL,
    captured_monotonic_ms bigint NOT NULL,
    control_version bigint NOT NULL,
    timing_version bigint NOT NULL,
    state_blob bytea NOT NULL,
    state_sha256 bytea NOT NULL,
    control_blob bytea NOT NULL,
    control_sha256 bytea NOT NULL,
    timing_blob bytea NOT NULL,
    timing_sha256 bytea NOT NULL,
    cheat_blob bytea NULL,
    cheat_sha256 bytea NULL,
    facts_blob bytea NULL,
    facts_sha256 bytea NULL,
    history_blob bytea NULL,
    history_sha256 bytea NULL,
    bindings_blob bytea NULL,
    bindings_sha256 bytea NULL,
    persisted_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (room_id, state_version),
    CONSTRAINT ck_checkpoint_versions CHECK (
        state_version >= 0 AND last_event_seq >= 0 AND control_version >= 0 AND
        timing_version >= 0 AND schema_version >= 1 AND ruleset_version >= 1),
    CONSTRAINT ck_checkpoint_time CHECK (
        created_at_utc_ms >= 0 AND captured_monotonic_ms >= 0),
    CONSTRAINT ck_checkpoint_hashes CHECK (
        octet_length(state_sha256) = 32 AND octet_length(control_sha256) = 32 AND
        octet_length(timing_sha256) = 32 AND
        (cheat_blob IS NULL) = (cheat_sha256 IS NULL) AND
        (facts_blob IS NULL) = (facts_sha256 IS NULL) AND
        (history_blob IS NULL) = (history_sha256 IS NULL) AND
        (bindings_blob IS NULL) = (bindings_sha256 IS NULL)),
    CONSTRAINT ck_checkpoint_optional_hashes CHECK (
        (cheat_sha256 IS NULL OR octet_length(cheat_sha256) = 32) AND
        (facts_sha256 IS NULL OR octet_length(facts_sha256) = 32) AND
        (history_sha256 IS NULL OR octet_length(history_sha256) = 32) AND
        (bindings_sha256 IS NULL OR octet_length(bindings_sha256) = 32))
);

CREATE INDEX IF NOT EXISTS ix_table_checkpoint_latest
    ON table_store.table_checkpoint (room_id, state_version DESC);

CREATE TABLE IF NOT EXISTS table_store.table_timing_journal (
    room_id text NOT NULL REFERENCES table_store.table_state (room_id) ON DELETE CASCADE,
    timing_version bigint NOT NULL,
    state_version bigint NOT NULL,
    control_version bigint NOT NULL,
    boot_id text NOT NULL,
    captured_at_utc_ms bigint NOT NULL,
    captured_monotonic_ms bigint NOT NULL,
    timing_blob bytea NOT NULL,
    timing_sha256 bytea NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (room_id, timing_version),
    CONSTRAINT ck_timing_versions CHECK (
        timing_version >= 1 AND state_version >= 0 AND control_version >= 0),
    CONSTRAINT ck_timing_hash CHECK (octet_length(timing_sha256) = 32)
);

CREATE TABLE IF NOT EXISTS table_store.table_command_journal (
    room_id text NOT NULL REFERENCES table_store.table_state (room_id) ON DELETE CASCADE,
    command_version bigint NOT NULL,
    evaluated_state_version bigint NOT NULL,
    request_id text NOT NULL,
    account_id text NOT NULL,
    scope_id text NOT NULL,
    api text NOT NULL,
    payload_sha256 bytea NOT NULL,
    command_payload_blob bytea NOT NULL,
    command_payload_sha256 bytea NOT NULL,
    schema_version integer NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (room_id, command_version),
    CONSTRAINT ck_command_version CHECK (
        command_version >= 1 AND command_version = evaluated_state_version + 1),
    CONSTRAINT ck_command_hash CHECK (
        octet_length(payload_sha256) = 32 AND octet_length(command_payload_sha256) = 32),
    CONSTRAINT ck_command_schema CHECK (schema_version >= 1)
);

CREATE INDEX IF NOT EXISTS ix_table_command_request
    ON table_store.table_command_journal (room_id, request_id, scope_id);

CREATE TABLE IF NOT EXISTS table_store.table_control_journal (
    room_id text NOT NULL REFERENCES table_store.table_state (room_id) ON DELETE CASCADE,
    control_version bigint NOT NULL,
    state_version bigint NOT NULL,
    round_id text NOT NULL,
    seat integer NOT NULL,
    kind integer NOT NULL,
    previous_epoch bigint NULL,
    new_epoch bigint NULL,
    previous_kind integer NULL,
    new_kind integer NULL,
    previous_status integer NULL,
    new_status integer NULL,
    grace_deadline_millis bigint NULL,
    at_monotonic_millis bigint NOT NULL,
    at_utc_millis bigint NOT NULL,
    reason text NOT NULL,
    disconnected_at_millis bigint NULL,
    ai_profile_id text NULL,
    policy_mode integer NULL,
    policy_grace_millis bigint NULL,
    policy_ai_profile_id text NULL,
    owner_instance text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (room_id, control_version),
    CONSTRAINT ck_control_versions CHECK (control_version >= 1 AND state_version >= 0),
    CONSTRAINT ck_control_seat CHECK (seat >= -1 AND seat <= 3),
    CONSTRAINT ck_control_time CHECK (at_monotonic_millis >= 0 AND at_utc_millis >= 0)
);

CREATE TABLE IF NOT EXISTS table_store.table_committed_facts (
    room_id text PRIMARY KEY REFERENCES table_store.table_state (room_id) ON DELETE CASCADE,
    payload_blob bytea NOT NULL,
    payload_sha256 bytea NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_facts_hash CHECK (octet_length(payload_sha256) = 32)
);

CREATE TABLE IF NOT EXISTS table_store.table_recovery_audit (
    audit_id bigserial PRIMARY KEY,
    room_id text NOT NULL REFERENCES table_store.table_state (room_id) ON DELETE CASCADE,
    kind integer NOT NULL,
    recorded_at_utc_ms bigint NOT NULL,
    previous_boot_id text NOT NULL,
    current_boot_id text NOT NULL,
    downtime_millis bigint NOT NULL,
    grace_millis bigint NOT NULL,
    previous_owner_instance text NOT NULL,
    previous_owner_epoch bigint NOT NULL,
    current_owner_epoch bigint NOT NULL,
    detail text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_recovery_kind CHECK (kind IN (0, 1, 2)),
    CONSTRAINT ck_recovery_epoch CHECK (previous_owner_epoch >= 0 AND current_owner_epoch >= 0),
    CONSTRAINT ck_recovery_time CHECK (recorded_at_utc_ms >= 0 AND downtime_millis >= 0 AND grace_millis >= 0)
);

CREATE INDEX IF NOT EXISTS ix_recovery_audit_room
    ON table_store.table_recovery_audit (room_id, audit_id);

CREATE TABLE IF NOT EXISTS table_store.settlement_outbox (
    match_id text NOT NULL,
    result_version integer NOT NULL,
    payload_blob bytea NOT NULL,
    payload_sha256 bytea NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    published_at timestamptz NULL,
    PRIMARY KEY (match_id, result_version),
    CONSTRAINT ck_outbox_version CHECK (result_version >= 0),
    CONSTRAINT ck_outbox_hash CHECK (octet_length(payload_sha256) = 32)
);

CREATE INDEX IF NOT EXISTS ix_settlement_outbox_unpublished
    ON table_store.settlement_outbox (created_at, match_id, result_version)
    WHERE published_at IS NULL;

INSERT INTO table_store.schema_migration (version)
VALUES (1)
ON CONFLICT (version) DO NOTHING;

-- Least-privilege application role. Production deployments may pre-provision
-- this role; the migration only creates it when absent and never elevates an
-- existing role. It deliberately has no DDL/TRUNCATE rights.
DO $p52_role$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mahjong_table_runtime') THEN
        CREATE ROLE mahjong_table_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE
            NOREPLICATION NOBYPASSRLS;
    END IF;
END
$p52_role$;

DO $p52_connect$
BEGIN
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO mahjong_table_runtime', current_database());
END
$p52_connect$;

REVOKE ALL ON SCHEMA table_store FROM PUBLIC;
GRANT USAGE ON SCHEMA table_store TO mahjong_table_runtime;

REVOKE ALL ON ALL TABLES IN SCHEMA table_store FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA table_store TO mahjong_table_runtime;

REVOKE ALL ON ALL SEQUENCES IN SCHEMA table_store FROM PUBLIC;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA table_store TO mahjong_table_runtime;

ALTER DEFAULT PRIVILEGES IN SCHEMA table_store
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mahjong_table_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA table_store
    GRANT USAGE, SELECT ON SEQUENCES TO mahjong_table_runtime;
