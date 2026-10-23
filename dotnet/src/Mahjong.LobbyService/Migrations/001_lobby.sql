-- P5.3 LobbyService schema.
--
-- Ownership: the lobby owns account/session, room directory, matchmaking,
-- assignment and table-ticket issuance/consumption rows. It does NOT create or
-- read table_event, private snapshots, hands, seeds, cheat ledgers or leases.
-- The only table-owned input is the public room_projection read model queried
-- by PostgresRoomProjectionReader (schema/table owned by the future
-- TableService/P5.1 and therefore not created here).
--
-- This migration is intentionally idempotent so an explicit deployment step can
-- re-run it. No table is added to the shared Mahjong solution.

CREATE SCHEMA IF NOT EXISTS lobby;

CREATE TABLE IF NOT EXISTS lobby.account (
    account_id text PRIMARY KEY,
    is_guest boolean NOT NULL,
    display_name text NOT NULL,
    device_digest text NOT NULL,
    -- SHA-256 of the client-held high-entropy guest bootstrap secret.
    -- device_digest alone is never an authentication key.
    bootstrap_hash text NOT NULL,
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    CONSTRAINT uq_account_bootstrap UNIQUE (device_digest, bootstrap_hash),
    CONSTRAINT ck_account_guest CHECK (is_guest),
    CONSTRAINT ck_account_bootstrap_hash CHECK (char_length(bootstrap_hash) = 64),
    CONSTRAINT ck_account_display_name CHECK (char_length(display_name) BETWEEN 1 AND 32)
);

CREATE TABLE IF NOT EXISTS lobby.session (
    token_hash text PRIMARY KEY,
    account_id text NOT NULL REFERENCES lobby.account (account_id),
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz NULL,
    CONSTRAINT ck_session_token_hash CHECK (char_length(token_hash) = 64),
    CONSTRAINT ck_session_expiry CHECK (expires_at > created_at)
);

CREATE INDEX IF NOT EXISTS ix_session_account ON lobby.session (account_id, expires_at);

CREATE TABLE IF NOT EXISTS lobby.request_receipt (
    principal text NOT NULL,
    request_id text NOT NULL,
    payload_digest text NOT NULL,
    status text NOT NULL,
    http_status integer NOT NULL,
    result_body text NOT NULL,
    created_at timestamptz NOT NULL,
    completed_at timestamptz NULL,
    expires_at timestamptz NOT NULL,
    CONSTRAINT pk_request_receipt PRIMARY KEY (principal, request_id),
    CONSTRAINT ck_receipt_digest CHECK (char_length(payload_digest) = 64),
    CONSTRAINT ck_receipt_status CHECK (status IN ('IN_PROGRESS', 'COMPLETED'))
);

CREATE INDEX IF NOT EXISTS ix_receipt_expiry ON lobby.request_receipt (expires_at);

CREATE TABLE IF NOT EXISTS lobby.room (
    room_id text PRIMARY KEY,
    title text NOT NULL,
    visibility text NOT NULL,
    state text NOT NULL,
    allow_spectators boolean NOT NULL,
    max_players integer NOT NULL,
    ruleset_version bigint NOT NULL,
    host_account_id text NOT NULL REFERENCES lobby.account (account_id),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    closed_at timestamptz NULL,
    CONSTRAINT ck_room_visibility CHECK (visibility IN ('PUBLIC', 'PRIVATE')),
    CONSTRAINT ck_room_state CHECK (state IN ('OPEN', 'CLOSED')),
    CONSTRAINT ck_room_title CHECK (char_length(title) BETWEEN 1 AND 64),
    CONSTRAINT ck_room_players CHECK (max_players = 4),
    CONSTRAINT ck_room_ruleset CHECK (ruleset_version = 1)
);

CREATE INDEX IF NOT EXISTS ix_room_directory
    ON lobby.room (created_at DESC, room_id ASC)
    WHERE visibility = 'PUBLIC' AND state = 'OPEN';

CREATE TABLE IF NOT EXISTS lobby.assignment (
    assignment_id text PRIMARY KEY,
    room_id text NOT NULL REFERENCES lobby.room (room_id),
    account_id text NOT NULL REFERENCES lobby.account (account_id),
    role text NOT NULL,
    seat_id integer NULL,
    status text NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    expires_at timestamptz NULL,
    confirmed_at timestamptz NULL,
    table_confirmed_at timestamptz NULL,
    match_ticket_id text NULL,
    CONSTRAINT ck_assignment_role CHECK (role IN ('PLAYER', 'SPECTATOR')),
    CONSTRAINT ck_assignment_status CHECK (status IN ('RESERVED', 'CONFIRMED', 'CANCELLED', 'EXPIRED')),
    CONSTRAINT ck_assignment_seat CHECK (
        (role = 'PLAYER' AND seat_id BETWEEN 0 AND 3) OR
        (role = 'SPECTATOR' AND seat_id IS NULL)
    ),
    CONSTRAINT ck_assignment_reservation_expiry CHECK (
        status <> 'RESERVED' OR expires_at IS NOT NULL
    )
);

-- One active player assignment per room/seat and one active assignment per
-- room/account. These are the no-double-booking invariants; application checks
-- are only a friendlier pre-check.
CREATE UNIQUE INDEX IF NOT EXISTS uq_assignment_active_seat
    ON lobby.assignment (room_id, seat_id)
    WHERE role = 'PLAYER' AND status IN ('RESERVED', 'CONFIRMED');

-- One active lobby placement per account, globally. room-level uniqueness was
-- insufficient: it allowed the same account to hold player seats in two rooms.
CREATE UNIQUE INDEX IF NOT EXISTS uq_assignment_active_account
    ON lobby.assignment (account_id)
    WHERE status IN ('RESERVED', 'CONFIRMED');

CREATE UNIQUE INDEX IF NOT EXISTS uq_assignment_match_ticket
    ON lobby.assignment (match_ticket_id)
    WHERE match_ticket_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_assignment_room_status
    ON lobby.assignment (room_id, status, expires_at);

CREATE TABLE IF NOT EXISTS lobby.match_ticket (
    match_ticket_id text PRIMARY KEY,
    account_id text NOT NULL REFERENCES lobby.account (account_id),
    mode text NOT NULL,
    status text NOT NULL,
    ruleset_version bigint NOT NULL,
    region text NOT NULL,
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    reserved_at timestamptz NULL,
    confirmed_at timestamptz NULL,
    cancelled_at timestamptz NULL,
    assignment_id text NULL REFERENCES lobby.assignment (assignment_id),
    room_id text NULL REFERENCES lobby.room (room_id),
    CONSTRAINT ck_match_mode CHECK (mode IN ('CASUAL', 'RANKED')),
    CONSTRAINT ck_match_status CHECK (status IN ('QUEUED', 'RESERVED', 'CONFIRMED', 'CANCELLED', 'EXPIRED')),
    CONSTRAINT ck_match_ruleset CHECK (ruleset_version = 1),
    CONSTRAINT ck_match_expiry CHECK (expires_at > created_at)
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_match_active_account
    ON lobby.match_ticket (account_id)
    WHERE status IN ('QUEUED', 'RESERVED');

CREATE UNIQUE INDEX IF NOT EXISTS uq_match_assignment
    ON lobby.match_ticket (assignment_id)
    WHERE assignment_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_match_queue
    ON lobby.match_ticket (mode, ruleset_version, region, created_at)
    WHERE status = 'QUEUED';

CREATE TABLE IF NOT EXISTS lobby.table_ticket (
    jti text PRIMARY KEY,
    assignment_id text NOT NULL REFERENCES lobby.assignment (assignment_id),
    account_id text NOT NULL REFERENCES lobby.account (account_id),
    room_id text NOT NULL REFERENCES lobby.room (room_id),
    role text NOT NULL,
    seat_id integer NULL,
    issued_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    kid text NOT NULL,
    consumed_at timestamptz NULL,
    superseded_at timestamptz NULL,
    CONSTRAINT ck_table_ticket_role CHECK (role IN ('PLAYER', 'SPECTATOR')),
    CONSTRAINT ck_table_ticket_seat CHECK (
        (role = 'PLAYER' AND seat_id BETWEEN 0 AND 3) OR
        (role = 'SPECTATOR' AND seat_id IS NULL)
    ),
    CONSTRAINT ck_table_ticket_ttl CHECK (expires_at > issued_at)
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_table_ticket_active_assignment
    ON lobby.table_ticket (assignment_id)
    WHERE consumed_at IS NULL AND superseded_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_table_ticket_expiry
    ON lobby.table_ticket (expires_at)
    WHERE consumed_at IS NULL AND superseded_at IS NULL;

-- Consumption is keyed by jti, which is the replay fence. A reconnect may
-- consume a fresh ticket for the same already-confirmed assignment; the active
-- assignment unique indexes above still prevent a second seat owner.
CREATE TABLE IF NOT EXISTS lobby.table_ticket_consumption (
    jti text NOT NULL REFERENCES lobby.table_ticket (jti),
    assignment_id text NOT NULL REFERENCES lobby.assignment (assignment_id),
    account_id text NOT NULL REFERENCES lobby.account (account_id),
    room_id text NOT NULL REFERENCES lobby.room (room_id),
    seat_id integer NULL,
    consumed_at timestamptz NOT NULL,
    CONSTRAINT pk_table_ticket_consumption PRIMARY KEY (jti),
    CONSTRAINT ck_consumption_seat CHECK (seat_id IS NULL OR seat_id BETWEEN 0 AND 3)
);

CREATE INDEX IF NOT EXISTS ix_consumption_room_seat
    ON lobby.table_ticket_consumption (room_id, seat_id);
