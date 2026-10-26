using Marten.Storage;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Sources;

using Npgsql;

using Weasel.Postgresql;

namespace MartenStudio.Services;

/// <summary>
/// One Marten database as the shared studio sees it: <c>Id.Identity</c>, <c>CreateConnection</c> and the data source,
/// and nothing else of <see cref="IMartenDatabase" /> - in particular no member that builds schema objects, runs a
/// migration or opens a session (AGENTS.md hard rules 14 and 15).
/// </summary>
/// <remarks>
/// A record over the instance, so that two wrappers of one <see cref="IMartenDatabase" /> are equal (reference equality
/// on the instance, which Marten's databases do not override) and a <see cref="ResolvedScope" /> keeps value equality.
/// Same-identity twins of a master-table tenancy are different instances and so different wrappers, as they must be:
/// the instance is what decides the role and the pool a tenant's connection uses.
/// </remarks>
/// <param name="Database">The Marten database.</param>
internal sealed record MartenStudioDatabase(IMartenDatabase Database) : IStudioDatabase
{
    /// <inheritdoc />
    public string Identity => Database.Id.Identity;

    /// <inheritdoc />
    public string Name => Database.Id.Name;

    /// <inheritdoc />
    public string Server => Database.Id.Server;

    /// <inheritdoc />
    /// <remarks>
    /// Marten 9's <c>MartenDatabase</c> derives from Weasel's <see cref="PostgresqlDatabase" />, whose public
    /// <see cref="PostgresqlDatabase.DataSource" /> is the data source every connection of that database comes from -
    /// the daemon's leadership lock included (read at Marten V9.39.0).
    /// </remarks>
    public NpgsqlDataSource? DataSource => Database is PostgresqlDatabase { DataSource: { } dataSource } ? dataSource : null;

    /// <inheritdoc />
    /// <remarks>
    /// Metered, open to close, for the studio's cost line (<see cref="StudioConnectionMeter" />, W5-Q1) - as is every
    /// connection MartenStudio's own reads take from a Marten database (<see cref="CreateConnection(IMartenDatabase, ConnectionUsage)" />).
    /// </remarks>
    public NpgsqlConnection CreateConnection(StudioConnectionUsage usage) =>
        CreateConnection(Database, usage == StudioConnectionUsage.Read ? ConnectionUsage.Read : ConnectionUsage.ReadWrite);

    /// <summary>
    /// A new, unopened connection to <paramref name="database" />, metered for the studio's cost line - the one way
    /// MartenStudio's own reads (documents, events, projections, the Query page, the schema reads, tenant discovery) take a
    /// connection from a Marten database, so that every statement of theirs is counted (W5-Q1, backlog §C). A Marten
    /// session the studio opens for a write or to steer the daemon takes its connection from Marten, and is not.
    /// </summary>
    /// <param name="database">The Marten database.</param>
    /// <param name="usage">Which pool: Marten's read pool, where the database has one, or the primary.</param>
    public static NpgsqlConnection CreateConnection(IMartenDatabase database, ConnectionUsage usage)
    {
        ArgumentNullException.ThrowIfNull(database);
        return StudioConnectionMeter.Attach(database.CreateConnection(usage));
    }

    /// <summary>The wrapper of <paramref name="database" />.</summary>
    public static MartenStudioDatabase Of(IMartenDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new MartenStudioDatabase(database);
    }

    /// <summary>The Marten database behind a shared one this studio's provider answered.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="database" /> is not one of Marten's.</exception>
    public static IMartenDatabase Unwrap(IStudioDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database is MartenStudioDatabase marten
            ? marten.Database
            : throw new InvalidOperationException("The database " + database.Identity + " was not answered by a Marten store.");
    }
}
