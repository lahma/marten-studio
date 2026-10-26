using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.Sources;

/// <summary>
/// PostgresStudio's five seam implementations (PS-3b): what a plain data source declares, which tables its health checks
/// judge and whose each one is, what its remedies say, how its locks are labelled, and where its links go.
/// </summary>
public sealed class PostgresSeamTests : IDisposable
{
    private readonly NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=localhost;Database=app;Username=app");

    public void Dispose() => dataSource.Dispose();

    private ResolvedSource Resolved(string key = "default") =>
        new(new StudioScope(key, key, null), new StudioSourceRegistration(key, key, StudioSourceKind.DataSource), new DataSourceDatabase(key, dataSource));

    private PostgresDataSourceSource Source(string key = "default") =>
        new PostgresDataSourceSource(new StudioSourceRegistration(key, key, StudioSourceKind.DataSource), new DataSourceDatabase(key, dataSource));

    private static PostgresDatabaseDeclarationsSource Declarations(params string[] browsable)
    {
        PostgresStudioOptions options = new();
        options.BrowsableSchemas.Clear();

        foreach (string schema in browsable)
        {
            options.BrowsableSchemas.Add(schema);
        }

        return new PostgresDatabaseDeclarationsSource(PostgresStudioSettings.Of(options));
    }

    // ---- The declarations ---------------------------------------------------------------------------------------

    [Fact]
    public void Own_schemas_are_the_exact_names_BrowsableSchemas_lists_and_never_the_star_or_a_system_schema()
    {
        DatabaseDeclarations declared = Declarations("public", "*", "legacy", "pg_catalog", "information_schema", "public", "with\"quote")
            .Read(Resolved());

        declared.Succeeded.Should().BeTrue("a plain data source has nothing that could fail to be read");
        declared.StoreSchemas.Should().Equal(["public", "legacy"], "in order, once each - \"*\" is a statement about the database, not configuration");
        declared.Classifier!.Stores.Should().BeEmpty();
        declared.Classifier.MayHideDocumentTypes.Should().BeFalse("no store is registered, so nothing is hidden");
    }

    [Fact]
    public void Both_reads_answer_the_same_schemas_whichever_database_was_resolved()
    {
        PostgresDatabaseDeclarationsSource declarations = Declarations("public", "quartz");

        DatabaseDeclarations byResolved = declarations.Read(Resolved());
        DatabaseDeclarations bySource = declarations.Read(Source());
        DatabaseDeclarations uncached = declarations.ReadUncached(Resolved("reporting"));

        bySource.StoreSchemas.Should().Equal(byResolved.StoreSchemas);
        uncached.StoreSchemas.Should().Equal(byResolved.StoreSchemas);
    }

    [Fact]
    public void An_empty_list_declares_no_schema_of_its_own()
    {
        Declarations().Read(Resolved()).StoreSchemas.Should().BeEmpty();
        Declarations("*").Read(Resolved()).StoreSchemas.Should().BeEmpty();
    }

    [Theory]
    [InlineData("mt_doc_order")]
    [InlineData("mt_events")]
    [InlineData("mt_streams")]
    [InlineData("mt_hilo")]
    public void A_table_named_like_Martens_is_Marten_internal_and_its_rows_are_never_read(string table)
    {
        DatabaseObjectClassifier classifier = Declarations("public").Read(Resolved()).Classifier!;

        DatabaseObjectOwnership ownership = classifier.ClassifyRelation("public", table)!;

        ownership.Owner.Should().Be(DatabaseObjectOwner.MartenInfrastructure);
        ownership.IsMarten.Should().BeTrue("the mt_ rule is the shared one, and its rows are refused (DatabaseRefusal.MartenOwned)");
    }

    [Theory]
    [InlineData("qrtz_triggers", "Quartz.NET")]
    [InlineData("__EFMigrationsHistory", "EF Core")]
    [InlineData("orders", null)]
    public void Everything_else_is_Other_with_a_recognised_libraries_hint(string table, string? hint)
    {
        DatabaseObjectOwnership ownership = Declarations("public").Read(Resolved()).Classifier!.ClassifyRelation("public", table)!;

        ownership.Owner.Should().Be(DatabaseObjectOwner.Other);
        ownership.RecognisedAs.Should().Be(hint);
    }

    [Fact]
    public async Task No_other_source_is_ever_asked_about()
    {
        PostgresDatabaseDeclarationsSource declarations = Declarations("public");
        DatabaseObjectClassifier classifier = declarations.Read(Resolved()).Classifier!;

        (await declarations.OtherSourcesAsync(Resolved(), classifier, capabilityEnabled: true, TestContext.Current.CancellationToken))
            .Should().BeEmpty();
    }

    // ---- The table set ------------------------------------------------------------------------------------------

    private static HealthRelationalTableRow Row(string schema, string table, long pages = 10, int total = 4) => new(schema, table, "r", pages, total);

    [Fact]
    public void Each_table_is_relational_and_owned_a_librarys_or_somebody_elses()
    {
        IStudioTableSet set = PostgresHealthTableSource.Build(
            [
                Row("public", "orders"),
                Row("public", "qrtz_triggers"),
                Row("public", "__EFMigrationsHistory"),
                Row("public", "mt_doc_order"),
                Row("public", "mt_events"),
                Row("legacy", "ledger"),
            ],
            ["public"],
            PostgresHealthTableSource.TableCap);

        set.Tables.Should().OnlyContain(static x => x.Kind == StudioTableKind.Relational);

        set.Find("public", "orders")!.Ownership.Should().Be(StudioTableOwnership.Owned);
        set.Find("public", "qrtz_triggers")!.Should().Match<StudioTable>(static x => x.Ownership == StudioTableOwnership.Library && x.Library == "Quartz.NET" && x.ExemptFromIndexDesign);
        set.Find("public", "__EFMigrationsHistory")!.Library.Should().Be("Entity Framework Core");
        set.Find("public", "mt_events")!.Library.Should().Be("Marten", "Marten's own tables are a library's");
        set.Find("public", "mt_doc_order")!.Ownership.Should().Be(StudioTableOwnership.Owned, "a document table is the application's documents (G1)");
        set.Find("legacy", "ledger")!.Ownership.Should().Be(StudioTableOwnership.Other, "only \"*\" admits its schema");
        set.Find("legacy", "ledger")!.GatedSchemas.Should().Equal("legacy");
        set.Find("public", "orders")!.GatedSchemas.Should().BeEmpty("an own schema's table needs no gate beyond the check's");
        set.Coverage.Should().BeNull("every table was listed");
    }

    [Fact]
    public void A_capped_set_says_how_many_it_left_out()
    {
        IStudioTableSet set = PostgresHealthTableSource.Build([Row("public", "a", total: 3412), Row("public", "b", total: 3412)], ["public"], 2);

        set.Coverage.Should().Be("the 2 largest of 3,412 tables by estimated size (a sweep judges at most 2)");
    }

    [Fact]
    public void Every_index_but_the_primary_key_is_the_hosts()
    {
        IStudioTableSet set = PostgresHealthTableSource.Build([Row("public", "orders")], ["public"], PostgresHealthTableSource.TableCap);
        StudioTable orders = set.Find("public", "orders")!;

        set.OriginOf(new HealthIndexRow { Schema = "public", Table = "orders", Name = "orders_pkey", IsPrimaryKey = true }, orders)
            .Should().Be(IndexOrigin.PrimaryKey);
        set.OriginOf(new HealthIndexRow { Schema = "public", Table = "orders", Name = "orders_by_customer" }, orders)
            .Should().Be(IndexOrigin.Host, "nothing reconciles a relational table's indexes, so no apply drops one");
    }

    [Fact]
    public void The_statement_binds_the_schemas_and_names_no_table()
    {
        HealthQuery query = HealthTableQueries.RelationalTables(["public", "quartz"], everything: true, 2000);

        query.Sql.Should().NotContain("public").And.NotContain("pg_relation_size").And.Contain("limit @cap");
        query.Parameters.Select(static x => x.ParameterName).Should().BeEquivalentTo(["names", "everything", "cap", "system"]);
        HealthTableQueries.SystemSchemasBeyondPrefix.Should().OnlyContain(static x => BrowsableSchemaMatcher.IsSystemSchema(x));
    }

    // ---- The remedies -------------------------------------------------------------------------------------------

    private static StudioTable Relational(string table, StudioTableOwnership ownership = StudioTableOwnership.Owned, string schema = "public") =>
        new(schema, table, StudioTableKind.Relational, ownership);

    private static HealthIndexRow Index(string name, bool unique = false, bool primary = false) =>
        new() { Schema = "public", Table = "orders", Name = name, IsUnique = unique, IsPrimaryKey = primary };

    [Fact]
    public void Every_remedy_is_migration_SQL_with_every_name_quoted_and_says_the_studio_never_runs_it()
    {
        PostgresHealthTableRemedies remedies = PostgresHealthTableRemedies.Instance;
        StudioTable orders = Relational("Order Lines");
        HealthIndexRow index = Index("by customer") with { Table = "Order Lines" };

        HealthRemedy?[] all =
        [
            remedies.IndexTheFilteredMember(orders),
            remedies.IndexTheUnnamedMember(orders),
            remedies.RebuildInvalidIndex(orders, index, IndexOrigin.Host),
            remedies.RemoveRedundantIndex(orders, index, IndexOrigin.Host, Index("orders_pkey")),
            remedies.RemoveUnusedIndex(orders, index, IndexOrigin.Host),
            remedies.LeaveRoomForHotUpdates(orders, 80),
            remedies.ReenableAutovacuum(orders),
            remedies.AutovacuumSetting(orders, "autovacuum_vacuum_scale_factor", "0.05", "Vacuums sooner."),
        ];

        all.Should().OnlyContain(static x => x != null && x.IsMigration && x.Explanation!.EndsWith(HealthRemedy.HandOverNote));
        all.Select(static x => x!.Code).Should().OnlyContain(static code => !code.Contains("\"Order Lines\"") || code.Contains("\"public\".\"Order Lines\""));

        all[2]!.Code.Should().Be("REINDEX INDEX CONCURRENTLY \"public\".\"by customer\";");
        all[4]!.Code.Should().Be("DROP INDEX CONCURRENTLY \"public\".\"by customer\";");
        all[5]!.Code.Should().Be("ALTER TABLE \"public\".\"Order Lines\" SET (fillfactor = 80);");
        all[6]!.Code.Should().Be("ALTER TABLE \"public\".\"Order Lines\" RESET (autovacuum_enabled);");
        all[7]!.Code.Should().Be("ALTER TABLE \"public\".\"Order Lines\" SET (autovacuum_vacuum_scale_factor = 0.05);");
        all[7]!.Explanation.Should().StartWith("Vacuums sooner.");
        remedies.IndexAColumnInPlaceOfAnExpression(orders).Should().BeNull("which column replaces an expression is the schema's design");
    }

    [Theory]
    [InlineData("mt_doc_order")]
    [InlineData("mt_events")]
    public void A_table_named_like_Martens_gets_no_SQL_because_Martens_apply_would_undo_it(string table)
    {
        PostgresHealthTableRemedies remedies = PostgresHealthTableRemedies.Instance;
        StudioTable marten = Relational(table, StudioTableOwnership.Owned);

        remedies.ReenableAutovacuum(marten).Should().BeNull();
        remedies.AutovacuumSetting(marten, "autovacuum_vacuum_scale_factor", "0.05", "x").Should().BeNull();
        remedies.IndexTheFilteredMember(marten).Should().BeNull();
        remedies.RemoveUnusedIndex(marten, Index("ix"), IndexOrigin.Host).Should().BeNull();
    }

    [Fact]
    public void A_table_that_is_not_relational_gets_none()
    {
        StudioTable document = new("public", "mt_doc_order", StudioTableKind.DocumentTable, StudioTableOwnership.Owned) { TypeName = "Order" };

        PostgresHealthTableRemedies.Instance.LeaveRoomForHotUpdates(document, 80).Should().BeNull();
        PostgresHealthTableRemedies.Instance.AutovacuumSetting(document, "fillfactor", "80", "x").Should().BeNull();
    }

    [Fact]
    public void A_librarys_table_gets_vacuum_and_a_rebuild_but_no_index_design()
    {
        PostgresHealthTableRemedies remedies = PostgresHealthTableRemedies.Instance;
        StudioTable quartz = Relational("qrtz_triggers", StudioTableOwnership.Library) with { Library = "Quartz.NET" };

        remedies.AutovacuumSetting(quartz, "autovacuum_vacuum_scale_factor", "0.05", "x").Should().NotBeNull();
        remedies.ReenableAutovacuum(quartz).Should().NotBeNull();
        remedies.RebuildInvalidIndex(quartz, Index("idx_qrtz_t_next_fire_time"), IndexOrigin.Host).Should().NotBeNull("an invalid index is broken, not badly designed");
        remedies.IndexTheFilteredMember(quartz).Should().BeNull();
        remedies.RemoveUnusedIndex(quartz, Index("idx_qrtz_t_state"), IndexOrigin.Host).Should().BeNull();
        remedies.LeaveRoomForHotUpdates(quartz, 80).Should().BeNull();
    }

    [Fact]
    public void An_index_that_backs_a_constraint_is_never_offered_to_DROP_INDEX()
    {
        PostgresHealthTableRemedies remedies = PostgresHealthTableRemedies.Instance;
        StudioTable orders = Relational("orders");

        remedies.RemoveUnusedIndex(orders, Index("orders_number_key", unique: true), IndexOrigin.Host).Should().BeNull();
        remedies.RemoveUnusedIndex(orders, Index("orders_pkey", primary: true), IndexOrigin.PrimaryKey).Should().BeNull();
        remedies.RemoveRedundantIndex(orders, Index("orders_number_key", unique: true), IndexOrigin.Host, Index("other")).Should().BeNull();
    }

    [Theory]
    [InlineData("we\"ird")]
    [InlineData("rlo‮txt")]
    public void A_name_that_cannot_be_quoted_gets_no_remedy_rather_than_a_misleading_one(string table)
    {
        StudioTable hostile = Relational(table);

        PostgresHealthTableRemedies.Instance.LeaveRoomForHotUpdates(hostile, 80).Should().BeNull();
        PostgresHealthTableRemedies.Instance.ReenableAutovacuum(hostile).Should().BeNull();
        PostgresHealthTableRemedies.Instance.RemoveUnusedIndex(Relational("orders"), Index(table), IndexOrigin.Host).Should().BeNull();
    }

    // ---- Locks and links ----------------------------------------------------------------------------------------

    [Fact]
    public void Locks_name_relations_only_in_a_databases_words()
    {
        DatabaseObjectClassifier classifier = Declarations("public").Read(Resolved()).Classifier!;
        SessionLockLabels labels = new PostgresSessionLockLabeler().For(Resolved(), classifier);

        labels.DaemonLockIds.Should().BeEmpty();
        labels.Advisory(4004).Should().Be(new LockTarget(LockTargetKind.OtherAdvisoryLock, "an advisory lock the application took"));
        labels.Describe("relation", new LockedRelation("public", "orders", "r")).Label.Should().Be("a relation");
        labels.Describe("relation", new LockedRelation("public", "orders", "r", LockedPart.Index)).Label.Should().Be("an index of a relation");
        labels.Describe("relation", new LockedRelation("public", "mt_doc_order", "r")).Should().Match<LockTarget>(static x =>
            x.Kind == LockTargetKind.UndeclaredMartenRelation && x.Label == "a relation named like Marten's" && x.Name == null);
    }

    [Fact]
    public void There_is_nowhere_to_link_a_Marten_object_to()
    {
        StudioScope scope = new("default", "default", null);
        PostgresStudioLinks links = PostgresStudioLinks.Instance;

        new[]
        {
            links.ToDocuments(scope), links.ToCollection(scope, "order"), links.ToDocument(scope, "order", "1"),
            links.ToStreams(scope), links.ToFeed(scope), links.ToProjections(scope), links.ToSchema(scope),
        }.Should().OnlyContain(static x => x == null);
    }
}
