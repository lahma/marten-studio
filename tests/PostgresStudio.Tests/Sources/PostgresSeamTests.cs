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

        set.Tables.Where(static x => !x.Table.StartsWith("mt_", StringComparison.Ordinal)).Should().OnlyContain(static x => x.Kind == StudioTableKind.Relational);

        set.Find("public", "orders")!.Ownership.Should().Be(StudioTableOwnership.Owned);
        set.Find("public", "qrtz_triggers")!.Should().Match<StudioTable>(static x => x.Ownership == StudioTableOwnership.Library && x.Library == "Quartz.NET" && x.ExemptFromIndexDesign);
        set.Find("public", "__EFMigrationsHistory")!.Library.Should().Be("Entity Framework Core");
        set.Find("public", "mt_events")!.Should().Match<StudioTable>(static x => x.Library == "Marten" && x.Kind == StudioTableKind.EventTable);
        set.Find("public", "mt_doc_order")!.Should().Match<StudioTable>(static x => x.Ownership == StudioTableOwnership.Library
            && x.Library == "Marten" && x.Kind == StudioTableKind.DocumentTable && x.ExemptFromIndexDesign,
            "a Marten store manages its document tables: its apply drops an index nobody declared, so none is the host's design");
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
        set.OwnSchemasCoverage.Should().Be(set.Coverage, "every table counted is in the data source's own schemas");
    }

    /// <summary>
    /// G1M F6, H1: with <c>"*"</c> the count takes in schemas only it admits, which the database browser shows only past its
    /// gate. A visitor short of it reads the sentence counted over the data source's own schemas alone - the ones
    /// <c>BrowsableSchemas</c> names exactly - which says nothing of what the others hold.
    /// </summary>
    [Fact]
    public void A_capped_set_over_star_says_to_a_visitor_kept_out_only_what_the_own_schemas_hold()
    {
        IStudioTableSet set = PostgresHealthTableSource.Build(
            [
                Row("legacy", "ledger", total: 3412) with { OwnTotal = 10 },
                Row("public", "orders", total: 3412) with { OwnTotal = 10 },
            ],
            ["public"],
            2);

        set.Coverage.Should().Be("the 2 largest of 3,412 tables by estimated size (a sweep judges at most 2)");
        set.OwnSchemasCoverage.Should().Be("the 1 largest of 10 tables in the data source's own schemas by estimated size (a sweep judges at most 2)");

        IStudioTableSet whole = PostgresHealthTableSource.Build(
            [Row("legacy", "ledger", total: 2) with { OwnTotal = 1 }, Row("public", "orders", total: 2) with { OwnTotal = 1 }], ["public"], 2);
        whole.Coverage.Should().BeNull("every table was listed");
        whole.OwnSchemasCoverage.Should().BeNull("and every one of the own schemas' too");

        HealthTableQueries.RelationalTables(["public"], everything: true, 2)
            .Parameters.Single(static x => x.ParameterName == "own").Value.Should().BeEquivalentTo((string[]) ["public"],
                "the schemas named exactly are the data source's own");
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
    public void A_Marten_tables_indexes_are_its_stores_never_the_hosts()
    {
        IStudioTableSet set = PostgresHealthTableSource.Build([Row("public", "mt_doc_order")], ["public"], PostgresHealthTableSource.TableCap);

        set.OriginOf(new HealthIndexRow { Schema = "public", Table = "mt_doc_order", Name = "mt_doc_order_idx_number" }, set.Find("public", "mt_doc_order")!)
            .Should().Be(IndexOrigin.StoreManaged, "that store's apply decides it, so no claim that no apply drops it");
    }

    [Fact]
    public void A_capped_sets_coverage_is_said_in_every_table_checks_verdict()
    {
        IStudioTableSet set = PostgresHealthTableSource.Build([Row("public", "a", total: 3412), Row("public", "b", total: 3412)], ["public"], 2);
        HealthFinding finding = new() { Code = "T07", Severity = HealthSeverity.Warning, Title = "t", WhyItMatters = "w", Caveat = "Read in pg_stat_user_tables." };

        HealthVerdict quiet = HealthTables.WithCoverage(HealthVerdict.Quiet(), set.Coverage);
        HealthVerdict found = HealthTables.WithCoverage(HealthVerdict.Of(finding), set.Coverage);

        quiet.Note.Should().Be("Judged: the 2 largest of 3,412 tables by estimated size (a sweep judges at most 2).");
        found.Findings.Single().Caveat.Should().Be(
            "Read in pg_stat_user_tables. Judged: the 2 largest of 3,412 tables by estimated size (a sweep judges at most 2).");
        HealthTables.WithCoverage(HealthVerdict.Of(finding), null).Findings.Single().Should().BeSameAs(finding, "an uncapped set adds nothing");
    }

    [Fact]
    public void T07s_verdict_over_a_capped_set_says_what_was_left_out()
    {
        StudioTable table = new("public", "orders", StudioTableKind.Relational, StudioTableOwnership.Owned);
        TableFacts facts = new(table, new HealthTableRow
        {
            Schema = "public", Table = "orders", EstimatedRows = 1_000_000, LiveRows = 500_000, DeadRows = 400_000,
            AutovacuumEnabled = true,
        });

        HealthVerdict verdict = new VacuumBehindCheck().Evaluate(new VacuumFacts(DateTimeOffset.UnixEpoch, [facts]), HealthEvaluation.At(DateTimeOffset.UnixEpoch));
        HealthVerdict said = HealthTables.WithCoverage(verdict, "the 2,000 largest of 3,412 tables by estimated size (a sweep judges at most 2,000)");

        (said.Note + string.Concat(said.Findings.Select(static x => x.Caveat))).Should().Contain("Judged: the 2,000 largest of 3,412 tables");
    }

    [Fact]
    public void The_statement_binds_the_schemas_and_names_no_table()
    {
        HealthQuery query = HealthTableQueries.RelationalTables(["public", "quartz"], everything: true, 2000);

        query.Sql.Should().NotContain("public").And.NotContain("pg_relation_size").And.Contain("limit @cap");
        query.Parameters.Select(static x => x.ParameterName).Should().BeEquivalentTo(["names", "everything", "cap", "system", "own"]);
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

        all.Should().OnlyContain(static x => x != null && x.Kind == HealthRemedyKind.Migration && x.Explanation!.EndsWith(HealthRemedy.HandOverNote));
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
    [MemberData(nameof(MigrationSqlTextTests.CommentBreakingNames), MemberType = typeof(MigrationSqlTextTests))]
    public void A_covering_index_name_that_could_end_the_comment_gets_no_remedy(string coveredBy)
    {
        PostgresHealthTableRemedies.Instance
            .RemoveRedundantIndex(Relational("orders"), Index("orders_by_customer"), IndexOrigin.Host, Index(coveredBy))
            .Should().BeNull("the covering name would plant a statement under the comment that names it");
    }

    [Fact]
    public void A_covering_index_is_named_quoted_in_its_comment()
    {
        PostgresHealthTableRemedies.Instance
            .RemoveRedundantIndex(Relational("orders"), Index("orders_by_customer"), IndexOrigin.Host, Index("orders by customer, date"))!
            .Code.Should().Be("-- \"orders by customer, date\" already does this index's whole job:\nDROP INDEX CONCURRENTLY \"public\".\"orders_by_customer\";");
    }

    [Theory]
    [MemberData(nameof(UnquotableTableNames))]
    public void A_name_that_cannot_be_quoted_gets_no_remedy_rather_than_a_misleading_one(string table)
    {
        StudioTable hostile = Relational(table);

        PostgresHealthTableRemedies.Instance.LeaveRoomForHotUpdates(hostile, 80).Should().BeNull();
        PostgresHealthTableRemedies.Instance.ReenableAutovacuum(hostile).Should().BeNull();
        PostgresHealthTableRemedies.Instance.RemoveUnusedIndex(Relational("orders"), Index(table), IndexOrigin.Host).Should().BeNull();
    }

    /// <summary>A quote, and the characters that change what a line says on screen - built at run time, never in this file.</summary>
    public static TheoryData<string> UnquotableTableNames() =>
    [
        "we\"ird",
        "rlo" + char.ConvertFromUtf32(0x202E) + "txt",
        "line" + char.ConvertFromUtf32(0x2028) + "separator",
        "paragraph" + char.ConvertFromUtf32(0x2029) + "separator",
    ];

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
