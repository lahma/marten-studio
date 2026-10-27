using MartenStudio.Services;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Export;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// MartenStudio's words, held to the literals 0.4 wrote (PS-3b): every sentence the shared studio now reads from
/// <see cref="StudioText" />, and every sentence composed from it, renders byte for byte as it did before the shared text
/// became studio-driven.
/// </summary>
/// <remarks>
/// Each expected value below is a copy of the literal the shared code used to say itself - not a reference to a constant -
/// so that editing a constant, or a <see cref="StudioText.Marten" /> member, fails here rather than changing what a
/// MartenStudio host reads.
/// </remarks>
public class StudioTextPinTests
{
    private static readonly StudioText Marten = StudioText.Marten;

    [Fact]
    public void MartenStudio_settings_speak_in_MartenStudios_words()
    {
        MartenStudioSettings.Of(new MartenStudioOptions()).Text.Should().BeSameAs(StudioText.Marten);
        Marten.Product.Should().Be(StudioProduct.MartenStudio);
        Marten.ProductName.Should().Be("Marten Studio");
        Marten.OptionsTypeName.Should().Be("MartenStudioOptions");
        Marten.RegistrationMethod.Should().Be("AddMartenStudio");
        Marten.SourceNoun.Should().Be("Marten store");
    }

    [Fact]
    public void The_database_browsers_sentences_are_04s()
    {
        Marten.BrowseReadOnlyDenial.Should().Be(
            "MartenStudioOptions.ReadOnly is true, which turns MartenStudioOptions.Capabilities.BrowseDatabase off with every other capability.");
        Marten.BrowseCapabilityDenial.Should().Be(
            "Other schemas, and the rows and definitions of objects Marten does not own, need MartenStudioOptions.Capabilities.BrowseDatabase.");
        Marten.BrowseWritePolicyDenial.Should().Be(
            "Your account may not browse the database here. The write policy (MartenStudioOptions.WriteAuthorizationPolicy, or "
            + "StoreAuthorizationPolicy when no write policy is set) refused BrowseDatabase for this store and database as a whole, "
            + "with no tenant selected.");
        Marten.BrowseStorePolicyDenial.Should().Be(
            "Your account may not browse the database here. The store policy (MartenStudioOptions.StoreAuthorizationPolicy) refused "
            + "this store and database as a whole, with no tenant selected.");
        Marten.BrowseEmptyListDenial.Should().Be(
            "MartenStudioOptions.BrowsableSchemas is empty, so no schema's non-Marten rows or definitions are browsable. List the "
            + "schema names, or \"*\".");
        Marten.MartenDependencyDenial.Should().Be(
            "This view reads one of Marten's own tables - a document or event table, or Marten's bookkeeping - whose rows are never "
            + "read raw: open them in Documents or Events, where tenancy, soft delete and the store's serializer apply.");
        Marten.NoSelectPrivilege.Should().Be("The store's Postgres role has no SELECT privilege on it.");
        Marten.OwnSchemaFlag.Should().Be("M");
        Marten.OwnSchemaFlagTitle.Should().Be(
            "Declared by this Marten store. Its structure is shown whether or not Capabilities.BrowseDatabase is on.");
        Marten.MartenInternalTitle.Should().Be("Marten's own bookkeeping. It is described on the Schema screen and never browsed raw.");
        Marten.MartenStoreNotice.Should().BeNull("MartenStudio is where a Marten store is browsed");
        Marten.OwnerFilterMartenTitle.Should().Be(
            "Only what Marten owns: document tables, the event store, projections and its own bookkeeping");
        Marten.OwnerFilterOtherTitle.Should().Be(
            "Only what Marten does not own: this application's own objects, and other libraries'");
        Marten.ScopeDeniedEntry.Should().Be("Not authorized for this store, database or tenant.");

        DatabaseGate.SchemaDenial("secret", ["quartz"], Marten).Should().Be(
            "Schema 'secret' is not one MartenStudioOptions.BrowsableSchemas admits (it must be listed by exact name, or \"*\" must "
            + "be listed and the studio's role must have USAGE on it).");
        DatabaseGate.SchemaDenial("secret", [], Marten).Should().Be(Marten.BrowseEmptyListDenial);
    }

    /// <summary>
    /// 0.8's additions, issue #22: the sentences a refused definition is told, which name both ways in - <c>ViewDefinitions</c>
    /// and <c>BrowseDatabase</c> - where 0.4's rows' sentences above name the second alone. New, so they are pinned too: a
    /// host reads exactly these.
    /// </summary>
    [Fact]
    public void The_definitions_sentences_name_both_ways_in()
    {
        Marten.DefinitionReadOnlyDenial.Should().Be(
            "MartenStudioOptions.ReadOnly is true, which turns MartenStudioOptions.Capabilities.ViewDefinitions and "
            + "MartenStudioOptions.Capabilities.BrowseDatabase off with every other capability.");
        Marten.DefinitionCapabilityDenial.Should().Be(
            "The definitions of objects Marten does not own need MartenStudioOptions.Capabilities.ViewDefinitions, for the schemas "
            + "MartenStudioOptions.BrowsableSchemas names exactly, or MartenStudioOptions.Capabilities.BrowseDatabase.");
        Marten.DefinitionWritePolicyDenialFor("ViewDefinitions and BrowseDatabase").Should().Be(
            "Your account may not read definitions here. The write policy (MartenStudioOptions.WriteAuthorizationPolicy, or "
            + "StoreAuthorizationPolicy when no write policy is set) refused ViewDefinitions and BrowseDatabase for this store and "
            + "database as a whole, with no tenant selected.");

        DatabaseGate.DefinitionSchemaDenial("legacy", ["quartz", "*"], Marten).Should().Be(
            "Schema 'legacy' is admitted only through \"*\" in MartenStudioOptions.BrowsableSchemas, so its definitions need "
            + "MartenStudioOptions.Capabilities.BrowseDatabase: MartenStudioOptions.Capabilities.ViewDefinitions covers only the "
            + "schemas that list names exactly.");
        DatabaseGate.DefinitionSchemaDenial("legacy", ["quartz"], Marten).Should().Be(DatabaseGate.SchemaDenial("legacy", ["quartz"], Marten),
            "a schema no entry admits is the entry list's refusal, whichever way in");
        DatabaseGate.MartenDefinitionDenial(Marten).Should().Be(
            "The definitions of Marten's own objects here need MartenStudioOptions.Capabilities.BrowseDatabase: "
            + "MartenStudioOptions.Capabilities.ViewDefinitions covers the host's own objects only.");

        WritePolicyRefusal.For(StudioCapability.ViewDefinitions).Should().Be("Your account may not read this database's object definitions here.");
    }

    [Fact]
    public void The_sessions_and_query_sentences_are_04s()
    {
        Marten.SessionsCapabilityDenial.Should().Be(
            "Process ids, roles, client addresses, application names, connection times and statements need "
            + "MartenStudioOptions.Capabilities.ViewDatabaseActivity.");
        Marten.SessionsReadOnlyDenial.Should().Be(
            "MartenStudioOptions.ReadOnly is true, which turns MartenStudioOptions.Capabilities.ViewDatabaseActivity off with every "
            + "other capability.");
        Marten.SessionsStorePolicyDenial.Should().Be(
            "Your account may not see this database's sessions. The store policy (MartenStudioOptions.StoreAuthorizationPolicy) "
            + "refused this store and database as a whole, with no tenant selected: a session belongs to no tenant.");
        Marten.SessionsWritePolicyDenial.Should().Be(
            "Your account may not see who is using this database. The write policy (MartenStudioOptions.WriteAuthorizationPolicy, "
            + "or StoreAuthorizationPolicy when no write policy is set) refused ViewDatabaseActivity for this store and database "
            + "as a whole, with no tenant selected.");

        // W5-CAP's, new in 0.5 rather than moved: pinned so the Storage page's refusal cannot drift either.
        Marten.StorageStorePolicyDenial.Should().Be(
            "Your account may not see this database's storage. The store policy (MartenStudioOptions.StoreAuthorizationPolicy) "
            + "refused this store and database as a whole, with no tenant selected: a table's size belongs to no tenant.");

        Marten.ExamplesIntro.Should().Be("Built from this store's own aliases and tables. Click one to load it.");
        Marten.ExamplesEmpty.Should().Be("This store has no document type to build an example from.");
        Marten.RelationshipTableKey.Should().Be("A key on a table Marten does not map - enforced by Postgres.");

        // UI-fix: moved from the Relationships components' markup, each the sentence it rendered.
        Marten.RelationshipTableCaption.Should().Be(
            "Every foreign key between the document types and tables on the diagram, with the end that points, the end pointed at, "
            + "the columns, whether the database has the constraint, and what a delete does.");
        Marten.RelationshipIsolatedLabel.Should().Be("Document types and tables with no foreign key");
        Marten.RelationshipUnmatchedNote.Should().Be(
            "These foreign keys have an end that is not a document type of this store, so there is nothing to draw them between.");
        Marten.RelationshipNeighbourhoodUnmatched.Should().Be(
            "an end that is not a document type of this store or a table the studio can place, so there is nothing to draw them between.");
        Marten.RelationshipTablesUnavailable.Should().Be("Only document types are drawn:");

        QuerySqlErrors.Describe(new SqlError("57014", "canceled", 0, null, null), TimeSpan.FromSeconds(12), Marten).Should().Be(
            "Statement timed out after 12 s. MartenStudioOptions.QueryTimeout is what sets that.");
        QuerySqlErrors.Describe(new SqlError("42501", "denied", 0, null, null), TimeSpan.FromSeconds(12), Marten).Should().Be(
            "The role this statement ran as may not read that. MartenStudioOptions.SqlConsoleRole is what narrows it.");

        SessionLockWording.Marten.Should().Be(new SessionLockWording(
            "an advisory lock that is not Marten's",
            "a relation Marten does not own",
            "a Marten relation this store does not declare"));
    }

    [Fact]
    public void The_health_sentences_and_remedies_are_04s()
    {
        Marten.StatementsOnSessionsPage.Should().Be(
            "The statements themselves are on Database › Sessions, behind MartenStudioOptions.Capabilities.ViewDatabaseActivity, "
            + "masked for the schemas you may not see.");
        Marten.ServerSettingComment.Should().Be("A server setting, not a StoreOptions one");
        Marten.DeadlockCause.Should().Be(
            "In a Marten store they come from two transactions taking the same rows in different orders: documents saved in a "
            + "different order by two code paths, a projection and a request writing the same documents, an inline projection "
            + "updating rows another session holds.");
        Marten.TempSpillCause.Should().Be(
            "In a Marten store it is usually an ORDER BY on a document field no index covers, or a large aggregation");
        Marten.LongStatementCause.Should().Be(
            "In a Marten store it is most often a query over a large document table with no index for its filter, a projection "
            + "rebuild's batch, or a report somebody started by hand.");
        Marten.ShortTransactionCode.Should().Be(
            "// Keep transactions short, and end what you begin:\n"
            + "await using var session = store.LightweightSession();\n"
            + "// ... work ...\n"
            + "await session.SaveChangesAsync(cancellationToken);");
        Marten.EndWhatYouBeginCode.Should().Be(
            "// Dispose every session on every path, and end what it began:\n"
            + "await using var session = store.LightweightSession();\n"
            + "// ... work ...\n"
            + "await session.SaveChangesAsync(cancellationToken);");
        // G1M (issue #8, G1): reworded when MartenStudio's table checks began to judge every table beside the store, so
        // that "Marten table" no longer names a Quartz.NET one.
        Marten.JudgedTables.Should().Be("Tables the studio judges");
        Marten.JudgedTable.Should().Be("table the studio judges");
        Marten.NotJudged.Should().Be("not among the tables the studio judges");
        Marten.JudgedTablesRead.Should().Be("the tables the studio judges");
        Marten.NoJudgedTable.Should().Be("No table the studio judges exists yet.");
        Marten.UnjudgedOldest.Should().Be(
            " The oldest table in this database is not one the studio judges - in a schema neither the store nor "
            + "MartenStudioOptions.BrowsableSchemas admits, another registered store's, a document type the studio hides, or "
            + "beyond the largest tables a sweep reads - and wraparound protection counts every table in the database, so it "
            + "holds the whole database back as surely as one of these would.");
        Marten.MartenTablePartitionParameters.Should().Be("the Schema screen's Tables tab shows which parameters the partitions differ on");

        // W5-S2: new in 0.5 - T14's timeout backstop and HotCold leader (K08), and S10's writes.
        Marten.LeaderTimeoutClause.Should().Be(
            " - but where a Marten HotCold daemon leads from a transaction, the first must be longer than its leader's idle stretch "
            + "and the second unset for the store's role, or they end that leadership too (K08)");
        Marten.ReadOnlyWrites.Should().Be("SaveChanges, append and projection write");
        Marten.ReadOnlyNeverAsks.Should().Be("and Marten, like most data access, never asks");

        HealthEvaluation.At(DateTimeOffset.UnixEpoch).Text.Should().BeSameAs(Marten, "a unit test's evaluation is MartenStudio's");
    }

    /// <summary>
    /// Wave 6: the sentences the shared checks said themselves until Postgres Studio needed its own, each now a
    /// <see cref="StudioText" /> member, held to the literal the check wrote - so a MartenStudio host reads every finding,
    /// caveat, remedy and status row as before.
    /// </summary>
    [Fact]
    public void The_shared_checks_sentences_routed_through_the_text_are_as_the_checks_wrote_them()
    {
        Marten.CheckTitles.Should().BeEmpty("MartenStudio titles every check as its attribute does");
        Marten.CheckTitle("S02", "Settings that blind or endanger the store").Should().Be("Settings that blind or endanger the store");

        Marten.AmbiguousPoolNotChecked.Should().Be(
            "More than one Npgsql pool in this process uses this database's connection string - the host's own "
            + "AddNpgsqlDataSource beside Marten's, or NpgsqlConnections opened with Marten's connection string - and Npgsql "
            + "reports each under that same name. Their numbers differ, so which pool is Marten's cannot be told, and the "
            + "pool is not judged; naming the host's data source (NpgsqlDataSourceBuilder.Name), or giving its connections an "
            + "Application Name of their own, tells them apart.");
        Marten.PoolBorrowers.Should().Be("Every Marten session, query and daemon batch in this process");
        Marten.PoolRemedyCode.Should().Be(
            "// The ceiling is the connection string's \"Maximum Pool Size\" (Npgsql's default is 100).\n"
            + "// For every data source Marten builds from a connection string:\n"
            + "opts.ConfigureNpgsqlDataSourceBuilder(b => b.ConnectionStringBuilder.MaxPoolSize = 200);");
        Marten.PoolRemedyExplanation.Should().Be(
            "Raise it only when the server has the headroom (C03, and C02 for a store with a database per tenant), and after "
            + "ruling out sessions that hold their connection too long: dispose every IDocumentSession (await using), and keep "
            + "transactions short. A data source the host builds itself (UseNpgsqlDataSource) is configured where it is built.");
        Marten.NextConnectionExamples.Should().Be("a pool that wants to grow, a deployment's new instance, the async daemon's next connection");
        Marten.EveryDatabasesConnections.Should().Be("this store's and everybody else's");
        Marten.FewerConnectionsRemedyCode.Should().Be(
            "// Fewer connections per process - every data source Marten builds from a connection string:\n"
            + "opts.ConfigureNpgsqlDataSourceBuilder(b => b.ConnectionStringBuilder.MaxPoolSize = 50);");
        Marten.IdleTransactionIdCost.Should().Be(" that Marten's high-water detection can wait behind, so async projections stop moving (D01)");
        Marten.SkippedCommit.Should().Be("SaveChangesAsync");
        Marten.IdleTimeoutLeaderClause.Should().Be(
            " With the HotCold leadership lock transaction-scoped (Events.UseAdvisoryLockTransaction, the default at Marten 9.39 and 9.40) "
            + "it can also end the daemon's leadership session: one shorter than the minute or so the leader sits idle in its transaction - "
            + "after taking the lead at 9.39, between DistributedLock's keepalives at 9.50.2 - or any timeout with "
            + "Events.UseMonitoredAdvisoryLock off.");
        Marten.StrongerLockExamples.Should().Be("a schema apply, a rebuild's truncate");
        Marten.LongStatementRemedyCode.Should().Be(
            "// A ceiling for Marten's own statements, in seconds (Npgsql's default is 30):\n"
            + "opts.CommandTimeout = 60;\n"
            + "\n"
            + "// ...and an index for the filter a slow document query uses:\n"
            + "opts.Schema.For<Order>().Index(x => x.CustomerId);");
        Marten.LongStatementRemedyExplanation.Should().Be(
            "CommandTimeout bounds what Marten sends; it does not reach another application's statements. The Query page's index "
            + "advisor says which index a document query needs.");
        Marten.PreparedTransactionHolds.Should().Be(
            "for good, blocks whatever needs its locks, and, having a transaction id, can hold Marten's high-water mark still so that "
            + "async projections stop");
        Marten.RetriedFailures.Should().Be("optimistic concurrency on documents, an expected version on an event stream, unique violations");
        Marten.ScopePolicyNoun.Should().Be("store");
        Marten.ChangeDataCaptureConsumer.Should().Be("a change data capture connector reading Marten's events");
        Marten.EveryWriteExamples.Should().Be("every Marten append, every document upsert");
        Marten.EverySourceDatabase.Should().Be("every store database on this server");
        Marten.UnclearedDeadRows.Should().Be("every document Marten upserts leaves a dead row version that is never cleared");
        Marten.LostCommitsMeaning.Should().Be(" For an event store that is events an async projection or a subscriber may already have acted on.");
        Marten.ZeroedPageRows.Should().Be("including a document or an event the store still points at");
        Marten.SettingsReadOn.Should().Be(
            "on a connection from the store's own data source - so a value set for its role, its database or in its connection string "
            + "is the one the store's sessions run with");
        Marten.PageRowsExamples.Should().Be("documents, events, index entries");
        Marten.ConnectionCarries.Should().Be("every document and event it reads or writes");
        ExpensiveStatementsCheck.GenericPlanCaveat(Marten).Should().Be(
            " The plan is PostgreSQL's generic plan for the text as pg_stat_statements recorded it, in which the statement's "
            + "constants - the JSON member names Marten writes into its SQL included - are placeholders; a filter on data ->> $n "
            + "can never use an expression index in that plan, so a sequential scan filtering on one does not show that the table "
            + "lacks an index.");
        Marten.OwnRoleOnTables.Should().Be("the store's own role on its tables");
        Marten.OneOfTheTables.Should().Be("one of its tables");
        Marten.SlowStatementCause.Should().Be(
            "A statement this slow is most often a query over a large document table with no index for its filter, or one that reads "
            + "far more rows than it returns.");
        Marten.UnboundedRead.Should().Be("a ToListAsync() with no Take() or paging");
        Marten.PlanReadsNoneOfTheTables.Should().Be("The plan reads none of the store's tables directly");
        Marten.UnnamedPlanScans.Should().Be(" of a partition, or of a table this store does not declare or the studio does not show, not named here");
        Marten.NothingRecorded.Should().Be(
            "pg_stat_statements has recorded no statement of the store's own role in this database yet - since its last reset, "
            + "or since the extension was installed - so there is nothing to judge until the application has run.");
        Marten.PartitionedIndexRepair.Should().Be(
            " On a partitioned table a re-apply cannot resume the build: Weasel reads only the partitioned table's own indexes, "
            + "and rebuilding under the same child index name fails while the leftover index on the partition is still there. A "
            + "DBA drops that leftover partition index first; the next apply then builds the index again. The partition is not "
            + "named here, because Marten names a tenant's partition after the tenant.");
        Marten.DeclaredIndexRebuild.Should().Be(" Marten's configuration declares it, so a re-apply drops it and builds it again.");
        Marten.AppendOnlyExamples.Should().Be("an event log such as mt_events, a history table");
        Marten.HorizonDeadRowsLabel.Should().Be("Dead rows in the store's tables");
        Marten.LogicalSlotConsumers.Should().Be("Debezium, a CDC pipeline on mt_events, a subscription");
        Marten.ConfiguredKeyColumnClause.Should().Be(
            ", and a column the table's own configuration adds to every key (a conjoined Marten table's tenant_id) need not lead");
        Marten.JudgedStatementNoun.Should().Be("store statement");
        Marten.ShortStatementsJit.Should().Be("Marten's statements are short, and a large table's cost estimates are what push them over the line.");
        Marten.JitRemedyCode.Should().Be(
            "opts.ConfigureNpgsqlDataSourceBuilder(b =>\n"
            + "    b.ConnectionStringBuilder.Options = ((b.ConnectionStringBuilder.Options ?? string.Empty) + \" -c jit=off\").Trim());");
        Marten.JitRemedyExplanation.Should().Be(
            "Turns JIT off for the store's own connections only - every data source Marten builds, tenant databases included - and "
            + "leaves the server's setting alone for everybody else. A data source the host builds itself (UseNpgsqlDataSource) "
            + "takes the same Options on its own NpgsqlDataSourceBuilder. To keep JIT for the occasional long query, a DBA can "
            + "raise jit_above_cost for the store's role instead.");
        Marten.DatabaseGateReason.Should().Be("needs the store policy to allow this database as a whole, with no tenant selected in the policy's resource");
        Marten.ExportSourceNoun.Should().Be("store");
        Marten.PickInTheHeader.Should().Be("a store and database");
    }

    /// <summary>
    /// T07's and T08's sentence about a Marten table whose partitions disagree on <c>autovacuum_enabled</c>, composed from
    /// <see cref="StudioText.MartenTablePartitionParameters" />, byte for byte as 0.4 wrote it - for every kind of Marten
    /// table, since each says it.
    /// </summary>
    [Theory]
    [InlineData(nameof(StudioTableKind.DocumentTable), "")]
    [InlineData(nameof(StudioTableKind.EventTable), " No StoreOptions line")]
    [InlineData(nameof(StudioTableKind.Projection), " No StoreOptions line")]
    [InlineData(nameof(StudioTableKind.Bookkeeping), " No StoreOptions line")]
    public void A_Marten_tables_differing_partitions_are_04s_sentence(string kindName, string continues)
    {
        StudioTable table = new("public", "mt_doc_order", Enum.Parse<StudioTableKind>(kindName), StudioTableOwnership.Owned);
        TableFacts facts = new(table, new HealthTableRow
        {
            Schema = "public", Table = "mt_doc_order", Kind = "p", AutovacuumEnabled = true,
            DifferingStorageOptions = ["autovacuum_enabled"],
        });

        string said = Autovacuum.State(facts, "autovacuum_vacuum_scale_factor", "0.02", Marten);

        said.Should().StartWith(
            " Its partitions do not all have the same autovacuum_enabled setting, so autovacuum may be switched off for some of "
            + "them (the Schema screen's Tables tab shows which parameters the partitions differ on)." + continues);
    }

    [Fact]
    public void The_health_exports_sentences_are_04s()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        HealthExportSummary.NotRegistered(now, Marten).Problem.Should().Be(
            "Marten Studio's services are not registered: call services.AddMartenStudio() as well as AddHealthChecks().AddMartenStudio()");
        HealthExportSummary.NoStores(now, Marten).Problem.Should().Be("Marten Studio found no Marten store to check");
        HealthExportSummary.Stopping(now, Marten).Problem.Should().Be("The application is stopping, so Marten Studio did not finish checking");
        HealthExportSummary.Broken(now, "IOException", Marten).Problem.Should().Be(
            "Marten Studio could not check: its health export failed with IOException, logged as event 9270");

        new HealthExportRunner(Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
                new Microsoft.Extensions.DependencyInjection.ServiceCollection())).Text
            .Should().BeSameAs(Marten, "a runner MartenStudio's registration made, with no settings, still names AddMartenStudio()");
    }

    [Fact]
    public void A_table_rows_sentences_name_MartenStudios_option()
    {
        TableRowService.ShouldWithhold(
                MartenStudio.Tests.Database.TableRowTestData.Relation(estimatedRows: 2_000_000),
                new RowFilterVerdict { FilterLevel = IndexVerdictLevel.Red },
                100_000,
                Marten)
            .Should().Contain("(more than MartenStudioOptions.ExactCountThreshold, 100,000)");
    }

    [Fact]
    public void Martens_own_definitions_are_structure_only_where_a_store_declares_them()
    {
        string schema = MartenStudio.Tests.Database.DatabaseTestStores.DocumentSchema;
        DatabaseObjectOwnership marten = new(DatabaseObjectOwner.MartenInfrastructure, "default");
        IReadOnlyList<CatalogSchema> live = [new(schema, true, false)];

        // MartenStudio, unchanged: a store declares the schema, so its Marten objects' definitions are the Schema screen's.
        DatabaseGate store = new([schema], live, ["quartz"], capabilityEnabled: false, readOnly: false, authorized: null,
            MartenStudio.Tests.Database.DatabaseTestStores.Classifier());
        store.DefinitionAccess(schema, marten).Allowed.Should().BeTrue();

        // A studio over plain data sources: nothing declares it, so an object only named like Marten's is gated like any.
        DatabaseGate plain = new([schema], live, [schema], capabilityEnabled: false, readOnly: false, authorized: null,
            new DatabaseObjectClassifier([]), text: PostgresStudio.Services.Postgres.PostgresStudioText.Instance);
        plain.ExemptsMartensOwnDefinitions.Should().BeFalse();
        plain.DefinitionAccess(schema, marten).Should().Match<DatabaseRowAccess>(static x => !x.Allowed && x.Refusal == DatabaseRefusal.CapabilityOff);
    }

    [Theory]
    [InlineData(nameof(StudioTableKind.DocumentTable))]
    [InlineData(nameof(StudioTableKind.EventTable))]
    [InlineData(nameof(StudioTableKind.Projection))]
    [InlineData(nameof(StudioTableKind.Bookkeeping))]
    public void No_remedy_for_a_table_that_is_not_relational_is_migration_SQL(string kindName)
    {
        StudioTableKind kind = Enum.Parse<StudioTableKind>(kindName);
        StudioTable table = new("public", "orders", kind, StudioTableOwnership.Owned) { TypeName = "Order", Alias = "order" };
        HealthIndexRow index = new() { Schema = "public", Table = "orders", Name = "orders_by_customer" };
        HealthIndexRow cover = new() { Schema = "public", Table = "orders", Name = "orders_by_customer_and_date" };

        foreach (IHealthTableRemedies remedies in new IHealthTableRemedies[] { MartenHealthRemedyProvider.Instance, PostgresStudio.Services.Postgres.PostgresHealthTableRemedies.Instance })
        {
            HealthRemedy?[] all =
            [
                remedies.IndexTheFilteredMember(table),
                remedies.IndexTheUnnamedMember(table),
                remedies.RebuildInvalidIndex(table, index, IndexOrigin.Declared),
                remedies.RebuildInvalidIndex(table, index, IndexOrigin.Host),
                remedies.RemoveRedundantIndex(table, index, IndexOrigin.Declared, cover),
                remedies.RemoveRedundantIndex(table, index, IndexOrigin.Host, cover),
                remedies.RemoveUnusedIndex(table, index, IndexOrigin.Declared),
                remedies.RemoveUnusedIndex(table, index, IndexOrigin.Host),
                remedies.LeaveRoomForHotUpdates(table, 80),
                remedies.IndexAColumnInPlaceOfAnExpression(table),
                remedies.ReenableAutovacuum(table),
                remedies.AutovacuumSetting(table, "autovacuum_vacuum_scale_factor", "0.02", "Vacuums sooner."),
            ];

            all.Where(static x => x is not null).Should().OnlyContain(static x => x!.Kind != HealthRemedyKind.Migration,
                "design §3.6: migration SQL is a relational table's remedy only - " + remedies.GetType().Name + ", " + kind);
        }
    }

    [Fact]
    public void A_catalog_name_that_could_end_a_line_gets_no_configuration_line_either()
    {
        StudioTable table = new("public", "mt_doc_order", StudioTableKind.DocumentTable, StudioTableOwnership.Owned) { TypeName = "Order" };
        HealthIndexRow hostile = new() { Schema = "public", Table = "mt_doc_order", Name = "x\nopts.Schema.For<Secret>();\n//" };
        HealthIndexRow plain = new() { Schema = "public", Table = "mt_doc_order", Name = "mt_doc_order_idx_number" };

        MartenHealthRemedyProvider.Instance.RemoveUnusedIndex(table, hostile, IndexOrigin.Declared).Should().BeNull();
        MartenHealthRemedyProvider.Instance.RemoveRedundantIndex(table, plain, IndexOrigin.Declared, hostile).Should().BeNull();
        MartenHealthRemedyProvider.Instance.RemoveUnusedIndex(table, plain, IndexOrigin.Declared)!.Code.Should().Be(
            "// opts.Schema.For<Order>() declares mt_doc_order_idx_number, and nothing has scanned it:\n"
            + "// remove the .Index(...), .Duplicate(...) or GIN/full-text index that declares it from the registration.",
            "an ordinary name renders as it always did");
    }

    [Fact]
    public void A_migration_remedy_is_never_MartenStudios()
    {
        HealthRemedy remedy = new("opts.Schema.For<Order>().FillFactor(80);");

        remedy.Kind.Should().Be(HealthRemedyKind.Configuration, "a StoreOptions line is a configuration line, as it always was");
        FluentActions.Invoking(() => new HealthRemedy("ALTER TABLE x SET (fillfactor = 80);"))
            .Should().Throw<ArgumentException>("the constructor's DDL rule is unchanged: only ForMigration makes SQL a remedy");
    }
}
