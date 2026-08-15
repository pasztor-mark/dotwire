-- Runtime service role. The migrator role (compose POSTGRES_USER, owner) applies schema;
-- the app connects as dotwire_app, whose grants are the real append-only enforcement on
-- audit_log, owners bypass grants, so testing enforcement as the owner proves nothing.
-- Password is set by the migration runner (ALTER ROLE from config) . never embedded here.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'dotwire_app') THEN
        CREATE ROLE dotwire_app LOGIN;
    END IF;
END
$$;

GRANT USAGE ON SCHEMA public TO dotwire_app;
