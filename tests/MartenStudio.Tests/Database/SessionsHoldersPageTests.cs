using AngleSharp.Dom;

using Bunit;

using PostgresStudio.Components.Pages.Database;
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
}
