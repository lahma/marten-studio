namespace MartenStudio;

/// <summary>
/// The mutating operations Marten Studio is allowed to perform. Every property defaults to
/// <see langword="false"/>: a freshly mapped studio is a read-only browser until the host enables
/// what it needs, one switch at a time, or calls <see cref="All"/>.
/// <see cref="MartenStudioOptions.ReadOnly"/> overrides every property here.
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
    };
}
