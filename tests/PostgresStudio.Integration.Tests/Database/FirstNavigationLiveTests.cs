using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Live;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Relationships;

namespace PostgresStudio.Integration.Tests.Database;

/// <summary>
/// Hard rule 14's analogue for Postgres Studio: a first navigation over every core page - with every capability on and
/// every schema browsable - creates no object and writes no row, in a data source's database and in each database a host
/// source lists.
/// </summary>
/// <remarks>
/// <para>
/// <b>What "navigate" means here.</b> Every read a page makes on its first interactive render, through the services the
/// page injects, in a circuit-shaped scope - and then every read the page offers a click away: each object's detail and
/// definition, each relation's rows, a row and its references, an exact count, a sequence's value, the deeper health
/// checks, Sessions' details. The layout settles a scope first, as <c>StudioLayout</c> does. The browser suite walks the
/// same pages in Chromium over the sample (<c>PostgresStudioScenarioTests</c>).
/// </para>
/// <para>
/// <b>What "nothing" means.</b> <see cref="ServerSnapshot" />, read before and after on connections of the test's own:
/// every object of every non-system schema, and every row of every table and every sequence's position - so a statement
/// that wrote a row, even one that left its values as they were, is a failure. The databases are the test's own, built to
/// have one of everything the browser lists: tables with keys both ways, a partitioned table, a view, a materialized
/// view, a function, a trigger, a sequence, an enum, a domain - and a Marten-named table, whose rows are never read.
/// </para>
/// </remarks>
public class FirstNavigationLiveTests(PostgresFixture postgres)
{
    private const string Prefix = "ps_nav_";

    private const string Seed =
        """
        create table public.customers (id int primary key, name text not null);
        create table public.orders (id int primary key, customer_id int not null references public.customers (id), placed date not null);
        create index orders_by_customer on public.orders (customer_id);
        insert into public.customers select g, 'customer ' || g from generate_series(1, 20) g;
        insert into public.orders select g, 1 + g % 20, date '2026-01-01' + g from generate_series(1, 60) g;
        create table public.events (id int not null, at date not null, primary key (id, at)) partition by range (at);
        create table public.events_2026 partition of public.events for values from ('2026-01-01') to ('2027-01-01');
        insert into public.events values (1, '2026-03-01');
        create view public.recent_orders as select o.id, c.name from public.orders o join public.customers c on c.id = o.customer_id;
        create materialized view public.order_counts as select customer_id, count(*) as n from public.orders group by customer_id;
        create sequence public.invoice_numbers;
        create type public.mood as enum ('calm', 'busy');
        create domain public.positive as int check (value > 0);
        create function public.touch() returns trigger language plpgsql as $$ begin return new; end $$;
        create trigger customers_touched before update on public.customers for each row execute function public.touch();
        create table public.mt_doc_order (id uuid primary key, data jsonb not null);
        insert into public.mt_doc_order values ('00000000-0000-0000-0000-000000000001', '{}');
        create schema app;
        create table app.settings (key text primary key, value text);
        insert into app.settings values ('theme', 'dark');
        comment on table app.settings is 'the application''s settings';
        analyze;
        """;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task Every_core_page_on_a_data_source_and_on_a_host_sources_databases_writes_nothing()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "admin", Prefix + "tenant_x", Prefix + "tenant_y"], Token);

        foreach (string database in owned.ConnectionStrings)
        {
            await OwnedDatabases.ExecuteAsync(database, Seed, Token);
        }

        ServerSnapshot before = await ServerSnapshot.ReadAsync(owned.ConnectionStrings, Prefix, Token);
        before.Rows.Should().Contain(static x => x.Contains("public.orders rows=60", StringComparison.Ordinal), "the premise: there are rows to write to");

        Dictionary<string, string> tenants = new(StringComparer.Ordinal) { ["tenant_x"] = owned[1], ["tenant_y"] = owned[2] };

        await using StudioHost host = StudioHost.Create(
            services =>
            {
                services.AddSingleton(_ => NpgsqlDataSource.Create(owned[0]));
                services.AddSingleton(_ => new FixedDatabases(tenants));
            },
            static options =>
            {
                options.Capabilities = PostgresStudioCapabilities.All();
                options.BrowsableSchemas.Add("*");
                options.AddDatabaseSource("tenants", static services => services.GetRequiredService<FixedDatabases>());
            });

        Navigation walked = new();

        await using (StudioCircuit circuit = host.Circuit("admin"))
        {
            StudioState state = circuit.Get<StudioState>();
            await state.EnsureInitializedAsync(Token);
            state.AvailableStores.Select(static x => x.Key).Should().Equal(["default", "tenants"]);

            await WalkAsync(circuit, new StudioScope("default", "default", null), walked);

            await state.SetScopeAsync("tenants", null, null, Token);
            state.AvailableDatabases.Select(static x => x.Identity).Should().Equal(["tenant_x", "tenant_y"]);

            foreach (string tenant in tenants.Keys)
            {
                await state.SetScopeAsync("tenants", tenant, null, Token);
                await WalkAsync(circuit, state.ActiveScope!, walked);
            }
        }

        // Not vacuous: every kind of page and object was reached, on all three databases.
        walked.Scopes.Should().Be(3);
        walked.Relations.Should().BeGreaterThanOrEqualTo(3 * 7, "tables, partitioned table, partition-less views and the Marten table, per database");
        walked.RowsRead.Should().BeGreaterThanOrEqualTo(3 * 4, "customers, orders, app.settings and the views' rows were read");
        walked.Definitions.Should().BeGreaterThanOrEqualTo(3 * 5);
        walked.HealthChecks.Should().BePositive();

        before.ShouldBeUnchangedIn(await ServerSnapshot.ReadAsync(owned.ConnectionStrings, Prefix, Token));
        host.Logs.WarningsOrWorse.Should().BeEmpty("a first navigation of a healthy database is nothing to warn about");
    }

    /// <summary>One scope's pages, in the order a visitor reaches them, and every read each one offers.</summary>
    private static async Task WalkAsync(StudioCircuit circuit, StudioScope scope, Navigation walked)
    {
        walked.Scopes++;

        // The layout's nav badges, and the Overview.
        _ = await circuit.Get<INavIndicatorService>().ReadAsync(Token);
        (await circuit.Get<IPostgresServerReader>().ReadAsync(scope, cancellationToken: Token)).Facts.Should().NotBeNull();

        IDatabaseObjectService objects = circuit.Get<IDatabaseObjectService>();
        DatabaseBrowserOverview overview = await objects.GetOverviewAsync(scope, Token);
        overview.Refusal.Should().Be(DatabaseRefusal.None, overview.Reason);

        ISessionActivityService sessions = circuit.Get<ISessionActivityService>();
        (await sessions.GetSummaryAsync(scope, Token)).Refusal.Should().Be(DatabaseRefusal.None);

        IHealthService health = circuit.Get<IHealthService>();
        HealthReport regular = await health.GetReportAsync(scope, HealthReportRequest.Regular, Token);
        regular.Refusal.Should().Be(HealthRefusal.None, regular.Reason);
        _ = await circuit.Get<StudioActionLog>().GetVisibleAsync(circuit.Get<StudioAuthorization>(), circuit.Get<StudioCapabilityGuard>(), 10, Token);

        // The database browser: every kind, every object, every definition, every relation's rows.
        ITableRowService rows = circuit.Get<ITableRowService>();

        foreach (DatabaseObjectCategory category in Enum.GetValues<DatabaseObjectCategory>())
        {
            DatabaseObjectList list = await objects.ListAsync(scope, new DatabaseObjectQuery(category), Token);
            list.Refusal.Should().Be(DatabaseRefusal.None, list.Reason);

            foreach (DatabaseObjectSummary item in list.Items)
            {
                if (category is DatabaseObjectCategory.Tables or DatabaseObjectCategory.Views)
                {
                    walked.Relations++;
                    _ = await objects.GetObjectAsync(scope, item.Schema, item.Name, Token);

                    TableRowPage page = await rows.ListRowsAsync(scope, item.Schema, item.Name, new TableRowRequest(), Token);
                    if (page.State == TableRowPageState.Loaded)
                    {
                        walked.RowsRead++;

                        if (page.Rows.Count > 0 && page.Rows[0].Key is { } key)
                        {
                            _ = await rows.GetRowAsync(scope, item.Schema, item.Name, key, Token);
                            _ = await rows.GetReferencesAsync(scope, item.Schema, item.Name, key, Token);
                        }

                        if (item.Kind is DatabaseObjectKind.Table)
                        {
                            _ = await rows.CountExactAsync(scope, item.Schema, item.Name, cancellationToken: Token);
                        }
                    }
                }

                if (category is not DatabaseObjectCategory.Tables)
                {
                    walked.Definitions++;
                    _ = await objects.GetDefinitionAsync(scope, item.Ref, Token);
                }

                if (category is DatabaseObjectCategory.Sequences)
                {
                    _ = await objects.GetSequenceValueAsync(scope, item.Schema, item.Name, Token);
                }
            }
        }

        // Sessions, Relationships, and Health's deeper checks.
        (await sessions.GetDetailsAsync(scope, Token)).Refusal.Should().Be(DatabaseRefusal.None);
        (await circuit.Get<IRelationshipDataService>().GetGraphAsync(scope, Token)).Error.Should().BeNull();

        HealthReport deeper = await health.GetReportAsync(scope, new HealthReportRequest(HealthDeeperChecks.Run), Token);
        deeper.Refusal.Should().Be(HealthRefusal.None, deeper.Reason);
        walked.HealthChecks += deeper.Checks.Count(static x => x.Outcome == HealthCheckOutcome.Checked);
        _ = await health.GlanceAsync(scope, Token);
    }

    /// <summary>What a walk reached, so "nothing changed" cannot mean "nothing was asked".</summary>
    private sealed class Navigation
    {
        public int Scopes { get; set; }

        public int Relations { get; set; }

        public int RowsRead { get; set; }

        public int Definitions { get; set; }

        public int HealthChecks { get; set; }
    }

    /// <summary>A host's database source over fixed databases, building each data source once.</summary>
    private sealed class FixedDatabases(IReadOnlyDictionary<string, string> databases) : IPostgresDatabaseSource, IAsyncDisposable
    {
        private readonly Dictionary<string, NpgsqlDataSource> built = new(StringComparer.Ordinal);
        private readonly Lock gate = new();

        public async IAsyncEnumerable<string> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (string name in databases.Keys.Order(StringComparer.Ordinal))
            {
                await Task.Yield();
                yield return name;
            }
        }

        public ValueTask<NpgsqlDataSource?> GetDataSourceAsync(string name, CancellationToken cancellationToken)
        {
            if (!databases.TryGetValue(name, out string? connection))
            {
                return ValueTask.FromResult<NpgsqlDataSource?>(null);
            }

            lock (gate)
            {
                if (!built.TryGetValue(name, out NpgsqlDataSource? source))
                {
                    source = NpgsqlDataSource.Create(connection);
                    built[name] = source;
                }

                return ValueTask.FromResult<NpgsqlDataSource?>(source);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (NpgsqlDataSource source in built.Values)
            {
                await source.DisposeAsync();
            }
        }
    }
}
