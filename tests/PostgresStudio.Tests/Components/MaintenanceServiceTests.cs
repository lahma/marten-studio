using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
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

    private static CancellationToken Token => TestContext.Current.CancellationToken;

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

        public Task<DatabaseGateRead> GateAsync(StudioScope scope, ResolvedSource resolved, CancellationToken cancellationToken)
        {
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
