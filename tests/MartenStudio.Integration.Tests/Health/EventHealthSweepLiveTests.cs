using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// Acceptance 2 and 4 for the event checks: the traffic profile is one statement per deeper sweep and none per
/// regular one, and a regular and a deeper sweep over a store with data run no DDL and hold no lock beyond the
/// <c>AccessShareLock</c> a read takes on the event store's own tables - measured with H1's
/// <see cref="HealthSweepProbe" />.
/// </summary>
public class EventHealthSweepLiveTests(PostgresFixture postgres)
{
    /// <summary>What every statement of the profile, and nothing else, contains.</summary>
    private const string ProfileSignature = "win as materialized";

    /// <summary>The event checks, and only them, so the measurement is theirs.</summary>
    private static readonly Type[] EventChecks =
    [
        typeof(SkippedGapsCheck),
        typeof(RebuiltElsewhereCheck),
        typeof(TombstonesCheck),
        typeof(HotStreamCheck),
        typeof(LargeEventPayloadsCheck),
        typeof(UnregisteredEventTypesCheck),
        typeof(LongStreamsWithoutSnapshotCheck),
        typeof(ArchivedShareCheck),
        typeof(DcbTagVersionChurnCheck),
    ];

    private async Task<(EventHealthHost Host, StatementLog Probe)> HostAsync(string schema)
    {
        StatementLog probe = new(new HealthSweepProbe(postgres));

        EventHealthHost host = await EventHealthHost.CreateAsync(
            postgres,
            schema,
            store: static options => options.Projections.LiveStreamAggregation<HealthCounter>(),
            services: services => services
                .AddSingleton<IHealthStatementObserver>(probe)
                .AddSingleton(HealthCheckRegistry.ForTypes(EventChecks)));

        await EventHealthData.GenerateSampleAsync(host.Store);
        await EventHealthData.AppendAsync(host.Store, streams: 3, eventsPerStream: 1_100);
        await EventHealthData.AnalyzeAsync(postgres, host.EventSchema);

        return (host, probe);
    }

    [PostgresFact]
    public async Task The_traffic_profile_runs_once_per_deeper_sweep_and_never_in_a_regular_one()
    {
        (EventHealthHost host, StatementLog probe) = await HostAsync("health_events_profile");
        await using EventHealthHost _ = host;

        HealthReport regular = await host.ReportAsync();

        regular.Checks.Select(static x => x.Code).Should().NotContain(["E02", "E03", "E04"], "they are tier 2");
        probe.Statements.Should().NotContain(static x => x.Contains(ProfileSignature, StringComparison.Ordinal),
            "a regular sweep never reads the window");

        HealthReport deeper = await host.DeeperReportAsync();

        deeper.Checks.Where(static x => x.Code is "E02" or "E03" or "E04")
            .Should().HaveCount(3).And.OnlyContain(static x => x.Outcome == HealthCheckOutcome.Checked);
        probe.Statements.Count(static x => x.Contains(ProfileSignature, StringComparison.Ordinal))
            .Should().Be(1, "three checks share one bounded read of the window");

        await host.DeeperReportAsync();
        probe.Statements.Count(static x => x.Contains(ProfileSignature, StringComparison.Ordinal))
            .Should().Be(1, "the deeper sweep is cached for five minutes");
    }

    [PostgresFact]
    public async Task A_regular_and_a_deeper_sweep_create_nothing_and_lock_only_what_they_read()
    {
        (EventHealthHost host, StatementLog probe) = await HostAsync("health_events_noddl");
        await using EventHealthHost _ = host;

        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(postgres, host.Schema, host.EventSchema);

        HealthReport regular = await host.ReportAsync();
        HealthReport deeper = await host.DeeperReportAsync();

        regular.Checks.Concat(deeper.Checks).Should().OnlyContain(
            static x => x.Outcome != HealthCheckOutcome.Failed, "every check ran against a real store");
        probe.Locks.StatementsObserved.Should().BePositive("so 'no lock' is not 'nothing ran'");

        probe.Locks.BeyondCatalogReads.Should().OnlyContain(
            x => x.Mode == "AccessShareLock" && x.LockType == "relation" && x.Schema == host.EventSchema,
            "an event check reads the event store's own tables and takes only the lock a read takes on them");
        probe.Locks.BeyondCatalogReads.Select(static x => x.Relation).Should().Contain("mt_events", "the profile read the window");

        IReadOnlySet<string> after = await HealthSweepProbe.SnapshotAsync(postgres, host.Schema, host.EventSchema);
        after.Should().BeEquivalentTo(before, "a sweep never runs DDL");
    }
}

/// <summary>
/// <see cref="HealthSweepProbe" />, plus the text of every statement it is told about - the sweeper takes one
/// observer, and these tests need both what was locked and what ran.
/// </summary>
/// <param name="locks">The probe that reads pg_locks for each statement.</param>
internal sealed class StatementLog(HealthSweepProbe locks) : IHealthStatementObserver
{
    private readonly List<string> statements = [];
    private readonly Lock gate = new();

    /// <summary>The lock probe.</summary>
    public HealthSweepProbe Locks { get; } = locks;

    /// <summary>Every statement observed, in order.</summary>
    public IReadOnlyList<string> Statements
    {
        get
        {
            lock (gate)
            {
                return [.. statements];
            }
        }
    }

    /// <inheritdoc />
    public async Task ObserveAsync(int backendProcessId, HealthStatement statement, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            statements.Add(statement.Sql);
        }

        await Locks.ObserveAsync(backendProcessId, statement, cancellationToken);
    }
}
