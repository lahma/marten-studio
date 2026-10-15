namespace MartenStudio;

/// <summary>
/// The operations Marten Studio is allowed to perform beyond browsing the store: the mutating ones, and
/// the reads that are authorized like writes - the two that reach past the store's own documents and
/// events (<see cref="RunSql"/> and <see cref="BrowseDatabase"/>), the one that names other sessions
/// (<see cref="ViewDatabaseActivity"/>) and the one that evaluates a typed expression
/// (<see cref="RunLinqQueries"/>). Every property defaults to <see langword="false"/>: a freshly mapped
/// studio is a read-only browser of the store until the host enables what it needs, one switch at a
/// time, or calls <see cref="All"/>. <see cref="MartenStudioOptions.ReadOnly"/> overrides every property
/// here.
/// </summary>
public sealed class MartenStudioCapabilities
{
    /// <summary>Edit a document's JSON and save it through the store's serializer and session.</summary>
    public bool EditDocuments { get; set; }

    /// <summary>Delete, soft-delete or undelete documents.</summary>
    public bool DeleteDocuments { get; set; }

    /// <summary>Archive event streams.</summary>
    public bool ArchiveStreams { get; set; }

    /// <summary>Discard dead-letter events, mark events as skipped, or rewind a subscription to a sequence.</summary>
    public bool ManageDeadLetters { get; set; }

    /// <summary>Start and stop projection agents and the async daemon hosted in this process.</summary>
    public bool ControlDaemon { get; set; }

    /// <summary>Rebuild projections.</summary>
    public bool RebuildProjections { get; set; }

    /// <summary>Advance the high-water mark or correct projection progression in the database.</summary>
    public bool CorrectProgression { get; set; }

    /// <summary>Apply pending schema migrations to the database.</summary>
    public bool ApplySchemaChanges { get; set; }

    /// <summary>
    /// Run read-only SQL from the query page. The statement runs inside a read-only transaction
    /// under a statement timeout, but it reads everything the store's Postgres role can read;
    /// grant this only to people you would give <c>psql</c> to.
    /// </summary>
    public bool RunSql { get; set; }

    /// <summary>
    /// Read the rows and the definitions (view SQL, function bodies, trigger definitions) of the
    /// database objects Marten does not own, in the schemas <see cref="MartenStudioOptions.BrowsableSchemas"/>
    /// names, and see those schemas' structure at all. Like <see cref="RunSql"/> it is a read that is
    /// treated as a write for authorization: <see cref="MartenStudioOptions.WriteAuthorizationPolicy"/> is
    /// asked, against the database as a whole with no tenant, and <see cref="MartenStudioOptions.ReadOnly"/>
    /// turns it off with everything else. Rows of Marten's own document and event tables are never read
    /// this way; they are browsed where tenancy, soft delete and the serializer apply.
    /// </summary>
    public bool BrowseDatabase { get; set; }

    /// <summary>
    /// See who and what is using the store's database: the process ids, roles, client addresses and
    /// application names of other sessions, the text of the statements they are running or have run, and
    /// the tenants a database-wide finding names. It is for the people who operate the database rather
    /// than the people who browse its data, and what it reveals is other applications' as much as this
    /// one's - statement text carries literal values and other schemas' names. Without it the health
    /// advisor still shows counts and ages, never those details. Like <see cref="BrowseDatabase"/> it is a
    /// read authorized as a write: <see cref="MartenStudioOptions.WriteAuthorizationPolicy"/> is asked,
    /// against the database as a whole with no tenant, and <see cref="MartenStudioOptions.ReadOnly"/> turns
    /// it off with everything else.
    /// </summary>
    public bool ViewDatabaseActivity { get; set; }

    /// <summary>
    /// Compact event streams: replace a long stream's history with a snapshot of its aggregate, through
    /// Marten's own <c>CompactStreamAsync</c>. It is for the people who own the event store, and it changes
    /// what a stream's events are - the compacted events are gone from the stream, and a projection
    /// rebuilt afterwards sees only what compaction left.
    /// </summary>
    public bool CompactStreams { get; set; }

    /// <summary>
    /// Run LINQ queries from the Query page: a typed predicate over a document type, parsed in this process
    /// and handed to Marten's own <c>Query&lt;T&gt;()</c>, so tenancy and soft deletes stay Marten's. It
    /// changes nothing, but it evaluates an expression somebody typed inside the host application, so it
    /// is for people trusted with the store's data; like <see cref="RunSql"/>, it is a read authorized as a
    /// write, and <see cref="MartenStudioOptions.ReadOnly"/> turns it off with everything else.
    /// </summary>
    public bool RunLinqQueries { get; set; }

    /// <summary>Every capability enabled. Still subject to <see cref="MartenStudioOptions.ReadOnly"/>.</summary>
    public static MartenStudioCapabilities All() => new()
    {
        EditDocuments = true,
        DeleteDocuments = true,
        ArchiveStreams = true,
        ManageDeadLetters = true,
        ControlDaemon = true,
        RebuildProjections = true,
        CorrectProgression = true,
        ApplySchemaChanges = true,
        RunSql = true,
        BrowseDatabase = true,
        ViewDatabaseActivity = true,
        CompactStreams = true,
        RunLinqQueries = true,
    };
}
