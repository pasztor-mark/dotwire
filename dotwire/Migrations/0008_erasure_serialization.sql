CREATE OR REPLACE FUNCTION messages_skip_redacted() RETURNS trigger AS $$
BEGIN
    PERFORM pg_advisory_xact_lock_shared(hashtextextended(NEW.room_id::text, 0));
    IF EXISTS (SELECT 1 FROM message_redactions WHERE room_id = NEW.room_id AND seq = NEW.seq) THEN
        RETURN NULL;
    END IF;
    RETURN NEW;
END
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION redact_message(p_room_id uuid, p_seq bigint) RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
DECLARE
    removed integer;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended(p_room_id::text, 0));

    INSERT INTO message_redactions (room_id, seq) VALUES (p_room_id, p_seq)
    ON CONFLICT DO NOTHING;

    DELETE FROM messages WHERE room_id = p_room_id AND seq = p_seq;
    GET DIAGNOSTICS removed = ROW_COUNT;
    RETURN removed;
END
$$;

REVOKE ALL ON FUNCTION redact_message(uuid, bigint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION redact_message(uuid, bigint) TO dotwire_app;

CREATE FUNCTION message_redactions_block_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'message_redactions is append-only';
END
$$ LANGUAGE plpgsql;

CREATE TRIGGER message_redactions_append_only
    BEFORE UPDATE OR DELETE ON message_redactions
    FOR EACH ROW EXECUTE FUNCTION message_redactions_block_mutation();

CREATE TRIGGER message_redactions_no_truncate
    BEFORE TRUNCATE ON message_redactions
    FOR EACH STATEMENT EXECUTE FUNCTION message_redactions_block_mutation();
