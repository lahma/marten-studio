using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// One PostgreSQL database reached through the application's own <see cref="NpgsqlDataSource" />, named by its source:
/// a DI or <c>AddDataSource</c> source's registration name, or the name a host's <see cref="IPostgresDatabaseSource" />
/// lists it under (the design's identity amendment).
/// </summary>
/// <remarks>
/// <para>
/// <b>Never disposed here.</b> The data source is the application's; the studio opens connections from it and closes
/// them, and that is all. A record, so that two wrappers of one data source under one name are equal and a
/// <see cref="ResolvedSource" /> keeps value equality.
/// </para>
/// <para>
/// <see cref="Name" /> and <see cref="Server" /> are read from the data source's connection string, which Npgsql hands
/// back without the password unless <c>Persist Security Info</c> is set - and neither is ever a credential: the database
/// falls back to the user name, as Postgres does, and a multi-host string keeps Npgsql's comma list.
/// </para>
/// </remarks>
/// <param name="Identity">The source-defined identity.</param>
/// <param name="DataSource">The application's data source.</param>
internal sealed record DataSourceDatabase(string Identity, NpgsqlDataSource DataSource) : IStudioDatabase
{
    private readonly (string Name, string Server) endpoint = Describe(DataSource);

    /// <inheritdoc />
    public string Name => endpoint.Name;

    /// <inheritdoc />
    public string Server => endpoint.Server;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// One pool for both usages: a plain data source does not tell a read replica from the primary, and a multi-host one
    /// is not re-targeted - what answered is what a server read reports.
    /// </para>
    /// <para>
    /// Metered, open to close, for the studio's cost line (<see cref="StudioConnectionMeter" />, W5-Q1): every statement
    /// the studio issues here runs on a connection from this.
    /// </para>
    /// </remarks>
    public NpgsqlConnection CreateConnection(StudioConnectionUsage usage) => StudioConnectionMeter.Attach(DataSource.CreateConnection());

    private static (string Name, string Server) Describe(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        try
        {
            NpgsqlConnectionStringBuilder settings = new(dataSource.ConnectionString);
            return (settings.Database ?? settings.Username ?? string.Empty, settings.Host ?? string.Empty);
        }
        catch (ArgumentException)
        {
            return (string.Empty, string.Empty);
        }
    }
}

/// <summary>
/// A database a host's <see cref="IPostgresDatabaseSource" /> listed, before anybody opened it: a name and nothing more,
/// because listing five hundred tenants must not build five hundred pools (API design S1).
/// </summary>
/// <remarks>
/// It is what the pickers list and what the resolver matches a scope against. Opening it is
/// <see cref="IStudioSource.CompleteAsync" />'s job, which asks the host for the data source and answers a
/// <see cref="DataSourceDatabase" /> in its place; nothing ever opens a connection from this one.
/// </remarks>
/// <param name="SourceKey">The source that listed it.</param>
/// <param name="Identity">The name the host listed it under.</param>
internal sealed record HostListedDatabase(string SourceKey, string Identity) : IStudioDatabase
{
    /// <inheritdoc />
    public string Name => Identity;

    /// <inheritdoc />
    /// <remarks>Unknown until the host hands over the data source.</remarks>
    public string Server => string.Empty;

    /// <inheritdoc />
    public NpgsqlDataSource? DataSource => null;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Always: a listed database is opened through the source, never here.</exception>
    public NpgsqlConnection CreateConnection(StudioConnectionUsage usage) =>
        throw new InvalidOperationException(
            "The database '" + Identity + "' of source '" + SourceKey + "' has been listed, not opened: resolve the scope first.");
}
