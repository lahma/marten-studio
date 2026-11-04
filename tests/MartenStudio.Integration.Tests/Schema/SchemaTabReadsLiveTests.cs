using System.Diagnostics;
using System.Text.Json;

using Marten;

using MartenStudio.Integration.Tests.Database;
using PostgresStudio.Services.Database;
using MartenStudio.Services.Schema;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;
using PostgresStudio.Internal.Sql;

namespace MartenStudio.Integration.Tests.Schema;

/// <summary>
/// DB-7-fix against a real Postgres: the Schema screen's three navigation reads - Tables, Indexes, Functions -
/// over the database browser's own fixture, which puts a withheld schema's names into the store's own
/// structure and a hidden document type beside a visible one.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// <b>Masked like the browser</b> (the DB-1-fix re-review): an expression index calling a function in the
/// withheld schema, and a function taking a type from it, name <c>‹withheld›</c> - with no capability needed,
/// the Schema screen printed them in the clear.
/// </description></item>
/// <item><description>
/// <b>A hidden type's per-type routine is absent</b>: a database an earlier Marten wrote keeps
/// <c>mt_upsert_&lt;alias&gt;</c>, whose body names the hidden table.
/// </description></item>
/// <item><description>
/// <b>Degraded, not blanked</b> (item 3): an ancillary store whose factory throws used to blank Tables and
/// Indexes for every store and withhold every one of Marten's own function bodies.
/// </description></item>
/// <item><description>
/// <b>A lock gives up after three seconds</b> (item 6): a size function queues behind an
/// <c>ACCESS EXCLUSIVE</c> lock, and the tabs used to queue with it for the whole query timeout.
/// </description></item>
/// </list>
/// </remarks>
public class SchemaTabReadsLiveTests(SchemaTabReadsLiveTests.Fixture fixture) : IClassFixture<SchemaTabReadsLiveTests.Fixture>
{
    private const string Mask = WithheldNames.Token;

    private const string BrokenMessage = "its connection string is wrong";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The browser's fixture, plus a function taking a withheld type and two per-type routines.</summary>
    public sealed class Fixture(PostgresFixture postgres) : DatabaseBrowserFixture(postgres)
    {
        protected override string ExtraSql =>
            """
            create function {d}.zz_grade_of(g {hr}.grade) returns int language sql as $$ select 1 $$;
            create function {d}.mt_upsert_browsersecret(doc jsonb) returns void language plpgsql
                as $$ begin insert into {d}.{hidden} (data) values (doc); end $$;
            create function {d}.mt_upsert_browsercustomer(doc jsonb) returns void language sql as $$ select $$;
            """;
    }

    // ------------------------------------------------------------------------------------------------
    // Masking, and the hidden type's routines
    // ------------------------------------------------------------------------------------------------

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_Schema_tab_names_a_withheld_schema(bool capability)
    {
        await using BrowserHost host = fixture.Host(options =>
        {
            options.Capabilities.BrowseDatabase = capability;
            options.BrowsableSchemas.Add(fixture.DocumentSchema);
        });

        SchemaTables tables = await SchemaAsync(host, x => x.TablesAsync(BrowserHost.Scope, Token));
        SchemaIndexes indexes = await SchemaAsync(host, x => x.IndexesAsync(BrowserHost.Scope, Token));
        SchemaFunctions functions = await SchemaAsync(host, x => x.FunctionsAsync(BrowserHost.Scope, Token));

        tables.Reason.Should().BeNull();
        indexes.Reason.Should().BeNull();
        functions.Reason.Should().BeNull();

        IndexInfo expression = indexes.Indexes.Single(static x => x.Name == "hr_refs_norm");
        expression.Summary.Should().Be("btree ((expression))", "the list prints no statement (D27), so it can name no schema");

        // The statement is read on request, masked through the same gate: the expression calls a function in the withheld schema.
        SchemaIndexDefinition statement = await SchemaAsync(host, x => x.IndexDefinitionAsync(BrowserHost.Scope, expression.Schema, expression.Table, expression.Name, Token));
        statement.Sql.Should().Contain("(" + Mask + ".norm(id))").And.NotContain(fixture.HrSchema);

        FunctionInfo grade = functions.Functions.Single(static x => x.Name == "zz_grade_of");
        grade.IdentityArguments.Should().Be("g " + Mask + ".grade");
        grade.Signature.Should().NotContain(fixture.HrSchema);

        JsonSerializer.Serialize(tables).Should().NotContain(fixture.HrSchema);
        JsonSerializer.Serialize(indexes).Should().NotContain(fixture.HrSchema);
        JsonSerializer.Serialize(functions).Should().NotContain(fixture.HrSchema);
    }

    /// <summary>
    /// The masked signature the list shows is the one the definition read is asked with, and it finds its
    /// overload - with the body masked too.
    /// </summary>
    [PostgresFact]
    public async Task A_masked_signature_resolves_back_to_its_body_through_the_definition_read()
    {
        await using BrowserHost host = fixture.Host(options => options.BrowsableSchemas.Add(fixture.DocumentSchema));

        SchemaFunctions functions = await SchemaAsync(host, x => x.FunctionsAsync(BrowserHost.Scope, Token));
        FunctionInfo grade = functions.Functions.Single(static x => x.Name == "zz_grade_of");

        grade.DefinitionAvailable.Should().BeTrue(grade.DefinitionRefusal);

        DatabaseObjectDefinition body = await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, grade.Ref, Token));

        body.Found.Should().BeTrue(body.Reason);
        body.Sql.Should().Contain("zz_grade_of").And.Contain(Mask + ".grade").And.NotContain(fixture.HrSchema);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_hidden_types_per_type_routine_is_not_on_the_Functions_tab_and_a_visible_types_is(bool capability)
    {
        await using BrowserHost host = fixture.Host(options => options.Capabilities.BrowseDatabase = capability);

        SchemaFunctions functions = await SchemaAsync(host, x => x.FunctionsAsync(BrowserHost.Scope, Token));

        functions.Functions.Should().NotContain(static x => x.Name == "mt_upsert_browsersecret",
            "its body names the hidden type's table");
        functions.Functions.Should().Contain(static x => x.Name == "mt_upsert_browsercustomer");
        JsonSerializer.Serialize(functions).Should().NotContain("browsersecret");
    }

    // ------------------------------------------------------------------------------------------------
    // A store that cannot be built: degraded, not blanked
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task With_a_store_that_will_not_build_the_Tables_tab_keeps_what_is_declared_and_withholds_the_rest()
    {
        await using BrowserHost host = BrokenHost();

        SchemaTables tables = await SchemaAsync(host, x => x.TablesAsync(BrowserHost.Scope, Token));

        tables.Reason.Should().BeNull("an ancillary store that will not build no longer blanks the tab");
        tables.ClassificationNotice.Should().Contain("Marten store '" + nameof(ISchemaTabBrokenStore) + "' could not be built")
            .And.Contain(BrokenMessage)
            .And.Contain("2 tables here that no readable store declares are withheld");

        tables.Tables.Should().Contain(x => x.Table == fixture.VisibleTable && x.CollectionAlias != null);
        tables.Tables.Should().Contain(static x => x.Table == "mt_events", "the event store's tables are declared");
        tables.Tables.Should().NotContain(static x => x.Table == "host_settings" || x.Table == "hr_refs",
            "a table nobody readable declares could be the broken store's");
        tables.Tables.Should().NotContain(x => x.Table == fixture.HiddenTable);
    }

    [PostgresFact]
    public async Task With_a_store_that_will_not_build_the_Indexes_tab_keeps_the_declared_tables_indexes()
    {
        await using BrowserHost host = BrokenHost();

        SchemaIndexes indexes = await SchemaAsync(host, x => x.IndexesAsync(BrowserHost.Scope, Token));

        indexes.Reason.Should().BeNull();
        indexes.ClassificationNotice.Should().Contain("The indexes on 2 tables that no readable store declares are withheld");
        indexes.Indexes.Should().Contain(x => x.Table == fixture.VisibleTable && x.IsPrimaryKey);
        indexes.Indexes.Should().NotContain(static x => x.Table == "host_settings" || x.Table == "hr_refs");
        indexes.Indexes.Should().NotContain(x => x.Table == fixture.HiddenTable);
    }

    /// <summary>
    /// Marten's own routines keep their bodies - read with the list, since the database browser reads
    /// nothing while a store cannot be built - and the host's are withheld with the rest of what the broken
    /// store could own.
    /// </summary>
    [PostgresFact]
    public async Task With_a_store_that_will_not_build_Martens_own_function_bodies_are_still_shown()
    {
        await using BrowserHost host = BrokenHost();

        SchemaFunctions functions = await SchemaAsync(host, x => x.FunctionsAsync(BrowserHost.Scope, Token));

        functions.Reason.Should().BeNull();
        functions.ClassificationNotice.Should().Contain(nameof(ISchemaTabBrokenStore)).And.Contain("routines here that no readable store declares");

        List<FunctionInfo> martens = [.. functions.Functions.Where(static x => x.Name.StartsWith("mt_", StringComparison.Ordinal))];
        martens.Should().Contain(static x => x.Name == "mt_jsonb_patch").And.Contain(static x => x.Name == "mt_quick_append_events");
        martens.Should().OnlyContain(static x => x.DefinitionAvailable && x.Definition != null && x.Definition.Contains("CREATE OR REPLACE", StringComparison.Ordinal),
            "Marten's own bodies were always on this tab");

        functions.Functions.Should().NotContain(static x => x.Name == "host_fn" || x.Name == "zz_grade_of");
        functions.Functions.Should().NotContain(static x => x.Name == "mt_upsert_browsersecret");
        JsonSerializer.Serialize(functions).Should().NotContain(fixture.HrSchema);
    }

    /// <summary>A visitor the broken store's own store policy refuses is not told its key or its error.</summary>
    [PostgresFact]
    public async Task The_notice_names_the_broken_store_only_to_a_visitor_its_store_policy_passes()
    {
        await using BrowserHost host = BrokenHost(
            new ResourcePolicy(static resource => resource.StoreName != nameof(ISchemaTabBrokenStore)),
            static options => options.StoreAuthorizationPolicy = "schema-store");

        SchemaTables tables = await SchemaAsync(host, x => x.TablesAsync(BrowserHost.Scope, Token));

        tables.ClassificationNotice.Should().StartWith("A registered Marten store you may not see could not be built")
            .And.NotContain(nameof(ISchemaTabBrokenStore))
            .And.NotContain(BrokenMessage);
    }

    // ------------------------------------------------------------------------------------------------
    // A lock gives up after three seconds - and since 1.0 the Tables read takes none
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 1.0-HARD (D27): the Tables read sizes every table from the catalog's page counts and calls no size function, so a migration
    /// holding a listed table in <c>ACCESS EXCLUSIVE</c> mode no longer makes it wait - it answers at once, the locked table
    /// included, its sizes marked as estimates. (Before 1.0 it gave up after the three-second lock timeout and said it was busy.)
    /// </summary>
    [PostgresFact]
    public async Task The_tables_read_answers_while_a_migration_holds_a_table()
    {
        await using BrowserHost host = fixture.Host(static options => options.QueryTimeout = TimeSpan.FromSeconds(60));

        await using NpgsqlConnection migration = await fixture.Postgres.OpenAsync(Token);
        await using NpgsqlTransaction holding = await migration.BeginTransactionAsync(Token);
        await ExecuteAsync(migration, "lock table " + SqlIdentifier.Qualify(fixture.DocumentSchema, "host_settings") + " in access exclusive mode");

        var watch = Stopwatch.StartNew();
        SchemaTables tables = await SchemaAsync(host, x => x.TablesAsync(BrowserHost.Scope, Token));
        watch.Stop();

        tables.Busy.Should().BeFalse("the read locks nothing it lists, so another session's lock does not stop it");
        tables.Tables.Should().Contain(static x => x.Table == "host_settings");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "it never waits for the lock");

        await holding.RollbackAsync(Token);
    }

    /// <summary>
    /// 1.2-LOCKS (D27): like the Tables read before it, the index list prints no definition and so waits for nothing - it answers at
    /// once while a migration holds a listed index, the locked one included. An uncommitted <c>DROP INDEX</c> is the way to hold an
    /// <c>ACCESS EXCLUSIVE</c> lock on an index: a <c>LOCK TABLE</c> takes its table's, never its indexes'. The one statement that
    /// does deparse gives up after the three-second lock timeout and says it was busy.
    /// </summary>
    [PostgresFact]
    public async Task The_index_list_answers_while_a_migration_holds_an_index_and_only_its_statement_says_it_was_busy()
    {
        await using BrowserHost host = fixture.Host(static options => options.QueryTimeout = TimeSpan.FromSeconds(60));

        await using NpgsqlConnection migration = await fixture.Postgres.OpenAsync(Token);
        await using NpgsqlTransaction holding = await migration.BeginTransactionAsync(Token);
        await ExecuteAsync(migration, "drop index " + SqlIdentifier.Qualify(fixture.DocumentSchema, "hr_refs_norm"));

        var watch = Stopwatch.StartNew();
        SchemaIndexes indexes = await SchemaAsync(host, x => x.IndexesAsync(BrowserHost.Scope, Token));
        watch.Stop();

        indexes.Busy.Should().BeFalse(indexes.Reason, "the list locks nothing it lists, so another session's lock does not stop it");
        indexes.Indexes.Should().Contain(static x => x.Name == "hr_refs_norm");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "it never waits for the lock");

        IndexInfo locked = indexes.Indexes.Single(static x => x.Name == "hr_refs_norm");

        watch.Restart();
        SchemaIndexDefinition statement = await SchemaAsync(host, x => x.IndexDefinitionAsync(BrowserHost.Scope, locked.Schema, locked.Table, locked.Name, Token));
        watch.Stop();

        statement.Busy.Should().BeTrue(statement.Reason);
        statement.Reason.Should().Contain("The table this index is on is locked right now");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));

        await holding.RollbackAsync(Token);
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    /// <summary>The fixture's studio, with an ancillary store registered whose factory throws.</summary>
    private BrowserHost BrokenHost(
        Microsoft.AspNetCore.Authorization.IAuthorizationService? authorization = null,
        Action<MartenStudioOptions>? configure = null) =>
        fixture.Host(
            configure,
            authorization,
            services: static services => services.AddSingleton<ISchemaTabBrokenStore>(
                static _ => throw new InvalidOperationException(BrokenMessage)));

    private static async Task<T> SchemaAsync<T>(BrowserHost host, Func<ISchemaDataService, Task<T>> call)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredService<ISchemaDataService>());
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Token);
    }
}

/// <summary>An ancillary store whose factory throws - a bad connection string, as far as the studio can tell.</summary>
public interface ISchemaTabBrokenStore : IDocumentStore;
