using Bunit;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Live;
using PostgresStudio.Services.Maintenance;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Statements;
using PostgresStudio.Services.Storage;

using MaintenancePage = PostgresStudio.Components.Pages.Maintenance.Maintenance;
using StatementsPage = PostgresStudio.Components.Pages.Statements.Statements;
using StoragePage = PostgresStudio.Components.Pages.Storage.Storage;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// Mark on Statements, Storage and Maintenance (1.2-H-A, X6), rendered over fakes the test moves forward: the control only once
/// the page is interactive and has a read, the chip in the selected zone, the numbers switching to the change since the mark and
/// back on Clear, a mark that no longer holds said on the page, no interop call, and the mark living in the circuit's
/// <see cref="SinceMarks" /> - so a new page instance sees it and another database does not.
/// </summary>
public class SinceMarkPagesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset T1 = T0.AddMinutes(10);

    private const string Advisor = "the advisor's window";

    private sealed class FakeStatements : IStatementsService
    {
        public StatementsView View { get; set; } = new();

        public TaskCompletionSource<StatementsView>? Pending { get; set; }

        public Func<long, StatementPlanView>? Plan { get; set; }

        public Task<StatementsView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
            Pending?.Task ?? Task.FromResult(View);

        public Task<StatementsView> GetLatestAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
            Pending?.Task ?? Task.FromResult(View);

        public Task<StatementPlanView> PlanAsync(StudioScope scope, long queryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Plan?.Invoke(queryId) ?? new StatementPlanView { QueryId = queryId, NoPlan = "none" });

        public Task<WhatIfResult> WhatIfAsync(StudioScope scope, long queryId, HypotheticalIndex index, CancellationToken cancellationToken = default) =>
            Task.FromResult(WhatIfResult.NotCompared(index, "none"));
    }

    private sealed class FakeMaintenance : IMaintenanceService
    {
        public MaintenanceView View { get; set; } = new();

        public Task<MaintenanceView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default) => Task.FromResult(View);

        public Task<BloatMeasureResult> MeasureBloatAsync(StudioScope scope, string schema, string table, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BloatMeasureResult(schema + "." + table, null, "refused", T0));
    }

    private static StatementCounts Counts(double calls, double exec) => new(calls, exec, calls * 2, calls * 10, calls, 0, 0, null);

    private static StatementsView Statements(DateTimeOffset at, double calls, double exec, double all, DateTimeOffset? statsReset = null) => new()
    {
        State = StatementsState.Read,
        MeasuredAt = at,
        AllExecMsSinceBegan = all,
        TotalExecMs = all,
        Watched = true,
        WindowText = Advisor,
        StatsReset = statsReset,
        Rows =
        [
            new StatementRowView(1, "select 1", null, "select · public.orders", "select · public.orders", Counts(calls, exec), Counts(calls, exec), 0.5, null, null)
            {
                CountersSince = T0.AddDays(-1),
            },
        ],
    };

    private static async Task<(PostgresStudioComponentContext Context, FakeStatements Service)> StatementsContextAsync(StatementsView view)
    {
        FakeStatements service = new() { View = view };
        PostgresStudioComponentContext context = new(services => services.AddSingleton<IStatementsService>(service));
        context.WithAllCapabilities();
        return (await context.ReadyAsync(), service);
    }

    private static void ClickRefresh(IRenderedComponent<StatementsPage> page) =>
        page.FindAll("button").Single(static x => x.TextContent.Trim() == "Refresh").Click();

    [Fact]
    public async Task Mark_is_offered_only_once_the_page_is_interactive_and_has_a_read()
    {
        (PostgresStudioComponentContext context, FakeStatements service) = await StatementsContextAsync(Statements(T0, 10, 100, 1_000));
        await using PostgresStudioComponentContext disposing = context;
        service.Pending = new TaskCompletionSource<StatementsView>(TaskCreationOptions.RunContinuationsAsynchronously);

        var page = context.Render<StatementsPage>();

        page.Find(".ms-mark-button").HasAttribute("disabled").Should().BeTrue("nothing is read yet, so there is nothing to remember");

        service.Pending.SetResult(service.View);

        page.WaitForAssertion(() => page.Find(".ms-mark-button").HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public async Task Mark_is_disabled_for_a_page_that_shows_a_status_and_no_read()
    {
        (PostgresStudioComponentContext context, FakeStatements _) = await StatementsContextAsync(
            new StatementsView { State = StatementsState.NotInstalled, MeasuredAt = T0, Status = "pg_stat_statements is not installed." });
        await using PostgresStudioComponentContext disposing = context;

        var page = context.Render<StatementsPage>();

        page.WaitForAssertion(() => page.Find(".ms-stmts-status").TextContent.Should().Contain("not installed"));
        page.Find(".ms-mark-button").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Statements_count_from_the_mark_and_Clear_restores_the_advisors_window()
    {
        (PostgresStudioComponentContext context, FakeStatements service) = await StatementsContextAsync(Statements(T0, 10, 100, 1_000));
        await using PostgresStudioComponentContext disposing = context;
        context.State.SelectedTimeZoneId = "Asia/Kolkata";

        var page = context.Render<StatementsPage>();
        page.WaitForAssertion(() => page.Find(".ms-stmts-window").TextContent.Should().Be(Advisor));

        int interop = context.JSInterop.Invocations.Count;
        page.Find(".ms-mark-button").Click();

        page.WaitForAssertion(() => page.Find(".ms-mark-chip").TextContent.Trim().Should().Be("Counting since 2026-10-26 17:30:00"));
        context.JSInterop.Invocations.Count.Should().Be(interop, "marking is the circuit's memory and nothing else: no interop call");

        service.View = Statements(T1, 30, 300, 1_400);
        ClickRefresh(page);

        page.WaitForAssertion(() => page.Find(".ms-stmts-window").TextContent.Trim().Should().StartWith("Since your mark at 2026-10-26 17:30 (10 min)"));
        page.Find(".ms-stmts-table tbody tr").QuerySelectorAll("td")[3].TextContent.Trim().Should().Be("20", "30 calls now less the 10 at the mark");

        page.Find(".ms-mark-clear").Click();

        page.WaitForAssertion(() => page.Find(".ms-stmts-window").TextContent.Should().Be(Advisor));
        page.FindAll(".ms-mark-chip").Should().BeEmpty();
        page.Find(".ms-stmts-table tbody tr").QuerySelectorAll("td")[3].TextContent.Trim().Should().Be("30");
    }

    [Fact]
    public async Task A_mark_that_no_longer_holds_is_said_on_the_page_and_Mark_again_counts_from_the_new_read()
    {
        (PostgresStudioComponentContext context, FakeStatements service) = await StatementsContextAsync(Statements(T0, 10, 100, 1_000));
        await using PostgresStudioComponentContext disposing = context;
        context.State.SelectedTimeZoneId = "Asia/Kolkata";

        var page = context.Render<StatementsPage>();
        page.WaitForAssertion(() => page.Find(".ms-mark-button").HasAttribute("disabled").Should().BeFalse());
        page.Find(".ms-mark-button").Click();
        page.WaitForAssertion(() => page.Find(".ms-mark-chip"));

        service.View = Statements(T1, 5, 50, 90, statsReset: T0.AddMinutes(3));
        ClickRefresh(page);

        page.WaitForAssertion(() => page.Find(".ms-mark-alert").TextContent.Should().Contain("Your mark at 2026-10-26 17:30 no longer holds: pg_stat_statements was reset at 2026-10-26 17:33"));
        page.Find(".ms-mark-alert").TextContent.Should().Contain("mark again to count from now");
        page.Find(".ms-stmts-window").TextContent.Should().Be(Advisor, "the page counts over the advisor's window again");
        page.Find(".ms-mark-chip").ClassList.Should().Contain("ms-mark-chip-invalid");

        page.Find(".ms-mark-again").Click();

        page.WaitForAssertion(() => page.Find(".ms-mark-chip").TextContent.Trim().Should().Be("Counting since 2026-10-26 17:40:00"));
        page.FindAll(".ms-mark-alert").Should().BeEmpty();
    }

    [Fact]
    public async Task A_mark_survives_a_new_page_instance_and_another_database_shows_none()
    {
        (PostgresStudioComponentContext context, FakeStatements _) = await StatementsContextAsync(Statements(T0, 10, 100, 1_000));
        await using PostgresStudioComponentContext disposing = context;

        var first = context.Render<StatementsPage>();
        first.WaitForAssertion(() => first.Find(".ms-mark-button").HasAttribute("disabled").Should().BeFalse());
        first.Find(".ms-mark-button").Click();
        first.WaitForAssertion(() => first.Find(".ms-mark-chip"));
        await context.DisposeComponentsAsync();

        var second = context.Render<StatementsPage>();
        second.WaitForAssertion(() => second.Find(".ms-mark-chip").TextContent.Trim().Should().Be("Counting since 2026-10-26 12:00:00"));
        await context.DisposeComponentsAsync();

        // A mark of another database, in the same circuit, is not this database's.
        SinceMarks marks = context.Services.GetRequiredService<SinceMarks>();
        StudioScope here = context.State.ActiveScope!;
        marks.Clear(StatementsSinceMark.Page, here);
        marks.Set(StatementsSinceMark.Page, here with { DatabaseId = "another" }, StatementsSinceMark.Take(Statements(T0, 1, 1, 1))!);

        var third = context.Render<StatementsPage>();
        third.WaitForAssertion(() => third.Find(".ms-mark-button").HasAttribute("disabled").Should().BeFalse());
        third.FindAll(".ms-mark-chip").Should().BeEmpty();
    }

    private static StorageTableView Named(string name, long total, long inserts, long updates, long deletes) =>
        new("public", name, "/postgres/database/" + name, null, null, total, total, 0, 0, 1_000, 0.1, null, null, null)
        {
            Inserts = inserts,
            Updates = updates,
            Deletes = deletes,
        };

    private static StorageView Storage(DateTimeOffset at, long database, long orders, long writes, DateTimeOffset? windowSince = null) => new()
    {
        MeasuredAt = at,
        DatabaseBytes = database,
        EstimatedDatabaseBytes = database,
        Window = new StatisticsWindow
        {
            MeasuredAt = at,
            Since = windowSince ?? T0.AddDays(-2),
            Basis = StatisticsBasis.ServerReset,
            Reason = StatisticsResetReason.ServerStarted,
            SharedView = "pg_stat_checkpointer",
        },
        Tables =
        [
            Named("orders", orders, writes, 0, 0),
            new StorageTableView(null, null, null, null, null, 5L << 20, 5L << 20, 0, 0, 100, 0.01, null, null, null),
        ],
    };

    [Fact]
    public async Task Storage_adds_one_column_since_the_mark_and_a_withheld_table_is_a_dash()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.Storage.View = Storage(T0, 400L << 20, 100L << 20, 1_000);

        var page = context.Render<StoragePage>();
        page.WaitForAssertion(() => page.Find(".ms-mark-button").HasAttribute("disabled").Should().BeFalse());
        int columns = page.FindAll(".ms-storage-table thead th").Count;
        page.FindAll(".ms-mark-column").Should().BeEmpty();

        page.Find(".ms-mark-button").Click();
        page.WaitForAssertion(() => page.Find(".ms-mark-chip").TextContent.Trim().Should().Be("Counting since 2026-10-26 12:00:00"));

        context.Storage.View = Storage(T1, 434L << 20, 112L << 20, 3_400);
        page.FindAll("button").Single(static x => x.TextContent.Trim() == "Refresh").Click();

        page.WaitForAssertion(() => page.Find("th.ms-mark-column").TextContent.Trim().Should().Be("Since 2026-10-26 12:00"));
        page.FindAll(".ms-storage-table thead th").Count.Should().Be(columns + 1, "a mark adds one column and no more");

        var rows = page.FindAll(".ms-storage-table tbody tr");
        rows[0].QuerySelector("td.ms-mark-column")!.TextContent.Trim().Should().Be("+12.0 MiB · 2,400 rows written");
        rows[1].QuerySelector("td.ms-mark-column")!.TextContent.Trim().Should().Be("–");
        page.Find(".ms-storage-tile-note").TextContent.Should().Contain("+34.0 MiB since 2026-10-26 12:00");

        page.Find(".ms-mark-clear").Click();

        page.WaitForAssertion(() => page.FindAll(".ms-mark-column").Should().BeEmpty());
        page.FindAll(".ms-storage-table thead th").Count.Should().Be(columns);
    }

    [Fact]
    public async Task Storage_says_so_when_the_statistics_began_again_after_the_mark()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.Storage.View = Storage(T0, 400L << 20, 100L << 20, 1_000);

        var page = context.Render<StoragePage>();
        page.WaitForAssertion(() => page.Find(".ms-mark-button").HasAttribute("disabled").Should().BeFalse());
        page.Find(".ms-mark-button").Click();
        page.WaitForAssertion(() => page.Find(".ms-mark-chip"));

        context.Storage.View = Storage(T1, 434L << 20, 112L << 20, 3_400, windowSince: T0.AddMinutes(4));
        page.FindAll("button").Single(static x => x.TextContent.Trim() == "Refresh").Click();

        page.WaitForAssertion(() => page.Find(".ms-mark-alert").TextContent.Should().Contain("no longer holds: the statistics began again at"));
        page.FindAll(".ms-mark-column").Should().BeEmpty("nothing is counted from a mark that no longer holds");
        page.Find(".ms-mark-again").TextContent.Trim().Should().Be("Mark again");
    }

    private static MaintenanceView Maintenance(DateTimeOffset at, long vacuums, DateTimeOffset? windowSince = null) => new()
    {
        MeasuredAt = at,
        TablesRead = true,
        ServerVersion = 180_006,
        Statistics = new StatisticsWindow
        {
            MeasuredAt = at,
            Since = windowSince ?? T0.AddDays(-2),
            Basis = StatisticsBasis.ServerReset,
            Reason = StatisticsResetReason.Crash,
            SharedView = "pg_stat_checkpointer",
        },
        Tables =
        [
            new MaintenanceTableView
            {
                Key = "public.orders",
                Schema = "public",
                Table = "orders",
                Description = "the table public.orders",
                Vacuums = vacuums,
                Analyzes = 4,
                TotalVacuumMilliseconds = vacuums * 600,
                Measurable = true,
            },
        ],
    };

    [Fact]
    public async Task Maintenance_adds_one_column_since_the_mark_and_Clear_removes_it()
    {
        FakeMaintenance service = new() { View = Maintenance(T0, 10) };
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext(services => services.AddSingleton<IMaintenanceService>(service)).ReadyAsync();

        var page = context.Render<MaintenancePage>();
        page.WaitForAssertion(() => page.Find(".ms-mark-button").HasAttribute("disabled").Should().BeFalse());
        int columns = page.FindAll(".ms-maint-table thead th").Count;

        page.Find(".ms-mark-button").Click();
        page.WaitForAssertion(() => page.Find(".ms-mark-chip").TextContent.Trim().Should().Be("Counting since 2026-10-26 12:00:00"));

        service.View = Maintenance(T1, 12);
        page.FindAll("button").Single(static x => x.TextContent.Trim() == "Refresh").Click();

        page.WaitForAssertion(() => page.Find("th.ms-mark-column").TextContent.Trim().Should().Be("Since 2026-10-26 12:00"));
        page.FindAll(".ms-maint-table thead th").Count.Should().Be(columns + 1);
        page.Find(".ms-maint-table tbody td.ms-mark-column").TextContent.Trim().Should().Be("2 vacuums, 0 analyzes · 1.2 s vacuuming");

        page.Find(".ms-mark-clear").Click();

        page.WaitForAssertion(() => page.FindAll(".ms-mark-column").Should().BeEmpty());
    }

    [Fact]
    public async Task Maintenance_says_so_when_the_statistics_began_again_after_the_mark()
    {
        FakeMaintenance service = new() { View = Maintenance(T0, 10) };
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext(services => services.AddSingleton<IMaintenanceService>(service)).ReadyAsync();

        var page = context.Render<MaintenancePage>();
        page.WaitForAssertion(() => page.Find(".ms-mark-button").HasAttribute("disabled").Should().BeFalse());
        page.Find(".ms-mark-button").Click();
        page.WaitForAssertion(() => page.Find(".ms-mark-chip"));

        service.View = Maintenance(T1, 12, windowSince: T0.AddMinutes(4));
        page.FindAll("button").Single(static x => x.TextContent.Trim() == "Refresh").Click();

        page.WaitForAssertion(() => page.Find(".ms-mark-alert").TextContent.Should().Contain("no longer holds"));
        page.FindAll(".ms-mark-column").Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------------------------------
    // 1.2-H-A-FIX
    // ------------------------------------------------------------------------------------------------------------------

    private static StatementPlanView AdvisorPlan(long queryId) => new()
    {
        QueryId = queryId,
        NoPlan = "none",
        Statement = new StatementRowView(queryId, "select 1", null, "g", "g", Counts(1_234, 9_999), Counts(1_234, 9_999), null, null, null),
    };

    [Fact]
    public async Task The_drill_in_counts_from_the_mark_with_the_window_line_not_from_the_advisors_window()
    {
        (PostgresStudioComponentContext context, FakeStatements service) = await StatementsContextAsync(Statements(T0, 10, 100, 1_000));
        await using PostgresStudioComponentContext disposing = context;
        service.Plan = AdvisorPlan;
        context.Navigate("postgres/statements?queryid=1");

        var page = context.Render<StatementsPage>();
        page.WaitForAssertion(() => page.Find(".ms-plan-drill-counts").TextContent.Should().Contain("1,234 calls"));

        page.Find(".ms-mark-button").Click();
        page.WaitForAssertion(() => page.Find(".ms-mark-chip"));

        service.View = Statements(T1, 30, 300, 1_400);
        ClickRefresh(page);

        page.WaitForAssertion(() => page.Find(".ms-plan-drill-counts").TextContent.Should().Contain("20 calls").And.Contain("over the window above"));
        page.Find(".ms-plan-drill-counts").TextContent.Should().NotContain("1,234");
        page.Find(".ms-stmts-window").TextContent.Trim().Should().StartWith("Since your mark");

        page.Find(".ms-mark-clear").Click();

        page.WaitForAssertion(() => page.Find(".ms-plan-drill-counts").TextContent.Should().Contain("1,234 calls"));
    }

    [Fact]
    public async Task The_drill_in_of_a_statement_the_mark_cannot_count_says_why_and_not_the_advisors_numbers()
    {
        (PostgresStudioComponentContext context, FakeStatements service) = await StatementsContextAsync(Statements(T0, 10, 100, 1_000));
        await using PostgresStudioComponentContext disposing = context;
        service.Plan = AdvisorPlan;
        context.Navigate("postgres/statements?queryid=2");

        // Statement 2 is new to the kept statements after the mark, on a server that does not give an entry its own start.
        StatementsView later = Statements(T1, 30, 300, 1_400);
        later = later with
        {
            Rows = [.. later.Rows, later.Rows[0] with { QueryId = 2, Text = "select 2" }],
        };

        var page = context.Render<StatementsPage>();
        page.WaitForAssertion(() => page.Find(".ms-mark-button").HasAttribute("disabled").Should().BeFalse());
        page.Find(".ms-mark-button").Click();
        page.WaitForAssertion(() => page.Find(".ms-mark-chip"));

        service.View = later;
        ClickRefresh(page);

        page.WaitForAssertion(() => page.Find(".ms-plan-drill-counts").TextContent.Should().Contain("Not among the statements kept at your mark"));
        page.Find(".ms-plan-drill").TextContent.Should().NotContain("1,234");
    }

    [Fact]
    public async Task A_refused_read_after_a_mark_counts_nothing_and_Clear_stays_available_on_Statements()
    {
        (PostgresStudioComponentContext context, FakeStatements service) = await StatementsContextAsync(Statements(T0, 10, 100, 1_000));
        await using PostgresStudioComponentContext disposing = context;

        var page = context.Render<StatementsPage>();
        page.WaitForAssertion(() => page.Find(".ms-mark-button").HasAttribute("disabled").Should().BeFalse());
        page.Find(".ms-mark-button").Click();
        page.WaitForAssertion(() => page.Find(".ms-mark-chip"));

        service.View = StatementsView.Refused(DatabaseRefusal.WritePolicy, "refused");
        ClickRefresh(page);

        page.WaitForAssertion(() => page.Find(".ms-stmts-refusal"));
        page.FindAll(".ms-mark-column").Should().BeEmpty();
        page.FindAll(".ms-mark-delta").Should().BeEmpty();
        page.Markup.Should().NotContain("Since your mark");
        page.Find(".ms-mark-clear").HasAttribute("disabled").Should().BeFalse("a mark can always be put down");

        page.Find(".ms-mark-clear").Click();

        page.WaitForAssertion(() => page.FindAll(".ms-mark-chip").Should().BeEmpty());
    }

    [Fact]
    public async Task A_refused_read_after_a_mark_counts_nothing_and_Clear_stays_available_on_Storage()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.Storage.View = Storage(T0, 400L << 20, 100L << 20, 1_000);

        var page = context.Render<StoragePage>();
        page.WaitForAssertion(() => page.Find(".ms-mark-button").HasAttribute("disabled").Should().BeFalse());
        page.Find(".ms-mark-button").Click();
        page.WaitForAssertion(() => page.Find(".ms-mark-chip"));

        context.Storage.View = StorageView.Refused(DatabaseRefusal.StorePolicy, "refused");
        page.FindAll("button").Single(static x => x.TextContent.Trim() == "Refresh").Click();

        page.WaitForAssertion(() => page.Find(".ms-storage-refusal"));
        page.FindAll(".ms-mark-column").Should().BeEmpty();
        page.FindAll(".ms-mark-delta").Should().BeEmpty();
        page.Markup.Should().NotContain("Since your mark");
        page.Find(".ms-mark-clear").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task A_refused_read_after_a_mark_counts_nothing_and_Clear_stays_available_on_Maintenance()
    {
        FakeMaintenance service = new() { View = Maintenance(T0, 10) };
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext(services => services.AddSingleton<IMaintenanceService>(service)).ReadyAsync();

        var page = context.Render<MaintenancePage>();
        page.WaitForAssertion(() => page.Find(".ms-mark-button").HasAttribute("disabled").Should().BeFalse());
        page.Find(".ms-mark-button").Click();
        page.WaitForAssertion(() => page.Find(".ms-mark-chip"));

        service.View = MaintenanceView.Refused(MaintenanceRefusal.StorePolicy, "refused");
        page.FindAll("button").Single(static x => x.TextContent.Trim() == "Refresh").Click();

        page.WaitForAssertion(() => page.Find(".ms-maint-refusal"));
        page.FindAll(".ms-mark-column").Should().BeEmpty();
        page.FindAll(".ms-mark-delta").Should().BeEmpty();
        page.Markup.Should().NotContain("Since your mark");
        page.Find(".ms-mark-clear").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task A_valid_marks_chip_says_which_read_it_counts_from_and_an_ended_marks_says_what_the_page_does_instead()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.State.SelectedTimeZoneId = "Asia/Kolkata";
        context.Storage.View = Storage(T0, 400L << 20, 100L << 20, 1_000);

        var page = context.Render<StoragePage>();
        page.WaitForAssertion(() => page.Find(".ms-mark-button").HasAttribute("disabled").Should().BeFalse());
        page.Find(".ms-mark-button").Click();
        page.WaitForAssertion(() => page.Find(".ms-mark-chip"));

        page.Find(".ms-mark-chip").GetAttribute("title").Should()
            .Be("Counted from the read at 2026-10-26 17:30:00: the counters on screen when you pressed Mark.");

        context.Storage.View = Storage(T1, 434L << 20, 112L << 20, 3_400, windowSince: T0.AddMinutes(4));
        page.FindAll("button").Single(static x => x.TextContent.Trim() == "Refresh").Click();

        page.WaitForAssertion(() => page.Find(".ms-mark-alert"));
        const string Gone = "The Since column is gone; mark again to count from now.";
        page.Find(".ms-mark-alert").TextContent.Should().Contain(Gone).And.NotContain("advisor")
            .And.Contain("Your mark at 2026-10-26 17:30 no longer holds: the statistics began again at 2026-10-26 17:34");
        page.Find(".ms-mark-chip").GetAttribute("title").Should().Be(Gone);
    }

    [Fact]
    public async Task Maintenances_ended_mark_says_the_Since_column_is_gone()
    {
        FakeMaintenance service = new() { View = Maintenance(T0, 10) };
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext(services => services.AddSingleton<IMaintenanceService>(service)).ReadyAsync();

        var page = context.Render<MaintenancePage>();
        page.WaitForAssertion(() => page.Find(".ms-mark-button").HasAttribute("disabled").Should().BeFalse());
        page.Find(".ms-mark-button").Click();
        page.WaitForAssertion(() => page.Find(".ms-mark-chip"));

        service.View = Maintenance(T1, 12, windowSince: T0.AddMinutes(4));
        page.FindAll("button").Single(static x => x.TextContent.Trim() == "Refresh").Click();

        page.WaitForAssertion(() => page.Find(".ms-mark-alert").TextContent.Should().Contain("The Since column is gone; mark again to count from now.").And.NotContain("advisor"));
    }

    [Fact]
    public async Task A_moment_is_spelt_in_the_selected_zone_and_carries_its_date_unless_it_is_today()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.State.SelectedTimeZoneId = "Asia/Kolkata";

        SinceMarkTime.When(context.State, T0, now: T0.AddHours(1)).Should().Be("17:30");
        SinceMarkTime.When(context.State, T0, seconds: true, now: T0.AddHours(1)).Should().Be("17:30:00");
        SinceMarkTime.When(context.State, T0, now: T0.AddDays(1)).Should().Be("2026-10-26 17:30");

        // Today is the selected zone's today: 20:00 UTC is already the next day in Kolkata.
        SinceMarkTime.When(context.State, T0, now: T0.AddHours(8)).Should().Be("2026-10-26 17:30", "20:00 UTC is 01:30 the next day in Kolkata");
    }
}
