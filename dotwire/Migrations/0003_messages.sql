-- Message history: hypertable keyed (room_id, time), JetStream seq stored alongside so
-- rows correlate back to the ordering token (ARCHITECTURE.md, "Data layout").
-- content is the encryption envelope: nonce (12) ‖ ciphertext ‖ GCM tag (16); key_id is a
-- column because rotation tooling queries it. time IS the send time (gateway clock at
-- publish) . do not add a second timestamp column.
-- The PK includes time because any hypertable unique constraint must contain the
-- partition column; (room_id, seq) alone is rejected by Timescale.
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

-- Gap-fill ("room X, seq > N") runs on this index; room_id leads per shard-key discipline.
CREATE INDEX messages_room_seq ON messages (room_id, seq);

GRANT SELECT, INSERT, UPDATE, DELETE ON messages TO dotwire_app;
