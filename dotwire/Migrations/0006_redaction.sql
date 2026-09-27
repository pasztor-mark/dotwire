-- Redaction (ARCHITECTURE.md, "Redaction mechanics"; COMPLIANCE.md, GDPR Art. 17).
--
-- The runtime role keeps NO blanket UPDATE/DELETE on messages (0003). Erasure goes through
-- one narrow SECURITY DEFINER function instead, so the only way dotwire_app can remove a row
-- is by (room_id, seq) - the shard key plus the JetStream ordering token - never a broad
-- DELETE. Erasure is physical: the row goes away, not a soft-delete flag.
--
-- message_redactions is an ids-only tombstone (never content). It closes the batch-writer
-- race: history lags the JetStream ack by ~one batch interval, so a redaction can land
-- before the row does. The BEFORE INSERT trigger makes a tombstoned (room_id, seq) skip the
-- insert, whichever order the two arrive in. The audit event itself is published to the
-- AUDIT stream by the endpoint, ids only, so the hash chain never needs rewriting.
CREATE TABLE message_redactions (
    room_id     uuid        NOT NULL,
    seq         bigint      NOT NULL,
    redacted_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (room_id, seq)
);

GRANT SELECT, INSERT ON message_redactions TO dotwire_app;
REVOKE UPDATE, DELETE, TRUNCATE ON message_redactions FROM dotwire_app;

CREATE FUNCTION messages_skip_redacted() RETURNS trigger AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM message_redactions WHERE room_id = NEW.room_id AND seq = NEW.seq) THEN
        RETURN NULL;
    END IF;
    RETURN NEW;
END
$$ LANGUAGE plpgsql;

-- Row-level BEFORE INSERT triggers on a hypertable run before chunk routing, so this covers
-- every chunk, present and future.
CREATE TRIGGER messages_skip_redacted
    BEFORE INSERT ON messages
    FOR EACH ROW EXECUTE FUNCTION messages_skip_redacted();

-- Runs as the owner (the migrator), which is what lets it DELETE while dotwire_app cannot.
-- search_path is pinned so a SECURITY DEFINER function can't be hijacked via a temp schema.
CREATE FUNCTION redact_message(p_room_id uuid, p_seq bigint) RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
DECLARE
    removed integer;
BEGIN
    INSERT INTO message_redactions (room_id, seq) VALUES (p_room_id, p_seq)
    ON CONFLICT DO NOTHING;

    DELETE FROM messages WHERE room_id = p_room_id AND seq = p_seq;
    GET DIAGNOSTICS removed = ROW_COUNT;
    RETURN removed;
END
$$;

-- Functions are executable by PUBLIC by default; make the erasure path explicit.
REVOKE ALL ON FUNCTION redact_message(uuid, bigint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION redact_message(uuid, bigint) TO dotwire_app;
