namespace PostgresStudio;

/// <summary>Database size, growth and the largest tables: numbers about the database as a whole.</summary>
public sealed record PostgresStorageSection
{
    /// <summary>Whether it was read; when not, the other properties keep their defaults.</summary>
    public PostgresReadOutcome Outcome { get; init; }

    /// <summary>
    /// Why it was not read, or - when it was - what is missing from it: the database's own size where the role may not
    /// measure it (naming the grant), or why there is no growth figure yet.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>When it was read.</summary>
    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary>
    /// The database's size on disk, in bytes (<c>pg_database_size</c>) - or, where the role may not measure it, the catalog's
    /// estimate of it, which <see cref="Reason" /> then says.
    /// </summary>
    public long DatabaseBytes { get; init; }

    /// <summary>
    /// Whether table sizes come from the catalog's page counts (as of the last VACUUM or ANALYZE) rather than from the files.
    /// A list locks nothing it lists, so this is true.
    /// </summary>
    public bool SizesAreEstimates { get; init; }

    /// <summary>Since when the statistics behind the growth figures have counted.</summary>
    public DateTimeOffset? StatisticsSince { get; init; }

    /// <summary>Estimated growth a day; null until the statistics have counted for a day, and while they look reset.</summary>
    public long? GrowthBytesPerDay { get; init; }

    /// <summary>The largest tables, largest first: at most fifteen.</summary>
    public IReadOnlyList<PostgresTableStorage> LargestTables { get; init; } = [];
}
