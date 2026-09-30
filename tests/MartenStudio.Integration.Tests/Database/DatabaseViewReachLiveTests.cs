using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// The DB-1-fix re-review's probes against a real Postgres 17: a hidden type's own functions are absent
/// with the capability on and off, and a definition that names its table is refused (item 1); a view is
/// judged on what it reaches - a schema named as a value, a collation, a text search configuration, what a
/// SQL-standard function body reads, code nobody can follow, Marten's own tables (item 2).
/// </summary>
public class DatabaseViewReachLiveTests(DatabaseViewReachLiveTests.Fixture fixture) : IClassFixture<DatabaseViewReachLiveTests.Fixture>
{
    /// <summary>The shared browser fixture, and the probes' objects.</summary>
    public sealed class Fixture(PostgresFixture postgres) : DatabaseBrowserFixture(postgres)
    {
        /// <inheritdoc />
        protected override string ExtraSql =>
            """
            create function {d}.mt_upsert_browsersecret(doc jsonb) returns void language plpgsql
                as $$ begin insert into {d}.{hidden} (data) values (doc); end $$;
            create function {l}.secret_count() returns bigint language plpgsql
                as $$ begin return (select count(*) from {d}.{hidden}); end $$;
            create function {l}.tag() returns trigger language plpgsql as $$ begin return new; end $$;
            create trigger codes_tagged before insert on {l}.codes for each row execute function {l}.tag('{hidden}');

            create table {hr}.zz_pay (id int primary key, amount int);
            insert into {hr}.zz_pay values (1, 987654);
            create function {l}.zz_wrap() returns table (id int, amount int) language sql
                as $$ select id, amount from {hr}.zz_pay $$;
            create view {l}.zz_v_wrap as select * from {l}.zz_wrap();
            create function {l}.zz_atomic() returns int language sql stable
                return (select amount from {hr}.zz_pay limit 1);
            create view {l}.zz_v_atomic as select {l}.zz_atomic() as amount;
            create function {l}.zz_plain() returns int language sql stable return 42;
            create view {l}.zz_v_plain as select {l}.zz_plain() as answer;

            create view {l}.zz_ns as select '{hr}'::regnamespace as n;
            create collation {hr}.c from "C";
            create view {l}.zz_collate as select 'x'::text collate {hr}.c as s;
            create text search configuration {hr}.cfg (copy = pg_catalog.simple);
            create view {l}.zz_tsconfig as select pg_catalog.to_tsvector('{hr}.cfg'::regconfig, 'x') as v;
            create text search dictionary {hr}.dict (template = pg_catalog.simple);
            create view {l}.zz_tsdict as select pg_catalog.ts_lexize('{hr}.dict'::regdictionary, 'x') as v;
            """;

        /// <inheritdoc />
        protected override async Task InitializeExtrasAsync() =>
            await ExecuteAsync(Bind("create view {l}.zz_docs as select id, data from {d}." + SqlIdentifier.Quote(VisibleTable) + ";"));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------
    // Item 1: a hidden type's own functions
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// With the capability off - where the probe found it listed, its definition naming the hidden table -
    /// and on: the hidden type's <c>mt_upsert_</c> function is in no list and has no definition, as its table
    /// has none.
    /// </summary>
    [PostgresFact]
    public async Task A_hidden_types_own_function_is_absent_with_the_capability_on_and_off()
    {
        string function = "mt_upsert_" + fixture.HiddenTable["mt_doc_".Length..];

        foreach (bool capability in new[] { false, true })
        {
            await using BrowserHost host = fixture.Host(options => options.Capabilities.BrowseDatabase = capability);

            DatabaseObjectList functions = await host.ObjectsAsync(x => x.ListAsync(
                BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Functions, fixture.DocumentSchema), Token));

            functions.Refusal.Should().Be(DatabaseRefusal.None, functions.Reason);
            functions.Items.Should().NotContain(x => x.Name == function, "capability " + capability);
            functions.Items.Should().Contain(static x => x.Name == "host_fn", "the list is otherwise read");

            DatabaseObjectDefinition definition = await host.ObjectsAsync(x => x.GetDefinitionAsync(
                BrowserHost.Scope, new DatabaseObjectRef(DatabaseObjectKind.Function, fixture.DocumentSchema, function, "doc jsonb"), Token));

            definition.Refusal.Should().Be(DatabaseRefusal.NotFound, "capability " + capability);
            definition.Sql.Should().BeNull();

            DatabaseBrowserOverview overview = await host.ObjectsAsync(x => x.GetOverviewAsync(BrowserHost.Scope, Token));
            overview.Schemas.Single(x => x.Name == fixture.DocumentSchema).Functions
                .Should().Be(functions.Items.Count, "the count leaves out what the list leaves out");
        }
    }

    /// <summary>A routine or a trigger anybody else wrote whose definition names the hidden table is refused its definition.</summary>
    [PostgresFact]
    public async Task A_definition_that_names_a_hidden_types_table_is_refused()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseObjectDefinition routine = await host.ObjectsAsync(x => x.GetDefinitionAsync(
            BrowserHost.Scope, new DatabaseObjectRef(DatabaseObjectKind.Function, fixture.LegacySchema, "secret_count", string.Empty), Token));

        routine.Refusal.Should().Be(DatabaseRefusal.HiddenDependency, routine.Reason);
        routine.Sql.Should().BeNull();

        DatabaseObjectDefinition trigger = await host.ObjectsAsync(x => x.GetDefinitionAsync(
            BrowserHost.Scope, new DatabaseObjectRef(DatabaseObjectKind.Trigger, fixture.LegacySchema, "codes_tagged", Table: "codes"), Token));

        trigger.Refusal.Should().Be(DatabaseRefusal.HiddenDependency, trigger.Reason);

        DatabaseObjectDefinition other = await host.ObjectsAsync(x => x.GetDefinitionAsync(
            BrowserHost.Scope, new DatabaseObjectRef(DatabaseObjectKind.Function, fixture.LegacySchema, "zz_plain", string.Empty), Token));

        other.Refusal.Should().Be(DatabaseRefusal.None, "a definition that names no hidden table is shown: " + other.Reason);

        host.Ring.GetLatest().Should().Contain(x => x.Target == fixture.LegacySchema + ".secret_count" && !x.Succeeded);
    }

    // ---------------------------------------------------------------------------------------------
    // Item 2: what a view reaches
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A view that names a withheld schema as a value - whose row would be that schema's name - or uses a
    /// collation, a text search configuration or dictionary in it, is refused its rows and its query.
    /// </summary>
    [PostgresFact]
    public async Task A_view_that_names_a_withheld_schema_its_collation_or_its_text_search_objects_is_refused()
    {
        await using BrowserHost host = fixture.Host();

        foreach (string view in new[] { "zz_ns", "zz_collate", "zz_tsconfig", "zz_tsdict" })
        {
            DatabaseRowAccessResult rows = await host.AccessAsync(x =>
                x.RequireRowAccessAsync(BrowserHost.Scope, fixture.LegacySchema, view, cancellationToken: Token));

            rows.Refusal.Should().Be(DatabaseRefusal.WithheldDependency, view);

            DatabaseObjectDefinition definition = await host.ObjectsAsync(x => x.GetDefinitionAsync(
                BrowserHost.Scope, new DatabaseObjectRef(DatabaseObjectKind.View, fixture.LegacySchema, view), Token));

            definition.Refusal.Should().Be(DatabaseRefusal.WithheldDependency, view);
        }

        await using BrowserHost both = fixture.Host(options => options.BrowsableSchemas.Add(fixture.HrSchema));

        (await both.AccessAsync(x => x.RequireRowAccessAsync(BrowserHost.Scope, fixture.LegacySchema, "zz_ns", cancellationToken: Token)))
            .Allowed.Should().BeTrue("with the schema browsable, naming it is no secret");
    }

    /// <summary>
    /// With nothing hidden - where the probe read the withheld row through both - a view over a function with
    /// a SQL-standard body is judged on what that body reads, and a view over one whose body is a string is
    /// refused its rows while the role can read a withheld schema.
    /// </summary>
    [PostgresFact]
    public async Task A_view_is_judged_on_what_its_functions_read_or_refused_when_nobody_can_tell()
    {
        await using BrowserHost host = fixture.Host(static options => options.IsDocumentTypeVisible = null);

        DatabaseRowAccessResult atomic = await host.AccessAsync(x =>
            x.RequireRowAccessAsync(BrowserHost.Scope, fixture.LegacySchema, "zz_v_atomic", cancellationToken: Token));

        atomic.Refusal.Should().Be(DatabaseRefusal.WithheldDependency, "the SQL-standard body reads the withheld table, and Postgres says so");
        atomic.Reason.Should().Be(DatabaseGate.WithheldDependencyDenial);

        DatabaseObjectDetail detail = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.LegacySchema, "zz_v_atomic", Token));
        detail.Dependencies.Should().Contain(static x => x.Visible == false, "the withheld table is a dependency, blanked");

        DatabaseRowAccessResult wrapped = await host.AccessAsync(x =>
            x.RequireRowAccessAsync(BrowserHost.Scope, fixture.LegacySchema, "zz_v_wrap", cancellationToken: Token));

        wrapped.Refusal.Should().Be(DatabaseRefusal.WithheldDependency);
        wrapped.Reason.Should().Be(DatabaseGate.OpaqueWithheldDenial);

        DatabaseRowAccessResult plain = await host.AccessAsync(x =>
            x.RequireRowAccessAsync(BrowserHost.Scope, fixture.LegacySchema, "zz_v_plain", cancellationToken: Token));

        plain.Allowed.Should().BeTrue("a SQL-standard body that reads nothing is followed and found harmless: " + plain.Reason);

        await using AsyncServiceScope circuit = host.Services.CreateAsyncScope();
        TableRowPage page = await circuit.ServiceProvider.GetRequiredService<ITableRowService>().ListRowsAsync(
            BrowserHost.Scope, fixture.LegacySchema, "zz_v_wrap", new TableRowRequest(), Token);

        page.Rows.Should().BeEmpty();
        page.Refusal.Should().Be(DatabaseRefusal.WithheldDependency);
    }

    /// <summary>A view over a visible Marten document table is refused its rows: Marten's tables are never read raw.</summary>
    [PostgresFact]
    public async Task A_view_over_a_visible_marten_document_table_is_refused_its_rows()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseRowAccessResult rows = await host.AccessAsync(x =>
            x.RequireRowAccessAsync(BrowserHost.Scope, fixture.LegacySchema, "zz_docs", cancellationToken: Token));

        rows.Refusal.Should().Be(DatabaseRefusal.MartenOwned);
        rows.Reason.Should().Be(DatabaseGate.MartenDependencyDenial);

        DatabaseObjectDefinition query = await host.ObjectsAsync(x => x.GetDefinitionAsync(
            BrowserHost.Scope, new DatabaseObjectRef(DatabaseObjectKind.View, fixture.LegacySchema, "zz_docs"), Token));

        query.Refusal.Should().Be(DatabaseRefusal.None, "the query names a table the list shows anyway: " + query.Reason);

        (await host.AccessAsync(x => x.RequireRowAccessAsync(BrowserHost.Scope, fixture.LegacySchema, "job_summary", cancellationToken: Token)))
            .Allowed.Should().BeTrue("a view over the host's own tables is read as before");
    }
}
