using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;
using MartenStudio.Tests.Sql;

using Microsoft.Extensions.DependencyInjection;

using static MartenStudio.Tests.Health.TableCheckBuilders;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>
/// PS-1c: the health engine behind the shared seams - the table set, the remedy wording, the runtime - with MartenStudio's
/// answers exactly what the checks said before, and a table that is not a Marten table worded as what it is.
/// </summary>
public class HealthSeamTests
{
    // -----------------------------------------------------------------------------------------------
    // MartenStudio's table set: every declared table is the application's own (no behaviour change)
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The library classification of <c>mt_*</c> is for a table whose declarations are not known. Every table a store
    /// declares - its event tables and bookkeeping included - is Owned, so the index-design rules judge them as before.
    /// </summary>
    [Fact]
    public void Every_table_the_store_declares_is_owned_so_no_index_design_rule_exempts_it()
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        using DocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = "studio_seam";
            options.Events.DatabaseSchemaName = "studio_seam_events";
            options.Schema.For<HealthOrderDoc>();
            options.Schema.For<HealthIntDoc>();
        });

        MartenTableSet set = MartenTables.Read(new HealthCheckContext(
            new ResolvedScope(
                new StudioScope("default", string.Empty, null),
                new MartenStoreRegistration("default", "Default", typeof(IDocumentStore)),
                store,
                store.Storage.Database),
            MartenStudioSettings.Of(new MartenStudioOptions()),
            MartenStudioRuntime.Current,
            services,
            new HealthSqlRunner(new HealthConnection(MartenStudioDatabase.Of(store.Storage.Database)), new ReadOnlySqlOptions(), null),
            Now,
            new HealthSweepMemo(),
            CancellationToken.None));

        set.Tables.Should().Contain(static x => x.Table == "mt_events" && x.Kind == StudioTableKind.EventTable)
            .And.Contain(static x => x.Table == "mt_hilo" && x.Kind == StudioTableKind.Bookkeeping);
        set.Tables.Should().OnlyContain(static x => x.Ownership == StudioTableOwnership.Owned && x.Library == null,
            "a store's declared tables are its own; their indexes are told apart by origin");
        set.Tables.Should().OnlyContain(static x => !x.ExemptFromIndexDesign);
        set.Coverage.Should().BeNull("the declared tables are never capped");
    }

    /// <summary>Today's T03-T05 on an event table, pinned: a hand-made index on mt_events is judged like any other.</summary>
    [Fact]
    public void A_hand_made_index_on_an_event_table_is_still_judged_by_T03_T04_and_T05()
    {
        MartenTable events = EventTable();
        events.ExemptFromIndexDesign.Should().BeFalse();

        IndexFacts declared = Index(events, IndexRow(events, "mt_events_idx_type", "type"), IndexOrigin.EventStore);
        IndexFacts handMade = Index(events, IndexRow(events, "z_hand_made", "type"), IndexOrigin.Undeclared);

        HealthFinding redundant = new RedundantIndexCheck().Evaluate(new RedundantIndexFacts([declared, handMade]), Evaluation())
            .Findings.Should().ContainSingle().Subject;
        redundant.Subject.Should().EndWith(".z_hand_made");
        redundant.Evidence.Should().Contain(new HealthEvidence("Table", "the event store's table " + events.Key));
        redundant.Remedy.Should().BeNull("nothing in the configuration declares it");

        IndexFacts unused = Index(events, IndexRow(events, "z_unused", "seq_id") with { Scans = 0, Bytes = 2 * 1024 * 1024 }, IndexOrigin.Undeclared);
        TableFacts written = Table(events, row => row with { Inserts = 200_000 });
        new UnusedIndexCheck().Evaluate(new UnusedIndexFacts(Now, [unused], [written], new DatabaseStatistics(null, true, 2)), UsageEvaluation())
            .Findings.Should().ContainSingle().Which.Subject.Should().EndWith(".z_unused");

        IndexFacts invalid = Index(events, IndexRow(events, "z_invalid", "type") with { IsValid = false }, IndexOrigin.Undeclared);
        HealthFinding broken = new InvalidIndexCheck().Evaluate(new InvalidIndexFacts([invalid], [Table(events)]), Evaluation())
            .Findings.Should().ContainSingle().Subject;
        broken.Remedy!.Code.Should().Be(MartenHealthRemedyProvider.ReapplyCode);
        broken.Remedy.Explanation.Should().Be(MartenHealthRemedyProvider.ReapplyExplanation);
    }

    // -----------------------------------------------------------------------------------------------
    // A library's table: exempt from the index-design rules only (issue #8, G1)
    // -----------------------------------------------------------------------------------------------

    private static StudioTable Library(string table) =>
        new("quartz", table, StudioTableKind.Relational, StudioTableOwnership.Library) { Library = StudioTableLibraries.LibraryOf(table) };

    [Fact]
    public void A_librarys_table_is_exempt_from_the_index_design_rules_and_not_from_vacuum()
    {
        StudioTable jobs = Library("qrtz_job_details");
        jobs.ExemptFromIndexDesign.Should().BeTrue();
        jobs.Describe().Should().Be("the Quartz.NET table quartz.qrtz_job_details");

        IndexFacts first = Index(jobs, IndexRow(jobs, "a_first", "sched_name"), IndexOrigin.Host);
        IndexFacts second = Index(jobs, IndexRow(jobs, "z_second", "sched_name"), IndexOrigin.Host);
        new RedundantIndexCheck().Evaluate(new RedundantIndexFacts([first, second]), Evaluation())
            .Findings.Should().BeEmpty("a redundant index on a library's table is the library's design");

        IndexFacts unused = Index(jobs, IndexRow(jobs, "z_unused", "job_group") with { Scans = 0, Bytes = 2 * 1024 * 1024 }, IndexOrigin.Host);
        TableFacts written = Table(jobs, row => row with { Inserts = 200_000 });
        new UnusedIndexCheck().Evaluate(new UnusedIndexFacts(Now, [unused], [written], new DatabaseStatistics(null, true, 2)), UsageEvaluation())
            .Findings.Should().BeEmpty("an unused index on a library's table is the library's design");

        TableFacts scanned = Table(jobs, row => row with { LiveRows = 500_000, SequentialScans = 10, SequentialRowsRead = 5_000_000 });
        new SequentialScansCheck().Sample(new SequentialScanFacts(Now, [scanned], DatabaseStatistics.Of([scanned])))
            .Should().BeEmpty("T01 does not watch a table whose missing index is not the host's to add");

        TableFacts bloated = Table(jobs, row => row with
        {
            LiveRows = 50_000,
            DeadRows = 50_000,
            LastAutovacuum = Now.AddDays(-3),
            Vacuum = Unit(50_000, 10_050, Now.AddDays(-3), tuples: 50_000),
        });
        VacuumBehindCheck.IsBehind(bloated, VacuumBehindKind.DeadRows, Now).Should().BeTrue("vacuum rules still apply to a library's table");
    }

    // -----------------------------------------------------------------------------------------------
    // A table that is not a Marten table words itself as what it is
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_relational_table_with_no_type_name_is_named_as_a_table_and_offered_no_Marten_line()
    {
        StudioTable orders = new("app", "orders", StudioTableKind.Relational, StudioTableOwnership.Owned);

        orders.Describe().Should().Be("the table app.orders");
        orders.DescribeUngated().Should().Be("the table app.orders");
        orders.NoteName.Should().Be("app.orders");
        (orders with { GatedSchemas = ["app"] }).NoteName.Should().Be("a table outside the studio's own schemas");

        // Autovacuum is off for the table, so the condition needs no history to be a finding.
        TableFacts off = Table(orders, row => row with
        {
            LiveRows = 50_000,
            DeadRows = 50_000,
            LastAutovacuum = Now.AddDays(-3),
            StorageOptions = ["autovacuum_enabled=false"],
            Vacuum = Unit(50_000, 10_050, Now.AddDays(-3), tuples: 50_000),
        });

        HealthFinding finding = new VacuumBehindCheck().Evaluate(new VacuumFacts(Now, [off]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Evidence.Should().Contain(new HealthEvidence("Table", "the table app.orders"));

        // G1M (issue #8, G1): a table no store declares is the host's migrations' to change, so MartenStudio composes
        // Postgres Studio's migration SQL for it - never a StoreOptions line.
        finding.Remedy.Should().NotBeNull();
        finding.Remedy!.Kind.Should().Be(HealthRemedyKind.Migration);
        finding.Remedy.Code.Should().Be("ALTER TABLE \"app\".\"orders\" RESET (autovacuum_enabled);");
        finding.WhyItMatters.Should().Contain("the host's to do, in its own migrations")
            .And.NotContain("StoreOptions").And.NotContain("Marten");
    }

    [Fact]
    public void An_invalid_index_the_host_owns_says_who_rebuilds_it_and_carries_no_Marten_line()
    {
        StudioTable orders = new("app", "orders", StudioTableKind.Relational, StudioTableOwnership.Owned);
        IndexFacts invalid = Index(orders, IndexRow(orders, "orders_by_status", "status") with { IsValid = false }, IndexOrigin.Host);

        HealthFinding finding = new InvalidIndexCheck().Evaluate(new InvalidIndexFacts([invalid], [Table(orders)]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.WhyItMatters.Should().Contain("the host's own index").And.NotContain("Marten");
        finding.Evidence.Should().Contain(new HealthEvidence("Declared", "by the host's own schema"));

        // G1M: the rebuild the finding describes, as the host's migration - no re-apply, which would not touch it.
        finding.Remedy!.Kind.Should().Be(HealthRemedyKind.Migration);
        finding.Remedy.Code.Should().Contain("REINDEX INDEX CONCURRENTLY").And.NotContain("ApplyAllConfiguredChangesToDatabaseAsync");
    }

    // -----------------------------------------------------------------------------------------------
    // The remedy wording and the runtime travel in the evaluation
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void Without_the_studios_wording_a_table_check_offers_no_remedy_and_with_it_MartenStudios_line()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts single = Index(orders, IndexRow(orders, "by_status", "(data ->> 'Status'::text)"));
        IndexFacts pair = Index(orders, IndexRow(orders, "by_status_and_id", "(data ->> 'Status'::text)", "id"));
        RedundantIndexFacts facts = new([single, pair]);

        new RedundantIndexCheck().Evaluate(facts, HealthEvaluation.At(Now)).Findings.Single().Remedy
            .Should().BeNull("the shared evaluation carries no studio's wording");

        HealthEvaluation marten = HealthEvaluation.At(Now, MartenRuntime.Current);
        marten.TableRemedies.Should().BeSameAs(MartenHealthRemedyProvider.Instance);
        marten.PlanAdvisor.Should().BeSameAs(MartenStatementPlanAdvisor.Instance);
        marten.Runtime.Should().BeSameAs(MartenRuntime.Current);

        new RedundantIndexCheck().Evaluate(facts, marten).Findings.Single().Remedy!.Code
            .Should().Be("// opts.Schema.For<HealthOrderDoc>() declares by_status, which by_status_and_id already covers:\n"
                + "// remove the .Index(...), .Duplicate(...) or .UniqueIndex(...) that declares it from the registration.");
    }

    [Fact]
    public void MartenStudios_runtime_judges_its_own_probe_table_and_no_runtime_claims_nothing()
    {
        MartenStudioRuntime floor = new(Floor);

        floor.FindFeature(nameof(RuntimeFeature.DocumentFillFactor)).Should().BeSameAs(RuntimeFeature.DocumentFillFactor);
        floor.FindFeature("TimeTravel").Should().BeNull();
        floor.IsAvailable(RuntimeFeature.DocumentFillFactor).Should().BeFalse("FillFactor arrived in Marten 9.47");
        floor.DescribeHost(RuntimeFeature.DocumentFillFactor).Should().Be(Floor.DescribeHost(RuntimePackage.Marten));
        RuntimeFeature.DocumentFillFactor.Needs.Should().Be(RuntimeFeature.DocumentFillFactor.Requirement);

        NoStudioRuntime.Instance.FindFeature(nameof(RuntimeFeature.DocumentFillFactor)).Should().BeNull();
        NoStudioRuntime.Instance.IsAvailable(RuntimeFeature.DocumentFillFactor).Should().BeFalse();
    }

    [Fact]
    public void The_registry_a_studio_builds_holds_every_check_in_its_descriptors_assemblies()
    {
        HealthCheckRegistry registry = new(MartenStudioDescriptor.Instance, MartenStudioRuntime.Current);

        registry.All.Select(static x => x.Info.CheckType).Should().BeEquivalentTo(
            MartenStudioDescriptor.Instance.HealthCheckAssemblies.Select(static x => x.Assembly).Distinct().SelectMany(HealthCheckRegistry.Discover));
        registry.All.Should().HaveCount(74,
            "the studio ships 74 checks: 0.4's 49, W5's D09 and C11, W5-V1's T16 and T24, W5-R2's R02-R04, W5-S2's S04-S10 and K05-K08, "
            + "W5-B1's T15, T17, T19, T20, T25 and T26, and 0.6's T18 (X4)");

        HealthCheckRegistry.Describe(typeof(ScriptedLightUpCheck), MartenStudioRuntime.Current).Requires
            .Should().BeSameAs(RuntimeFeature.TransientFailureClassifier);
        HealthCheckRegistry.Problems([typeof(ScriptedLightUpCheck)]).Should().ContainSingle()
            .Which.Should().Contain("not in the probe table", "a registry with no runtime refuses a check that requires a feature");
    }

    [Fact]
    public void AddMartenStudio_registers_MartenStudios_answers_to_the_health_seams()
    {
        ServiceCollection services = new();
        services.AddSingleton(Floor);
        services.AddMartenStudio();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IHealthTableRemedies>().Should().BeSameAs(MartenHealthRemedyProvider.Instance);
        provider.GetRequiredService<IHealthPlanAdvisor>().Should().BeSameAs(MartenStatementPlanAdvisor.Instance);
        provider.GetRequiredService<IHorizonContributor>().Should().BeOfType<MartenHorizonContributor>();
        provider.GetRequiredService<IStudioRuntime>().Should().Be(new MartenStudioRuntime(Floor),
            "a test's made-up runtime, registered first, is the one the studio judges features against");
    }

    /// <summary>T22 is MartenStudio's own; the shared statement read keeps the guarded writes it will judge, and no others.</summary>
    [Fact]
    public void The_statement_reads_guarded_floor_is_the_concurrency_checks_own()
    {
        StatementReads.GuardedCallsFloor.Should().Be(ConcurrencyConflictCheck.MinCalls);
        StatementReads.Selection.GuardedCalls.Should().Be(ConcurrencyConflictCheck.MinCalls);
    }
}
