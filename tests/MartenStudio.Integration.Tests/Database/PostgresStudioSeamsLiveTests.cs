using System.Security.Claims;

using MartenStudio.Integration.Tests.Health;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// PostgresStudio's seams (PS-3b) against a real server: a database holding Quartz.NET-like <c>qrtz_</c> tables, a Marten
/// table, EF Core's history and the application's own table. The table source judges each by whose it is and locks
/// nothing; the <c>mt_</c> rule refuses a Marten table's rows and a view over one; nothing writes DDL. PS-5 builds the
/// full PostgresStudio suite; these are the seams' own few.
/// </summary>
public class PostgresStudioSeamsLiveTests(PostgresFixture postgres)
{
    private const string App = "ps3b_app";
    private const string Legacy = "ps3b_legacy";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task The_table_source_judges_every_admitted_table_by_whose_it_is_and_locks_nothing_it_lists()
    {
        await SeedAsync();
        HealthSweepProbe probe = new(postgres);
        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(postgres, App, Legacy);

        await using ServiceProvider services = Host(postgres.ConnectionString, [App, "*"]);
        ResolvedSource resolved = await ResolveAsync(services);
        await using HealthConnection connection = new(resolved.Target);

        HealthCheckContext context = new(
            resolved,
            services.GetRequiredService<IStudioSettings>(),
            NoStudioRuntime.Instance,
            services,
            new HealthSqlRunner(connection, new ReadOnlySqlOptions(), probe),
            DateTimeOffset.UtcNow,
            new HealthSweepMemo(),
            Token);

        IStudioTableSet set = await HealthTables.SetAsync(context);

        set.Find(App, "orders")!.Should().Match<StudioTable>(static x => x.Kind == StudioTableKind.Relational
            && x.Ownership == StudioTableOwnership.Owned && x.GatedSchemas.Count == 0);
        set.Find(App, "qrtz_triggers")!.Should().Match<StudioTable>(static x => x.Ownership == StudioTableOwnership.Library
            && x.Library == "Quartz.NET" && x.ExemptFromIndexDesign);
        set.Find(App, "__EFMigrationsHistory")!.Library.Should().Be("Entity Framework Core");
        set.Find(App, "mt_hilo")!.Library.Should().Be("Marten");
        set.Find(App, "mt_doc_order")!.Should().Match<StudioTable>(static x => x.Kind == StudioTableKind.DocumentTable
            && x.Ownership == StudioTableOwnership.Library && x.Library == "Marten", "a Marten store manages it");
        set.Find(Legacy, "ledger")!.Should().Match<StudioTable>(static x => x.Ownership == StudioTableOwnership.Other
            && x.GatedSchemas.Single() == Legacy, "only \"*\" admits its schema");
        set.Tables.Should().NotContain(static x => x.Schema.StartsWith("pg_", StringComparison.Ordinal) || x.Schema == "information_schema");

        IReadOnlyList<TableFacts> facts = await HealthTables.TablesAsync(context);
        facts.Select(static x => x.Key).Should().Contain(App + ".orders", "the stats read works on a relational table");

        probe.StatementsObserved.Should().BePositive();
        probe.BeyondCatalogReads.Should().BeEmpty("a list locks nothing it lists (D27) - no size function, no row read");
        (await HealthSweepProbe.SnapshotAsync(postgres, App, Legacy)).Should().BeEquivalentTo(before, "a read path writes nothing");
    }

    [PostgresFact]
    public async Task The_mt_rule_refuses_a_Marten_tables_rows_and_a_view_over_one_and_reads_the_hosts_own()
    {
        await SeedAsync();
        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(postgres, App, Legacy);

        await using ServiceProvider services = Host(postgres.ConnectionString, [App]);
        await using AsyncServiceScope circuit = services.CreateAsyncScope();
        StudioScope scope = new("default", "default", null);

        DatabaseObjectList tables = await circuit.ServiceProvider.GetRequiredService<IDatabaseObjectService>()
            .ListAsync(scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, App), Token);

        tables.Refusal.Should().Be(DatabaseRefusal.None);
        tables.Items.Single(static x => x.Name == "mt_doc_order").Ownership.Owner.Should().Be(DatabaseObjectOwner.MartenInfrastructure);
        tables.Items.Single(static x => x.Name == "qrtz_triggers").Ownership.RecognisedAs.Should().Be("Quartz.NET");
        tables.Items.Single(static x => x.Name == "orders").Ownership.Owner.Should().Be(DatabaseObjectOwner.Other);
        tables.Counts.Marten.Should().BePositive("which is what puts the Marten Studio notice on the page");

        ITableRowService rows = circuit.ServiceProvider.GetRequiredService<ITableRowService>();

        TableRowPage marten = await rows.ListRowsAsync(scope, App, "mt_doc_order", new TableRowRequest(), Token);
        marten.State.Should().Be(TableRowPageState.Refused);
        marten.Refusal.Should().Be(DatabaseRefusal.MartenOwned);
        marten.Rows.Should().BeEmpty();

        TableRowPage view = await rows.ListRowsAsync(scope, App, "order_docs", new TableRowRequest(), Token);
        view.Refusal.Should().Be(DatabaseRefusal.MartenOwned);
        view.Reason.Should().Be(PostgresStudioText.Instance.MartenDependencyDenial, "the refusal is in Postgres Studio's words");

        TableRowPage own = await rows.ListRowsAsync(scope, App, "orders", new TableRowRequest(), Token);
        own.State.Should().Be(TableRowPageState.Loaded);
        own.Rows.Should().HaveCount(2);

        (await HealthSweepProbe.SnapshotAsync(postgres, App, Legacy)).Should().BeEquivalentTo(before, "a read path writes nothing");
    }

    private static ServiceProvider Host(string connectionString, string[] browsable)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, Anonymous>();
        services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        services.AddPostgresStudio(options =>
        {
            options.BrowsableSchemas.Clear();

            foreach (string schema in browsable)
            {
                options.BrowsableSchemas.Add(schema);
            }

            options.Capabilities = PostgresStudioCapabilities.All();
        });

        return services.BuildServiceProvider();
    }

    private static async Task<ResolvedSource> ResolveAsync(IServiceProvider services)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<StudioSourceResolver>()
            .ResolveAsync(new StudioScope("default", "default", null), null, Token);
    }

    private async Task SeedAsync()
    {
        await postgres.CreateSchemaAsync(App);
        await postgres.CreateSchemaAsync(Legacy);
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);

        await using NpgsqlCommand command = new(
            """
            create table ps3b_app.orders (id int primary key, customer int not null);
            create index orders_by_customer on ps3b_app.orders (customer);
            insert into ps3b_app.orders values (1, 10), (2, 20);
            create table ps3b_app.qrtz_triggers (sched_name text, trigger_name text, primary key (sched_name, trigger_name));
            create table ps3b_app."__EFMigrationsHistory" ("MigrationId" varchar(150) primary key, "ProductVersion" varchar(32) not null);
            create table ps3b_app.mt_doc_order (id uuid primary key, data jsonb not null);
            insert into ps3b_app.mt_doc_order values ('00000000-0000-0000-0000-000000000001', '{"secret": "a Marten document"}');
            create table ps3b_app.mt_hilo (entity_name varchar primary key, hi_value bigint);
            create view ps3b_app.order_docs as select id, data from ps3b_app.mt_doc_order;
            create table ps3b_legacy.ledger (id int primary key);
            analyze ps3b_app.orders;
            """,
            connection);

        await command.ExecuteNonQueryAsync(Token);
    }

    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
