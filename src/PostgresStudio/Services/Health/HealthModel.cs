namespace PostgresStudio.Services.Health;

/// <summary>How much a finding matters.</summary>
/// <remarks>Declared worst first: a list sorted by this value puts the Critical findings at the top.</remarks>
internal enum HealthSeverity
{
    /// <summary>Something is broken now, or will be soon, and somebody should act today.</summary>
    Critical,

    /// <summary>Something is wrong or risky, and is worth fixing on purpose.</summary>
    Warning,

    /// <summary>A suggestion: a setting that would let the store, or the advisor, tell you more.</summary>
    Info,
}

/// <summary>Which part of the store a check is about - how the Health page groups its findings.</summary>
/// <remarks>
/// <para>
/// Declared in the order the page draws them: the daemon first, because "why aren't my projections moving" is
/// the question people ask most; then the store's own data; then the server the store runs on; and the store's
/// configuration last. The value is never persisted - a mute key, a history series and a docs link all name an
/// area by its code or its file - so a member added in the middle moves nothing a visitor kept.
/// </para>
/// <para>
/// <see cref="Replication" /> and <see cref="Server" /> were added in 0.4 (R1, decision 1): what a replication
/// slot holds and what the server is configured to risk are neither the store's tables nor its sessions, and
/// <see cref="HealthArea.Configuration" /> stays the store's own, read with no I/O.
/// </para>
/// </remarks>
internal enum HealthArea
{
    /// <summary>The async daemon, its shards and the high-water mark (D codes).</summary>
    Daemon,

    /// <summary>The event store's traffic and shape (E codes).</summary>
    Events,

    /// <summary>Marten's tables and their indexes (T codes).</summary>
    Tables,

    /// <summary>Connections, sessions and locks (C codes).</summary>
    Connections,

    /// <summary>Replication slots and the WAL they hold (R codes).</summary>
    Replication,

    /// <summary>The Postgres server itself: its version, its settings, its storage (S codes).</summary>
    Server,

    /// <summary>The store's own configuration (K codes).</summary>
    Configuration,
}

/// <summary>What a check costs the database it looks at, and so when it may run.</summary>
internal enum HealthCostTier
{
    /// <summary>Configuration and in-process state. No I/O at all.</summary>
    InProcess = 0,

    /// <summary>The catalog and the statistics views: <c>pg_class</c>, <c>pg_stat_*</c>, <c>pg_locks</c>.</summary>
    Catalog = 1,

    /// <summary>
    /// Bounded reads of the data itself. Only behind "Run deeper checks", and cached for five minutes.
    /// </summary>
    BoundedData = 2,

    /// <summary>
    /// <c>pg_stat_statements</c> and <c>EXPLAIN</c>. Only for a visitor through the
    /// <see cref="HealthGate.Activity" /> gate, because what it reads is other people's statements.
    /// </summary>
    Statements = 3,
}

/// <summary>Who may see a finding, or one piece of its evidence.</summary>
/// <remarks>
/// <para>
/// Asked per visitor, after the shared sweep: a sweep's facts are about the database and the same for
/// everybody, and what each visitor is shown of them is decided when the report is drawn (AGENTS.md D27).
/// </para>
/// <para>
/// A check has a gate, and so does each piece of evidence, so that a check can show a count to everybody
/// and the names behind the count only to a visitor through <see cref="Activity" /> - which is the shape the
/// sessions and statements checks need.
/// </para>
/// </remarks>
internal enum HealthGate
{
    /// <summary>
    /// Anyone the store policy passes for the scope they selected - what every studio page asks. A
    /// configuration fact is no more than the Configuration page already shows.
    /// </summary>
    None,

    /// <summary>
    /// The store policy, asked for the database as a whole (<c>TenantId = null</c>): a database-wide number
    /// is about every tenant at once.
    /// </summary>
    Database,

    /// <summary>
    /// <c>ViewDatabaseActivity</c>: the capability on (and <c>ReadOnly</c> off), the store policy for the
    /// database as a whole, and the write policy with the capability named, against the same tenant-less
    /// resource. Anything that names a session, a role, a client address, an application, a statement or a
    /// tenant is behind this.
    /// </summary>
    Activity,
}
