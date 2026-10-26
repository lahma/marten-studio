using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Integration.Tests.Database;

/// <summary>
/// The database browser's catalog and gate (D27) on a plain database, through Postgres Studio's own services: what
/// <c>BrowsableSchemas</c> admits, rows refused without <c>BrowseDatabase</c> or the write policy and every refusal
/// audited, Marten's rows refused whatever the options say, and a withheld schema masked.
/// </summary>
/// <remarks>
/// The database is this class's own (<see cref="Fixture" />), so <c>"*"</c> lists exactly the schemas it made:
/// <c>public</c> with an application table, a Marten document table and a view over it; <c>legacy</c>; and
/// <c>secret</c>, which the masking test withholds while <c>public</c> and <c>legacy</c> refer to it.
/// </remarks>
public class DatabaseGateLiveTests(DatabaseGateLiveTests.Fixture database) : IClassFixture<DatabaseGateLiveTests.Fixture>
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The class's database, seeded once.</summary>
    public sealed class Fixture(PostgresFixture postgres) : IAsyncLifetime
    {
        private OwnedDatabases? owned;

        /// <summary>The database's connection string.</summary>
        public string ConnectionString => owned![0];

        public async ValueTask InitializeAsync()
        {
            if (!DockerAvailability.IsAvailable)
            {
                return;
            }

            owned = await OwnedDatabases.CreateAsync(postgres, ["ps_gate"], CancellationToken.None);
            await OwnedDatabases.ExecuteAsync(
                ConnectionString,
                """
                create table public.orders (id int primary key, customer text not null);
                insert into public.orders values (1, 'acme'), (2, 'globex');
                create table public.mt_doc_order (id uuid primary key, data jsonb not null);
                insert into public.mt_doc_order values ('00000000-0000-0000-0000-000000000001', '{"secret": "a Marten document"}');
                create view public.order_docs as select id, data from public.mt_doc_order;
                create schema secret;
                create table secret.salaries (id int primary key, amount int not null);
                insert into secret.salaries values (1, 100000);
                create type secret.grade as enum ('junior', 'senior');
                create view public.salary_peek as select id, amount from secret.salaries;
                create schema legacy;
                create table legacy.ledger (id int primary key, amount numeric not null);
                insert into legacy.ledger values (1, 10);
                create table legacy.reviews (id int primary key, grade secret.grade not null);
                """,
                CancellationToken.None);
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
    public async Task With_the_default_BrowsableSchemas_public_is_listed_and_every_other_schema_is_counted_never_named()
    {
        await using StudioHost host = Host(static o => o.Capabilities = PostgresStudioCapabilities.All());
        await using StudioCircuit circuit = host.Circuit();
        IDatabaseObjectService objects = circuit.Get<IDatabaseObjectService>();

        circuit.Get<IStudioSettings>().BrowsableSchemas.Should().Equal(["public"],
            "the premise: with nothing named, the default is applied last and is public");

        DatabaseBrowserOverview overview = await objects.GetOverviewAsync(Scope, Token);

        overview.Refusal.Should().Be(DatabaseRefusal.None, overview.Reason);
        overview.Schemas.Select(static x => x.Name).Should().Equal(["public"]);
        overview.Access.WithheldSchemaCount.Should().Be(2, "legacy and secret are counted");
        string.Join(" ", overview.Access.BrowsableSchemas).Should().NotContain("secret").And.NotContain("legacy");

        DatabaseObjectList legacy = await objects.ListAsync(Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, "legacy"), Token);
        legacy.Refusal.Should().Be(DatabaseRefusal.SchemaNotBrowsable);
        legacy.Items.Should().BeEmpty();

        StudioActionLogEntry entry = host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == DatabaseAccess.ListAction).Subject;
        entry.Succeeded.Should().BeFalse("a schema outside the gate is refused, and the refusal is audited");
        entry.DatabaseId.Should().Be("default");
        entry.TenantId.Should().BeNull();
    }

    [PostgresFact]
    public async Task With_star_every_schema_the_role_may_use_is_listed_and_Postgres_own_are_not()
    {
        await using StudioHost host = Host(static o =>
        {
            o.Capabilities = PostgresStudioCapabilities.All();
            o.BrowsableSchemas.Clear();
            o.BrowsableSchemas.Add("*");
        });
        await using StudioCircuit circuit = host.Circuit();

        DatabaseBrowserOverview overview = await circuit.Get<IDatabaseObjectService>().GetOverviewAsync(Scope, Token);

        overview.Refusal.Should().Be(DatabaseRefusal.None, overview.Reason);
        overview.Schemas.Select(static x => x.Name).Should().BeEquivalentTo(["legacy", "public", "secret"]);
        overview.Access.WithheldSchemaCount.Should().Be(0);

        DatabaseObjectList tables = await circuit.Get<IDatabaseObjectService>().ListAsync(Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, "legacy"), Token);
        tables.Refusal.Should().Be(DatabaseRefusal.None, tables.Reason);
        tables.Items.Select(static x => x.Name).Should().BeEquivalentTo(["ledger", "reviews"]);
    }

    [PostgresFact]
    public async Task Rows_are_refused_without_BrowseDatabase_while_the_structure_stays_visible_and_the_refusal_is_audited()
    {
        await using StudioHost host = Host(static _ => { });
        await using StudioCircuit circuit = host.Circuit();

        new PostgresStudioOptions().Capabilities.BrowseDatabase.Should().BeFalse("the premise: off until the host says otherwise (D4)");

        TableRowPage rows = await circuit.Get<ITableRowService>().ListRowsAsync(Scope, "public", "orders", new TableRowRequest(), Token);
        rows.Refusal.Should().Be(DatabaseRefusal.CapabilityOff, rows.Reason);
        rows.Rows.Should().BeEmpty();
        rows.Reason.Should().Contain("BrowseDatabase", "a refusal names the property a host would set");

        DatabaseObjectDetail orders = await circuit.Get<IDatabaseObjectService>().GetObjectAsync(Scope, "public", "orders", Token);
        orders.Found.Should().BeTrue("public's structure is free: " + orders.Reason);
        orders.Columns.Select(static x => x.Name).Should().Equal("id", "customer");

        host.Ring.GetLatest().Should().Contain(static x => x.Action == DatabaseAccess.RowsAction && !x.Succeeded && x.Target == "public.orders");
    }

    [PostgresFact]
    public async Task Rows_need_the_write_policy_asked_about_BrowseDatabase_for_the_whole_database()
    {
        await using StudioHost host = Host(
            static o => o.Capabilities = PostgresStudioCapabilities.All(),
            writePolicy: static (resource, user) => user.Identity?.Name == "admin");

        await using (StudioCircuit viewer = host.Circuit("viewer"))
        {
            TableRowPage refused = await viewer.Get<ITableRowService>().ListRowsAsync(Scope, "public", "orders", new TableRowRequest(), Token);

            refused.Refusal.Should().Be(DatabaseRefusal.WritePolicy, refused.Reason);
            refused.Rows.Should().BeEmpty();
            refused.Reason.Should().Contain("WriteAuthorizationPolicy");
        }

        await using (StudioCircuit admin = host.Circuit("admin"))
        {
            TableRowPage read = await admin.Get<ITableRowService>().ListRowsAsync(Scope, "public", "orders", new TableRowRequest(), Token);

            read.State.Should().Be(TableRowPageState.Loaded, read.Reason);
            read.Rows.Should().HaveCount(2);
        }

        host.Calls.All.Should().Contain(static x => x.Policy == StudioHost.WritePolicy && x.User == "viewer" && !x.Allowed
            && x.Resource == new PostgresStudioResource("default", "default", nameof(PostgresStudioCapabilities.BrowseDatabase)));

        IReadOnlyList<StudioActionLogEntry> audited = [.. host.Ring.GetLatest().Where(static x => x.Action == DatabaseAccess.RowsAction)];
        audited.Should().Contain(static x => x.User == "viewer" && !x.Succeeded, "the refusal is audited");
        audited.Should().Contain(static x => x.User == "admin" && x.Succeeded, "and so is the first read of a relation's rows");
    }

    [PostgresFact]
    public async Task ReadOnly_refuses_the_rows_whatever_the_capabilities_say()
    {
        await using StudioHost host = Host(static o =>
        {
            o.Capabilities = PostgresStudioCapabilities.All();
            o.ReadOnly = true;
        });
        await using StudioCircuit circuit = host.Circuit();

        TableRowPage rows = await circuit.Get<ITableRowService>().ListRowsAsync(Scope, "public", "orders", new TableRowRequest(), Token);

        rows.Refusal.Should().Be(DatabaseRefusal.ReadOnly, rows.Reason);
        rows.Rows.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task A_Marten_tables_rows_and_a_view_over_one_are_refused_with_every_gate_open()
    {
        await using StudioHost host = Host(static o =>
        {
            o.Capabilities = PostgresStudioCapabilities.All();
            o.BrowsableSchemas.Add("*");
        });
        await using StudioCircuit circuit = host.Circuit();
        ITableRowService rows = circuit.Get<ITableRowService>();

        TableRowPage document = await rows.ListRowsAsync(Scope, "public", "mt_doc_order", new TableRowRequest(), Token);
        document.Refusal.Should().Be(DatabaseRefusal.MartenOwned, "PostgresStudio never reads a Marten table's rows (design R2)");
        document.Rows.Should().BeEmpty();

        TableRowPage view = await rows.ListRowsAsync(Scope, "public", "order_docs", new TableRowRequest(), Token);
        view.Refusal.Should().Be(DatabaseRefusal.MartenOwned, "nor through a view over one");
        view.Rows.Should().BeEmpty();

        TableRowPage own = await rows.ListRowsAsync(Scope, "public", "orders", new TableRowRequest(), Token);
        own.State.Should().Be(TableRowPageState.Loaded, "the application's own table beside them is read: " + own.Reason);

        host.Ring.GetLatest().Where(static x => x.Action == DatabaseAccess.RowsAction && !x.Succeeded).Select(static x => x.Target)
            .Should().Contain(["public.mt_doc_order", "public.order_docs"]);
    }

    [PostgresFact]
    public async Task A_withheld_schema_is_masked_in_structure_and_a_view_reading_it_is_refused_its_query_and_its_rows()
    {
        // A list that names schemas replaces the default, so public is named beside legacy.
        await using StudioHost host = Host(static o =>
        {
            o.Capabilities = PostgresStudioCapabilities.All();
            o.BrowsableSchemas.Add("public");
            o.BrowsableSchemas.Add("legacy");
        });
        await using StudioCircuit circuit = host.Circuit();
        IDatabaseObjectService objects = circuit.Get<IDatabaseObjectService>();

        DatabaseObjectDefinition peek = await objects.GetDefinitionAsync(Scope, new DatabaseObjectRef(DatabaseObjectKind.View, "public", "salary_peek"), Token);

        // D27: a view that reads from anything in a withheld schema is refused its rows and its query.
        peek.Refusal.Should().Be(DatabaseRefusal.WithheldDependency, peek.Reason);
        peek.Sql.Should().BeNull();
        (peek.Reason ?? string.Empty).Should().NotContain("secret", "the refusal does not name what it withholds");

        TableRowPage rows = await circuit.Get<ITableRowService>().ListRowsAsync(Scope, "public", "salary_peek", new TableRowRequest(), Token);
        rows.Refusal.Should().Be(DatabaseRefusal.WithheldDependency, rows.Reason);
        rows.Rows.Should().BeEmpty();

        DatabaseObjectDetail reviews = await objects.GetObjectAsync(Scope, "legacy", "reviews", Token);
        reviews.Found.Should().BeTrue(reviews.Reason);
        reviews.Columns.Single(static x => x.Name == "grade").Type.Should().Be(WithheldNames.Token + ".grade",
            "a column's type in a withheld schema is printed with its qualifier masked");

        DatabaseBrowserOverview overview = await objects.GetOverviewAsync(Scope, Token);
        overview.Schemas.Select(static x => x.Name).Should().BeEquivalentTo(["legacy", "public"]);
        overview.Access.WithheldSchemaCount.Should().Be(1);
    }

    private StudioHost Host(
        Action<PostgresStudioOptions> studio,
        Func<PostgresStudioResource, System.Security.Claims.ClaimsPrincipal, bool>? writePolicy = null) =>
        StudioHost.Create(
            services => services.AddSingleton(_ => NpgsqlDataSource.Create(database.ConnectionString)),
            studio,
            writePolicy: writePolicy);
}
