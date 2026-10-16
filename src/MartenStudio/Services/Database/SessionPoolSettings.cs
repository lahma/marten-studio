using Marten.Storage;

using Npgsql;

using Weasel.Postgresql;

namespace MartenStudio.Services.Database;

/// <summary>
/// What a store database's own connection pool is configured to be, read from its <c>NpgsqlDataSource</c>'s
/// connection string - without opening a connection.
/// </summary>
/// <param name="Pooling">Whether the data source pools at all (<c>Pooling=false</c> opens a physical connection each time).</param>
/// <param name="MaxPoolSize">Its <c>Max Pool Size</c>: Npgsql's default is 100.</param>
/// <param name="ServerKey">
/// The server it points at, as <c>host:port</c> in lower case - what C02 groups a store's databases by. Two
/// spellings of one server ("localhost" and "127.0.0.1") are two keys, which errs towards a smaller sum.
/// </param>
/// <param name="DatabaseName">The Postgres database it connects to.</param>
internal sealed record DatabasePoolSettings(bool Pooling, int MaxPoolSize, string ServerKey, string? DatabaseName)
{
    /// <summary>
    /// The pool settings of <paramref name="database" />, or <see langword="null" /> when its data source is not
    /// one the studio can read (a database that is not Weasel's <see cref="PostgresqlDatabase" />, or a
    /// connection string Npgsql will not parse).
    /// </summary>
    /// <remarks>
    /// Marten 9's <c>MartenDatabase</c> derives from Weasel's <see cref="PostgresqlDatabase" />, whose public
    /// <see cref="PostgresqlDatabase.DataSource" /> is the data source every connection of that database comes
    /// from - the async daemon's leadership lock included (<c>ProjectionCoordinator</c> builds its
    /// <c>AdvisoryLock</c> on the same <c>DataSource</c>, read at Marten V9.39.0). Its
    /// <see cref="NpgsqlDataSource.ConnectionString" /> is the connection string without the password, unless
    /// <c>Persist Security Info</c> is on; it is parsed here and never kept.
    /// </remarks>
    public static DatabasePoolSettings? Of(IMartenDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (database is not PostgresqlDatabase { DataSource: { } dataSource })
        {
            return null;
        }

        try
        {
            NpgsqlConnectionStringBuilder settings = new(dataSource.ConnectionString);

            return new DatabasePoolSettings(
                settings.Pooling,
                settings.MaxPoolSize,
                ((settings.Host ?? string.Empty) + ":" + settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ToLowerInvariant(),
                settings.Database);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="poolName" /> - Npgsql's <c>pool.name</c> tag - is <paramref name="database" />'s
    /// pool. Npgsql names a data source after its connection string unless the host gave it a name of its own
    /// (<c>NpgsqlDataSourceBuilder.Name</c>, which Npgsql keeps internal); such a pool cannot be matched, and
    /// C01 says so rather than guessing.
    /// </summary>
    public static bool IsPoolOf(IMartenDatabase database, string poolName)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(poolName);

        return database is PostgresqlDatabase { DataSource: { } dataSource }
            && string.Equals(dataSource.ConnectionString, poolName, StringComparison.Ordinal);
    }
}
