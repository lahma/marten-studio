using JasperFx.Events;
using JasperFx.Events.Projections;

using Marten;
using Marten.Events.Projections;

using MartenStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// E05, E06 and E07 live: each condition produced in a real store, sampled by a real <c>ANALYZE</c> and found at the
/// regular interval with no scan; each quiet on the healthy sample store.
/// </summary>
public class EventStatisticsLiveTests(PostgresFixture postgres)
{
    // ---------------------------------------------------------------------------------------------------
    // E05
    // ---------------------------------------------------------------------------------------------------

    private const string GhostType = "Ghost.GhostEvent, Ghost.Assembly";

    /// <summary>
    /// Events another service wrote, of a type this one has never heard of: an alias nothing maps and, stored beside
    /// it, the .NET type name that alias came from, which nothing here can load - the shape of a type that was renamed
    /// or deleted. Every projection the sample registers filters by its own event types, so no rebuild here would
    /// meet them: an Info.
    /// </summary>
    [PostgresFact]
    [HealthFires("E05")]
    public async Task Events_of_a_type_this_process_cannot_load_are_E05()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_e05_fires");
        await AppendGhostsAsync(host);

        HealthReport report = await host.ReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "E05").Subject;
        finding.Severity.Should().Be(HealthSeverity.Info, "the sample's three projections each filter by the event types they handle");
        finding.Finding.Title.Should().EndWith("though nothing registered here reads every event type");
        finding.Evidence.Should().Contain(new HealthEvidence("Aliases whose stored .NET type does not load here", "ghost_event (" + GhostType + ")"));
        finding.Evidence.Should().Contain(static x => x.Label == "Projections and subscriptions here that read every event type" && x.Value.StartsWith("none", StringComparison.Ordinal));
    }

    /// <summary>The same events under a projection that reads every type: its rebuild would stop on them.</summary>
    [PostgresFact]
    [HealthFires("E05")]
    public async Task Under_a_projection_that_reads_every_type_E05_is_a_warning_that_a_rebuild_will_stop()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(
            postgres,
            "health_e05_reader",
            store: static o => o.Projections.Add(new EveryEventCounter(), ProjectionLifecycle.Async));
        await AppendGhostsAsync(host);

        HealthReport report = await host.ReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "E05").Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning, "the sample's rebuilds skip nothing, and this projection reads every event");
        finding.Finding.Title.Should().EndWith(", so a rebuild will stop on them");
        finding.Evidence.Should().Contain(static x =>
            x.Label == "Projections and subscriptions here that read every event type"
            && x.Value.Contains(nameof(EveryEventCounter), StringComparison.Ordinal)
            && x.Value.Contains("(async projection)", StringComparison.Ordinal));
    }

    private async Task AppendGhostsAsync(EventHealthHost host)
    {
        await EventHealthData.AppendAsync(host.Store, streams: 100, eventsPerStream: 3);
        await EventHealthData.ExecuteAsync(
            postgres,
            $"update \"{host.EventSchema}\".mt_events set type = 'ghost_event', mt_dotnet_type = '{GhostType}' where version = 3");
        await EventHealthData.AnalyzeAsync(postgres, host.EventSchema);
    }

    /// <summary>An event projection written as one <c>ApplyAsync</c> override: no event-type filter, so it is handed every event.</summary>
    private sealed class EveryEventCounter : EventProjection
    {
        public override ValueTask ApplyAsync(IDocumentOperations operations, IEvent e, CancellationToken cancellation) => ValueTask.CompletedTask;
    }

    [PostgresFact]
    [HealthQuiet("E05")]
    public async Task The_healthy_sample_store_reads_every_type_it_holds()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_e05_quiet");
        await EventHealthData.GenerateSampleAsync(host.Store);
        await EventHealthData.AnalyzeAsync(postgres, host.EventSchema);

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "E05");
        HealthCheckStatus status = EventHealthHost.Status(report, "E05");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
    }

    /// <summary>A table nobody has analysed yet is an expected state - not checked, and said so - and logs no warning.</summary>
    [PostgresFact]
    public async Task An_unanalysed_event_table_is_not_checked_rather_than_failed()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_e05_unanalysed");

        HealthReport report = await host.ReportAsync();

        HealthCheckStatus status = EventHealthHost.Status(report, "E05");
        status.Outcome.Should().Be(HealthCheckOutcome.Unavailable, status.Problem);
        status.Problem.Should().Contain("has not been analysed");
        host.Logs.WarningsOrWorse.Should().NotContain(static x => x.Message.Contains("E05", StringComparison.Ordinal), "an expected state is a value");
    }

    // ---------------------------------------------------------------------------------------------------
    // E06
    // ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("E06")]
    public async Task A_live_only_aggregate_and_long_streams_are_E06()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(
            postgres,
            "health_e06_fires",
            store: static options => options.Projections.LiveStreamAggregation<HealthCounter>());

        await EventHealthData.AppendAsync(host.Store, streams: 60, eventsPerStream: 3);
        await EventHealthData.AppendAsync(host.Store, streams: 5, eventsPerStream: 1_200);
        await EventHealthData.AnalyzeAsync(postgres, host.EventSchema);

        HealthReport report = await host.ReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "E06").Subject;
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Finding.Remedy!.Code.Should().Be("opts.Projections.Snapshot<HealthCounter>(SnapshotLifecycle.Inline);");
        finding.Evidence.Should().Contain(new HealthEvidence("Longest stream the statistics saw", "1,200 events"));
    }

    [PostgresFact]
    [HealthQuiet("E06")]
    public async Task The_healthy_sample_store_snapshots_its_aggregate()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_e06_quiet");
        await EventHealthData.GenerateSampleAsync(host.Store);

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "E06");
        HealthCheckStatus status = EventHealthHost.Status(report, "E06");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.Note.Should().Contain("none is replayed live", "OrderSummary is an inline projection");
    }

    // ---------------------------------------------------------------------------------------------------
    // E07
    // ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("E07")]
    public async Task A_mostly_archived_store_that_is_not_partitioned_is_E07()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_e07_fires");
        await EventHealthData.InsertSyntheticAsync(postgres, host.EventSchema, events: 120_000, archivedShare: 0.7);
        await EventHealthData.AnalyzeAsync(postgres, host.EventSchema);

        HealthReport report = await host.ReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "E07").Subject;
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Finding.Remedy!.Code.Should().Be("opts.Events.UseArchivedStreamPartitioning = true;");
        finding.Finding.Title.Should().MatchRegex("^(6|7|8)[0-9](\\.[0-9])?% of the events", "ANALYZE samples, so the share is close to 70%, not exactly it");
    }

    [PostgresFact]
    [HealthQuiet("E07")]
    public async Task The_healthy_sample_store_is_mostly_live()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_e07_quiet");
        await EventHealthData.GenerateSampleAsync(host.Store);
        await EventHealthData.AnalyzeAsync(postgres, host.EventSchema);

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "E07");
        EventHealthHost.Status(report, "E07").Outcome.Should().Be(HealthCheckOutcome.Checked, EventHealthHost.Status(report, "E07").Problem);
    }

    /// <summary>A store that already partitions by the archive flag is answered by its configuration, with no statement.</summary>
    [PostgresFact]
    [HealthQuiet("E07")]
    public async Task A_partitioned_store_is_quiet_and_reads_nothing()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(
            postgres,
            "health_e07_partitioned",
            store: static options => options.Events.UseArchivedStreamPartitioning = true);

        HealthReport report = await host.ReportAsync();

        HealthCheckStatus status = EventHealthHost.Status(report, "E07");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.Statements.Should().BeEmpty();
        status.Note.Should().Contain("UseArchivedStreamPartitioning is on");
    }
}
