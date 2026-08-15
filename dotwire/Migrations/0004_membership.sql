-- Authoritative role/membership tables (ARCHITECTURE.md, "Data layout"): the JWT claims a
-- role, these tables decide. Role spelling is normative (AUTH.md) - no variants.
CREATE TABLE user_roles (
    user_id text PRIMARY KEY,
    role    text NOT NULL CHECK (role IN ('member', 'auditor', 'admin'))
);

CREATE TABLE room_members (
    room_id uuid NOT NULL,
    user_id text NOT NULL,
    PRIMARY KEY (room_id, user_id)
);

GRANT SELECT, INSERT, UPDATE, DELETE ON user_roles, room_members TO dotwire_app;
