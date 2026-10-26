namespace PostgresStudio;

/// <summary>A database the studio can read: a source's name, and that source's name for one of its databases.</summary>
public sealed record PostgresDatabaseRef
{
    /// <summary>Names a database.</summary>
    /// <param name="source">
    /// The source's name: <c>default</c>, a name given to <see cref="PostgresStudioOptions.AddDataSource" /> or
    /// <see cref="PostgresStudioOptions.AddDatabaseSource" />, or in Marten Studio a store's key.
    /// </param>
    /// <param name="database">The source's name for the database. Empty means the source's only (or default) database.</param>
    /// <exception cref="ArgumentException"><paramref name="source" /> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="database" /> is null.</exception>
    public PostgresDatabaseRef(string source, string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(database);

        Source = source;
        Database = database;
    }

    /// <summary>The source's name. The studio matches it case-insensitively; equality here is ordinal.</summary>
    public string Source { get; }

    /// <summary>
    /// The database: a data source's registration name, a name a host's <see cref="IPostgresDatabaseSource" /> lists, or
    /// in Marten Studio the database's identity.
    /// </summary>
    public string Database { get; }
}
