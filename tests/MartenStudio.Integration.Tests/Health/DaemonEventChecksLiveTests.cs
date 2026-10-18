using JasperFx.Events.Daemon;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health;

using Microsoft.Extensions.DependencyInjection;

using Testcontainers.PostgreSql;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// A Postgres server of <see cref="DaemonEventChecksLiveTests" />' own, for the one condition the shared server cannot
/// hold: that no transaction anywhere on it began before a gap was seen and is still open. Never reused.
/// </summary>
public sealed class GapSkipServerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithReuse(false)
        .Build();

    private bool started;

    /// <summary>The server's superuser connection string.</summary>
    public string ConnectionString => started
        ? container.GetConnectionString()
        : throw new InvalidOperationException("The gap-skip server is not running; a test that needs it must be a [PostgresFact].");

    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await container.StartAsync();
        started = true;
    }

    public async ValueTask DisposeAsync() => await container.DisposeAsync();
}

/// <summary>
/// D07 and D08 live: a real skip made by a real daemon, a configuration that turns it into a warning, a shard
/// position that falls between two sweeps, a progression row that says it is rebuilding - and the healthy sample
/// store, quiet.
/// </summary>
public class DaemonEventChecksLiveTests(PostgresFixture postgres, GapSkipServerFixture gapServer) : IClassFixture<GapSkipServerFixture>
{
    // ---------------------------------------------------------------------------------------------------
    // D07
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A number drawn from the event sequence and never used is a permanent gap; a daemon started over it holds the
    /// mark for <c>StaleSequenceThreshold</c>, sees nothing alive that could fill it, and skips it - which advanced
    /// async tracking records.
    /// </summary>
    /// <remarks>
    /// <b>On a server of this class's own.</b> "Nothing alive that could fill it" is Marten's
    /// <c>GapLivenessProbe</c>, and it is a question about the whole server: it holds the gap while any client
    /// transaction in the database began before the gap was first seen, or while any write transaction anywhere on the
    /// server - <c>pg_snapshot_xip(pg_current_snapshot())</c> - is older than the snapshot it took then. A daemon started
    /// over a gap it never saw open has no allocation fence, so neither clause excludes anyone. On the shared server, a
    /// full run always has some other class's long transaction open - a 300,000-row insert, a transaction held idle for a
    /// minute on purpose - and the detector rightly waited behind it, past this test's minute. Alone it skips in seconds.
    /// </remarks>
    [PostgresFact]
    [HealthFires("D07")]
    public async Task A_gap_the_daemon_skipped_is_D07_and_an_info_under_the_defaults()
    {
        string server = gapServer.ConnectionString;

        await using EventHealthHost host = await EventHealthHost.CreateAsync(
            server,
            "health_d07_fires",
            store: static options => options.Projections.StaleSequenceThreshold = TimeSpan.FromSeconds(1));

        await EventHealthData.AppendAsync(host.Store, streams: 5, eventsPerStream: 2);
        await LiveWait.ScalarAsync<long>(server, $"select nextval('{SqlIdentifier.Qualify(host.EventSchema, "mt_events_sequence")}')");
        await EventHealthData.AppendAsync(host.Store, streams: 5, eventsPerStream: 2);

        // A daemon of the test's own - the studio never starts one (hard rule 11).
        using (IProjectionDaemon daemon = await host.Store.BuildProjectionDaemonAsync())
        {
            await daemon.StartAllAsync();

            string skips = $"select count(*) from {SqlIdentifier.Qualify(host.EventSchema, HealthEventQueries.HighWaterSkipsTable)}";
            await EventHealthData.WaitForAsync(
                async () => await LiveWait.ScalarAsync<long>(server, skips) > 0,
                "the high-water detector to skip the gap and record it");

            await daemon.StopAllAsync();
        }

        HealthReport report = await host.ReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "D07").Subject;
        finding.Severity.Should().Be(HealthSeverity.Info, "with the transaction evidence on, the skipped number was one nobody alive could fill");
        finding.Evidence.Should().Contain(static x => x.Label == "Skips in the last hour" && x.Value != "0");
        finding.Evidence.Should().Contain(static x => x.Label == "Latest skip" && x.Value.Contains(" to #21 ", StringComparison.Ordinal),
            "the detector moved the mark past the drawn number 11 to the last committed event, #21");
    }

    /// <summary>A store that skips on the clock alone: the same recorded skip is a warning, with the line that stops it.</summary>
    [PostgresFact]
    [HealthFires("D07")]
    public async Task With_the_transaction_evidence_off_a_recent_skip_is_a_warning()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(
            postgres,
            "health_d07_clock",
            store: static options => options.Projections.UseTransactionEvidenceForGapSkipping = false);

        // The row the detector writes when it skips (ending, starting, transaction_timestamp()).
        await EventHealthData.ExecuteAsync(
            postgres,
            $"insert into {SqlIdentifier.Qualify(host.EventSchema, HealthEventQueries.HighWaterSkipsTable)} (ending_sequence, starting_sequence) values (40, 20)");

        HealthReport report = await host.ReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "D07").Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Remedy!.Code.Should().Be("opts.Projections.UseTransactionEvidenceForGapSkipping = true;");
    }

    /// <summary>
    /// The sample's data run through by a real daemon to its end: the detector moved the mark over every event and
    /// found no gap to skip, so the table it writes skips to is empty.
    /// </summary>
    [PostgresFact]
    [HealthQuiet("D07")]
    public async Task The_healthy_sample_store_has_skipped_nothing()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_d07_quiet");
        await EventHealthData.GenerateSampleAsync(host.Store);

        using (IProjectionDaemon daemon = await host.Store.BuildProjectionDaemonAsync())
        {
            await daemon.StartAllAsync();
            await daemon.WaitForNonStaleData(TimeSpan.FromSeconds(60));
            await daemon.StopAllAsync();
        }

        (await EventHealthData.ScalarAsync(postgres, $"select last_seq_id from {SqlIdentifier.Qualify(host.EventSchema, HealthEventQueries.ProgressionTable)} where name = 'HighWaterMark'"))
            .Should().Be(
                await EventHealthData.ScalarAsync(postgres, $"select max(seq_id) from {SqlIdentifier.Qualify(host.EventSchema, HealthEventQueries.EventsTable)}"),
                "the precondition: the daemon ran, and its mark reached the last event");

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "D07");
        HealthCheckStatus status = EventHealthHost.Status(report, "D07");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.Note.Should().Contain("never skipped");
    }

    [PostgresFact]
    public async Task Without_advanced_tracking_there_is_nothing_to_read_and_D07_says_so()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(
            postgres,
            "health_d07_untracked",
            store: static options => options.Events.EnableAdvancedAsyncTracking = false);

        HealthReport report = await host.ReportAsync();

        HealthCheckStatus status = EventHealthHost.Status(report, "D07");
        status.Outcome.Should().Be(HealthCheckOutcome.Unavailable);
        status.Problem.Should().Contain("K03");
        host.Logs.WarningsOrWorse.Should().NotContain(static x => x.Message.Contains("D07", StringComparison.Ordinal), "an expected state is a value");
    }

    // ---------------------------------------------------------------------------------------------------
    // D08
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A shard's progression row deleted between two sweeps - what a rebuild's teardown does - by something that is
    /// not this studio.
    /// </summary>
    [PostgresFact]
    [HealthFires("D08")]
    public async Task A_shard_that_goes_back_between_two_sweeps_with_nothing_here_doing_it_is_D08()
    {
        SteppedClock clock = new();
        await using EventHealthHost host = await EventHealthHost.CreateAsync(
            postgres, "health_d08_fires", services: services => services.AddSingleton<TimeProvider>(clock));
        string progression = SqlIdentifier.Qualify(host.EventSchema, HealthEventQueries.ProgressionTable);

        await EventHealthData.ExecuteAsync(
            postgres,
            $"insert into {progression} (name, last_seq_id) values ('HighWaterMark', 500), ('DailySales:All', 500)");

        HealthSweep first = await host.SweepAsync();
        EventHealthHost.Result(first, "D08").Findings.Should().BeEmpty("one sample shows no fall");

        await EventHealthData.ExecuteAsync(postgres, $"delete from {progression} where name = 'DailySales:All'");

        clock.Advance(TimeSpan.FromMinutes(1));
        HealthSweep second = await host.SweepAsync();

        HealthFinding finding = EventHealthHost.Result(second, "D08").Findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Subject.Should().Be("DailySales:All");
        finding.Evidence.Should().Contain(new HealthEvidence("Position now", "no progression row"));
        finding.Evidence.Should().Contain(new HealthEvidence("High-water mark", "#500"));
    }

    /// <summary>
    /// The <c>mode</c> column exists at the floor and at the latest Marten (#5309), and no Marten writes it; a row
    /// whose mode a host's own tooling set to <c>rebuilding</c> is believed.
    /// </summary>
    [PostgresFact]
    [HealthFires("D08")]
    public async Task A_row_marked_rebuilding_with_nothing_here_rebuilding_it_is_D08()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_d08_mode");
        string progression = SqlIdentifier.Qualify(host.EventSchema, HealthEventQueries.ProgressionTable);

        await EventHealthData.ExecuteAsync(
            postgres,
            $"insert into {progression} (name, last_seq_id) values ('HighWaterMark', 500); "
            + $"insert into {progression} (name, last_seq_id, mode) values ('ShipmentTracker:All', 10, 'rebuilding')");

        HealthSweep sweep = await host.SweepAsync();

        HealthFinding finding = EventHealthHost.Result(sweep, "D08").Findings.Should().ContainSingle().Subject;
        finding.Title.Should().Contain("marked as rebuilding");
        finding.Evidence.Should().Contain(new HealthEvidence("mode", "rebuilding"));
    }

    [PostgresFact]
    public async Task Marten_creates_the_mode_column_with_none_as_its_default()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_d08_default");
        string progression = SqlIdentifier.Qualify(host.EventSchema, HealthEventQueries.ProgressionTable);

        await EventHealthData.ExecuteAsync(postgres, $"insert into {progression} (name, last_seq_id) values ('DailySales:All', 1)");

        (await EventHealthData.ScalarAsync(postgres, $"select count(*) from {progression} where mode = 'none'"))
            .Should().Be(1, "ShardMode.none is the column's default at every Marten since 9.31, which is all a reader ever sees");
    }

    /// <summary>
    /// The review's case: a shard seen to fall, rebuilt by a real daemon past where it stood, and then a little behind
    /// the mark again while appends go on - as every shard under steady appends sits. Back at or past its pre-fall
    /// position, whatever replayed it is done: quiet, though the shard is below the mark.
    /// </summary>
    [PostgresFact]
    [HealthQuiet("D08")]
    public async Task A_shard_rebuilt_past_where_it_stood_is_quiet_while_appends_keep_it_behind_the_mark()
    {
        const string Shard = "DailySales:All";
        SteppedClock clock = new();
        await using EventHealthHost host = await EventHealthHost.CreateAsync(
            postgres, "health_d08_recovered", services: services => services.AddSingleton<TimeProvider>(clock));
        string progression = SqlIdentifier.Qualify(host.EventSchema, HealthEventQueries.ProgressionTable);
        string position = $"select coalesce((select last_seq_id from {progression} where name = '{Shard}'), 0)";
        string mark = $"select coalesce((select last_seq_id from {progression} where name = 'HighWaterMark'), 0)";

        await EventHealthData.GenerateSampleAsync(host.Store, streams: 100);

        // A daemon of the test's own, so nothing in the studio knows it is rebuilding (hard rule 11).
        using IProjectionDaemon daemon = await host.Store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await daemon.WaitForNonStaleData(TimeSpan.FromSeconds(60));

        await host.SweepAsync();
        long before = await EventHealthData.ScalarAsync(postgres, position);
        before.Should().BePositive("the precondition: the shard had a position to fall from");

        // The fall, seen by a sweep: the shard stopped and its row gone, which is what a rebuild's teardown leaves.
        await daemon.StopAgentAsync(Shard);
        await EventHealthData.ExecuteAsync(postgres, $"delete from {progression} where name = '{Shard}'");
        clock.Advance(TimeSpan.FromMinutes(1));
        HealthSweep fallen = await host.SweepAsync();
        EventHealthHost.Result(fallen, "D08").Findings.Should().ContainSingle("the precondition: the fall was seen, and nothing here explains it");

        // Rebuilt by the daemon, then held behind the mark while more is appended.
        await daemon.RebuildProjectionAsync("DailySales", TestContext.Current.CancellationToken);
        await daemon.StopAgentAsync(Shard);
        await EventHealthData.AppendAsync(host.Store, streams: 20, eventsPerStream: 3);
        await EventHealthData.WaitForAsync(
            async () => await EventHealthData.ScalarAsync(postgres, mark) > await EventHealthData.ScalarAsync(postgres, position),
            "the high-water mark to move past the held shard");

        long after = await EventHealthData.ScalarAsync(postgres, position);
        after.Should().BeGreaterThanOrEqualTo(before, "the precondition: the rebuild took the shard back to where it stood");

        clock.Advance(TimeSpan.FromMinutes(1));
        HealthSweep recovered = await host.SweepAsync();

        HealthCheckResult result = EventHealthHost.Result(recovered, "D08");
        result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
        result.Findings.Should().BeEmpty("the shard is back past its pre-fall position; the mark alone would call it still replaying");

        await daemon.StopAllAsync();
    }

    [PostgresFact]
    [HealthQuiet("D08")]
    public async Task The_healthy_sample_store_is_quiet_across_two_sweeps()
    {
        SteppedClock clock = new();
        await using EventHealthHost host = await EventHealthHost.CreateAsync(
            postgres, "health_d08_quiet", services: services => services.AddSingleton<TimeProvider>(clock));
        await EventHealthData.GenerateSampleAsync(host.Store);

        await host.SweepAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        HealthSweep second = await host.SweepAsync();

        HealthCheckResult result = EventHealthHost.Result(second, "D08");
        result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
        result.Findings.Should().BeEmpty();
    }
}
