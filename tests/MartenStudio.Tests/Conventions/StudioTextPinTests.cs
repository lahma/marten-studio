using MartenStudio.Services;

using PostgresStudio.Internal.Sql;
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

        Marten.ExamplesIntro.Should().Be("Built from this store's own aliases and tables. Click one to load it.");
        Marten.ExamplesEmpty.Should().Be("This store has no document type to build an example from.");
        Marten.RelationshipTableKey.Should().Be("A key on a table Marten does not map - enforced by Postgres.");

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
        Marten.JudgedTables.Should().Be("Marten tables");
        Marten.JudgedTable.Should().Be("Marten table");
        Marten.NotJudged.Should().Be("not the store's");
        Marten.JudgedTablesRead.Should().Be("the store's own tables");
        Marten.NoJudgedTable.Should().Be("No table this store declares exists yet.");
        Marten.UnjudgedOldest.Should().Be(
            " The oldest table in this database is not one the store declares - a legacy schema, another library's job store, "
            + "a document type the studio hides - and wraparound protection counts every table in the database, so it holds the "
            + "whole database back as surely as a Marten table would.");
        Marten.MartenTablePartitionParameters.Should().Be("the Schema screen's Tables tab shows which parameters the partitions differ on");

        HealthEvaluation.At(DateTimeOffset.UnixEpoch).Text.Should().BeSameAs(Marten, "a unit test's evaluation is MartenStudio's");
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
