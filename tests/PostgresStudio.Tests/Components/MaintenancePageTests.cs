using Bunit;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components.Pages.Maintenance;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Maintenance;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// The Maintenance page (W5-V1) over a stated view: what it draws for each table, what is running, the workers, the
/// forecast, the horizon - and the one thing it does, the bloat measure, through the service and nothing else.
/// </summary>
public class MaintenancePageTests
{
    private static readonly DateTimeOffset Measured = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static AutovacuumUnit Unit(long counter, double trigger) => new()
    {
        Counter = counter,
        Trigger = trigger,
        Threshold = 50,
        ScaleFactor = 0.01,
        Tuples = 1_000_000,
        Last = Measured.AddDays(-3),
    };

    private static MaintenanceView View() => new()
    {
        MeasuredAt = Measured,
        TablesRead = true,
        ServerVersion = 180_006,
        Tables =
        [
            new MaintenanceTableView
            {
                Key = "public.orders",
                Schema = "public",
                Table = "orders",
                Description = "the table public.orders",
                Vacuum = Unit(40_000, 10_050),
                Analyze = Unit(100, 1_050),
                Overrides = ["autovacuum_vacuum_scale_factor=0.01"],
                Due = true,
                Behind = true,
                Measurable = true,
                TotalVacuumMilliseconds = 3_200,
            },
            new MaintenanceTableView
            {
                Key = "public.events",
                Schema = "public",
                Table = "events",
                Description = "the table public.events",
                Partitions = 12,
                Off = "switched off for 2 partitions",
            },
        ],
        TablesWithheld = 1,
        Running = new MaintenanceRunningView(
            true,
            [new MaintenanceRunningRow("vacuum", "public.orders", "scanning heap", 1_200, 10_000, 0, 2, "autovacuum, to prevent wraparound", TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(9))],
            2,
            "Not checked: this needs pg_read_all_stats (pg_monitor includes it) for role app. On a self-hosted server a superuser can grant it."),
        Workers = new MaintenanceWorkersView(3, 16, "3 of 3 autovacuum workers are busy", 60, true, true),
        Freeze = new MaintenanceFreezeView(
            new MaintenanceForecast("transaction ids", 150_000_000, 200_000_000, 2_144_483_647, TimeSpan.FromDays(3), null, "about 700,000 ids an hour, at the rate of the last 40 minutes"),
            new MaintenanceForecast("multixact ids", 10, 400_000_000, 2_144_483_647, null, null, null),
            [new MaintenanceFreezeTable("public.orders", 150_000_000)]),
        Horizon = new MaintenanceHorizonView(null, "Nothing holds the vacuum horizon back."),
    };

    private static async Task<(PostgresStudioComponentContext Context, FakeMaintenanceService Service)> ContextAsync(MaintenanceView view)
    {
        FakeMaintenanceService service = new(view);
        PostgresStudioComponentContext context = await new PostgresStudioComponentContext(services => services.AddSingleton<IMaintenanceService>(service)).ReadyAsync();
        return (context, service);
    }

    [Fact]
    public async Task Each_table_is_drawn_against_its_own_triggers_with_its_badges_worst_first()
    {
        (PostgresStudioComponentContext context, FakeMaintenanceService _) = await ContextAsync(View());
        await using PostgresStudioComponentContext disposing = context;

        var page = context.Render<Maintenance>();

        page.WaitForAssertion(() => page.FindAll(".ms-maint-table tbody tr").Should().HaveCount(2));
        page.Find(".ms-page-title").TextContent.Should().Be("Maintenance");

        var first = page.FindAll(".ms-maint-table tbody tr")[0];
        first.TextContent.Should().Contain("public.orders").And.Contain("behind").And.Contain("40,000 of 10,050").And.Contain("autovacuum_vacuum_scale_factor=0.01");
        first.QuerySelector(".ms-maint-bar")!.ClassName.Should().Contain("ms-maint-bar-behind");
        first.QuerySelector(".ms-maint-bar")!.GetAttribute("aria-label").Should().Be("Dead rows: 40,000 against a trigger of 10,050, past twice the trigger");
        first.TextContent.Should().Contain("3.2 s", "PostgreSQL 18 reports the time spent vacuuming");

        var second = page.FindAll(".ms-maint-table tbody tr")[1];
        second.TextContent.Should().Contain("autovacuum off").And.Contain("12 partitions").And.Contain("per partition");

        page.Find(".ms-maint-withheld").TextContent.Should().Contain("1 table in schemas the database browser does not show you");
    }

    [Fact]
    public async Task What_is_running_the_workers_the_forecast_and_the_horizon_are_drawn_from_the_view()
    {
        (PostgresStudioComponentContext context, FakeMaintenanceService _) = await ContextAsync(View());
        await using PostgresStudioComponentContext disposing = context;

        var page = context.Render<Maintenance>();

        page.WaitForAssertion(() => page.FindAll(".ms-maint-running-table tbody tr").Should().ContainSingle());
        page.Find(".ms-maint-running-table").TextContent.Should().Contain("autovacuum, to prevent wraparound")
            .And.Contain("1,200 of 10,000 blocks (12%)").And.Contain("about 9 min");
        page.Find(".ms-maint-unseen").TextContent.Should().Contain("2 maintenance processes").And.Contain("pg_read_all_stats");
        page.Find(".ms-maint-workers").TextContent.Should().Contain("3 of 3 autovacuum workers are busy").And.Contain("autovacuum_worker_slots");
        page.Find(".ms-maint-freeze-table").TextContent.Should().Contain("150 million").And.Contain("about 3 days");
        page.Find(".ms-maint-horizon").TextContent.Should().Contain("Nothing holds the vacuum horizon back.");
    }

    [Fact]
    public async Task Measure_asks_the_service_for_that_table_and_draws_what_it_found()
    {
        (PostgresStudioComponentContext context, FakeMaintenanceService service) = await ContextAsync(View());
        await using PostgresStudioComponentContext disposing = context;

        service.Measure = new BloatMeasureRow(8_192_000, 12.5, 9_000, 1_000, 9.5, 819_200, 10, [new BloatIndexRow("orders_pkey", "btree", 245_760, 45.2, 3, null, null)]);

        var page = context.Render<Maintenance>();
        page.WaitForAssertion(() => page.FindAll(".ms-maint-measure").Should().ContainSingle());

        await page.Find(".ms-maint-measure").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        page.WaitForAssertion(() => page.FindAll(".ms-maint-bloat").Should().ContainSingle());
        service.Measured.Should().Equal(("public", "orders"));
        page.Find(".ms-maint-bloat").TextContent.Should().Contain("9.5%").And.Contain("orders_pkey").And.Contain("45%");
    }

    [Fact]
    public async Task A_refused_view_says_why_and_draws_nothing_else()
    {
        (PostgresStudioComponentContext context, FakeMaintenanceService _) = await ContextAsync(
            MaintenanceView.Refused(MaintenanceRefusal.StorePolicy, "Your account may not see this database's maintenance."));
        await using PostgresStudioComponentContext disposing = context;

        var page = context.Render<Maintenance>();

        page.WaitForAssertion(() => page.FindAll(".ms-maint-refusal").Should().ContainSingle());
        page.Find(".ms-maint-refusal").TextContent.Should().Contain("may not see this database's maintenance");
        page.FindAll(".ms-maint-table").Should().BeEmpty();
    }

    /// <summary>A stated view, and every measure the page asked for.</summary>
    private sealed class FakeMaintenanceService(MaintenanceView view) : IMaintenanceService
    {
        public List<(string Schema, string Table)> Measured { get; } = [];

        public BloatMeasureRow? Measure { get; set; }

        public Task<MaintenanceView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default) => Task.FromResult(view);

        public Task<BloatMeasureResult> MeasureBloatAsync(StudioScope scope, string schema, string table, CancellationToken cancellationToken = default)
        {
            Measured.Add((schema, table));
            return Task.FromResult(new BloatMeasureResult(schema + "." + table, Measure, Measure is null ? "refused" : null, At));
        }

        private static DateTimeOffset At => new(2026, 10, 26, 9, 1, 0, TimeSpan.Zero);
    }
}
