CREATE TABLE messages (
    room_id   uuid        NOT NULL,
    time      timestamptz NOT NULL,
    seq       bigint      NOT NULL,
    sender_id text        NOT NULL,
    key_id    text        NOT NULL,
    content   bytea       NOT NULL,
    PRIMARY KEY (room_id, time, seq)
);

SELECT create_hypertable('messages', 'time', chunk_time_interval => INTERVAL '1 day');

CREATE INDEX messages_room_seq ON messages (room_id, seq);

GRANT SELECT, INSERT ON messages TO dotwire_app;
REVOKE UPDATE, DELETE, TRUNCATE ON messages FROM dotwire_app;
