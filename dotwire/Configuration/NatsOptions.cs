namespace Dotwire.Configuration;

public sealed class NatsOptions
{
    public const string SectionName = "Nats";

    public string Url { get; set; } = "nats://localhost:4222";

    /// <summary>
    /// Master switch for NATS at startup: provisioning and the batch-insert consumer.
    /// In-process tests turn this off, mirroring <c>Postgres:Migrate</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Per-room subject prefix; full subject is <c>room.{room_id}</c>.</summary>
    public string RoomSubjectPrefix { get; set; } = "room.";

    /// <summary>
    /// Matches exactly one token after <c>room.</c> so JetStream only captures durable room
    /// traffic (<c>room.{roomId}</c>) - the live/ephemeral core subjects (<c>room.{roomId}.live</c>,
    /// <c>room.{roomId}.ephemeral.*</c>) stay off the stream (spec §3.1).
    /// </summary>
    public string RoomsSubjectFilter { get; set; } = "room.*";

    /// <summary>Core (non-JetStream) subject for seq-tagged live fanout: <c>room.{room_id}.live</c>.</summary>
    public string RoomLiveSubjectSuffix { get; set; } = ".live";

    /// <summary>Batch-insert consumer: max messages per batch (docs/pre-implementation.md §1.6).</summary>
    public int BatchMaxMessages { get; set; } = 500;

    /// <summary>Batch-insert consumer: linger before a partial batch is flushed.</summary>
    public int BatchLingerMs { get; set; } = 1000;

    /// <summary>JetStream stream for room traffic; subjects <c>room.{room_id}</c>.</summary>
    public string RoomsStream { get; set; } = "ROOMS";

    /// <summary>
    /// Room stream retention. Only needs to cover batch-writer lag plus the reconnect
    /// gap-fill window. Postgres is the durable archive (ARCHITECTURE.md, "Data lifecycle").
    /// </summary>
    public int RoomsMaxAgeHours { get; set; } = 48;

    /// <summary>JetStream stream for audit events, kept off the room subject space.</summary>
    public string AuditStream { get; set; } = "AUDIT";

    public string AuditSubject { get; set; } = "audit.events";

    /// <summary>Durable consumer that batch-inserts messages into Postgres.</summary>
    public string PostgresWriterConsumer { get; set; } = "postgres-writer";

    /// <summary>Durable consumer that hash-chains audit events (single-writer . see ARCHITECTURE.md, "Audit log").</summary>
    public string AuditWriterConsumer { get; set; } = "audit-writer";

    public AuditWriterSection AuditWriter { get; set; } = new();
}

/// <summary>Audit-writer batch/standby knobs (spec §3.16: <c>Nats:AuditWriter:*</c>).</summary>
public sealed class AuditWriterSection
{
    /// <summary>Max messages the audit writer pulls per JetStream fetch.</summary>
    public int BatchMaxMessages { get; set; } = 200;

    /// <summary>Linger before a partial batch is flushed.</summary>
    public int BatchLingerMs { get; set; } = 1000;

    /// <summary>Delay before retrying the advisory lock when another node holds it.</summary>
    public int StandbyRetrySeconds { get; set; } = 5;
}
