CREATE FUNCTION compliance_block_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME;
END
$$ LANGUAGE plpgsql;

CREATE TABLE legal_holds (
    id         bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scope      text        NOT NULL CHECK (scope IN ('room', 'user')),
    room_id    uuid,
    user_id    text,
    matter_ref text        NOT NULL,
    placed_by  text        NOT NULL,
    placed_at  timestamptz NOT NULL DEFAULT now(),
    CHECK ((scope = 'room' AND room_id IS NOT NULL AND user_id IS NULL)
        OR (scope = 'user' AND user_id IS NOT NULL AND room_id IS NULL))
);
CREATE INDEX legal_holds_room ON legal_holds (room_id);
CREATE INDEX legal_holds_user ON legal_holds (user_id);

CREATE TABLE legal_hold_releases (
    hold_id     bigint      PRIMARY KEY REFERENCES legal_holds (id),
    released_by text        NOT NULL,
    released_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE erasures (
    id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id      text        NOT NULL,
    mode         text        NOT NULL CHECK (mode IN ('anonymize', 'delete')),
    requested_by text        NOT NULL,
    requested_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX erasures_user ON erasures (user_id);

CREATE TABLE erasure_revocations (
    erasure_id bigint      PRIMARY KEY REFERENCES erasures (id),
    lsn        pg_lsn      NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE erasure_completions (
    erasure_id   bigint      PRIMARY KEY REFERENCES erasures (id),
    completed_at timestamptz NOT NULL DEFAULT now()
);

GRANT SELECT, INSERT ON legal_holds, legal_hold_releases, erasures, erasure_revocations, erasure_completions TO dotwire_app;
REVOKE UPDATE, DELETE, TRUNCATE ON legal_holds, legal_hold_releases, erasures, erasure_revocations, erasure_completions FROM dotwire_app;

CREATE TRIGGER legal_holds_append_only BEFORE UPDATE OR DELETE ON legal_holds
    FOR EACH ROW EXECUTE FUNCTION compliance_block_mutation();
CREATE TRIGGER legal_holds_no_truncate BEFORE TRUNCATE ON legal_holds
    FOR EACH STATEMENT EXECUTE FUNCTION compliance_block_mutation();
CREATE TRIGGER legal_hold_releases_append_only BEFORE UPDATE OR DELETE ON legal_hold_releases
    FOR EACH ROW EXECUTE FUNCTION compliance_block_mutation();
CREATE TRIGGER legal_hold_releases_no_truncate BEFORE TRUNCATE ON legal_hold_releases
    FOR EACH STATEMENT EXECUTE FUNCTION compliance_block_mutation();
CREATE TRIGGER erasures_append_only BEFORE UPDATE OR DELETE ON erasures
    FOR EACH ROW EXECUTE FUNCTION compliance_block_mutation();
CREATE TRIGGER erasures_no_truncate BEFORE TRUNCATE ON erasures
    FOR EACH STATEMENT EXECUTE FUNCTION compliance_block_mutation();
CREATE TRIGGER erasure_revocations_append_only BEFORE UPDATE OR DELETE ON erasure_revocations
    FOR EACH ROW EXECUTE FUNCTION compliance_block_mutation();
CREATE TRIGGER erasure_revocations_no_truncate BEFORE TRUNCATE ON erasure_revocations
    FOR EACH STATEMENT EXECUTE FUNCTION compliance_block_mutation();
CREATE TRIGGER erasure_completions_append_only BEFORE UPDATE OR DELETE ON erasure_completions
    FOR EACH ROW EXECUTE FUNCTION compliance_block_mutation();
CREATE TRIGGER erasure_completions_no_truncate BEFORE TRUNCATE ON erasure_completions
    FOR EACH STATEMENT EXECUTE FUNCTION compliance_block_mutation();

CREATE FUNCTION message_change_block(
    p_room_id uuid, p_sender text, p_time timestamptz, p_min_keep_days integer, p_allow_changes boolean
) RETURNS integer
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM legal_holds h
        WHERE ((h.scope = 'room' AND h.room_id = p_room_id)
            OR (h.scope = 'user' AND p_sender IS NOT NULL AND h.user_id = p_sender))
          AND NOT EXISTS (SELECT 1 FROM legal_hold_releases r WHERE r.hold_id = h.id)
    ) THEN
        RETURN -1;
    END IF;

    IF NOT p_allow_changes THEN
        RETURN -3;
    END IF;

    IF p_min_keep_days > 0 AND p_time IS NOT NULL AND p_time > now() - make_interval(days => p_min_keep_days) THEN
        RETURN -2;
    END IF;

    RETURN 0;
END
$$;

CREATE FUNCTION redact_message_checked(
    p_room_id uuid, p_seq bigint, p_min_keep_days integer, p_allow_changes boolean,
    p_sender_hint text, p_time_hint timestamptz
) RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
DECLARE
    v_sender text;
    v_time   timestamptz;
    v_block  integer;
    removed  integer;
BEGIN
    PERFORM pg_advisory_xact_lock_shared(hashtextextended('dotwire.legal_hold', 0));
    PERFORM pg_advisory_xact_lock(hashtextextended(p_room_id::text, 0));

    SELECT sender_id, time INTO v_sender, v_time
    FROM messages WHERE room_id = p_room_id AND seq = p_seq;
    IF NOT FOUND THEN
        v_sender := p_sender_hint;
        v_time := p_time_hint;
    END IF;

    v_block := message_change_block(p_room_id, v_sender, v_time, p_min_keep_days, p_allow_changes);
    IF v_block <> 0 THEN
        RETURN v_block;
    END IF;

    INSERT INTO message_redactions (room_id, seq) VALUES (p_room_id, p_seq)
    ON CONFLICT DO NOTHING;

    DELETE FROM messages WHERE room_id = p_room_id AND seq = p_seq;
    GET DIAGNOSTICS removed = ROW_COUNT;
    RETURN removed;
END
$$;

CREATE FUNCTION reattribute_message(
    p_room_id uuid, p_seq bigint, p_old_sender text, p_key_id text, p_content bytea,
    p_min_keep_days integer, p_allow_changes boolean
) RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
DECLARE
    v_sender text;
    v_time   timestamptz;
    v_block  integer;
    updated  integer;
BEGIN
    PERFORM pg_advisory_xact_lock_shared(hashtextextended('dotwire.legal_hold', 0));
    PERFORM pg_advisory_xact_lock(hashtextextended(p_room_id::text, 0));

    IF NOT EXISTS (
        SELECT 1 FROM erasures e
        WHERE e.user_id = p_old_sender
          AND NOT EXISTS (SELECT 1 FROM erasure_completions c WHERE c.erasure_id = e.id)
    ) THEN
        RETURN -4;
    END IF;

    SELECT sender_id, time INTO v_sender, v_time
    FROM messages WHERE room_id = p_room_id AND seq = p_seq;

    v_block := message_change_block(p_room_id, v_sender, v_time, p_min_keep_days, p_allow_changes);
    IF v_block <> 0 THEN
        RETURN v_block;
    END IF;

    UPDATE messages SET sender_id = 'dotwire:erased', key_id = p_key_id, content = p_content
    WHERE room_id = p_room_id AND seq = p_seq AND sender_id = p_old_sender;
    GET DIAGNOSTICS updated = ROW_COUNT;
    RETURN updated;
END
$$;

CREATE FUNCTION sync_retention_schedule() RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
DECLARE
    v_job integer;
BEGIN
    PERFORM pg_advisory_xact_lock_shared(hashtextextended('dotwire.legal_hold', 0));

    SELECT job_id INTO v_job
    FROM timescaledb_information.jobs
    WHERE proc_name = 'policy_retention' AND hypertable_name = 'messages'
    LIMIT 1;

    IF v_job IS NOT NULL THEN
        PERFORM alter_job(v_job, scheduled => NOT EXISTS (
            SELECT 1 FROM legal_holds h
            WHERE NOT EXISTS (SELECT 1 FROM legal_hold_releases r WHERE r.hold_id = h.id)
        ));
    END IF;
END
$$;

CREATE OR REPLACE FUNCTION set_message_retention(p_days integer) RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
BEGIN
    PERFORM pg_advisory_xact_lock_shared(hashtextextended('dotwire.legal_hold', 0));

    PERFORM remove_retention_policy('messages', if_exists => true);

    IF p_days > 0 THEN
        PERFORM add_retention_policy('messages', make_interval(days => p_days));
    END IF;

    PERFORM sync_retention_schedule();

    RETURN p_days;
END
$$;

CREATE FUNCTION get_retention_paused() RETURNS boolean
LANGUAGE sql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
    SELECT EXISTS (
        SELECT 1 FROM timescaledb_information.jobs
        WHERE proc_name = 'policy_retention' AND hypertable_name = 'messages' AND scheduled = false
    )
$$;

CREATE FUNCTION retention_job_running() RETURNS boolean
LANGUAGE sql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
    SELECT EXISTS (
        SELECT 1
        FROM timescaledb_information.jobs j
        JOIN timescaledb_information.job_stats s ON s.job_id = j.job_id
        WHERE j.proc_name = 'policy_retention' AND j.hypertable_name = 'messages' AND s.job_status = 'Running'
    )
$$;

REVOKE ALL ON FUNCTION message_change_block(uuid, text, timestamptz, integer, boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION message_change_block(uuid, text, timestamptz, integer, boolean) TO dotwire_app;
REVOKE ALL ON FUNCTION redact_message_checked(uuid, bigint, integer, boolean, text, timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION redact_message_checked(uuid, bigint, integer, boolean, text, timestamptz) TO dotwire_app;
REVOKE ALL ON FUNCTION reattribute_message(uuid, bigint, text, text, bytea, integer, boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION reattribute_message(uuid, bigint, text, text, bytea, integer, boolean) TO dotwire_app;
REVOKE ALL ON FUNCTION sync_retention_schedule() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION sync_retention_schedule() TO dotwire_app;
REVOKE ALL ON FUNCTION get_retention_paused() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION get_retention_paused() TO dotwire_app;
REVOKE ALL ON FUNCTION retention_job_running() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION retention_job_running() TO dotwire_app;
REVOKE ALL ON FUNCTION compliance_block_mutation() FROM PUBLIC;
