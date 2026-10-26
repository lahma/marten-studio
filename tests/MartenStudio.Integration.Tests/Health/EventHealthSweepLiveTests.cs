using Marten;

using MartenStudio.Services.Health.Checks;

using Microsoft.Extensions.DependencyInjection;
using PostgresStudio.Services.Health;

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

    /// <summary>The daemon checks that read the event store's shape (D04 judges in process and reads none).</summary>
    private static readonly Type[] ShapeReadingDaemonChecks =
    [
        typeof(HighWaterPinnedCheck),
        typeof(DaemonNotRunningCheck),
        typeof(ShardStalledCheck),
        typeof(ShardFailedOrPausedCheck),
        typeof(DeadLettersGrowingCheck),
    ];

    /// <summary>
    /// CL1 item 6, the cost budget: a regular sweep that runs both areas' checks asks the catalog about the event store
    /// once. The daemon checks (D01-D03, D05, D06) and the event checks (E05-E08, D07, D08) used to read it in a statement
    /// each - H2's <c>exists</c> per object (<c>... as has_progression</c>) and H6's list of tables - memoised under two
    /// keys, so every such sweep paid for two. Now they share one statement and one fact.
    /// </summary>
    [PostgresFact]
    public async Task The_daemon_and_the_event_checks_share_one_shape_statement_per_sweep()
    {
        // Extended progression tracking off, as Marten ships it and as the budget below was measured: the sample turns it
        // on (DEMO), and then D05 also reads each shard's recorded status - a statement of D05's own, not the shape's.
        (EventHealthHost host, StatementLog probe) = await HostAsync(
            "health_events_one_shape",
            [.. ShapeReadingDaemonChecks, .. EventChecks],
            static options => options.Events.EnableExtendedProgressionTracking = false);
        await using EventHealthHost _ = host;

        HealthReport regular = await host.ReportAsync();

        regular.Checks.Where(static x => x.Code is "D01" or "D03" or "D05" or "D06" or "D07" or "D08" or "E05" or "E06" or "E07")
            .Should().HaveCount(9).And.OnlyContain(static x => x.Outcome != HealthCheckOutcome.Failed, "every reader of the shape ran");

        List<string> shapes = [.. probe.Statements.Where(IsShape)];
        shapes.Should().ContainSingle("both areas read one shape statement, once per sweep");
        shapes[0].Should().Contain("c.relkind = 'S'", "it answers the daemon checks' question about the sequence as well");
        probe.Statements.Should().NotContain(static x => x.Contains("as has_progression", StringComparison.Ordinal),
            "the daemon checks' own shape statement is gone");

        // The budget, as a number: eight statements. The two shape reads made it ten (measured at 9.39.0 and 9.50.2), one
        // shape made it nine, and since W5-G1M the registered shards' rows are one read per sweep too: D02 and D03 asked for
        // them in a statement each, which with extended tracking off is the same statement twice.
        probe.Statements.Should().HaveCount(8,
            "one sweep of these checks over the sample store runs eight statements, one of them the shape and one the shard rows "
            + "D02 and D03 share; it was ten. Ran: "
            + string.Join(" || ", probe.Statements.Select(static x => x[..Math.Min(60, x.Length)])));

        // The one read is every reader's: the memo attributes it to each check that asked, so each one's Show SQL has it.
        foreach (string code in (string[]) ["D01", "D03", "D06"])
        {
            HealthCheckStatus status = EventHealthHost.Status(regular, code);
            status.Outcome.Should().Be(HealthCheckOutcome.Checked, "{0} runs on the sample store: {1}", code, status.Problem);
            status.Statements.Select(static x => x.Sql).Any(IsShape).Should().BeTrue("{0} read the shared shape", code);
        }

        static bool IsShape(string sql) =>
            sql.Contains("pg_catalog.pg_inherits", StringComparison.Ordinal) && sql.Contains("as nspname", StringComparison.Ordinal);
    }

    private Task<(EventHealthHost Host, StatementLog Probe)> HostAsync(string schema) => HostAsync(schema, EventChecks);

    private async Task<(EventHealthHost Host, StatementLog Probe)> HostAsync(string schema, Type[] checks, Action<StoreOptions>? store = null)
    {
        StatementLog probe = new(new HealthSweepProbe(postgres));

        EventHealthHost host = await EventHealthHost.CreateAsync(
            postgres,
            schema,
            store: options =>
            {
                options.Projections.LiveStreamAggregation<HealthCounter>();
                store?.Invoke(options);
            },
            services: services => services
                .AddSingleton<IHealthStatementObserver>(probe)
                .AddSingleton(HealthCheckRegistry.ForTypes(checks)));

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
