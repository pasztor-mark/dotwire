-- dotwire:no-transaction
-- The timescale/timescaledb image preloads the library and pre-creates the extension in
-- POSTGRES_DB, so this is normally a no-op; it exists so a plain-Postgres-with-extension
-- deployment also converges. Kept outside a transaction: CREATE EXTENSION timescaledb is
-- picky about transaction blocks in some versions.
CREATE EXTENSION IF NOT EXISTS timescaledb;
