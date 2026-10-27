using AngleSharp.Dom;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;
using PostgresStudio.Tests.Components;

using ReplicationPage = PostgresStudio.Components.Pages.Replication.Replication;

namespace PostgresStudio.Tests.Replication;

/// <summary>
/// Operations › Replication rendered over a fake service: the sections in order, standby lag with and without
/// <c>pg_read_all_stats</c>, names hidden without the activity gate, Aurora's not-applicable sections, and the tablespace
/// size behind its button.
/// </summary>
public class ReplicationPageTests
{
    private static async Task<(PostgresStudioComponentContext Context, FakeReplicationService Service)> ContextAsync(ReplicationView view, bool capabilities = true)
    {
        FakeReplicationService service = new(view);
        PostgresStudioComponentContext context = new(services => services.AddSingleton<IReplicationService>(service));

        if (capabilities)
        {
            context.WithAllCapabilities();
        }

        return (await context.ReadyAsync(), service);
    }

    private static ReplicationView View(bool names, IReadOnlyList<StandbyRow>? standbys = null, PostgresPlatformKind platform = PostgresPlatformKind.SelfHosted) =>
        ReplicationService.Build(
            ReplicationViewTests.Facts(platform, standbys: standbys),
            names,
            names ? null : "Your account's write policy did not allow ViewDatabaseActivity for this database, so no standby or slot is named.",
            HealthHistoryView.Empty,
            PostgresStudioText.Instance);

    [Fact]
    public async Task The_sections_are_drawn_in_the_order_the_brief_gives_each_table_in_a_scroll_region()
    {
        (PostgresStudioComponentContext context, FakeReplicationService service) = await ContextAsync(View(names: true, [ReplicationViewTests.Standby()]));
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<ReplicationPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-repl-section").Should().HaveCount(7));

        page.Find(".ms-page-title").TextContent.Should().Be("Replication");
        page.FindAll(".ms-repl-heading").Select(static x => x.TextContent).Should().Equal(
            "Slots", "Standbys", "Archiver", "WAL", "Checkpoints", "I/O by backend type", "Tablespaces");
        page.FindAll("table.ms-table").Should().OnlyContain(static x => x.ParentElement != null && x.ParentElement.GetAttribute("role") == "region");

        service.Reads.Should().ContainSingle().Which.Should().Be(new StudioScope("default", "default", null));
        context.Health.CachedReads.Should().ContainSingle().Which.Should().BeEquivalentTo(["R01", "R02", "R03", "R04"]);
    }

    /// <summary>The value beside a term of the WAL section's definition list.</summary>
    private static string WalFact(IRenderedComponent<ReplicationPage> page, string term)
    {
        IElement section = page.Find("#ms-repl-wal").ParentElement!;
        IElement dt = section.QuerySelectorAll("dt").Single(x => x.TextContent == term);

        return dt.NextElementSibling!.TextContent.Trim();
    }

    [Fact]
    public async Task The_wal_section_says_the_writes_and_syncs_since_the_reset_and_what_they_took_when_the_server_timed_them()
    {
        ReplicationFacts facts = ReplicationViewTests.Facts() with
        {
            Wal = ReplicationRead<WalStatistics>.Of(new WalStatistics(1000, 250, 64e6, 0, 12_345, 678, 14.6, 4_049.8, ReplicationTestFacts.Now.AddDays(-3))),
        };

        (PostgresStudioComponentContext context, _) = await ContextAsync(
            ReplicationService.Build(facts, true, null, HealthHistoryView.Empty, PostgresStudioText.Instance));
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<ReplicationPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-repl-section").Should().HaveCount(7));

        string line = WalFact(page, "Writes and syncs");
        line.Should().StartWith("12,345 writes, 678 syncs since ").And.EndWith("; writing took 15 ms, syncing 4 s");
        WalFact(page, "Statistics since").Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Untimed_writes_and_syncs_are_counted_and_no_time_is_made_up()
    {
        // ReplicationViewTests.Facts(): 10 writes, 5 syncs, no timing and no reset time.
        (PostgresStudioComponentContext context, _) = await ContextAsync(View(names: true));
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<ReplicationPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-repl-section").Should().HaveCount(7));

        WalFact(page, "Writes and syncs").Should().Be("10 writes, 5 syncs");
    }

    [Fact]
    public async Task A_server_with_no_wal_counter_rows_draws_no_writes_and_syncs_line()
    {
        ReplicationFacts facts = ReplicationViewTests.Facts() with
        {
            Wal = ReplicationRead<WalStatistics>.Of(new WalStatistics(1000, 250, 64e6, 0, null, null, null, null, null)),
        };

        (PostgresStudioComponentContext context, _) = await ContextAsync(
            ReplicationService.Build(facts, true, null, HealthHistoryView.Empty, PostgresStudioText.Instance));
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<ReplicationPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-repl-section").Should().HaveCount(7));

        page.Find("#ms-repl-wal").ParentElement!.QuerySelectorAll("dt").Select(static x => x.TextContent).Should().NotContain("Writes and syncs");
    }

    [Fact]
    public async Task With_pg_read_all_stats_a_standbys_lag_is_drawn_and_its_name_through_the_activity_gate()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync(View(names: true, [ReplicationViewTests.Standby()]));
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<ReplicationPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-repl-standbys tbody tr").Should().ContainSingle());

        IElement row = page.Find(".ms-repl-standbys tbody tr");
        row.TextContent.Should().Contain("'reporting' (role replicator)").And.Contain("10.0.0.3").And.Contain("streaming").And.Contain("2.0 KiB");
        page.FindAll(".ms-repl-not-checked").Should().NotContain(static x => x.TextContent.Contains("pg_read_all_stats", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_pg_read_all_stats_the_page_says_not_checked_and_names_the_grant()
    {
        StandbyRow unreadable = new("walreceiver", null, "postgres", null, null, null, null, null, null, null);

        (PostgresStudioComponentContext context, _) = await ContextAsync(View(names: true, [unreadable]));
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<ReplicationPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-repl-standbys tbody tr").Should().ContainSingle());

        page.Find(".ms-repl-not-checked").TextContent.Should().StartWith(
            PostgresGrants.NotChecked(PostgresGrant.ReadAllStats, PostgresPlatform.SelfHosted, "app"));
        page.Find(".ms-repl-standbys tbody tr").TextContent.Should().Contain("not readable");
    }

    [Fact]
    public async Task Without_the_activity_gate_no_standby_or_slot_is_named()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync(View(names: false, [ReplicationViewTests.Standby()]));
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<ReplicationPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-repl-standbys tbody tr").Should().ContainSingle());

        page.Markup.Should().NotContain("reporting").And.NotContain("10.0.0.3").And.NotContain("replicator")
            .And.NotContain("standby1").And.NotContain("'cdc'").And.NotContain("billing").And.NotContain("/usr/local/bin/archive");
        page.Find(".ms-repl-standbys tbody tr").TextContent.Should().Contain("withheld").And.Contain("2.0 KiB", "the numbers are still shown");
        page.Find(".ms-repl-gate").TextContent.Should().Contain("did not allow ViewDatabaseActivity");
    }

    [Fact]
    public async Task With_the_capability_off_the_page_names_the_option_that_would_show_names()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync(View(names: false), capabilities: false);
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<ReplicationPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-repl-section").Should().HaveCount(7));

        page.Find(".ms-capability-disabled").TextContent.Should().Contain("PostgresStudioOptions.Capabilities.ViewDatabaseActivity");
    }

    [Fact]
    public async Task On_Aurora_five_sections_say_they_do_not_apply_and_the_slots_and_io_stay()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync(View(names: true, platform: PostgresPlatformKind.AmazonAurora));
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<ReplicationPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-repl-section").Should().HaveCount(7));

        page.FindAll(".ms-repl-unavailable").Select(static x => x.TextContent).Should().Equal(Enumerable.Repeat(ReplicationPlatform.Aurora, 5));
        page.FindAll(".ms-repl-slots tbody tr").Should().HaveCount(3);
        page.FindAll(".ms-repl-io tbody tr").Should().ContainSingle();
    }

    [Fact]
    public async Task A_refusal_is_said_and_nothing_else_is_drawn()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync(ReplicationView.Refused(ReplicationRefusal.StorePolicy, "Your account may not see this database's replication."));
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<ReplicationPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-repl-refusal").Should().ContainSingle());

        page.FindAll(".ms-repl-section").Should().BeEmpty();
        context.Health.CachedReads.Should().BeEmpty("a refused database shows no findings either");
    }

    [Fact]
    public async Task A_tablespace_is_measured_only_behind_its_button_and_a_denied_one_says_so()
    {
        (PostgresStudioComponentContext context, FakeReplicationService service) = await ContextAsync(View(names: false));
        await using PostgresStudioComponentContext owned = context;
        service.Size = new TablespaceSize("pg_global", null, "Not readable by this role: pg_tablespace_size needs CREATE on the tablespace.");

        var page = context.Render<ReplicationPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-repl-tablespaces tbody tr").Should().HaveCount(2));

        service.Measured.Should().BeEmpty("nothing is sized until asked");
        page.Markup.Should().NotContain("Not readable by this role");

        page.FindAll(".ms-repl-tablespaces tbody tr")[1].QuerySelector("button")!.Click();

        page.WaitForAssertion(() => page.Find(".ms-repl-measured[role=status]").TextContent.Should().Contain("pg_global").And.Contain("Not readable by this role"));
        service.Measured.Should().Equal("pg_global");
    }

    /// <summary>A replication service that answers what the test set, and records what it was asked.</summary>
    private sealed class FakeReplicationService(ReplicationView view) : IReplicationService
    {
        public List<StudioScope> Reads { get; } = [];

        public List<string> Measured { get; } = [];

        public TablespaceSize? Size { get; set; }

        public Task<ReplicationView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
        {
            Reads.Add(scope);
            return Task.FromResult(view);
        }

        public Task<TablespaceSize> MeasureTablespaceAsync(StudioScope scope, string tablespace, CancellationToken cancellationToken = default)
        {
            Measured.Add(tablespace);
            return Task.FromResult(Size ?? new TablespaceSize(tablespace, 1 << 20, null));
        }
    }
}
