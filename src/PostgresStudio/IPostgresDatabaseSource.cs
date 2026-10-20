using Npgsql;

namespace PostgresStudio;

/// <summary>
/// A set of this application's databases that the application lists itself: one database per tenant, for example.
/// Named with <see cref="PostgresStudioOptions.AddDatabaseSource" />.
/// </summary>
/// <remarks>
/// <para>
/// <b>Listing is cheap and changes nothing.</b> <see cref="ListAsync" /> names the databases and must not create a
/// database, run DDL or build a data source. The studio lists at most once a minute, and again when a visitor asks for
/// a refresh. It asks <see cref="GetDataSourceAsync" /> only for a database somebody opens or a sweep reaches.
/// </para>
/// <para>
/// <b>Your data sources, never new ones.</b> Return the application's own <see cref="NpgsqlDataSource" /> for the
/// database, cached by you. The studio never disposes it and never keeps it beyond one read. A source that built a new
/// data source per call would leak a pool per call.
/// </para>
/// <para>
/// <b>Names are identities.</b> A name keys authorization (<see cref="PostgresStudioResource.DatabaseIdentifier" />),
/// the health history, mutes and caches, so it must stay the same for the same database, including if it moves server.
/// It must match <c>^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$</c> and be unique, case-insensitively, within the source and
/// across every other source's database names. A name that is not is left out and logged.
/// </para>
/// <para>New members are added only as default interface members.</para>
/// </remarks>
public interface IPostgresDatabaseSource
{
    /// <summary>The databases' names, in the order the studio should list and sweep them.</summary>
    /// <param name="cancellationToken">Cancelled after <c>QueryTimeout</c>.</param>
    IAsyncEnumerable<string> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The application's own data source for <paramref name="name" />, or null when it is no longer one of this
    /// source's databases.
    /// </summary>
    /// <param name="name">A name <see cref="ListAsync" /> returned.</param>
    /// <param name="cancellationToken">Cancelled after <c>QueryTimeout</c>.</param>
    ValueTask<NpgsqlDataSource?> GetDataSourceAsync(string name, CancellationToken cancellationToken);
}
