-- Compliance trio: audit chain columns/checkpoints, retention functions, DSAR support index
-- (docs/superpowers/specs/2026-09-07-sdk-parity-and-platform-completion-design.md §3.13).

-- Audit events grow richer fields (subjectId, value, canonical event_time) and gain the
-- per-stream ordering token the writer uses to dedupe redelivered JetStream batches.
-- The default-then-drop pattern keeps this safe if a row ever existed before this ran.
ALTER TABLE audit_log
    ADD COLUMN subject_id text,
    ADD COLUMN value      bigint,
    ADD COLUMN event_time timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN stream_seq bigint      NOT NULL DEFAULT 0;
ALTER TABLE audit_log ALTER COLUMN event_time DROP DEFAULT, ALTER COLUMN stream_seq DROP DEFAULT;
CREATE UNIQUE INDEX audit_log_stream_seq ON audit_log (stream_seq);
CREATE INDEX audit_log_actor   ON audit_log (actor_id, id);
CREATE INDEX audit_log_subject ON audit_log (subject_id, id);

-- Daily hash-chain checkpoints so /audit/verify can walk from the latest anchor instead of
-- genesis. Same append-only enforcement as audit_log (grants layer + guard trigger).
CREATE TABLE audit_checkpoints (
    day        date        PRIMARY KEY,
    last_id    bigint      NOT NULL,
    hash       bytea       NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
GRANT SELECT, INSERT ON audit_checkpoints TO dotwire_app;
REVOKE UPDATE, DELETE, TRUNCATE ON audit_checkpoints FROM dotwire_app;

CREATE TRIGGER audit_checkpoints_append_only
    BEFORE UPDATE OR DELETE ON audit_checkpoints
    FOR EACH ROW EXECUTE FUNCTION audit_log_block_mutation();

CREATE TRIGGER audit_checkpoints_no_truncate
    BEFORE TRUNCATE ON audit_checkpoints
    FOR EACH STATEMENT EXECUTE FUNCTION audit_log_block_mutation();

-- DSAR export queries messages by sender across all rooms, ordered chronologically (§3.12).
CREATE INDEX messages_sender_time ON messages (sender_id, time);

-- The auditor role this seed grants has no ambient access to room content: /audit* routes
-- return ids only, per COMPLIANCE.md.
INSERT INTO user_roles (user_id, role) VALUES ('host-auditor', 'auditor') ON CONFLICT DO NOTHING;

-- Retention (§3.11). Runs as the owner (the migrator), matching redact_message's pattern:
-- SECURITY DEFINER with a pinned search_path so it can't be hijacked via a temp schema, and
-- PUBLIC revoked so only dotwire_app can call it.
CREATE FUNCTION set_message_retention(p_days integer) RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
BEGIN
    PERFORM remove_retention_policy('messages', if_exists => true);

    IF p_days > 0 THEN
        PERFORM add_retention_policy('messages', make_interval(days => p_days));
    END IF;

    RETURN p_days;
END
$$;

REVOKE ALL ON FUNCTION set_message_retention(integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION set_message_retention(integer) TO dotwire_app;

CREATE FUNCTION get_message_retention() RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
DECLARE
    drop_after interval;
BEGIN
    SELECT (config ->> 'drop_after')::interval INTO drop_after
    FROM timescaledb_information.jobs
    WHERE proc_name = 'policy_retention' AND hypertable_name = 'messages'
    LIMIT 1;

    IF drop_after IS NULL THEN
        RETURN 0;
    END IF;

    RETURN GREATEST(0, FLOOR(EXTRACT(EPOCH FROM drop_after) / 86400))::integer;
END
$$;

REVOKE ALL ON FUNCTION get_message_retention() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION get_message_retention() TO dotwire_app;
