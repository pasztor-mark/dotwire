CREATE UNIQUE INDEX audit_log_prev_hash ON audit_log (prev_hash);

ALTER TABLE audit_log ADD COLUMN stream_time timestamptz;

CREATE FUNCTION audit_checkpoints_validate() RETURNS trigger
LANGUAGE plpgsql
SET search_path = public, pg_temp
AS $$
BEGIN
    IF NEW.day > (now() AT TIME ZONE 'UTC')::date + 1 THEN
        RAISE EXCEPTION 'audit checkpoint day % is in the future', NEW.day;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM audit_log WHERE id = NEW.last_id AND hash = NEW.hash) THEN
        RAISE EXCEPTION 'audit checkpoint does not match audit_log row %', NEW.last_id;
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER audit_checkpoints_validate
    BEFORE INSERT ON audit_checkpoints
    FOR EACH ROW EXECUTE FUNCTION audit_checkpoints_validate();
