using Bunit;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components.Pages.Maintenance;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Maintenance;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// The Maintenance page's service over a fake of its facts and of the database browser's gate (W5-V1 review): the store
/// policy asked first and about the registered names, D27's withholding in each of the page's lists, and every refusal of
/// the bloat measure with the capability its audit entry is filed under.
/// </summary>
public sealed class MaintenanceServiceTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private readonly PostgresStudioComponentContext context = new();
    private readonly FakeFacts facts = new();
    private readonly FakeGate gate = new();
    private readonly BloatMeasureFlights flights = new();

    public ValueTask DisposeAsync() => context.DisposeAsync();

    [Fact]
    public async Task A_visitor_the_source_policy_refuses_is_told_so_and_starts_no_sweep()
    {
        context.Options.SourceAuthorizationPolicy = "source";
        context.AuthorizationService.Allow(static _ => false);

        MaintenanceView view = await Service().GetAsync(Scope, Token);

        view.Refusal.Should().Be(MaintenanceRefusal.StorePolicy);
        view.Reason.Should().Contain("SourceAuthorizationPolicy").And.Contain("no tenant selected");
        facts.Reads.Should().Be(0, "a refused visitor costs the database nothing");
    }

    [Fact]
    public async Task A_scope_spelled_another_way_is_asked_again_about_the_registered_key_and_database()
    {
        context.Options.SourceAuthorizationPolicy = "source";
        ResolvedSource resolved = await ResolveAsync();

        // The visitor's own spelling passes; the registered names do not.
        context.AuthorizationService.Allow(x => x.SourceName != resolved.Source.Key);

        MaintenanceView view = await Service().GetAsync(Scope with { StoreKey = "DEFAULT" }, Token);

        view.Refusal.Should().Be(MaintenanceRefusal.StorePolicy);
        context.AuthorizationService.Calls.Should().Contain(x => x.Resource.SourceName == resolved.Source.Key
            && x.Resource.DatabaseIdentifier == resolved.Target.Identity && x.Resource.Capability == null);
        facts.Reads.Should().Be(0);
    }

    [Theory]
    [InlineData(GateState.Closed)]
    [InlineData(GateState.Unreadable)]
    public async Task A_table_in_a_schema_the_browser_gate_withholds_is_counted_never_named_in_any_list(GateState state)
    {
        gate.State = state;
        TableFacts own = Table("public", "orders", xidAge: 1_000);
        TableFacts library = Table("quartz", "qrtz_triggers", gated: true, xidAge: 9_000);

        facts.Value = Facts([own, library]) with
        {
            Running =
            [
                Running("vacuum", 1, "public", "orders"),
                Running("vacuum", 2, "quartz", "qrtz_triggers"),
            ],
            Wraparound = new WraparoundFacts(
                new HealthWraparoundDatabase(),
                [new HealthWraparoundTable("public", "orders", 1_000, 0), new HealthWraparoundTable("quartz", "qrtz_triggers", 9_000, 0)]),
        };

        MaintenanceView view = await Service().GetAsync(Scope, Token);

        view.Tables.Select(static x => x.Key).Should().Equal("public.orders");
        view.TablesWithheld.Should().Be(1);

        view.Running.Rows.Select(static x => x.Table).Should().Equal("public.orders");
        view.Running.Unnamed.Should().Be(1, "the withheld table's VACUUM is counted, with no blocks or ETA to size it by");

        view.Freeze!.OldestTables.Select(static x => x.Table).Should().Equal(["public.orders"], "the oldest table is withheld, so it is not named");
        view.Freeze.TransactionIds.Age.Should().Be(9_000, "its age still counts in the database's");
    }

    [Fact]
    public async Task A_table_in_a_schema_the_open_gate_admits_is_named()
    {
        gate.State = GateState.Open;
        facts.Value = Facts([Table("quartz", "qrtz_triggers", gated: true)]) with { Running = [Running("vacuum", 2, "quartz", "qrtz_triggers")] };

        MaintenanceView view = await Service().GetAsync(Scope, Token);

        view.Tables.Select(static x => x.Key).Should().Equal("quartz.qrtz_triggers");
        view.Running.Rows.Should().ContainSingle().Which.Table.Should().Be("quartz.qrtz_triggers");
        view.Running.Unnamed.Should().Be(0);
    }

    /// <summary>
    /// H1b (G1M F6, Option A): the judged set's coverage counts tables in schemas only <c>BrowsableSchemas</c> admits, and how
    /// many tables those hold is the database browser's to show. So the page's "Judged: …" and the sentence T14's note ends
    /// with read the whole count only past the browser's gate (<c>DatabaseGate.IsOpen</c>), and the source's own schemas'
    /// alone for a gate that is shut or cannot be read - the rule the Health page follows.
    /// </summary>
    [Theory]
    [InlineData(GateState.Open, true)]
    [InlineData(GateState.Closed, false)]
    [InlineData(GateState.Unreadable, false)]
    public async Task The_coverage_and_T14s_note_count_the_other_schemas_only_past_the_browsers_gate(GateState state, bool whole)
    {
        gate.State = state;
        facts.Value = Covered(Facts([Table("public", "orders"), Table("quartz", "qrtz_triggers", gated: true)]));

        MaintenanceView view = await Service().GetAsync(Scope, Token);

        view.Coverage.Should().Be(whole ? WholeCoverage : OwnCoverage);
        view.Horizon.Finding.Should().BeNull();
        view.Horizon.Note.Should().Be(Quiet(whole ? WholeCoverage : OwnCoverage));
        gate.Reads.Should().Be(1, "the gate is read once per view, for the names and the coverage alike");
    }

    /// <summary>With one reading - nothing judged beyond the source's own schemas - the gate is never read for the coverage.</summary>
    [Fact]
    public async Task With_one_reading_the_coverage_reads_no_gate()
    {
        gate.State = GateState.Open;
        facts.Value = Facts([Table("public", "orders")]) with
        {
            Coverage = OwnCoverage,
            OwnSchemasCoverage = OwnCoverage,
            Horizon = T14(Quiet(OwnCoverage)),
        };

        MaintenanceView view = await Service().GetAsync(Scope, Token);

        view.Coverage.Should().Be(OwnCoverage);
        view.Horizon.Note.Should().Be(Quiet(OwnCoverage));
        gate.Reads.Should().Be(0);
    }

    /// <summary>The page, over the real service: what a visitor past the gate reads, and what one short of it does.</summary>
    [Theory]
    [InlineData(GateState.Open, true)]
    [InlineData(GateState.Closed, false)]
    public async Task The_page_draws_the_coverage_this_visitor_may_read(GateState state, bool whole)
    {
        gate.State = state;
        facts.Value = Covered(Facts([Table("public", "orders"), Table("quartz", "qrtz_triggers", gated: true)]));

        await using PostgresStudioComponentContext page = await new PostgresStudioComponentContext(services =>
            services.AddSingleton<IMaintenanceService>(provider => ActivatorUtilities.CreateInstance<MaintenanceService>(
                provider,
                ActivatorUtilities.CreateInstance<StudioSourceResolver>(provider),
                (IMaintenanceFacts) facts,
                (IMaintenanceNameGate) gate,
                flights))).ReadyAsync();

        var rendered = page.Render<Maintenance>();
        rendered.WaitForAssertion(() => rendered.FindAll(".ms-maint-horizon").Should().ContainSingle());

        rendered.Find(".ms-maint-triggers").TextContent.Should().Contain("Judged: " + (whole ? WholeCoverage : OwnCoverage) + ".");
        rendered.Find(".ms-maint-horizon").TextContent.Should().Contain(Quiet(whole ? WholeCoverage : OwnCoverage));

        if (!whole)
        {
            rendered.Markup.Should().NotContain("3,412").And.NotContain("a sweep judges at most",
                "nothing on the page counts what the schemas only BrowsableSchemas admits hold");
        }
    }

    /// <summary>What the set leaves out, over every admitted schema.</summary>
    private const string WholeCoverage = "the 2 largest of 3,412 tables by estimated size (a sweep judges at most 2)";

    /// <summary>The same, over the source's own schemas alone.</summary>
    private const string OwnCoverage = "the 1 largest of 10 tables in the data source's own schemas by estimated size";

    private static string Quiet(string coverage) => "Nothing holds the vacuum horizon back. Judged: " + coverage + ".";

    /// <summary><paramref name="facts" /> with a set that read beyond the source's own schemas, and T14's two readings.</summary>
    private static MaintenanceFacts Covered(MaintenanceFacts facts) => facts with
    {
        Coverage = WholeCoverage,
        OwnSchemasCoverage = OwnCoverage,
        Horizon = T14(Quiet(WholeCoverage)) with { WithinOwnSchemas = T14(Quiet(OwnCoverage)) },
    };

    /// <summary>T14's quiet result, with <paramref name="note" />.</summary>
    private static HealthCheckResult T14(string note) => new(
        new HealthCheckInfo("T14", HealthArea.Tables, HealthCostTier.Catalog, HealthGate.Database, "Vacuum held back", null, typeof(object)),
        HealthCheckOutcome.Checked,
        [],
        note,
        null,
        [],
        TimeSpan.Zero);

    [Fact]
    public async Task A_table_the_page_does_not_judge_is_refused_its_measure_under_BrowseDatabase()
    {
        facts.Value = Facts([Table("public", "orders")]);

        BloatMeasureResult result = await Service().MeasureBloatAsync(Scope, "public", "invoices", Token);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Contain("not one this page judges");
        Audited().Should().ContainSingle().Which.Should().Match<StudioActionLogEntry>(x =>
            !x.Succeeded && x.Target == "public.invoices" && x.Capability == nameof(StudioCapability.BrowseDatabase));
    }

    [Fact]
    public async Task A_withheld_table_is_refused_its_measure_as_though_the_page_did_not_judge_it()
    {
        gate.State = GateState.Closed;
        facts.Value = Facts([Table("quartz", "qrtz_triggers", gated: true)]);

        BloatMeasureResult result = await Service().MeasureBloatAsync(Scope, "quartz", "qrtz_triggers", Token);

        result.Refusal.Should().Contain("not one it shows you");
        Audited().Should().ContainSingle().Which.Capability.Should().Be(nameof(StudioCapability.BrowseDatabase),
            "a name in a withheld schema is shown on Activity only to whoever passes the browser's gate");
    }

    [Fact]
    public async Task A_partitioned_table_is_refused_its_measure_and_told_to_measure_a_partition()
    {
        facts.Value = Facts([Table("public", "events", partitioned: true)]);

        BloatMeasureResult result = await Service().MeasureBloatAsync(Scope, "public", "events", Token);

        result.Refusal.Should().Contain("partitions are measured one at a time");
        Audited().Should().ContainSingle().Which.Capability.Should().BeNull("a table in the source's own schemas is everyone's the page admits");
    }

    [Fact]
    public async Task A_second_table_while_another_is_measured_on_the_database_is_refused_and_audited()
    {
        facts.Value = Facts([Table("public", "orders"), Table("public", "lines")]);
        ResolvedSource resolved = await ResolveAsync();
        TaskCompletionSource<BloatMeasureResult> running = new(TaskCreationOptions.RunContinuationsAsynchronously);
        flights.Join(resolved.Source.Key + "|" + resolved.Target.Identity, "public.orders", () => running.Task).Should().NotBeNull();

        BloatMeasureResult result = await Service().MeasureBloatAsync(Scope, "public", "lines", Token);

        result.Refusal.Should().Contain("Another table's bloat is being measured");
        Audited().Should().ContainSingle().Which.Target.Should().Be("public.lines");

        running.SetResult(new BloatMeasureResult("public.orders", null, "done", Now));
    }

    public enum GateState
    {
        Open,
        Closed,
        Unreadable,
    }

    private StudioSourceResolver Resolver() => ActivatorUtilities.CreateInstance<StudioSourceResolver>(context.Services);

    private MaintenanceService Service() => ActivatorUtilities.CreateInstance<MaintenanceService>(
        context.Services,
        Resolver(),
        (IMaintenanceFacts) facts,
        (IMaintenanceNameGate) gate,
        flights);

    private async Task<ResolvedSource> ResolveAsync() => await Resolver().ResolveAsync(Scope, null, Token);

    private StudioActionLogEntry[] Audited() =>
        [.. context.ActionLog.GetLatest().Where(static x => x.Action == MaintenanceService.MeasureAction)];

    private static MaintenanceFacts Facts(IReadOnlyList<TableFacts> tables) => new()
    {
        MeasuredAt = Now,
        StoreKey = "default",
        DatabaseId = "default",
        Tables = tables,
        Running = [],
    };

    private static TableFacts Table(string schema, string table, bool gated = false, bool partitioned = false, long xidAge = 0) =>
        new(
            new StudioTable(schema, table, StudioTableKind.Relational, StudioTableOwnership.Owned) { GatedSchemas = gated ? [schema] : [] },
            new HealthTableRow
            {
                Schema = schema,
                Table = table,
                Kind = partitioned ? "p" : "r",
                PartitionLeaves = partitioned ? 4 : 0,
                LiveRows = 1_000,
                OldestXidAge = xidAge,
                Vacuum = new AutovacuumUnit { Counter = 10, Trigger = 250 },
            });

    private static MaintenanceProgressRow Running(string kind, int pid, string schema, string table) => new()
    {
        Kind = kind,
        ProcessId = pid,
        RelationId = (uint) (16_000 + pid),
        Schema = schema,
        Table = table,
        Part = "self",
        BlocksDone = 10,
        BlocksTotal = 100,
    };

    private sealed class FakeFacts : IMaintenanceFacts
    {
        public int Reads { get; private set; }

        public MaintenanceFacts Value { get; set; } = Facts([]);

        public Task<MaintenanceFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(Value);
        }
    }

    private sealed class FakeGate : IMaintenanceNameGate
    {
        public GateState State { get; set; } = GateState.Closed;

        /// <summary>How many times the gate was read.</summary>
        public int Reads { get; private set; }

        public Task<DatabaseGateRead> GateAsync(StudioScope scope, ResolvedSource resolved, CancellationToken cancellationToken)
        {
            Reads++;

            if (State == GateState.Unreadable)
            {
                throw new InvalidOperationException("The schema list could not be read.");
            }

            bool open = State == GateState.Open;
            DatabaseGate built = new(
                ["public"],
                [new CatalogSchema("public", true, false), new CatalogSchema("quartz", true, false)],
                ["quartz"],
                capabilityEnabled: open,
                readOnly: false,
                authorized: open ? true : null,
                new DatabaseObjectClassifier([]));

            return Task.FromResult(new DatabaseGateRead(built, DatabaseRefusal.None, null));
        }
    }
}
