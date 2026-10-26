using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>
/// W5-API's per-database bound (API design §3.5, the orchestrator's decision 5): the least recently used database past the
/// bound loses its history ring, its sweep-cache slots and its statement texts together, and a database a page keeps
/// reading is never the one that goes.
/// </summary>
public class HealthDatabaseBoundTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_least_recently_used_database_past_the_capacity_is_forgotten_by_every_participant()
    {
        HealthDatabaseBound bound = new(capacity: 2);
        HealthHistory history = new(bound);
        StatementTextCache texts = new(bound);
        Recorder recorder = new();
        bound.Register(recorder);

        foreach (string database in new[] { "a", "b" })
        {
            bound.Touch("tenants", database);
            history.Append("tenants", database, Sample(1));
            history.Append("tenants", HealthHistory.RingOf(database, HealthSweepKind.Statements), Sample(1));
            texts.Remember("tenants", database, [new StatementTextRow(1, "select 1", false)]);
        }

        bound.Touch("tenants", "a");
        bound.Touch("tenants", "c");

        recorder.Forgotten.Should().ContainSingle().Which.Should().Be("tenants|b", "b was used least recently: a was touched again after it");
        history.Read("tenants", "b").Samples.Should().BeEmpty();
        history.Read("tenants", HealthHistory.RingOf("b", HealthSweepKind.Statements)).Samples.Should().BeEmpty("the statement history goes with it");
        texts.Known("tenants", "b").Should().BeEmpty();

        history.Read("tenants", "a").Samples.Should().ContainSingle("a database still in use keeps its history");
        texts.Known("tenants", "a").Should().Equal(1);
        bound.Count.Should().Be(2);
    }

    [Fact]
    public void Touching_a_known_database_evicts_nothing()
    {
        HealthDatabaseBound bound = new(capacity: 2);
        Recorder recorder = new();
        bound.Register(recorder);

        bound.Touch("default", "a");
        bound.Touch("default", "b");
        bound.Touch("DEFAULT", "A");
        bound.Touch("default", "b");

        recorder.Forgotten.Should().BeEmpty("a key matches ignoring case, as every per-database cache does");
    }

    [Fact]
    public async Task A_sweep_touches_its_database_and_an_evicted_one_loses_its_slots_and_starts_its_history_again()
    {
        await using HealthHarness harness = HealthHarness.Create(before: static services => services.AddSingleton(new HealthDatabaseBound(capacity: 1)));
        harness.Script.Finds("K90", HealthSeverity.Warning);
        HealthDatabaseBound bound = harness.Services.GetRequiredService<HealthDatabaseBound>();

        HealthSweep sweep = await harness.Cache.GetAsync(harness.Resolved, HealthSweepKind.Regular, null, TestContext.Current.CancellationToken);

        harness.Cache.Latest(sweep.StoreKey, sweep.DatabaseId).Should().BeSameAs(sweep);
        harness.History.Read(sweep.StoreKey, sweep.DatabaseId).Since.Should().NotBeNull("the sweep marked when watching began");

        // Another database is used - a page, the export's rotation, a host's loop over the diagnostics API.
        bound.Touch("tenants", "acme");

        harness.Cache.Latest(sweep.StoreKey, sweep.DatabaseId).Should().BeNull("its finished slot went with it");
        harness.Cache.SlotCount.Should().Be(0);
        harness.History.Read(sweep.StoreKey, sweep.DatabaseId).Since.Should().BeNull("and its history: the page says it has only just started watching");

        int before = harness.Cache.SweepCount;
        await harness.Cache.GetAsync(harness.Resolved, HealthSweepKind.Regular, null, TestContext.Current.CancellationToken);
        harness.Cache.SweepCount.Should().Be(before + 1, "read again, it is swept again");
    }

    /// <summary>
    /// W5-API-fix F7: a database evicted while its own sweep was gathering - other databases used in the meantime - is touched
    /// again before the sweep writes its history and its slot, so what the sweep writes is tracked and is forgotten with it the
    /// next time it is the least recently used, rather than kept for ever outside the bound.
    /// </summary>
    [Fact]
    public async Task A_database_evicted_mid_sweep_is_tracked_again_when_its_sweep_writes()
    {
        await using HealthHarness harness = HealthHarness.Create(before: static services => services.AddSingleton(new HealthDatabaseBound(capacity: 1)));
        HealthDatabaseBound bound = harness.Services.GetRequiredService<HealthDatabaseBound>();

        // While the sweep gathers, another database is used, which evicts this one.
        harness.Script.Gather["T90"] = (_, _) =>
        {
            bound.Touch("tenants", "acme");
            return Task.FromResult(new ScriptedFacts("T90", [], null));
        };

        HealthSweep sweep = await harness.Cache.GetAsync(harness.Resolved, HealthSweepKind.Regular, null, TestContext.Current.CancellationToken);

        harness.History.Read(sweep.StoreKey, sweep.DatabaseId).Since.Should().NotBeNull("the sweep wrote its history");
        harness.Cache.Latest(sweep.StoreKey, sweep.DatabaseId).Should().BeSameAs(sweep);
        bound.Count.Should().Be(1);

        // The next database used evicts this one - which it can only do if the bound tracks it again.
        bound.Touch("tenants", "globex");

        harness.History.Read(sweep.StoreKey, sweep.DatabaseId).Since.Should().BeNull("its history went with it, as the bound promises");
        harness.Cache.Latest(sweep.StoreKey, sweep.DatabaseId).Should().BeNull("and its slot");
    }

    [Fact]
    public void The_host_bound_is_sixty_four_databases()
    {
        new HealthDatabaseBound().Capacity.Should().Be(64);
        FluentActions.Invoking(static () => new HealthDatabaseBound(0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    private static HealthSample Sample(double value) => new(At, new Dictionary<string, double>(StringComparer.Ordinal) { ["x"] = value })
    {
        Gates = new Dictionary<string, HealthGate>(StringComparer.Ordinal) { ["x"] = HealthGate.None },
    };

    private sealed class Recorder : IPerDatabaseState
    {
        public List<string> Forgotten { get; } = [];

        public void Forget(string storeKey, string databaseId) => Forgotten.Add(storeKey + "|" + databaseId);
    }
}
