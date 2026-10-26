using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Npgsql;

using NpgsqlTypes;

using PostgresStudio.SampleData;

namespace PostgresStudio.Sample;

/// <summary>
/// This application's tenant databases, as the application itself knows them: listed from the admin database's tenant
/// directory, each opened through a data source the application builds once and keeps. Registered with
/// <c>PostgresStudioOptions.AddDatabaseSource("tenants", …)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape of host B</b> (issue #8): an admin database that knows the tenants, and one database per tenant that
/// can be added while the process runs. Listing reads the directory and nothing else - no data source is built, no
/// database is touched - so a list of five hundred tenants costs one query. A tenant's data source is built the first
/// time somebody opens that tenant or a health sweep reaches it, and kept for the life of the process.
/// </para>
/// <para>
/// <b>The application's own data sources.</b> The studio never disposes what this hands it and never keeps it beyond
/// one read, so the caching is here, and so is the disposal: the container disposes this singleton when the host
/// stops, and it disposes every pool it built. A source that built a new data source per call would leak a pool per
/// call.
/// </para>
/// <para>
/// <b>Names are identities.</b> The studio authorizes, audits and keys its caches on the name the directory lists, never
/// on a connection string, so a tenant keeps its health history and its mutes if its database moves server. The
/// directory's own check holds names to the pattern the studio accepts.
/// </para>
/// </remarks>
/// <param name="admin">The application's admin data source, which the directory is read through.</param>
/// <param name="serverConnectionString">
/// The admin database's connection string, with its credentials: the tenant databases are on the same server and are
/// opened as the same role. A data source's own <c>ConnectionString</c> no longer carries the password, so it is passed
/// in rather than read back.
/// </param>
internal sealed class SampleTenantDatabases(NpgsqlDataSource admin, string serverConnectionString) : IPostgresDatabaseSource, IAsyncDisposable
{
    private const string Directory = RelationalDemoSchema.SampleSchemaName + "." + TenantDemo.DirectoryTable;

    private readonly ConcurrentDictionary<string, Lazy<NpgsqlDataSource>> dataSources = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await admin.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select name from " + Directory + " order by name", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            yield return reader.GetString(0);
        }
    }

    /// <inheritdoc />
    public async ValueTask<NpgsqlDataSource?> GetDataSourceAsync(string name, CancellationToken cancellationToken)
    {
        string? database = await DatabaseOfAsync(name, cancellationToken);

        if (database is null)
        {
            // No longer one of this application's tenants: the studio says so rather than open anything.
            return null;
        }

        // Lazy, so that two circuits opening the same tenant at once build one pool between them.
        return dataSources.GetOrAdd(database, key => new Lazy<NpgsqlDataSource>(() => Build(key))).Value;
    }

    /// <summary>Disposes every pool this source built. The admin data source is the container's, not this one's.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (Lazy<NpgsqlDataSource> dataSource in dataSources.Values.Where(static x => x.IsValueCreated))
        {
            await dataSource.Value.DisposeAsync();
        }

        dataSources.Clear();
    }

    private async Task<string?> DatabaseOfAsync(string name, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await admin.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select database_name from " + Directory + " where name = @name", connection);
        command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name });

        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private NpgsqlDataSource Build(string database) =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = database }.ConnectionString);
}
