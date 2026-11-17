using JasperFx.Events.Aggregation;

using Marten;

using DataStudio.Marten.Services.Health.Checks;

namespace DataStudio.Marten.Integration.Tests.Health;

/// <summary>
/// E06's list on the path every real host takes: Marten scans the application's assemblies for the evolvers its source
/// generator emitted (<c>DiscoverGeneratedEvolversOnStartup</c>, on by default) and reports each one's aggregate, whether
/// or not anything registers or ever fetches it. No database: the list is read from the options alone.
/// </summary>
/// <remarks>
/// The sample store switches the scan off (<c>SampleStore.Configure</c>), so the other health tests never see this path.
/// </remarks>
public sealed class GeneratedEvolverAggregatesTests
{
    [Fact]
    public void This_assembly_carries_a_generated_evolver_for_the_test_aggregate()
    {
        // The premise of the tests below: without the marker the scan would find nothing and they would prove nothing.
        typeof(HealthCounter).Assembly.GetCustomAttributes(typeof(GeneratedEvolverAttribute), inherit: false)
            .Cast<GeneratedEvolverAttribute>()
            .Should().Contain(static x => x.AggregateType == typeof(HealthCounter));
    }

    [Fact]
    public void An_aggregate_the_scan_finds_and_nothing_registers_is_listed_as_not_registered()
    {
        StoreOptions options = new();
        options.Projections.DiscoverGeneratedEvolvers(typeof(HealthCounter).Assembly);

        LongStreamsWithoutSnapshotCheck.LiveOnly(options)
            .Should().Contain((typeof(HealthCounter), false), "nothing registers it and this process never fetched it: the scan alone lists it");
    }

    [Fact]
    public void Without_the_scan_the_same_aggregate_is_not_listed()
    {
        StoreOptions options = new();

        LongStreamsWithoutSnapshotCheck.LiveOnly(options).Select(static x => x.Type).Should().NotContain(typeof(HealthCounter));
    }

    [Fact]
    public void A_snapshot_takes_a_scanned_aggregate_off_the_list()
    {
        StoreOptions options = new();
        options.Projections.DiscoverGeneratedEvolvers(typeof(HealthCounter).Assembly);
        options.Projections.Snapshot<HealthCounter>(JasperFx.Events.Projections.SnapshotLifecycle.Inline);

        LongStreamsWithoutSnapshotCheck.LiveOnly(options).Select(static x => x.Type).Should().NotContain(typeof(HealthCounter));
    }

    [Fact]
    public void A_live_registration_lists_a_scanned_aggregate_as_registered()
    {
        StoreOptions options = new();
        options.Projections.DiscoverGeneratedEvolvers(typeof(HealthCounter).Assembly);
        options.Projections.LiveStreamAggregation<HealthCounter>();

        LongStreamsWithoutSnapshotCheck.LiveOnly(options).Should().Contain((typeof(HealthCounter), true));
    }
}
