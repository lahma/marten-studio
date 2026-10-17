using System.Security.Claims;

using Marten;
using Marten.Schema;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Tenancy;

/// <summary>
/// A studio over one store of a given tenancy shape, and one circuit's first navigation of it.
/// </summary>
/// <remarks>
/// <para>
/// "First navigation" is every call a freshly opened studio makes before anybody clicks anything, in the order
/// the layout and the Overview make them: the scope the layout settles (the stores, the databases of the one it
/// picks, the header's facts for the database it picks), the Overview's store cards, and then - as a visitor
/// working through the picker would - every database the picker offers, described for the header and resolved
/// for a page, and every tenant the header then offers, resolved too. Each of those is an enumeration of the
/// store's databases, a tenant discovery, or both.
/// </para>
/// <para>
/// The store the studio reads is built here and nowhere else, so it is as cold as a host's own store is the
/// first time somebody opens the studio: whatever a test prepared, it prepared through a store instance of its
/// own. That matters for a master-table tenancy, which applies its lookup table once per <em>instance</em>.
/// </para>
/// </remarks>
internal sealed class TenancyStudio : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    private TenancyStudio(ServiceProvider provider) => this.provider = provider;

    /// <summary>The store the studio reads.</summary>
    public IDocumentStore Store => provider.GetRequiredService<IDocumentStore>();

    /// <summary>A studio over a store configured by <paramref name="configure" />, and nothing else.</summary>
    public static TenancyStudio Start(Action<StoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var services = new ServiceCollection();
        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, SignedInVisitor>();
        services.AddMarten(configure);

        // Tenant discovery stays on: tier 2 and tier 3 are part of what a first navigation does.
        services.AddMartenStudio(static options => options.AuthorizationPolicy = null);

        return new TenancyStudio(services.BuildServiceProvider());
    }

    /// <summary>One circuit's first navigation, and what it was shown.</summary>
    public async Task<FirstNavigation> FirstNavigationAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();
        IServiceProvider services = circuit.ServiceProvider;

        var state = services.GetRequiredService<StudioState>();
        await state.EnsureInitializedAsync(cancellationToken);

        StudioOverview overview = await services.GetRequiredService<IStoreInfoService>().GetOverviewAsync(cancellationToken);

        var catalog = services.GetRequiredService<IStudioScopeCatalog>();
        var resolver = services.GetRequiredService<StudioScopeResolver>();

        const string storeKey = MartenStoreRegistry.DefaultStoreKey;
        List<TenantList> tenantLists = [];
        List<string> tenants = [];

        foreach (DatabaseListing database in state.AvailableDatabases)
        {
            StoreScopeFacts facts = await catalog.DescribeAsync(storeKey, database.Identity, cancellationToken);
            tenantLists.Add(facts.Tenants);

            _ = await resolver.ResolveAsync(new StudioScope(storeKey, database.Identity, null), null, cancellationToken);

            foreach (string tenant in facts.Tenants.Ids)
            {
                _ = await resolver.ResolveAsync(new StudioScope(storeKey, database.Identity, tenant), null, cancellationToken);
                tenants.Add(tenant);
            }
        }

        return new FirstNavigation(
            state.ActiveScope,
            [.. state.AvailableDatabases.Select(static x => x.Identity)],
            overview,
            tenantLists,
            tenants);
    }

    public ValueTask DisposeAsync() => provider.DisposeAsync();

    /// <summary>Somebody is signed in; who they are is not what these tests are about.</summary>
    private sealed class SignedInVisitor : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "tenancy")], "test"))));
    }
}

/// <summary>What one first navigation was shown.</summary>
/// <param name="Scope">The scope the layout settled.</param>
/// <param name="Databases">The databases the picker offered, by identity.</param>
/// <param name="Overview">The Overview's store cards.</param>
/// <param name="TenantLists">What the header offered for each database, in picker order.</param>
/// <param name="Tenants">Every tenant offered, each of which was then resolved.</param>
internal sealed record FirstNavigation(
    StudioScope? Scope,
    IReadOnlyList<string> Databases,
    StudioOverview Overview,
    IReadOnlyList<TenantList> TenantLists,
    IReadOnlyList<string> Tenants);

/// <summary>
/// Everything a first navigation could change: the databases on the server, every object in the schemas a
/// test names, and the rows of a lookup table.
/// </summary>
/// <remarks>
/// <para>
/// Read from <c>pg_catalog</c> on a connection of the test's own, so nothing of the studio's is in the
/// measurement. An object is printed with its oid, so a table that was dropped and created again - what
/// <c>AutoCreate.All</c> does to one it cannot alter - reads as changed even though its name is the same; a
/// relation also carries its columns and their types, so an <c>alter table</c> reads as changed too.
/// </para>
/// <para>
/// A lookup table's rows carry their <c>xmin</c>, so a write that leaves every value as it was - the
/// <c>on conflict do update</c> a master-table tenancy's seeding runs - reads as changed. The connection
/// string is printed as a digest, so a failure message carries no password.
/// </para>
/// <para>
/// Only databases whose names start with the prefix a test passes are listed: other test classes create and
/// drop databases of their own on the same server, in parallel, so each class here names its own with a
/// prefix of its own (<see cref="FirstNavigationPrefix" />, <see cref="EnumerationPrefix" />).
/// </para>
/// </remarks>
internal sealed record ServerSnapshot(IReadOnlyList<string> Databases, IReadOnlyList<string> Objects, IReadOnlyList<string> Rows)
{
    /// <summary>The prefix of every database <c>FirstNavigationLiveTests</c> creates.</summary>
    public const string FirstNavigationPrefix = "f8d3_nav_";

    /// <summary>The prefix of every database <c>EnumerationAppliesLiveTests</c> creates.</summary>
    public const string EnumerationPrefix = "f8d3_apply_";

    private const string ObjectsSql =
        "select 'schema ' || n.nspname || ' ' || n.oid::text "
        + "from pg_namespace n where n.nspname = any(@schemas) "
        + "union all "
        + "select 'relation ' || n.nspname || '.' || c.relname || ' ' || c.relkind::text || ' ' || c.oid::text || ' ' "
        + "|| coalesce((select string_agg(a.attname || ':' || format_type(a.atttypid, a.atttypmod), ',' order by a.attnum) "
        + "from pg_attribute a where a.attrelid = c.oid and a.attnum > 0 and not a.attisdropped), '') "
        + "from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = any(@schemas) "
        + "union all "
        + "select 'routine ' || n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ') ' "
        + "|| p.oid::text || ' ' || md5(coalesce(p.prosrc, '')) "
        + "from pg_proc p join pg_namespace n on n.oid = p.pronamespace where n.nspname = any(@schemas) "
        + "order by 1";

    /// <summary>Reads the snapshot.</summary>
    /// <param name="databases">
    /// Each database to read objects from, as a connection string, with the schemas to read in it.
    /// </param>
    /// <param name="databasePrefix">The prefix of the server's databases to list.</param>
    /// <param name="lookupTable">
    /// A master-table tenancy's lookup table, qualified, whose rows to read - or <see langword="null" />.
    /// It is read through the first connection string.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<ServerSnapshot> ReadAsync(
        IReadOnlyList<(string ConnectionString, string[] Schemas)> databases,
        string databasePrefix,
        string? lookupTable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(databases);

        List<string> objects = [];
        IReadOnlyList<string> databaseNames = [];
        IReadOnlyList<string> rows = [];

        for (int i = 0; i < databases.Count; i++)
        {
            (string connectionString, string[] schemas) = databases[i];

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            string name = connection.Database;
            foreach (string line in await ReadAsync(connection, ObjectsSql, [("schemas", schemas)], cancellationToken))
            {
                objects.Add(name + ": " + line);
            }

            if (i == 0)
            {
                databaseNames = await ReadAsync(
                    connection,
                    "select datname from pg_database where starts_with(datname, @prefix) order by datname",
                    [("prefix", databasePrefix)],
                    cancellationToken);

                if (lookupTable is not null)
                {
                    rows = await ReadAsync(
                        connection,
                        $"select tenant_id || ' ' || md5(connection_string) || ' ' || disabled::text || ' xmin ' || xmin::text from {lookupTable} order by tenant_id",
                        [],
                        cancellationToken);
                }
            }
        }

        return new ServerSnapshot(databaseNames, objects, rows);
    }

    /// <summary>
    /// Asserts that nothing in <paramref name="after" /> differs from this one, part by part, so a failure
    /// says which part moved.
    /// </summary>
    public void ShouldBeUnchangedIn(ServerSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(after);

        after.Databases.Should().Equal(Databases, "a first navigation creates no database");
        after.Objects.Should().Equal(Objects, "a first navigation creates, alters, recreates or drops no schema object");
        after.Rows.Should().Equal(Rows, "a first navigation writes no row of the lookup table");
    }

    private static async Task<IReadOnlyList<string>> ReadAsync(
        NpgsqlConnection connection,
        string sql,
        (string Name, object Value)[] parameters,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach ((string parameterName, object value) in parameters)
        {
            command.Parameters.AddWithValue(parameterName, value);
        }

        List<string> lines = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(reader.GetString(0));
        }

        return lines;
    }
}

/// <summary>The fixture-side helpers the tenancy tests share.</summary>
internal static class TenancyDatabases
{
    /// <summary>
    /// A database of this name on the container, dropping whatever a previous run left, and its connection
    /// string. Outside a transaction, because Postgres will not create a database inside one.
    /// </summary>
    public static async Task<string> CreateAsync(PostgresFixture postgres, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        await DropAsync(postgres, name, cancellationToken);

        await using (NpgsqlConnection admin = await postgres.OpenAsync(cancellationToken))
        {
            await using var create = new NpgsqlCommand($"create database {SqlIdentifier.Quote(name)}", admin);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        return ConnectionStringFor(postgres, name);
    }

    /// <summary>Drops the database if it is there, ending any session on it first.</summary>
    public static async Task DropAsync(PostgresFixture postgres, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        await using NpgsqlConnection admin = await postgres.OpenAsync(cancellationToken);
        await using var drop = new NpgsqlCommand($"drop database if exists {SqlIdentifier.Quote(name)} with (force)", admin);
        await drop.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Whether a database of this name exists on the container.</summary>
    public static async Task<bool> ExistsAsync(PostgresFixture postgres, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        await using NpgsqlConnection connection = await postgres.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select exists (select 1 from pg_database where datname = @name)", connection);
        command.Parameters.AddWithValue("name", name);

        return (bool) (await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>The container's connection string, pointed at another database on it.</summary>
    public static string ConnectionStringFor(PostgresFixture postgres, string database)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        return new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString;
    }

    /// <summary>Whether a relation exists, by its qualified, quoted name.</summary>
    public static async Task<bool> RelationExistsAsync(PostgresFixture postgres, string qualified, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        await using NpgsqlConnection connection = await postgres.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select to_regclass(@name) is not null", connection);
        command.Parameters.AddWithValue("name", qualified);

        return (bool) (await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>The <c>xmin</c> of one lookup-table row, which every write of it changes.</summary>
    public static async Task<string> RowVersionAsync(
        PostgresFixture postgres,
        string lookupTable,
        string tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        await using NpgsqlConnection connection = await postgres.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"select xmin::text from {lookupTable} where tenant_id = @tenant", connection);
        command.Parameters.AddWithValue("tenant", tenantId);

        return (string) (await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>A statement that returns nothing, on the container's own database.</summary>
    public static async Task ExecuteAsync(PostgresFixture postgres, string sql, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        await using NpgsqlConnection connection = await postgres.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>A conjoined document, so a tenant selector means something and tier 3 has a table to read.</summary>
[DocumentAlias("tenancy_note")]
public class TenancyNote
{
    /// <summary>The identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Something to read back.</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>An event, so the store has an event store to create - or not to.</summary>
/// <param name="Text">What was written.</param>
public record TenancyNoteWritten(string Text);
