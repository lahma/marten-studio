using AngleSharp.Dom;

using Bunit;

using PostgresStudio;
using PostgresStudio.Components.Pages.Database;
using PostgresStudio.Services.ConnectionHistory;
using PostgresStudio.Services.Database;
using MartenStudio.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Database › Sessions › Holders in bUnit (issue #20), over the fake service: the tab the address names, the server's
/// connections per database with the databases the policy does not admit counted together and never named, the groups -
/// application, role, client address - only through the details gate, the budget C03 judges, "not checked" for the sessions a
/// role cannot see into, and every table in its own scroll region (D25).
/// </summary>
public class SessionsHoldersPageTests
{
    private static readonly SessionDetailsAccess Closed =
        new(false, false, null, DatabaseRefusal.CapabilityOff, SessionActivityService.CapabilityDenial);

    private static readonly SessionDetailsAccess Open = new(true, false, true, DatabaseRefusal.None, null);

    private static SessionHolderGroup Group(
        string database,
        string application,
        long total,
        long idle,
        long active = 0,
        long idleInTransaction = 0,
        long unknown = 0,
        string role = "app_user",
        string? client = "10.0.3.7",
        bool hidden = false,
        bool thisDatabase = false,
        TimeSpan? oldestIdle = null,
        TimeSpan? oldestIdleInTransaction = null) =>
        new(database, application, role, client, hidden, total, active, idle, idleInTransaction, unknown, oldestIdle, oldestIdleInTransaction, thisDatabase);

    private static readonly SessionHolderGroup[] Groups =
    [
        Group("app", "billing-api", 14, idle: 9, active: 5, thisDatabase: true, oldestIdle: TimeSpan.FromMinutes(4)),
        Group("billing", "billing-api", 30, idle: 22, active: 3, idleInTransaction: 5, client: "10.0.3.8", oldestIdle: TimeSpan.FromMinutes(7), oldestIdleInTransaction: TimeSpan.FromSeconds(40)),
        Group("secret", "payroll-sentinel", 11, idle: 11, role: "hr-sentinel", client: "10.9.9.9", oldestIdle: TimeSpan.FromMinutes(2)),
        Group("secret", "payroll-sentinel", 2, idle: 0, unknown: 2, role: "other-sentinel", client: null, hidden: true),
    ];

    private static SessionServerHolders Read(bool canReadAllStats = true, long groupCount = 4) =>
        new(canReadAllStats, 100, 3, 0, 170000, 58, groupCount, Groups);

    private static SessionActivitySummary Summary(SessionDetailsAccess access, bool canReadAllStats = true, long groupCount = 4) =>
        FakeSessionActivityService.SummaryWith(access) with
        {
            Holders = SessionHolders.Summarize(Read(canReadAllStats, groupCount), new HashSet<string>(["billing"], StringComparer.Ordinal)),
        };

    private static SessionActivityDetails Details(bool canReadAllStats = true, long groupCount = 4) =>
        FakeSessionActivityService.DetailsFor() with { Holders = SessionHolders.Detail(Read(canReadAllStats, groupCount)) };

    private static async Task<(StudioComponentContext Context, SessionPoolObserver Pools)> ReadyAsync(SessionActivitySummary summary, SessionActivityDetails? details = null)
    {
        FakeSessionActivityService sessions = new() { Summary = summary, Details = details ?? Details() };
        SessionPoolObserver pools = new("MartenStudio.Tests.SessionsHolders." + Guid.NewGuid().ToString("N"));

        StudioComponentContext context = new(services =>
        {
            services.AddSingleton<ISessionActivityService>(sessions);
            services.AddSingleton(pools);
        });

        await context.ReadyAsync();
        return (context, pools);
    }

    /// <summary>A row's database name: its first text, before the "this database" tag.</summary>
    private static string Name(IElement row) =>
        row.QuerySelector("th")!.ChildNodes.First(static x => x.NodeType == NodeType.Text && !string.IsNullOrWhiteSpace(x.TextContent)).TextContent.Trim();

    private static string[] Cells(IElement row) => [.. row.QuerySelectorAll("td").Select(static x => x.TextContent.Trim())];

    [Fact]
    public async Task The_page_has_a_third_tab_whose_address_is_the_holders_view()
    {
        (StudioComponentContext context, SessionPoolObserver pools) = await ReadyAsync(Summary(Closed));

        try
        {
            context.Navigate("marten/database/sessions");
            var page = context.Render<Sessions>();
            page.WaitForAssertion(() => page.FindAll(".ms-sessions-tabs .ms-tab").Should().HaveCount(3));

            page.FindAll(".ms-sessions-tabs .ms-tab").Select(static x => x.TextContent.Trim()).Should().Equal("Sessions", "Blocking", "Holders");
            page.FindAll(".ms-sessions-tabs .ms-tab")[2].GetAttribute("href").Should().Contain("view=holders");
            page.Find(".ms-sessions-tabs .ms-tab-active").TextContent.Should().Contain("Sessions", "no view named is the sessions themselves");
            page.FindAll(".ms-holders").Should().BeEmpty("the other tabs draw nothing of Holders");
            page.Find(".ms-sessions-measured").TextContent.Should().Contain("for this database only");
        }
        finally
        {
            await context.DisposeAsync();
            pools.Dispose();
        }
    }

    [Fact]
    public async Task Without_the_gate_the_databases_the_policy_admits_are_named_the_rest_are_one_unnamed_row_and_no_group_is_drawn()
    {
        (StudioComponentContext context, SessionPoolObserver pools) = await ReadyAsync(Summary(Closed));

        try
        {
            context.Navigate("marten/database/sessions?view=holders");
            var page = context.Render<Sessions>();
            page.WaitForAssertion(() => page.FindAll(".ms-holders-table").Should().NotBeEmpty());

            page.Find(".ms-sessions-tabs .ms-tab-active").TextContent.Should().Contain("Holders");
            page.FindAll(".ms-holders-table").Should().ContainSingle("only the per-database table; the groups are details");

            var rows = page.FindAll(".ms-holders-table tbody tr");
            rows.Select(Name).Should().Equal("billing", "app", "Other databases");
            rows[1].QuerySelector(".ms-holders-tag")!.TextContent.Should().Be("this database");
            rows[1].ClassList.Should().Contain("ms-holders-this");
            rows[2].ClassList.Should().Contain("ms-holders-others");

            Cells(rows[0]).Should().Equal("30", "3", "22", "5", "0", "7 min", "40 s");
            Cells(rows[2]).Should().Equal("13", "0", "11", "0", "2", "2 min", "–");

            page.Markup.Should().NotContain("secret").And.NotContain("payroll-sentinel").And.NotContain("hr-sentinel").And.NotContain("other-sentinel")
                .And.NotContain("10.9.9.9").And.NotContain("billing-api").And.NotContain("app_user", "no application, role or address is drawn without the gate");
            page.Find(".ms-holders-gated").TextContent.Should().Contain("session detail");
            page.Find(".ms-holders-note").TextContent.Should().Contain("may not tell you");
            System.Text.RegularExpressions.Regex.Replace(page.Find(".ms-holders-budget").TextContent, @"\s+", " ")
                .Should().Contain("58 client sessions on the whole server, 60% of the 97 an ordinary role may open")
                .And.Contain("97 (max_connections 100");
        }
        finally
        {
            await context.DisposeAsync();
            pools.Dispose();
        }
    }

    [Fact]
    public async Task Through_the_gate_every_database_is_named_and_every_group_is_drawn_with_its_application_role_and_address()
    {
        (StudioComponentContext context, SessionPoolObserver pools) = await ReadyAsync(Summary(Open));

        try
        {
            context.Navigate("marten/database/sessions?view=holders");
            var page = context.Render<Sessions>();
            page.WaitForAssertion(() => page.FindAll(".ms-holders-groups").Should().ContainSingle());

            page.FindAll(".ms-holders-table").Should().HaveCount(2, "by database, and by holder");
            page.FindAll(".ms-holders-table")[0].QuerySelectorAll("tbody tr").Select(Name).Should().Equal("billing", "app", "secret");
            page.FindAll(".ms-holders-others").Should().BeEmpty("every database is named, so none is counted unnamed");

            page.FindAll(".ms-holders-groups thead th").Select(static x => x.TextContent.Trim()).Should().Equal(
                "Database", "Application", "Role", "Client", "Sessions", "Active", "Idle", "Idle in transaction", "State not visible", "Oldest idle", "Oldest idle in transaction");

            var rows = page.FindAll(".ms-holders-groups tbody tr");
            rows.Should().HaveCount(4);
            Cells(rows[0]).Should().Equal("app", "billing-api", "app_user", "10.0.3.7", "14", "5", "9", "0", "0", "4 min", "–");
            Cells(rows[1]).Should().Equal("billing", "billing-api", "app_user", "10.0.3.8", "30", "3", "22", "5", "0", "7 min", "40 s");
            Cells(rows[2]).Should().Equal("secret", "payroll-sentinel", "hr-sentinel", "10.9.9.9", "11", "0", "11", "0", "0", "2 min", "–");
            Cells(rows[3]).Should().Equal("secret", "payroll-sentinel", "other-sentinel", "not readable", "2", "0", "0", "0", "2", "–", "–");
            rows[1].QuerySelectorAll("td")[7].ClassList.Should().Contain("ms-sessions-stat-alert", "idle in a transaction is the alert column");
            rows[0].ClassList.Should().Contain("ms-holders-this");

            page.FindAll(".ms-holders-gated").Should().BeEmpty();
            page.FindAll(".ms-holders-note").Should().BeEmpty();
        }
        finally
        {
            await context.DisposeAsync();
            pools.Dispose();
        }
    }

    [Fact]
    public async Task A_role_that_cannot_read_every_state_is_told_so_with_the_grant_and_a_capped_read_is_said()
    {
        (StudioComponentContext context, SessionPoolObserver pools) = await ReadyAsync(
            Summary(Open, canReadAllStats: false, groupCount: 1_450),
            Details(canReadAllStats: false, groupCount: 1_450));

        try
        {
            context.Navigate("marten/database/sessions?view=holders");
            var page = context.Render<Sessions>();
            page.WaitForAssertion(() => page.FindAll(".ms-holders-groups").Should().ContainSingle());

            string caveats = string.Join(" ", page.FindAll(".ms-sessions-caveat").Select(static x => x.TextContent));
            caveats.Should().Contain("pg_read_all_stats").And.Contain("State not visible").And.Contain("not checked");
            caveats.Should().Contain("Only the 4 largest of 1,450 groups");
        }
        finally
        {
            await context.DisposeAsync();
            pools.Dispose();
        }
    }

    [Fact]
    public async Task Every_table_of_the_tab_is_in_a_labelled_scroll_region_and_the_footer_says_it_is_the_whole_server()
    {
        (StudioComponentContext context, SessionPoolObserver pools) = await ReadyAsync(Summary(Open));

        try
        {
            context.Navigate("marten/database/sessions?view=holders");
            var page = context.Render<Sessions>();
            page.WaitForAssertion(() => page.FindAll(".ms-holders-groups").Should().ContainSingle());

            page.FindAll("table").Should().OnlyContain(static x => x.Closest("[role=region]") != null && x.Closest("[role=region]")!.HasAttribute("aria-label"));
            page.Find(".ms-sessions-measured").TextContent.Should().Contain("for the whole server");
        }
        finally
        {
            await context.DisposeAsync();
            pools.Dispose();
        }
    }

    // ---- Issue #43: the connection history -------------------------------------------------------------------------------------

    private static readonly DateTimeOffset HistoryStart = new(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);

    private static readonly PostgresConnectionSample[] Samples =
    [
        new() { At = HistoryStart, Server = "db:5432", Instance = "a", Connections = 10, ClientLimit = 95, ByApplication = [new() { ApplicationName = "orders-api", Total = 8 }] },
        new()
        {
            At = HistoryStart.AddMinutes(1),
            Server = "db:5432",
            Instance = "b",
            Connections = 90,
            ClientLimit = 95,
            ByApplication = [new() { ApplicationName = "orders-api", Total = 70 }, new() { ApplicationName = null, Total = 20 }],
        },
    ];

    private static async Task HistoryAsync(SessionDetailsAccess access, bool kept, ConnectionHistoryView? counts, ConnectionHistoryView? named, Func<Bunit.IRenderedComponent<Sessions>, Task> check, bool readOnly = false)
    {
        (StudioComponentContext context, SessionPoolObserver pools) = await ReadyAsync(
            Summary(access) with { ConnectionHistoryKept = kept, ConnectionHistoryReadOnly = readOnly, ConnectionHistory = counts },
            Details() with { ConnectionHistory = named });

        try
        {
            context.Navigate("marten/database/sessions?view=holders");
            var page = context.Render<Sessions>();
            page.WaitForAssertion(() => page.FindAll(".ms-holders-table").Should().NotBeEmpty());
            await check(page);
        }
        finally
        {
            await context.DisposeAsync();
            pools.Dispose();
        }
    }

    [Fact]
    public async Task Without_the_registration_Holders_says_how_to_keep_a_history() =>
        await HistoryAsync(Open, kept: false, null, null, static page =>
        {
            page.Find(".ms-holders-history-off").TextContent.Should().Contain("AddPostgresConnectionHistory()");
            page.FindAll(".ms-holders-history").Should().BeEmpty();
            page.FindAll(".ms-holders-history-none").Should().BeEmpty();
            return Task.CompletedTask;
        });

    [Fact]
    public async Task A_kept_history_with_no_sample_of_this_server_says_so() =>
        await HistoryAsync(Open, kept: true, null, null, static page =>
        {
            page.Find(".ms-holders-history-none").TextContent.Should().Contain("no sample of this server yet");
            page.FindAll(".ms-holders-history-off").Should().BeEmpty("the host registered it");
            return Task.CompletedTask;
        });

    [Fact]
    public async Task A_reader_only_history_with_no_sample_says_another_process_fills_it_and_never_asks_for_the_sampling_registration() =>
        await HistoryAsync(Open, kept: true, null, null, static page =>
        {
            string said = page.Find(".ms-holders-history-none").TextContent;
            said.Should().Contain("another process fills it").And.NotContain("samples each server once an interval");
            page.Markup.Should().NotContain("AddPostgresConnectionHistory()");
            page.FindAll(".ms-holders-history-off").Should().BeEmpty();
            return Task.CompletedTask;
        }, readOnly: true);

    [Fact]
    public async Task Without_the_gate_the_history_is_counts_and_names_no_application() =>
        await HistoryAsync(
            Closed,
            kept: true,
            ConnectionHistoryView.Of(Samples, names: false),
            ConnectionHistoryView.Of(Samples, names: true),
            static page =>
            {
                string said = page.Find(".ms-holders-history").TextContent;
                said.Should().Contain("90").And.Contain("95").And.Contain("2 samples").And.Contain("1 change of the answering instance");
                page.FindAll(".ms-holders-history-table").Should().BeEmpty("an application is named only past the activity gate");
                page.Markup.Should().NotContain("orders-api", "neither the history nor the groups name an application below the gate");
                return Task.CompletedTask;
            });

    [Fact]
    public async Task Through_the_gate_each_applications_own_peak_is_drawn_in_its_own_scroll_region() =>
        await HistoryAsync(
            Open,
            kept: true,
            ConnectionHistoryView.Of(Samples, names: false),
            ConnectionHistoryView.Of(Samples, names: true),
            static page =>
            {
                var table = page.Find(".ms-holders-history-table");
                table.ParentElement!.ClassList.Should().Contain("ms-table-scroll", "D25: every table in its own labelled scroll region");
                var rows = table.QuerySelectorAll("tbody tr");
                rows.Select(static x => x.QuerySelector("td")!.TextContent.Trim()).Should().Equal("orders-api", "(none set)");
                rows[0].QuerySelectorAll("td")[1].TextContent.Trim().Should().Be("70");
                return Task.CompletedTask;
            });
}
