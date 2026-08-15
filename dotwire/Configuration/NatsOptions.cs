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

    /// <summary>Wildcard filter covering all room subjects (stream + consumer scope).</summary>
    public string RoomsSubjectFilter { get; set; } = "room.>";

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
}
