-- Append-only audit log (ARCHITECTURE.md, "Audit log"). Plain table, NOT a hypertable:
-- the single-writer hash chain needs a strict total order, which the identity column plus
-- one ordered consumer provides. Ids only, never message content - so redaction never
-- rewrites an entry and the hash chain survives every erasure.
CREATE TABLE audit_log (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_type  text        NOT NULL,
    room_id     uuid,
    message_seq bigint,
    actor_id    text,
    prev_hash   bytea       NOT NULL,
    hash        bytea       NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- Enforcement layer 1: the runtime role simply has no mutation grants.
GRANT SELECT, INSERT ON audit_log TO dotwire_app;
REVOKE UPDATE, DELETE, TRUNCATE ON audit_log FROM dotwire_app;

-- Enforcement layer 2: guard trigger as a backstop for owner-path mistakes
-- (owners bypass grants; they do not bypass triggers).
CREATE FUNCTION audit_log_block_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'audit_log is append-only';
END
$$ LANGUAGE plpgsql;

CREATE TRIGGER audit_log_append_only
    BEFORE UPDATE OR DELETE ON audit_log
    FOR EACH ROW EXECUTE FUNCTION audit_log_block_mutation();

CREATE TRIGGER audit_log_no_truncate
    BEFORE TRUNCATE ON audit_log
    FOR EACH STATEMENT EXECUTE FUNCTION audit_log_block_mutation();
