using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Claims;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Relationships;

namespace PostgresStudio.Integration.Tests.Sources;

/// <summary>
/// Where Postgres Studio finds its databases, against real ones: the application's unkeyed <see cref="NpgsqlDataSource" />
/// as <c>default</c>, one added by name with <c>AddDataSource</c>, and a host <see cref="IPostgresDatabaseSource" /> that
/// lists three tenant databases - and isolation between them, for a visitor the source policy allows one tenant alone,
/// including through every cache the studio keeps across circuits.
/// </summary>
/// <remarks>
/// Five databases of the class's own (<see cref="Fixture" />): an admin database, a reporting one, and three tenants,
/// each holding a table no other one has, so a page that read the wrong database would show the wrong table.
/// </remarks>
public class DataSourceDiscoveryLiveTests(DataSourceDiscoveryLiveTests.Fixture databases) : IClassFixture<DataSourceDiscoveryLiveTests.Fixture>
{
    /// <summary>The tenants the host lists, in the order it lists them.</summary>
    private static readonly string[] Tenants = ["tenant_a", "tenant_b", "tenant_c"];

    /// <summary>The resolver's one answer for a scope refused and a scope that is not there.</summary>
    private const string NotAuthorized = "Not authorized for the requested store, database or tenant.";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The class's databases, each with a table of its own.</summary>
    public sealed class Fixture(PostgresFixture postgres) : IAsyncLifetime
    {
        private OwnedDatabases? owned;

        /// <summary>The admin database, the application's unkeyed data source.</summary>
        public string Admin => owned![0];

        /// <summary>The reporting database, added with <c>AddDataSource("reporting", …)</c>.</summary>
        public string Reporting => owned![1];

        /// <summary>Each tenant's database, by the name the host lists it under.</summary>
        public IReadOnlyDictionary<string, string> TenantDatabases { get; private set; } = new Dictionary<string, string>();

        public async ValueTask InitializeAsync()
        {
            if (!DockerAvailability.IsAvailable)
            {
                return;
            }

            owned = await OwnedDatabases.CreateAsync(
                postgres, ["ps_disc_admin", "ps_disc_reporting", "ps_disc_tenant_a", "ps_disc_tenant_b", "ps_disc_tenant_c"], CancellationToken.None);

            await OwnedDatabases.ExecuteAsync(Admin, "create table public.admin_only (id int primary key); insert into public.admin_only values (1)", CancellationToken.None);
            await OwnedDatabases.ExecuteAsync(Reporting, "create table public.reporting_only (id int primary key)", CancellationToken.None);

            Dictionary<string, string> tenants = new(StringComparer.Ordinal);
            for (int i = 0; i < Tenants.Length; i++)
            {
                string connection = owned[i + 2];
                string letter = Tenants[i][^1..];
                await OwnedDatabases.ExecuteAsync(
                    connection,
                    $"create table public.{letter}_only (id int primary key, secret text not null); insert into public.{letter}_only values (1, 'tenant {letter}''s row')",
                    CancellationToken.None);
                tenants[Tenants[i]] = connection;
            }

            TenantDatabases = tenants;
        }

        public async ValueTask DisposeAsync()
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }
        }
    }

    [PostgresFact]
    public async Task The_unkeyed_data_source_a_named_one_and_a_host_source_of_three_databases_are_all_offered_each_reading_its_own_database()
    {
        await using StudioHost host = Host(policy: null);
        await using StudioCircuit circuit = host.Circuit();
        StudioState state = circuit.Get<StudioState>();

        await state.EnsureInitializedAsync(Token);

        state.AvailableStores.Select(static x => x.Key).Should().Equal(["default", "reporting", "tenants"],
            "the unkeyed data source is found by itself, and the others are listed in the order they were added");
        state.ActiveScope!.StoreKey.Should().Be("default");
        state.AvailableDatabases.Select(static x => x.Identity).Should().Equal(["default"], "a data source is its one database, named by its registration");

        (await TablesAsync(circuit, new StudioScope("default", "default", null))).Should().Equal(["admin_only"]);
        (await TablesAsync(circuit, new StudioScope("reporting", "reporting", null))).Should().Equal(["reporting_only"]);

        await state.SetScopeAsync("tenants", null, null, Token);
        state.AvailableDatabases.Select(static x => x.Identity).Should().Equal(Tenants, "the host's list, under the names it lists");

        foreach (string tenant in Tenants)
        {
            (await TablesAsync(circuit, new StudioScope("tenants", tenant, null))).Should().Equal([tenant[^1..] + "_only"],
                "{0} is opened through the host's own data source for it", tenant);
        }
    }

    [PostgresFact]
    public async Task A_visitor_allowed_only_tenant_a_cannot_read_tenant_b_through_any_page_or_any_cache_another_visitor_filled()
    {
        await using StudioHost host = Host(policy: static (resource, user) =>
            user.Identity?.Name == "admin"
            || (resource.SourceName == "tenants" && resource.DatabaseIdentifier is "tenant_a" or ""));

        StudioScope tenantA = new("tenants", "tenant_a", null);
        StudioScope tenantB = new("tenants", "tenant_b", null);

        // The admin first: tenant_b's catalog, rows, sessions, server and health are all read and cached.
        await using (StudioCircuit admin = host.Circuit("admin"))
        {
            (await TablesAsync(admin, tenantB)).Should().Equal(["b_only"]);
            (await admin.Get<ITableRowService>().ListRowsAsync(tenantB, "public", "b_only", new TableRowRequest(), Token)).Rows.Should().ContainSingle();
            (await admin.Get<ISessionActivityService>().GetSummaryAsync(tenantB, Token)).Refusal.Should().Be(DatabaseRefusal.None);
            (await admin.Get<IPostgresServerReader>().ReadAsync(tenantB, cancellationToken: Token)).Facts.Should().NotBeNull();
            (await admin.Get<IHealthService>().GetReportAsync(tenantB, HealthReportRequest.Regular, Token)).Refusal.Should().Be(HealthRefusal.None);
            (await admin.Get<IHealthService>().GlanceAsync(tenantB, Token)).Should().NotBeNull("the admin's sweep of tenant_b is cached for the badge");
        }

        await using StudioCircuit alice = host.Circuit("alice");

        // The picker offers tenant_a and nothing else.
        StudioState state = alice.Get<StudioState>();
        await state.EnsureInitializedAsync(Token);
        state.AvailableStores.Select(static x => x.Key).Should().Equal(["tenants"]);
        state.ActiveScope.Should().Be(tenantA);
        state.AvailableDatabases.Select(static x => x.Identity).Should().Equal(["tenant_a"]);

        // tenant_a is hers, and it is tenant_a's: no cache answered her with tenant_b's catalog.
        (await TablesAsync(alice, tenantA)).Should().Equal(["a_only"]);

        // tenant_b refuses her on every page, although the admin's reads of it are all cached.
        // The catalog reads answer as the resolver does - the same sentence for a database refused and one that does not
        // exist, so a refusal cannot be used to enumerate them (StudioNotAuthorizedException).
        IDatabaseObjectService objects = alice.Get<IDatabaseObjectService>();
        DatabaseBrowserOverview overview = await objects.GetOverviewAsync(tenantB, Token);
        // Labelled as the policy's refusal (H1), and still the same answer a database that does not exist would get.
        (overview.Refusal, overview.Reason).Should().Be((DatabaseRefusal.ScopeRefused, NotAuthorized));
        overview.Schemas.Should().BeEmpty();

        DatabaseObjectList list = await objects.ListAsync(tenantB, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, "public"), Token);
        (list.Refusal, list.Reason).Should().Be((DatabaseRefusal.ScopeRefused, NotAuthorized));
        list.Items.Should().BeEmpty();

        DatabaseObjectDetail detail = await objects.GetObjectAsync(tenantB, "public", "b_only", Token);
        (detail.Refusal, detail.Reason).Should().Be((DatabaseRefusal.ScopeRefused, NotAuthorized));

        // And a database the policy refuses that does not exist at all reads exactly the same (non-enumeration).
        DatabaseObjectDetail ghost = await objects.GetObjectAsync(new StudioScope("tenants", "tenant_z", null), "public", "b_only", Token);
        (ghost.Refusal, ghost.Reason).Should().Be((detail.Refusal, detail.Reason));
        detail.Columns.Should().BeEmpty();

        // Rows and Sessions name the policy that refused.
        TableRowPage rows = await alice.Get<ITableRowService>().ListRowsAsync(tenantB, "public", "b_only", new TableRowRequest(), Token);
        rows.Refusal.Should().Be(DatabaseRefusal.StorePolicy);
        rows.Reason.Should().Contain("SourceAuthorizationPolicy");
        rows.Rows.Should().BeEmpty();

        SessionActivitySummary sessions = await alice.Get<ISessionActivityService>().GetSummaryAsync(tenantB, Token);
        sessions.Refusal.Should().Be(DatabaseRefusal.StorePolicy);
        sessions.Reason.Should().Contain("SourceAuthorizationPolicy");
        sessions.SessionCount.Should().Be(0);

        HealthReport health = await alice.Get<IHealthService>().GetReportAsync(tenantB, HealthReportRequest.Regular, Token);
        health.Refusal.Should().Be(HealthRefusal.StorePolicy);
        health.Findings.Should().BeEmpty();
        health.Checks.Should().BeEmpty();
        (await alice.Get<IHealthService>().GlanceAsync(tenantB, Token)).Should().BeNull("the badge reads only a scope the report would be shown to");

        await FluentActions.Awaiting(() => alice.Get<IPostgresServerReader>().ReadAsync(tenantB, cancellationToken: Token))
            .Should().ThrowAsync<StudioNotAuthorizedException>();
        // The graph never throws on a circuit: it comes back failed, with nothing of tenant_b's in it.
        RelationshipGraph graph = await alice.Get<IRelationshipDataService>().GetGraphAsync(tenantB, Token);
        graph.Error.Should().NotBeNull();
        graph.Nodes.Should().BeEmpty();
        graph.Edges.Should().BeEmpty();
        graph.Error.Should().NotContain("b_only");

        // Nor the default source, which her policy names nowhere.
        (await objects.GetOverviewAsync(new StudioScope("default", "default", null), Token)).Reason.Should().Be(NotAuthorized);

        host.Calls.All.Where(static x => x.User == "alice" && x.Allowed).Select(static x => x.Resource.DatabaseIdentifier)
            .Should().OnlyContain(static x => x == "tenant_a" || x == string.Empty, "nothing of tenant_b's was ever allowed her");
    }

    /// <summary>The tables of <paramref name="scope" />'s <c>public</c> schema, through the database browser.</summary>
    private static async Task<IReadOnlyList<string>> TablesAsync(StudioCircuit circuit, StudioScope scope)
    {
        DatabaseObjectList list = await circuit.Get<IDatabaseObjectService>()
            .ListAsync(scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, "public"), Token);

        list.Refusal.Should().Be(DatabaseRefusal.None, list.Reason);
        return [.. list.Items.Select(static x => x.Name)];
    }

    private StudioHost Host(Func<PostgresStudioResource, ClaimsPrincipal, bool>? policy) =>
        StudioHost.Create(
            services =>
            {
                services.AddSingleton(_ => NpgsqlDataSource.Create(databases.Admin));
                services.AddKeyedSingleton("reporting", (_, _) => NpgsqlDataSource.Create(databases.Reporting));
                services.AddSingleton(_ => new TenantDatabases(databases.TenantDatabases));
            },
            static options =>
            {
                options.Capabilities = PostgresStudioCapabilities.All();
                options.AddDataSource("reporting", static services => services.GetRequiredKeyedService<NpgsqlDataSource>("reporting"));
                options.AddDatabaseSource("tenants", static services => services.GetRequiredService<TenantDatabases>());
            },
            sourcePolicy: policy);

    /// <summary>
    /// The application's tenant databases, as a host would list them: names it owns, each opened through a data source it
    /// builds once and disposes with itself.
    /// </summary>
    private sealed class TenantDatabases(IReadOnlyDictionary<string, string> connectionStrings) : IPostgresDatabaseSource, IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, Lazy<NpgsqlDataSource>> built = new(StringComparer.Ordinal);

        public async IAsyncEnumerable<string> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (string name in Tenants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return name;
            }
        }

        public ValueTask<NpgsqlDataSource?> GetDataSourceAsync(string name, CancellationToken cancellationToken) =>
            ValueTask.FromResult(connectionStrings.TryGetValue(name, out string? connection)
                ? built.GetOrAdd(name, _ => new Lazy<NpgsqlDataSource>(() => NpgsqlDataSource.Create(connection))).Value
                : null);

        public async ValueTask DisposeAsync()
        {
            foreach (Lazy<NpgsqlDataSource> source in built.Values.Where(static x => x.IsValueCreated))
            {
                await source.Value.DisposeAsync();
            }
        }
    }
}
