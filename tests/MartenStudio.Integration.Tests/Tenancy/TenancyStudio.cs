using System.Security.Claims;

using Marten;
using Marten.Schema;

using MartenStudio.Services;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;
using PostgresStudio.Services;
using PostgresStudio.Internal.Sql;

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
/// Everything a first navigation could change: the databases on the server, every object in every
/// non-system schema of the databases a test owns, and the rows of a lookup table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every schema, because each test owns its databases.</b> A test here creates the databases its stores use
/// and drops them afterwards, so nothing else on the shared container writes to them - other test classes run
/// in parallel, but against databases of their own - and the snapshot can read every schema that is not
/// Postgres' own rather than only those a test thought to name. A store that created a schema nobody expected
/// is then a difference too.
/// </para>
/// <para>
/// Read from <c>pg_catalog</c> on an unpooled connection of the test's own, so nothing of the studio's is in
/// the measurement and no pooled connection outlives a database the test drops. Each object is printed with
/// its oid, so a table dropped and created again - what <c>AutoCreate.All</c> does to one it cannot alter -
/// reads as changed under the same name. A relation also carries its owner, grants, storage options,
/// partition bound and index definition, and each column its type, nullability and default, so an
/// <c>alter table</c> of any of those reads as changed. Constraints, triggers (with whether they are
/// enabled), routines (by source digest), domains, enums and ranges, row-security policies, extensions, and
/// the comments on any object a user made (oid 16384 and up - Postgres' own are below) are read as well.
/// </para>
/// <para>
/// A lookup table's rows carry their <c>xmin</c>, so a write that leaves every value as it was - the
/// <c>on conflict do update</c> a master-table tenancy's seeding runs - reads as changed. The connection
/// string is printed as a digest, so a failure message carries no password.
/// </para>
/// <para>
/// Only databases whose names start with the prefix a test passes are listed: other test classes create and
/// drop databases of their own on the same server, in parallel, so each class here names its own with a
/// prefix of its own. <c>ServerSnapshotLiveTests</c> is this type's anti-vacuity: each part is seen to move.
/// </para>
/// </remarks>
internal sealed record ServerSnapshot(IReadOnlyList<string> Databases, IReadOnlyList<string> Objects, IReadOnlyList<string> Rows)
{
    /// <summary>The prefix of every database <c>FirstNavigationLiveTests</c> creates.</summary>
    public const string FirstNavigationPrefix = "f8d3_nav_";

    /// <summary>The prefix of every database <c>EnumerationAppliesLiveTests</c> creates.</summary>
    public const string EnumerationPrefix = "f8d3_apply_";

    /// <summary>The prefix of every database <c>ServerSnapshotLiveTests</c> creates.</summary>
    public const string SnapshotPrefix = "f8d3_snap_";

    private const string ObjectsSql =
        "with schemas as (select oid, nspname from pg_namespace where nspname <> 'information_schema' and nspname !~ '^pg_') "
        + "select 'schema ' || n.nspname || ' ' || n.oid::text || ' owner=' || pg_get_userbyid(n.nspowner) "
        + "|| ' acl=' || coalesce(n.nspacl::text, '') "
        + "from pg_namespace n join schemas s on s.oid = n.oid "
        + "union all "
        + "select 'relation ' || s.nspname || '.' || c.relname || ' ' || c.relkind::text || ' ' || c.oid::text "
        + "|| ' owner=' || pg_get_userbyid(c.relowner) "
        + "|| ' acl=' || coalesce(c.relacl::text, '') "
        + "|| ' options=' || coalesce(array_to_string(c.reloptions, ','), '') "
        + "|| ' bound=' || coalesce(pg_get_expr(c.relpartbound, c.oid), '') "
        + "|| ' index=' || case when c.relkind in ('i', 'I') then pg_get_indexdef(c.oid) else '' end "
        + "|| ' columns=' || coalesce((select string_agg(a.attname || ':' || format_type(a.atttypid, a.atttypmod) "
        + "|| case when a.attnotnull then ' not null' else '' end "
        + "|| coalesce(' default ' || pg_get_expr(d.adbin, d.adrelid), ''), ',' order by a.attnum) "
        + "from pg_attribute a left join pg_attrdef d on d.adrelid = a.attrelid and d.adnum = a.attnum "
        + "where a.attrelid = c.oid and a.attnum > 0 and not a.attisdropped), '') "
        + "from pg_class c join schemas s on s.oid = c.relnamespace "
        + "union all "
        + "select 'constraint ' || s.nspname || '.' || coalesce(r.relname, '-') || '.' || k.conname || ' ' || k.oid::text "
        + "|| ' ' || pg_get_constraintdef(k.oid) "
        + "from pg_constraint k join schemas s on s.oid = k.connamespace left join pg_class r on r.oid = k.conrelid "
        + "union all "
        + "select 'trigger ' || s.nspname || '.' || r.relname || '.' || t.tgname || ' ' || t.oid::text || ' ' "
        + "|| t.tgenabled::text || ' ' || pg_get_triggerdef(t.oid) "
        + "from pg_trigger t join pg_class r on r.oid = t.tgrelid join schemas s on s.oid = r.relnamespace "
        + "where not t.tgisinternal "
        + "union all "
        + "select 'routine ' || s.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ') ' "
        + "|| p.oid::text || ' ' || md5(coalesce(p.prosrc, '')) || ' acl=' || coalesce(p.proacl::text, '') "
        + "from pg_proc p join schemas s on s.oid = p.pronamespace "
        + "union all "
        + "select 'type ' || s.nspname || '.' || t.typname || ' ' || t.typtype::text || ' ' || t.oid::text "
        + "from pg_type t join schemas s on s.oid = t.typnamespace where t.typtype in ('d', 'e', 'r', 'm') "
        + "union all "
        + "select 'policy ' || s.nspname || '.' || r.relname || '.' || p.polname || ' ' || p.oid::text "
        + "from pg_policy p join pg_class r on r.oid = p.polrelid join schemas s on s.oid = r.relnamespace "
        + "union all "
        + "select 'extension ' || e.extname || ' ' || e.extversion from pg_extension e "
        + "union all "
        + "select 'comment ' || d.classoid::regclass::text || ' ' || d.objoid::text || ' ' || d.objsubid::text "
        + "|| ' ' || md5(d.description) "
        + "from pg_description d where d.objoid >= 16384 "
        + "order by 1";

    /// <summary>Reads the snapshot.</summary>
    /// <param name="databases">
    /// The connection string of each database the test owns, every non-system schema of which is read.
    /// </param>
    /// <param name="databasePrefix">The prefix of the server's databases to list.</param>
    /// <param name="lookupTable">
    /// A master-table tenancy's lookup table, qualified, whose rows to read - or <see langword="null" />.
    /// It is read through the first connection string.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<ServerSnapshot> ReadAsync(
        IReadOnlyList<string> databases,
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
            await using var connection = new NpgsqlConnection(TenancyDatabases.Unpooled(databases[i]));
            await connection.OpenAsync(cancellationToken);

            string name = connection.Database;
            foreach (string line in await ReadAsync(connection, ObjectsSql, [], cancellationToken))
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

    /// <summary>A statement that returns nothing, on a database a test owns, over an unpooled connection.</summary>
    public static async Task ExecuteAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(Unpooled(connectionString));
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// The connection string without pooling: a test drops the databases it owns, and a pooled connection to
    /// one would outlive it and be handed to the next reader as a dead session.
    /// </summary>
    public static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
}

/// <summary>
/// Databases a test creates for itself and drops when it is done: what lets a <see cref="ServerSnapshot" /> read
/// every schema in them while other test classes run alongside against databases of their own.
/// </summary>
internal sealed class OwnedDatabases : IAsyncDisposable
{
    private readonly PostgresFixture postgres;
    private readonly string[] names;
    private readonly string[] connectionStrings;

    private OwnedDatabases(PostgresFixture postgres, string[] names, string[] connectionStrings)
    {
        this.postgres = postgres;
        this.names = names;
        this.connectionStrings = connectionStrings;
    }

    /// <summary>Each database's connection string, in the order the names were given.</summary>
    public IReadOnlyList<string> ConnectionStrings => connectionStrings;

    /// <summary>The connection string of the database named at this position.</summary>
    public string this[int index] => connectionStrings[index];

    /// <summary>Creates each database, dropping whatever a previous run left under the same name.</summary>
    public static async Task<OwnedDatabases> CreateAsync(
        PostgresFixture postgres,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(names);

        string[] connectionStrings = new string[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            connectionStrings[i] = await TenancyDatabases.CreateAsync(postgres, names[i], cancellationToken);
        }

        return new OwnedDatabases(postgres, [.. names], connectionStrings);
    }

    /// <summary>Drops every database, ending whatever sessions are left on them.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (string name in names)
        {
            await TenancyDatabases.DropAsync(postgres, name, CancellationToken.None);
        }
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
