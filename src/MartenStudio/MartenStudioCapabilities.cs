namespace MartenStudio;

/// <summary>
/// The operations Marten Studio is allowed to perform beyond browsing the store: the mutating ones, and
/// the two reads that reach past the store's own documents and events (<see cref="RunSql"/> and
/// <see cref="BrowseDatabase"/>). Every property defaults to <see langword="false"/>: a freshly mapped
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
    };
}
