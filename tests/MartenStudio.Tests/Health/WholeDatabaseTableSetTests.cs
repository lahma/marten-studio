using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Sql;

using Microsoft.Extensions.DependencyInjection;

using static MartenStudio.Tests.Health.TableCheckBuilders;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Tests.Health;

/// <summary>
/// G1M (issue #8, G1): MartenStudio's table checks judge every table of the store's schemas and of the ones the database
/// browser admits, with the store's declarations laid over the catalog - and nothing changes for a table the store
/// declares.
/// </summary>
/// <remarks>
/// No database: the catalog's rows are stated outright and handed to the same classification a sweep runs, over real
/// store configurations that never connect.
/// </remarks>
public class WholeDatabaseTableSetTests
{
    private const string StoreSchema = "g1m";
    private const string EventSchema = "g1m_events";
    private const string Legacy = "legacy";

    /// <summary>The swept store: two document types, an event store, HiLo for the int id.</summary>
    private static DocumentStore Store() => DocumentStore.For(options =>
    {
        options.Connection(SqlTestStore.Unreachable);
        options.DatabaseSchemaName = StoreSchema;
        options.Events.DatabaseSchemaName = EventSchema;
        options.Schema.For<HealthOrderDoc>();
        options.Schema.For<HealthIntDoc>();
    });

    /// <summary>Another registered store, sharing the document schema and with an event store of its own in <c>legacy</c>.</summary>
    private static DocumentStore OtherStore() => DocumentStore.For(options =>
    {
        options.Connection(SqlTestStore.Unreachable);
        options.DatabaseSchemaName = StoreSchema;
        options.Events.DatabaseSchemaName = Legacy;
        options.Schema.For<HealthLongDoc>();
    });

    private static MartenStudioOptions Options(bool browse = true, bool hide = false, bool readOnly = false, params string[] browsable)
    {
        MartenStudioOptions options = new() { ReadOnly = readOnly };
        options.Capabilities.BrowseDatabase = browse;

        foreach (string schema in browsable.Length == 0 ? [Legacy] : browsable)
        {
            options.BrowsableSchemas.Add(schema);
        }

        if (hide)
        {
            options.IsDocumentTypeVisible = static type => type != typeof(HealthIntDoc);
        }

        return options;
    }

    private static HealthCheckContext Context(DocumentStore store, MartenStudioOptions options, IServiceProvider services) => new(
        new ResolvedScope(
            new StudioScope("default", string.Empty, null),
            new MartenStoreRegistration("default", "Default", typeof(IDocumentStore)),
            store,
            store.Storage.Database),
        MartenStudioSettings.Of(options),
        MartenStudioRuntime.Current,
        services,
        new HealthSqlRunner(new HealthConnection(MartenStudioDatabase.Of(store.Storage.Database)), new ReadOnlySqlOptions(), null),
        Now,
        new HealthSweepMemo(),
        CancellationToken.None);

    private static DatabaseObjectClassifier Classifier(MartenStudioOptions options, params (string Key, DocumentStore Store)[] stores) =>
        new(
            [.. stores.Select(x => new StoreDeclarations(
                x.Key,
                SchemaDeclarationReader.ReadForClassification(x.Store.Options, options.IsDocumentTypeVisible).Declarations!))],
            hidesDocumentTypes: options.IsDocumentTypeVisible is not null);

    private static HealthRelationalTableRow Row(string schema, string table, long pages = 10, int total = 0) =>
        new(schema, table, "r", pages, total);

    /// <summary>The set a sweep would build from <paramref name="rows" />, the swept store's and the other store's declarations read.</summary>
    private static (MartenDatabaseTableSet Set, MartenTableScope Scope) Build(
        MartenStudioOptions options,
        IReadOnlyList<HealthRelationalTableRow> rows,
        int tableCap = MartenHealthTableSource.TableCap)
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        using DocumentStore store = Store();
        using DocumentStore other = OtherStore();

        MartenTableSet declared = MartenTables.Read(Context(store, options, services));
        DatabaseObjectClassifier classifier = Classifier(options, ("default", store), ("billing", other));
        MartenTableScope scope = MartenTableScope.For(declared, classifier, MartenStudioSettings.Of(options), "default", tableCap);

        return (MartenDatabaseTables.Build(declared, rows, scope, classifier, tableCap), scope);
    }

    // -----------------------------------------------------------------------------------------------
    // Classification
    // -----------------------------------------------------------------------------------------------

    /// <summary>Every kind of table the catalog can list beside the store, and what the set makes of it.</summary>
    /// <param name="schema">Its schema.</param>
    /// <param name="table">Its name.</param>
    /// <param name="hide">Whether the studio hides <c>HealthIntDoc</c> (so a document type may be hidden).</param>
    /// <param name="ownership">The ownership it is judged under, or <see langword="null" /> for a table left out.</param>
    /// <param name="kind">Its kind.</param>
    /// <param name="library">The library that owns it, for a library's table.</param>
    /// <param name="gated">Whether a finding naming it carries its schema in <c>GatedSchemas</c>.</param>
    [Theory]
    [InlineData(StoreSchema, "mt_doc_healthorderdoc", false, "Owned", "DocumentTable", null, false)]
    [InlineData(EventSchema, "mt_events", false, "Owned", "EventTable", null, false)]
    [InlineData(StoreSchema, "mt_hilo", false, "Owned", "Bookkeeping", null, false)]
    [InlineData(StoreSchema, "app_settings", false, "Owned", "Relational", null, false)]
    [InlineData(StoreSchema, "qrtz_triggers", false, "Library", "Relational", "Quartz.NET", false)]
    [InlineData(StoreSchema, "wolverine_incoming_envelopes", false, "Library", "Relational", "Wolverine", false)]
    [InlineData(StoreSchema, "mt_doc_orphan", false, "Library", "DocumentTable", "Marten", false)]
    [InlineData(StoreSchema, "mt_doc_orphan", true, null, null, null, false)]
    [InlineData(StoreSchema, "mt_doc_healthlongdoc", false, null, null, null, false)]
    [InlineData(Legacy, "mt_events", false, null, null, null, false)]
    [InlineData(Legacy, "departments", false, "Other", "Relational", null, true)]
    [InlineData(Legacy, "qrtz_locks", false, "Library", "Relational", "Quartz.NET", true)]
    [InlineData(StoreSchema, "mt_doc_healthintdoc", true, null, null, null, false)]
    public void A_table_beside_the_store_is_judged_as_whose_it_is(
        string schema,
        string table,
        bool hide,
        string? ownership,
        string? kind,
        string? library,
        bool gated)
    {
        (MartenDatabaseTableSet set, _) = Build(Options(hide: hide), [Row(schema, table, total: 1)]);
        StudioTable? found = set.Find(schema, table);

        if (ownership is null)
        {
            found.Should().BeNull("{0}.{1} is not this sweep's to judge", schema, table);
            return;
        }

        StudioTableOwnership owner = Enum.Parse<StudioTableOwnership>(ownership);
        StudioTableKind what = Enum.Parse<StudioTableKind>(kind!);

        found.Should().NotBeNull();
        found!.Ownership.Should().Be(owner);
        found.Kind.Should().Be(what);
        found.Library.Should().Be(library);
        found.GatedSchemas.Should().Equal(gated ? [schema] : []);
        found.ExemptFromIndexDesign.Should().Be(owner == StudioTableOwnership.Library, "a library's table is exempt from the index-design rules only");
        found.IsDeclaredByStore.Should().Be(owner == StudioTableOwnership.Owned && what != StudioTableKind.Relational);

        if (!found.IsDeclaredByStore)
        {
            found.TypeName.Should().BeNull("a table no store declares is never named as a document type");
            found.DocumentTypes.Should().BeEmpty();
        }
    }

    [Fact]
    public void A_declared_table_is_exactly_what_the_declared_set_says_it_is()
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        using DocumentStore store = Store();
        MartenTableSet declared = MartenTables.Read(Context(store, Options(), services));

        (MartenDatabaseTableSet set, _) = Build(Options(), [Row(StoreSchema, "app_settings", total: 1)]);

        foreach (StudioTable table in declared.Tables)
        {
            StudioTable judged = set.Find(table.Schema, table.Table)!;
            judged.Should().BeEquivalentTo(table, "a declared table keeps its kind, ownership, gates and type, whatever lies beside it");
            judged.IsDeclaredByStore.Should().BeTrue();
        }

        set.Tables.Should().HaveCount(declared.Tables.Count + 1);
        set.Tables.Select(static x => x.Key).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void GatedSchemas_is_set_exactly_when_the_schema_is_not_one_of_the_stores_own()
    {
        (MartenDatabaseTableSet set, MartenTableScope scope) = Build(
            Options(),
            [
                Row(StoreSchema, "app_settings"),
                Row(StoreSchema, "qrtz_triggers"),
                Row(Legacy, "departments"),
                Row(Legacy, "qrtz_locks"),
                Row(EventSchema, "audit_trail"),
            ]);

        set.Tables.Should().HaveCountGreaterThan(5);

        foreach (StudioTable table in set.Tables)
        {
            bool outside = !scope.StoreSchemas.Contains(table.Schema, StringComparer.Ordinal);
            table.GatedSchemas.Should().Equal(outside ? [table.Schema] : [], "{0}", table.Key);
        }
    }

    [Fact]
    public void The_declared_tables_are_always_kept_and_count_toward_the_cap_first()
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        using DocumentStore store = Store();
        int declared = MartenTables.Read(Context(store, Options(), services)).Tables.Count;

        // Room for two beside the declared tables; the statement listed the largest two of five, largest first.
        (MartenDatabaseTableSet set, MartenTableScope scope) = Build(
            Options(),
            [Row(StoreSchema, "big", 900, total: 5), Row(StoreSchema, "bigger", 800, total: 5), Row(StoreSchema, "small", 1, total: 5)],
            tableCap: declared + 2);

        scope.Cap.Should().Be(2);
        set.Tables.Should().HaveCount(declared + 2);
        set.Find(StoreSchema, "small").Should().BeNull("past the cap");
        set.Tables.Where(static x => x.IsDeclaredByStore).Should().HaveCount(declared, "every declared table is in the set");
        set.Coverage.Should().Be(
            "the store's " + declared + " declared tables and the 2 largest of 5 other tables by estimated size (a sweep judges at most "
            + (declared + 2) + "); another registered Marten store's tables are not judged here");
    }

    [Fact]
    public void Declared_tables_that_fill_the_cap_by_themselves_read_no_catalog_and_say_so()
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        using DocumentStore store = Store();
        int declared = MartenTables.Read(Context(store, Options(), services)).Tables.Count;

        (MartenDatabaseTableSet set, MartenTableScope scope) = Build(Options(), [], tableCap: declared);

        scope.ReadsCatalog.Should().BeFalse();
        set.Tables.Should().HaveCount(declared);
        set.Coverage.Should().Be("only the " + declared + " tables the store declares, which fill the " + declared + " a sweep judges by themselves"
            + "; another registered Marten store's tables are not judged here", "the other store shares the document schema");
    }

    [Fact]
    public void Another_stores_tables_are_left_out_and_the_coverage_says_so_without_naming_them()
    {
        (MartenDatabaseTableSet set, MartenTableScope scope) = Build(Options(), [Row(StoreSchema, "mt_doc_healthlongdoc", total: 1), Row(StoreSchema, "app_settings", total: 1)]);

        scope.OtherStoresDeclareHere.Should().BeTrue("the other store shares the document schema");
        set.Coverage.Should().Be("another registered Marten store's tables are not judged here");
        set.Coverage.Should().NotContain("billing").And.NotContain("healthlongdoc");
    }

    // -----------------------------------------------------------------------------------------------
    // What the statement is asked
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_schema_outside_the_stores_own_is_read_only_while_the_database_browser_is_open()
    {
        MartenTableScope open = Build(Options(browsable: [Legacy, "*"]), []).Scope;
        open.Names.Should().Equal([StoreSchema, EventSchema, Legacy]);
        open.Everything.Should().BeTrue();

        foreach (MartenStudioOptions closed in new[] { Options(browse: false, browsable: [Legacy, "*"]), Options(readOnly: true, browsable: [Legacy, "*"]) })
        {
            MartenTableScope scope = Build(closed, []).Scope;
            scope.Names.Should().Equal([StoreSchema, EventSchema], "D27 admits nothing outside the store's own schemas without the browser");
            scope.Everything.Should().BeFalse();
        }
    }

    [Fact]
    public void The_statement_leaves_out_every_declared_table_but_never_binds_a_hidden_ones_name()
    {
        MartenTableScope shown = Build(Options(), []).Scope;
        shown.SkipDocumentTables.Should().BeFalse("no type is hidden, so an orphan document table is judged as Marten's");
        shown.Declared.Should().Contain(new HealthTableTarget(StoreSchema, "mt_doc_healthorderdoc"))
            .And.Contain(new HealthTableTarget(StoreSchema, "mt_doc_healthintdoc"))
            .And.Contain(new HealthTableTarget(StoreSchema, "mt_doc_healthlongdoc"), "another store's table is not this sweep's")
            .And.Contain(new HealthTableTarget(Legacy, "mt_events"));
        shown.Cap.Should().Be(MartenHealthTableSource.TableCap - shown.DeclaredCount);

        MartenTableScope hidden = Build(Options(hide: true), []).Scope;
        hidden.SkipDocumentTables.Should().BeTrue("a document table nobody declares may be a hidden type's");
        hidden.Declared.Should().NotContain(static x => x.Table.Contains("healthintdoc", StringComparison.OrdinalIgnoreCase),
            "a hidden type's table name is never a value of a statement");
        HealthTableQueries.UndeclaredTables(hidden.Names, hidden.Everything, hidden.Declared, hidden.SkipDocumentTables, hidden.Cap)
            .Parameters.Single(static x => x.ParameterName == "skip_documents").Value.Should().Be(true);
    }

    /// <summary>
    /// The real fail-closed path: another registration that will not build makes every store's declarations a failure
    /// (<c>Succeeded: false</c>), and the source - through the real <see cref="MartenDatabaseDeclarationsSource" /> - judges
    /// the declared tables alone, reads no catalog, and names no store.
    /// </summary>
    [Fact]
    public async Task Another_registration_that_will_not_build_leaves_the_declared_tables_alone_judged()
    {
        using DocumentStore store = Store();
        MartenStudioOptions options = Options();

        ServiceCollection collection = new();
        collection.AddSingleton<IDocumentStore>(store);
        collection.AddSingleton<MartenStudio.Tests.Services.MartenStoreRegistryTest.IBrokenStore>(
            static _ => throw new InvalidOperationException("no connection string was configured"));
        MartenStoreRegistry registry = new(collection);
        collection.AddSingleton<IDatabaseDeclarationsSource>(provider =>
            new MartenDatabaseDeclarationsSource(registry, provider, Microsoft.Extensions.Options.Options.Create(options)));

        await using ServiceProvider services = collection.BuildServiceProvider();
        HealthCheckContext context = Context(store, options, services);

        services.GetRequiredService<IDatabaseDeclarationsSource>().ReadUncached(context.Source).Succeeded
            .Should().BeFalse("the precondition: a registered store that will not build fails the classification closed");

        IStudioTableSet set = await new MartenHealthTableSource().TablesAsync(context);

        set.Coverage.Should().Be(MartenDatabaseTables.UnclassifiedCoverage);
        set.Coverage.Should().NotContain(nameof(MartenStudio.Tests.Services.MartenStoreRegistryTest.IBrokenStore));
        set.Tables.Select(static x => x.IsDeclaredByStore).Should().AllBeEquivalentTo(true);
        context.Sql.Statements.Should().BeEmpty("no catalog is read for a set that cannot be classified");
    }

    /// <summary>A declarations source that throws - one that will not even resolve in a scope with no circuit - fails closed too.</summary>
    [Fact]
    public async Task A_declarations_source_that_throws_leaves_the_declared_tables_alone_judged()
    {
        using DocumentStore store = Store();
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IDatabaseDeclarationsSource, ThrowingDeclarations>()
            .BuildServiceProvider();
        HealthCheckContext context = Context(store, Options(), services);

        IStudioTableSet set = await new MartenHealthTableSource().TablesAsync(context);

        set.Coverage.Should().Be(MartenDatabaseTables.UnclassifiedCoverage);
        set.Tables.Select(static x => x.IsDeclaredByStore).Should().AllBeEquivalentTo(true);
        context.Sql.Statements.Should().BeEmpty();
    }

    /// <summary>The catalog's names are distinct as Postgres keeps them; a declared name is matched as Marten matches it.</summary>
    [Fact]
    public void Two_undeclared_tables_that_differ_only_in_case_are_both_judged_and_a_declared_name_ignores_case()
    {
        (MartenDatabaseTableSet set, _) = Build(
            Options(),
            [Row(StoreSchema, "Orders"), Row(StoreSchema, "orders"), Row(StoreSchema, "MT_DOC_HEALTHORDERDOC")]);

        StudioTable upper = set.Find(StoreSchema, "Orders")!;
        StudioTable lower = set.Find(StoreSchema, "orders")!;

        upper.Should().NotBeSameAs(lower, "two tables, each judged and counted");
        upper.Table.Should().Be("Orders");
        lower.Table.Should().Be("orders");
        set.Tables.Count(static x => !x.IsDeclaredByStore).Should().Be(2,
            "a name that differs from a declared one only in case is the declared table, which the statement leaves out too");
        StudioTable declared = set.Find(StoreSchema, "MT_DOC_HEALTHORDERDOC")!;
        declared.Should().BeSameAs(set.Find(StoreSchema, "mt_doc_healthorderdoc"));
        declared.IsDeclaredByStore.Should().BeTrue();
    }

    private sealed class ThrowingDeclarations : IDatabaseDeclarationsSource
    {
        public DatabaseDeclarations Read(ResolvedSource resolved) => throw new InvalidOperationException("will not read");

        public DatabaseDeclarations Read(IStudioSource source) => throw new InvalidOperationException("will not read");

        public DatabaseDeclarations ReadUncached(ResolvedSource resolved) => throw new InvalidOperationException("will not read");

        public Task<IReadOnlyDictionary<string, DatabaseStoreAccess>> OtherSourcesAsync(
            ResolvedSource resolved, DatabaseObjectClassifier classifier, bool capabilityEnabled, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("will not read");
    }

    [Fact]
    public async Task A_source_that_cannot_read_every_stores_declarations_judges_the_declared_tables_alone()
    {
        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        using DocumentStore store = Store();
        HealthCheckContext context = Context(store, Options(), services);

        IStudioTableSet set = await new MartenHealthTableSource().TablesAsync(context);

        set.Coverage.Should().Be(MartenDatabaseTables.UnclassifiedCoverage, "fail closed: nothing here can be told from another store's");
        set.Tables.Select(static x => x.IsDeclaredByStore).Should().AllBeEquivalentTo(true);
        context.Sql.Statements.Should().BeEmpty("no catalog is read for a set that cannot be classified");
    }

    // -----------------------------------------------------------------------------------------------
    // Where each index comes from, and the remedies
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void An_index_on_a_table_no_store_declares_is_its_owners_and_on_an_undeclared_Marten_table_Martens()
    {
        (MartenDatabaseTableSet set, _) = Build(Options(), [Row(StoreSchema, "app_settings"), Row(StoreSchema, "mt_doc_orphan"), Row(Legacy, "departments")]);

        StudioTable host = set.Find(StoreSchema, "app_settings")!;
        StudioTable orphan = set.Find(StoreSchema, "mt_doc_orphan")!;
        StudioTable other = set.Find(Legacy, "departments")!;

        set.OriginOf(IndexRow(host, "app_settings_by_key", "setting_key"), host).Should().Be(IndexOrigin.Host);
        set.OriginOf(IndexRow(host, "app_settings_pkey", "setting_key") with { IsPrimaryKey = true }, host).Should().Be(IndexOrigin.PrimaryKey);
        set.OriginOf(IndexRow(other, "departments_by_name", "name"), other).Should().Be(IndexOrigin.Host);
        set.OriginOf(IndexRow(orphan, "mt_doc_orphan_idx_x", "x"), orphan).Should().Be(IndexOrigin.StoreManaged);

        StudioTable events = set.Find(EventSchema, "mt_events")!;
        set.OriginOf(IndexRow(events, "z_hand_made", "type"), events).Should().Be(set.Declared.OriginOf(IndexRow(events, "z_hand_made", "type"), events),
            "a declared table's indexes are told apart exactly as before");
    }

    [Fact]
    public void The_hosts_own_table_beside_the_store_gets_Postgres_Studios_migration_SQL()
    {
        StudioTable orders = new(StoreSchema, "orders", StudioTableKind.Relational, StudioTableOwnership.Owned);
        HealthIndexRow index = IndexRow(orders, "orders_by_note", "note");
        MartenHealthRemedyProvider marten = MartenHealthRemedyProvider.Instance;

        HealthRemedy?[] remedies =
        [
            marten.IndexTheFilteredMember(orders),
            marten.RemoveUnusedIndex(orders, index, IndexOrigin.Host),
            marten.RemoveRedundantIndex(orders, index, IndexOrigin.Host, IndexRow(orders, "orders_by_note_and_id", "note", "id")),
            marten.RebuildInvalidIndex(orders, index, IndexOrigin.Host),
            marten.LeaveRoomForHotUpdates(orders, 80),
            marten.ReenableAutovacuum(orders),
            marten.AutovacuumSetting(orders, "autovacuum_vacuum_scale_factor", "0.02", "Vacuums sooner."),
        ];

        remedies.Should().OnlyContain(static x => x != null && x.Kind == HealthRemedyKind.Migration, "the host's own table is the host's migrations' to change");
        remedies[0]!.Code.Should().Be(PostgresHealthTableRemedies.Instance.IndexTheFilteredMember(orders)!.Code, "composed, never copied");
        remedies[5]!.Code.Should().Contain("\"g1m\".\"orders\"").And.Contain("RESET (autovacuum_enabled)");
        remedies.Should().OnlyContain(static x => !x!.Code.Contains("StoreOptions", StringComparison.Ordinal) && !x.Code.Contains("opts.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_librarys_table_gets_no_design_remedy_and_its_upkeep_says_the_design_is_left_alone()
    {
        StudioTable jobs = new(StoreSchema, "qrtz_fired_triggers", StudioTableKind.Relational, StudioTableOwnership.Library) { Library = "Quartz.NET" };
        HealthIndexRow index = IndexRow(jobs, "idx_qrtz_ft_j_g", "sched_name", "job_name");
        MartenHealthRemedyProvider marten = MartenHealthRemedyProvider.Instance;

        marten.IndexTheFilteredMember(jobs).Should().BeNull();
        marten.IndexTheUnnamedMember(jobs).Should().BeNull();
        marten.RemoveUnusedIndex(jobs, index, IndexOrigin.Host).Should().BeNull();
        marten.RemoveRedundantIndex(jobs, index, IndexOrigin.Host, index).Should().BeNull();
        marten.LeaveRoomForHotUpdates(jobs, 80).Should().BeNull();
        marten.IndexAColumnInPlaceOfAnExpression(jobs).Should().BeNull();

        foreach (HealthRemedy? upkeep in new[]
        {
            marten.ReenableAutovacuum(jobs),
            marten.AutovacuumSetting(jobs, "autovacuum_vacuum_scale_factor", "0.02", "Vacuums sooner."),
            marten.RebuildInvalidIndex(jobs, index, IndexOrigin.Host),
        })
        {
            upkeep.Should().NotBeNull();
            upkeep!.Kind.Should().Be(HealthRemedyKind.Migration);
            upkeep.Explanation.Should().StartWith("The table is Quartz.NET's: its columns and indexes are that library's design, and this changes neither");
            upkeep.Explanation.Should().EndWith(HealthRemedy.HandOverNote);
            upkeep.Explanation!.Split(HealthRemedy.HandOverNote).Should().HaveCount(2, "the hand-over note is said once");
        }
    }

    [Fact]
    public void Somebody_elses_table_gets_its_upkeep_only_and_an_undeclared_Marten_table_nothing()
    {
        StudioTable other = new(Legacy, "departments", StudioTableKind.Relational, StudioTableOwnership.Other) { GatedSchemas = [Legacy] };
        StudioTable orphan = new(StoreSchema, "mt_doc_orphan", StudioTableKind.DocumentTable, StudioTableOwnership.Library) { Library = "Marten" };
        MartenHealthRemedyProvider marten = MartenHealthRemedyProvider.Instance;

        marten.IndexTheFilteredMember(other).Should().BeNull("its design is whoever owns the schema's");
        marten.LeaveRoomForHotUpdates(other, 80).Should().BeNull();
        marten.ReenableAutovacuum(other)!.Explanation.Should().Be(PostgresHealthTableRemedies.Instance.ReenableAutovacuum(other)!.Explanation);

        marten.ReenableAutovacuum(orphan).Should().BeNull("no store declares it, and a statement on a Marten-named table is one a store may undo");
        marten.AutovacuumSetting(orphan, "autovacuum_vacuum_scale_factor", "0.02", "Vacuums sooner.").Should().BeNull();
        marten.RebuildInvalidIndex(orphan, IndexRow(orphan, "x", "y"), IndexOrigin.StoreManaged).Should().BeNull();
    }

    [Fact]
    public void Undeclared_tables_are_never_mistaken_for_the_stores_own()
    {
        new StudioTable("s", "t", StudioTableKind.Relational, StudioTableOwnership.Owned).IsDeclaredByStore.Should().BeFalse();
        new StudioTable("s", "t", StudioTableKind.Relational, StudioTableOwnership.Library).IsDeclaredByStore.Should().BeFalse();
        new StudioTable("s", "mt_doc_t", StudioTableKind.DocumentTable, StudioTableOwnership.Library).IsDeclaredByStore.Should().BeFalse();
        EventTable().IsDeclaredByStore.Should().BeTrue();
        Document<HealthOrderDoc>().IsDeclaredByStore.Should().BeTrue();
    }
}
