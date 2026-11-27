CREATE TABLE access_log (
    time        timestamptz NOT NULL,
    stream_seq  bigint      NOT NULL,
    event       text        NOT NULL CHECK (event IN ('live.opened', 'live.closed', 'history.read')),
    room_id     uuid        NOT NULL,
    user_id     text        NOT NULL,
    actor_id    text,
    role        text        NOT NULL CHECK (role IN ('member', 'auditor', 'admin')),
    via         text        NOT NULL CHECK (via IN ('hub', 'sse', 'rest')),
    window_id   uuid,
    expires_at  timestamptz,
    reason      text        CHECK (reason IN ('left', 'disconnected', 'revoked', 'error')),
    from_seq    bigint,
    to_seq      bigint,
    count       integer,
    CHECK (event <> 'live.opened' OR (window_id IS NOT NULL AND expires_at IS NOT NULL)),
    CHECK (event <> 'live.closed' OR (window_id IS NOT NULL AND reason IS NOT NULL)),
    CHECK (event <> 'history.read' OR count IS NOT NULL)
);
SELECT create_hypertable('access_log', 'time', chunk_time_interval => INTERVAL '7 days');
CREATE UNIQUE INDEX access_log_dedupe ON access_log (time, stream_seq);
CREATE INDEX access_log_room ON access_log (room_id, time);
CREATE INDEX access_log_user ON access_log (user_id, time);
CREATE INDEX access_log_actor ON access_log (actor_id, time) WHERE actor_id IS NOT NULL;
CREATE INDEX access_log_close ON access_log (room_id, window_id, time) WHERE event = 'live.closed';

GRANT SELECT, INSERT ON access_log TO dotwire_app;
REVOKE UPDATE, DELETE, TRUNCATE ON access_log FROM dotwire_app;

CREATE TRIGGER access_log_append_only BEFORE UPDATE OR DELETE ON access_log
    FOR EACH ROW EXECUTE FUNCTION compliance_block_mutation();
CREATE TRIGGER access_log_no_truncate BEFORE TRUNCATE ON access_log
    FOR EACH STATEMENT EXECUTE FUNCTION compliance_block_mutation();

CREATE OR REPLACE FUNCTION sync_retention_schedule() RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
DECLARE
    v_job integer;
BEGIN
    PERFORM pg_advisory_xact_lock_shared(hashtextextended('dotwire.legal_hold', 0));

    FOR v_job IN
        SELECT job_id
        FROM timescaledb_information.jobs
        WHERE proc_name = 'policy_retention' AND hypertable_name IN ('messages', 'access_log')
    LOOP
        PERFORM alter_job(v_job, scheduled => NOT EXISTS (
            SELECT 1 FROM legal_holds h
            WHERE NOT EXISTS (SELECT 1 FROM legal_hold_releases r WHERE r.hold_id = h.id)
        ));
    END LOOP;
END
$$;

CREATE OR REPLACE FUNCTION retention_job_running() RETURNS boolean
LANGUAGE sql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
    SELECT EXISTS (
        SELECT 1
        FROM timescaledb_information.jobs j
        JOIN timescaledb_information.job_stats s ON s.job_id = j.job_id
        WHERE j.proc_name = 'policy_retention' AND j.hypertable_name IN ('messages', 'access_log') AND s.job_status = 'Running'
    )
$$;

CREATE OR REPLACE FUNCTION get_retention_paused() RETURNS boolean
LANGUAGE sql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
    SELECT EXISTS (
        SELECT 1 FROM timescaledb_information.jobs
        WHERE proc_name = 'policy_retention' AND hypertable_name IN ('messages', 'access_log') AND scheduled = false
    )
$$;

CREATE FUNCTION set_access_log_retention(p_days integer) RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
BEGIN
    PERFORM pg_advisory_xact_lock_shared(hashtextextended('dotwire.legal_hold', 0));

    PERFORM remove_retention_policy('access_log', if_exists => true);

    IF p_days > 0 THEN
        PERFORM add_retention_policy('access_log', make_interval(days => p_days));
    END IF;

    PERFORM sync_retention_schedule();

    RETURN p_days;
END
$$;

REVOKE ALL ON FUNCTION set_access_log_retention(integer) FROM PUBLIC;
