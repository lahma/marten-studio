namespace PostgresStudio;

/// <summary>
/// The reads Postgres Studio authorizes like writes. Postgres Studio changes nothing in any database; each of these is
/// a read that reaches past a schema's structure - into rows and definitions, into whatever SQL can read, into other
/// people's sessions - and so is off until the host enables it, one switch at a time or with <see cref="All" />.
/// <see cref="PostgresStudioOptions.ReadOnly" /> overrides every property here.
/// </summary>
public sealed class PostgresStudioCapabilities
{
    /// <summary>
    /// Run read-only SQL from the Query page. The statement runs inside a read-only transaction under a statement
    /// timeout, but it reads everything the data source's Postgres role can read (or
    /// <see cref="PostgresStudioOptions.SqlConsoleRole" />, when set); grant this only to people you would give
    /// <c>psql</c> to.
    /// </summary>
    public bool RunSql { get; set; }

    /// <summary>
    /// Read the rows and the definitions (view SQL, function bodies, trigger definitions) of the database objects in
    /// the schemas <see cref="PostgresStudioOptions.BrowsableSchemas" /> names. Their structure is visible without it.
    /// It is a read treated as a write for authorization: <see cref="PostgresStudioOptions.WriteAuthorizationPolicy" />
    /// is asked, against the database as a whole, and <see cref="PostgresStudioOptions.ReadOnly" /> turns it off with
    /// everything else. A Marten table (<c>mt_</c>) is never read this way.
    /// </summary>
    public bool BrowseDatabase { get; set; }

    /// <summary>
    /// See who and what is using the database: the process ids, roles, client addresses and application names of
    /// other sessions, and the text of the statements they are running or have run. Statement text carries literal
    /// values and other schemas' names. Without it the health advisor and Sessions show counts and ages, never those
    /// details. A read authorized as a write, like <see cref="BrowseDatabase" />.
    /// </summary>
    public bool ViewDatabaseActivity { get; set; }

    /// <summary>Every capability enabled. Still subject to <see cref="PostgresStudioOptions.ReadOnly" />.</summary>
    public static PostgresStudioCapabilities All() => new()
    {
        RunSql = true,
        BrowseDatabase = true,
        ViewDatabaseActivity = true,
    };
}
