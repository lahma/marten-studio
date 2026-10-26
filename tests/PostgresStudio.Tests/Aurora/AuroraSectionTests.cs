using Bunit;

using PostgresStudio.Components.Pages.Server;
using PostgresStudio.Components.Shared;
using PostgresStudio.Services.Aurora;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Server;
using PostgresStudio.Tests.Components;

namespace PostgresStudio.Tests.Aurora;

/// <summary>
/// The Server page's Aurora section and the "answered by" line (W5-AUR): the wait profile with <c>IO:XactSync</c>, the
/// instances, the commit latency, the sessions only through the activity gate, nothing off Aurora but a capacity, and every
/// table in a scroll region (D25).
/// </summary>
public class AuroraSectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static AuroraView View(bool sessions = false) => new()
    {
        IsAurora = true,
        MeasuredAt = Now,
        AnsweredBy = "instance studio-test-instance-1 (the writer), Aurora 16.4.1",
        StartedAt = Now.AddDays(-1),
        Capacity = new CapacityReading(PostgresComputeCapacity.AuroraServerlessV2(4.5, 0.5, 16, Now.AddMinutes(-1)), Now, null, true),
        Replicas = ServerRead<AuroraReplicaStatus>.Of(AuroraReplicaStatus.FromJson(Now,
        [
            """{"server_id": "studio-test-instance-1", "session_id": "MASTER_SESSION_ID"}""",
            """{"server_id": "studio-test-instance-2", "session_id": "u2", "replica_lag_in_msec": 13}""",
        ])),
        Waits = ServerRead<AuroraWaitsView>.Of(new AuroraWaitsView(
            [new AuroraWaitRow(new AuroraWaitEvent(10, 167772231, "IO", "XactSync", 187073, 597041642), null, null)], null, 2)),
        Database = ServerRead<AuroraDatabaseView>.Of(AuroraService.Database(
            AuroraDatabaseActivity.FromJson(Now, 11, "22011", """{"select_count": 178957, "select_latency_microsecs": 66684115}"""),
            HealthHistoryView.Empty,
            Now)),
        SessionsShown = sessions,
        SessionsWithheld = sessions ? null : "Each session's waits - its process id, role and application - need PostgresStudioOptions.Capabilities.ViewDatabaseActivity.",
        Sessions = sessions
            ? ServerRead<AuroraSessionWaits>.Of(new AuroraSessionWaits(Now,
                [new AuroraSession(3027, "app", "svcA", "active", Now, 4870, [new AuroraSessionWait("IO:XactSync", 4867, 0.98)])]))
            : null,
    };

    [Fact]
    public async Task On_Aurora_it_draws_the_instance_the_capacity_the_instances_the_waits_and_the_commits()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var section = context.Render<AuroraSection>(parameters => parameters.Add(static x => x.Model, View()));

        section.Find(".ms-aur-facts").TextContent.Should().Contain("instance studio-test-instance-1 (the writer), Aurora 16.4.1")
            .And.Contain("4.5 ACU (min 0.5, max 16), measured 11:59 UTC");
        section.Find(".ms-aur-instances").TextContent.Should().Contain("studio-test-instance-2").And.Contain("13 ms");
        section.Find(".ms-aur-waits").TextContent.Should().Contain("IO:XactSync").And.Contain("187,073");
        section.Markup.Should().Contain("since the instance started").And.Contain("2 events");
        section.Find(".ms-aur-dml").TextContent.Should().Contain("select").And.Contain("178,957");
        section.Markup.Should().Contain("Mean commit latency 2 ms since the statistics were reset");

        section.FindAll("table.ms-table").Should().HaveCount(3).And.OnlyContain(static x => x.ParentElement!.ClassList.Contains("ms-table-scroll"), "D25");
        section.FindAll(".ms-aur-sessions").Should().BeEmpty("the sessions are the activity gate's");
        section.Find(".ms-aur-withheld").TextContent.Should().Contain("ViewDatabaseActivity");
    }

    [Fact]
    public async Task Through_the_activity_gate_each_session_is_counts_and_shares()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var section = context.Render<AuroraSection>(parameters => parameters.Add(static x => x.Model, View(sessions: true)));

        string sessions = section.Find(".ms-aur-sessions").TextContent;
        sessions.Should().Contain("pid 3027, role app, 'svcA'").And.Contain("IO:XactSync 98% (4,867)");
        sessions.Should().NotContain(" ms").And.NotContain(" s ", "a session's waits are never a time");
    }

    [Fact]
    public async Task Off_Aurora_it_draws_a_capacity_reading_if_there_is_one_and_nothing_otherwise()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        context.Render<AuroraSection>(parameters => parameters.Add(static x => x.Model, new AuroraView())).Markup.Trim().Should().BeEmpty();

        var capacity = context.Render<AuroraSection>(parameters => parameters.Add(static x => x.Model, new AuroraView
        {
            Capacity = new CapacityReading(PostgresComputeCapacity.Of(2, 1, 4, "vCPU", Now), Now, null, true),
        }));

        capacity.Find("section").GetAttribute("aria-label").Should().Be("Compute capacity");
        capacity.Markup.Should().Contain("2 vCPU (min 1, max 4)");
    }

    [Fact]
    public async Task A_refusal_is_drawn_in_the_section()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var section = context.Render<AuroraSection>(parameters => parameters.Add(static x => x.Model, new AuroraView { Refused = "Your account may not see this database's server." }));

        section.Find(".ms-aur-unavailable").TextContent.Should().Be("Your account may not see this database's server.");
    }

    [Fact]
    public async Task The_answer_line_names_the_instance_and_the_capacity_and_draws_nothing_without_them()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var line = context.Render<ServerAnswer>(parameters => parameters.Add(static x => x.Model,
            new HealthServerLine("instance studio-test-instance-2 (a reader), Aurora 16.4.1", Now.AddDays(-1), "4.5 ACU (min 0.5, max 16), measured 11:59 UTC", null)));

        line.Find(".ms-aur-answer-instance").TextContent.Should().StartWith("Answered by instance studio-test-instance-2 (a reader), Aurora 16.4.1, up since");
        line.Find(".ms-aur-answer-capacity").TextContent.Should().Be("Capacity 4.5 ACU (min 0.5, max 16), measured 11:59 UTC.");

        var unknown = context.Render<ServerAnswer>(parameters => parameters.Add(static x => x.Model,
            new HealthServerLine(null, null, null, "capacity unknown this interval: the capacity source took longer than 5 s")));
        unknown.Find(".ms-aur-answer-unknown").TextContent.Should().Be("Capacity unknown this interval: the capacity source took longer than 5 s.");

        context.Render<ServerAnswer>(parameters => parameters.Add(static x => x.Model, null)).Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public async Task The_Health_page_says_which_instance_answered_and_once_that_another_one_did()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.Health.Report = context.Health.Report with
        {
            MeasuredAt = Now,
            Server = new HealthServerLine("instance studio-test-instance-2 (the writer), Aurora 16.4.1", Now.AddMinutes(-3), null, null),
            StatisticsReset = "The statistics look reset since the last check: another instance answered; its counters started at 2026-10-26 11:57 UTC.",
        };

        var page = context.Render<PostgresStudio.Components.Pages.Health.HealthPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-aur-answer").Should().ContainSingle());
        page.Find(".ms-aur-answer-instance").TextContent.Should().StartWith("Answered by instance studio-test-instance-2 (the writer), Aurora 16.4.1");
        page.Find(".ms-aur-reset").TextContent.Trim().Should().Be(
            "The statistics look reset since the last check: another instance answered; its counters started at 2026-10-26 11:57 UTC.");

        context.Health.Report = context.Health.Report with { Server = null, StatisticsReset = null };
        var plain = context.Render<PostgresStudio.Components.Pages.Health.HealthPage>();
        plain.WaitForAssertion(() => plain.FindAll(".ms-health-summary").Should().ContainSingle());
        plain.FindAll(".ms-aur-answer").Should().BeEmpty("a self-hosted server with no capacity source has nothing to say");
        plain.FindAll(".ms-aur-reset").Should().BeEmpty();
    }

    [Fact]
    public void The_line_comes_from_the_sweeps_shared_reads_and_is_none_off_Aurora_without_a_source()
    {
        PostgresPlatform aurora = new(PostgresPlatformKind.AmazonAurora, "marker");

        HealthSweep Sweep(PostgresServerFacts server, CapacityReading capacity) =>
            new("default", "default", HealthSweepKind.Regular, Now, TimeSpan.Zero, [])
            {
                Shared = new Dictionary<string, object> { ["server.platform"] = server, [CapacityReading.Key] = capacity },
            };

        HealthServerLine.Of(Sweep(new PostgresServerFacts(Now, 170004, "17.4", PostgresPlatform.SelfHosted), new CapacityReading(null, Now, null, false)))
            .Should().BeNull();

        HealthServerLine? line = HealthServerLine.Of(Sweep(
            new PostgresServerFacts(Now, 160004, "16.4", aurora) { Instance = "i-1", AuroraVersion = "16.4.1", StartedAt = Now.AddDays(-2) },
            new CapacityReading(null, Now, "the capacity source failed (InvalidOperationException)", true)));

        line.Should().Be(new HealthServerLine("instance i-1 (the writer), Aurora 16.4.1", Now.AddDays(-2), null,
            "capacity unknown this interval: the capacity source failed (InvalidOperationException)"));
    }
}
