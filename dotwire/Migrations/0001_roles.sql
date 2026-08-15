DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'dotwire_app') THEN
        CREATE ROLE dotwire_app LOGIN;
    END IF;
END
$$;

REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE CREATE ON SCHEMA public FROM dotwire_app;
GRANT USAGE ON SCHEMA public TO dotwire_app;
