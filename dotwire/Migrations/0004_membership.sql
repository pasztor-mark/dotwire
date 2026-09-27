CREATE TABLE user_roles (
    user_id text PRIMARY KEY,
    role    text NOT NULL CHECK (role IN ('member', 'auditor', 'admin'))
);

INSERT INTO user_roles (user_id, role) VALUES ('host-admin', 'admin') ON CONFLICT DO NOTHING;

CREATE TABLE room_members (
    room_id uuid NOT NULL,
    user_id text NOT NULL,
    PRIMARY KEY (room_id, user_id)
);

GRANT SELECT, INSERT, UPDATE, DELETE ON user_roles, room_members TO dotwire_app;
REVOKE TRUNCATE ON user_roles, room_members FROM dotwire_app;
