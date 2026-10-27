using Bunit;

using PostgresStudio.Components.Pages.Database;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using MartenStudio.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Database › Sessions › Blocking in bUnit (X3), over the fake service: the tab the address names, the counts to everybody
/// the summary admits, the trees only through the details gate - head, nested waiters, the relation as the visitor may see
/// it, the statements to hand over and nothing that runs them - and PostgreSQL 19's <c>pg_stat_lock</c>, absent below 19.
/// </summary>
public class SessionsBlockingPageTests
{
    private static readonly SessionDetailsAccess Closed =
        new(false, false, null, DatabaseRefusal.CapabilityOff, SessionActivityService.CapabilityDenial);

    private static readonly SessionDetailsAccess Open = new(true, false, true, DatabaseRefusal.None, null);

    private static readonly DateTimeOffset Reset = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private static SessionActivitySummary Summary(SessionDetailsAccess access, bool postgres19 = false) =>
        FakeSessionActivityService.SummaryWith(access) with
        {
            Blocking = new SessionBlockingCounts(1, 2, TimeSpan.FromMinutes(3), 1, 0),
            LockStatistics = postgres19
                ? [new LockStatisticsRow("relation", 7, 2_395.5, 0, Reset), new LockStatisticsRow("transactionid", 0, 0, 0, Reset)]
                : null,
        };

    /// <summary>One chain: a head idle in its transaction, a waiter on a visible table, and one nested below it on a withheld one.</summary>
    private static SessionActivityDetails Details() => FakeSessionActivityService.DetailsFor() with
    {
        Blocking =
        [
            new BlockingChainView(
                new BlockingNodeView
                {
                    ProcessId = 5151,
                    User = "app_user",
                    Application = "billing-api",
                    State = "idle in transaction",
                    StateAge = TimeSpan.FromMinutes(4),
                    TransactionAge = TimeSpan.FromMinutes(5),
                    HeldModes = ["AccessExclusiveLock"],
                    Waiters =
                    [
                        new BlockingNodeView
                        {
                            ProcessId = 4242,
                            User = "report_user",
                            Application = "reports",
                            State = "active",
                            WaitEventType = "Lock",
                            WaitEvent = "relation",
                            Waited = TimeSpan.FromMinutes(3),
                            WantedMode = "AccessShareLock",
                            Target = "a relation",
                            Relation = "public.orders",
                            BlockedBy = [5151],
                            Waiters =
                            [
                                new BlockingNodeView
                                {
                                    ProcessId = 4343,
                                    State = "active",
                                    WaitEventType = "Lock",
                                    Waited = TimeSpan.FromSeconds(20),
                                    WantedMode = "AccessExclusiveLock",
                                    Target = "a relation",
                                    Relation = WithheldNames.Token,
                                    BlockedBy = [4242],
                                },
                            ],
                        },
                    ],
                },
                HeadWaits: false,
                Held: 2,
                LongestWait: TimeSpan.FromMinutes(3)),
            new BlockingChainView(
                new BlockingNodeView { ProcessId = 7070, State = "active", WaitEventType = "Lock", Waited = TimeSpan.FromSeconds(2), BlockedBy = [7171] },
                HeadWaits: true,
                Held: 0,
                LongestWait: TimeSpan.FromSeconds(2)),
        ],
    };

    private static async Task<(StudioComponentContext Context, FakeSessionActivityService Sessions, SessionPoolObserver Pools)> ReadyAsync(
        SessionActivitySummary summary,
        SessionActivityDetails? details = null)
    {
        FakeSessionActivityService sessions = new() { Summary = summary, Details = details ?? Details() };
        SessionPoolObserver pools = new("MartenStudio.Tests.SessionsBlocking." + Guid.NewGuid().ToString("N"));

        StudioComponentContext context = new(services =>
        {
            services.AddSingleton<ISessionActivityService>(sessions);
            services.AddSingleton(pools);
        });

        await context.ReadyAsync();
        return (context, sessions, pools);
    }

    [Fact]
    public async Task Through_the_gate_the_tab_draws_each_chain_from_its_head_with_its_waiters_nested()
    {
        (StudioComponentContext context, FakeSessionActivityService _, SessionPoolObserver pools) = await ReadyAsync(Summary(Open));

        try
        {
            context.Navigate("marten/database/sessions?view=blocking");
            var page = context.Render<Sessions>();
            page.WaitForAssertion(() => page.FindAll(".ms-blocking-chain").Should().HaveCount(2));

            page.Find(".ms-sessions-tabs .ms-tab-active").TextContent.Should().Contain("Blocking");
            page.FindAll(".ms-sessions-table thead th").Select(static x => x.TextContent).Should().NotContain("Statement",
                "the Blocking tab draws the trees in place of the session, lock and prepared lists");

            var head = page.Find(".ms-blocking-chain .ms-blocking-head");
            head.QuerySelector(".ms-blocking-pid dd")!.TextContent.Should().Be("5151");
            head.TextContent.Should().Contain("idle in transaction for 4 min").And.Contain("holds AccessExclusiveLock");

            var waiter = head.QuerySelector(".ms-blocking-waiters > .ms-blocking-node")!;
            waiter.TextContent.Should().Contain("4242").And.Contain("wants AccessShareLock on a relation").And.Contain("public.orders");

            var nested = waiter.QuerySelector(".ms-blocking-waiters > .ms-blocking-node")!;
            nested.TextContent.Should().Contain("4343").And.Contain(WithheldNames.Token, "a relation in a withheld schema is named by nothing but the token (D27)");

            page.Find(".ms-blocking-chain-unrooted").TextContent.Should().Contain("No head");
            page.FindAll(".ms-blocking-chain-unrooted .ms-blocking-handover").Should().BeEmpty("a chain with no head has nobody to hand over");
        }
        finally
        {
            await context.DisposeAsync();
            pools.Dispose();
        }
    }

    [Fact]
    public async Task A_heads_statements_are_text_to_copy_and_no_button_ends_a_session()
    {
        (StudioComponentContext context, FakeSessionActivityService _, SessionPoolObserver pools) = await ReadyAsync(Summary(Open));

        try
        {
            context.Navigate("marten/database/sessions?view=blocking");
            var page = context.Render<Sessions>();
            page.WaitForAssertion(() => page.FindAll(".ms-blocking-handover").Should().ContainSingle());

            var handover = page.Find(".ms-blocking-handover");
            handover.TextContent.Should().Contain("the studio never runs these").And.Contain("pg_signal_backend");
            handover.QuerySelectorAll("code").Select(static x => x.TextContent).Should().Equal(
                ["select pg_terminate_backend(5151);", "pg_signal_backend"],
                "a head idle in its transaction gets terminate alone: cancel does nothing to it");
            handover.QuerySelectorAll("button.ms-copy-button").Should().ContainSingle()
                .Which.GetAttribute("aria-label").Should().Be("Copy the statement that ends pid 5151's session");

            page.FindAll("button").Where(static x => !x.ClassList.Contains("ms-copy-button")).Select(static x => x.TextContent.Trim())
                .Should().Equal(["Refresh"], "the studio never cancels or terminates another session (K2): the statements are copied, never run");
        }
        finally
        {
            await context.DisposeAsync();
            pools.Dispose();
        }
    }

    [Fact]
    public async Task A_running_head_gets_cancel_and_terminate()
    {
        SessionActivityDetails details = Details();
        BlockingChainView chain = details.Blocking[0];
        details = details with { Blocking = [chain with { Head = chain.Head with { State = "active" } }] };

        (StudioComponentContext context, FakeSessionActivityService _, SessionPoolObserver pools) = await ReadyAsync(Summary(Open), details);

        try
        {
            context.Navigate("marten/database/sessions?view=blocking");
            var page = context.Render<Sessions>();
            page.WaitForAssertion(() => page.FindAll(".ms-blocking-handover").Should().ContainSingle());

            page.Find(".ms-blocking-handover").QuerySelectorAll("code").Select(static x => x.TextContent).Should().StartWith(
                ["select pg_cancel_backend(5151);", "select pg_terminate_backend(5151);"]);
        }
        finally
        {
            await context.DisposeAsync();
            pools.Dispose();
        }
    }

    [Fact]
    public async Task Without_the_gate_the_tab_shows_the_counts_names_nobody_and_never_asks_for_details()
    {
        (StudioComponentContext context, FakeSessionActivityService sessions, SessionPoolObserver pools) = await ReadyAsync(Summary(Closed));

        try
        {
            context.Navigate("marten/database/sessions?view=blocking");
            var page = context.Render<Sessions>();
            page.WaitForAssertion(() => page.FindAll(".ms-blocking-stats").Should().ContainSingle());

            page.Find(".ms-blocking-stats").TextContent.Should().Contain("Chains").And.Contain("Sessions held up").And.Contain("3 min");
            page.Find(".ms-blocking-gated").TextContent.Should().Contain("session detail");
            page.Find(".ms-capability-disabled").TextContent.Should().Contain("MartenStudioOptions.Capabilities.ViewDatabaseActivity");
            page.FindAll(".ms-blocking-chain").Should().BeEmpty();
            page.Markup.Should().NotContain("5151").And.NotContain("4242").And.NotContainEquivalentOf("pg_terminate_backend")
                .And.NotContain("public.orders");
            sessions.DetailsRequests.Should().BeEmpty("a page whose gate is closed refuses - and audits - nothing");
        }
        finally
        {
            await context.DisposeAsync();
            pools.Dispose();
        }
    }

    [Fact]
    public async Task On_PostgreSQL_19_the_tab_shows_pg_stat_lock_and_below_19_the_section_is_absent()
    {
        (StudioComponentContext context, FakeSessionActivityService _, SessionPoolObserver pools) = await ReadyAsync(Summary(Closed, postgres19: true));

        try
        {
            context.Navigate("marten/database/sessions?view=blocking");
            var page = context.Render<Sessions>();
            page.WaitForAssertion(() => page.FindAll(".ms-blocking-statistics").Should().ContainSingle());

            var rows = page.FindAll(".ms-blocking-statistics-table tbody tr");
            rows.Should().HaveCount(2);
            rows[0].TextContent.Should().Contain("relation").And.Contain("7").And.Contain("2 s");
            rows[1].TextContent.Should().Contain("transactionid").And.Contain("–", "no wait, no time");
            page.Find(".ms-blocking-statistics").TextContent.Should().Contain("pg_stat_lock").And.Contain("PostgreSQL 19");
        }
        finally
        {
            await context.DisposeAsync();
            pools.Dispose();
        }

        (StudioComponentContext older, FakeSessionActivityService _, SessionPoolObserver olderPools) = await ReadyAsync(Summary(Closed));

        try
        {
            older.Navigate("marten/database/sessions?view=blocking");
            var page = older.Render<Sessions>();
            page.WaitForAssertion(() => page.FindAll(".ms-blocking-stats").Should().ContainSingle());

            page.FindAll(".ms-blocking-statistics").Should().BeEmpty("there is no pg_stat_lock below 19");
        }
        finally
        {
            await older.DisposeAsync();
            olderPools.Dispose();
        }
    }

    /// <summary>
    /// 0.6's UI sweep: the empty Blocking tab ended in five disclosures that all said "Show SQL", and its way back to the
    /// database browser was a bare link the dark theme drew in the browser's own blue. Each now says what it reads, and the
    /// way back is the browser's own breadcrumb.
    /// </summary>
    [Fact]
    public async Task In_the_empty_state_each_Show_SQL_says_what_it_reads_and_the_way_back_is_the_browsers_breadcrumb()
    {
        SessionActivitySummary summary = FakeSessionActivityService.SummaryWith(Open) with
        {
            Blocking = SessionBlockingCounts.None,
            Statements = SessionActivityReader.Labelled(
            [
                new HealthStatement(HealthConnectionQueries.SessionSnapshot().Sql, []),
                new HealthStatement(HealthConnectionQueries.LockSnapshot().Sql, []),
                new HealthStatement(HealthConnectionQueries.PreparedTransactions().Sql, []),
                new HealthStatement(ConnectionsByApplicationQueries.Read().Sql, []),
                new HealthStatement(HealthConnectionQueries.LockTable().Sql, ["@limit"]),
            ]),
        };

        (StudioComponentContext context, FakeSessionActivityService _, SessionPoolObserver pools) = await ReadyAsync(summary);

        try
        {
            context.Navigate("marten/database/sessions?view=blocking");
            var page = context.Render<Sessions>();
            page.WaitForAssertion(() => page.FindAll(".ms-blocking-none").Should().ContainSingle());

            page.FindAll(".ms-sql-disclosure > summary").Select(static x => x.TextContent).Should().Equal(
                "Show SQL for the sessions",
                "Show SQL for the locks",
                "Show SQL for prepared transactions",
                "Show SQL for connections by application",
                "Show SQL for who blocks whom");

            var breadcrumb = page.Find("nav.ms-db-breadcrumb.ms-sessions-back");
            breadcrumb.GetAttribute("aria-label").Should().Be("Breadcrumb");
            breadcrumb.QuerySelector("a")!.TextContent.Should().Be("Database");
            breadcrumb.TextContent.Should().Contain("Sessions");
        }
        finally
        {
            await context.DisposeAsync();
            pools.Dispose();
        }
    }

    [Fact]
    public async Task The_sessions_tab_is_the_default_and_links_to_Blocking_with_its_count()
    {
        (StudioComponentContext context, FakeSessionActivityService _, SessionPoolObserver pools) = await ReadyAsync(Summary(Open));

        try
        {
            var page = context.Render<Sessions>();
            page.WaitForAssertion(() => page.FindAll(".ms-sessions-tabs").Should().ContainSingle());

            page.Find(".ms-sessions-tabs .ms-tab-active").TextContent.Trim().Should().Be("Sessions");
            page.FindAll(".ms-blocking").Should().BeEmpty();

            var blocking = page.FindAll(".ms-sessions-tabs a").Single(static x => x.TextContent.Contains("Blocking", StringComparison.Ordinal));
            blocking.GetAttribute("href").Should().StartWith("database/sessions?view=blocking");
            blocking.QuerySelector(".ms-tab-count")!.TextContent.Should().Be("1");
        }
        finally
        {
            await context.DisposeAsync();
            pools.Dispose();
        }
    }
}
