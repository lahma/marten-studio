using System.Reflection;

using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Core.Logging;
using KStudio.Core.Wire;
using KStudio.Core.Wire.Confluent;
using KStudio.Core.Windows;
using KStudio.Tests.Contract;
using KStudio.Tests.Support.FakeKafka;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Clusters;

/// <summary>
/// REVIEW-ENG-A 8: the options are validated once, by the registry, and the engine reads a snapshot of them. A write after validation
/// reaches nothing, so no bound can be skipped by it.
/// </summary>
public class EngineOptionsSnapshotTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static KafkaEngineOptions Options()
    {
        KafkaEngineOptions options = new();
        options.AddCluster("main", new ClientConfig { BootstrapServers = "localhost:1" });
        return options;
    }

    [Fact]
    public void The_registry_takes_the_snapshot_at_validation_and_a_later_write_to_the_options_changes_nothing()
    {
        KafkaEngineOptions options = Options();
        KafkaClusterRegistry registry = new(options);

        options.RequestTimeout = TimeSpan.FromMilliseconds(1);
        options.AdminIdleTimeout = TimeSpan.FromMilliseconds(1);
        options.GroupIdPrefix = "not valid!";
        options.Limits.CreateTimeMargin = TimeSpan.FromDays(9);
        options.Limits.QueueKilobytes = -5;
        options.Limits.FetchBytes = 1;

        KafkaEngineSnapshot settings = registry.Settings;
        settings.RequestTimeout.Should().Be(TimeSpan.FromSeconds(10));
        settings.AdminIdleTimeout.Should().Be(TimeSpan.FromMinutes(5));
        settings.GroupIdPrefix.Should().Be("kstudio-");
        settings.Limits.CreateTimeMargin.Should().Be(TimeSpan.FromMinutes(2));
        settings.Limits.QueueKilobytes.Should().Be(8192);
        settings.Limits.FetchBytes.Should().Be(8 * 1024 * 1024);
    }

    [Fact]
    public void The_scan_group_id_prefix_comes_from_the_snapshot()
    {
        KafkaEngineOptions options = Options();
        KafkaClusterRegistry registry = new(options);
        options.GroupIdPrefix = "changed-";

        using ConfluentScanReader reader = new(
            registry.Get("main"),
            registry.Settings,
            new WireReaderOptions(Guid.Parse("11111111-2222-3333-4444-555555555555"), WireIsolation.ReadCommitted, new KafkaScanLimits()),
            new ConfluentClientEvents(new KStudioLoggers(new CapturingLoggerProvider().CreateFactory()), new KafkaLogOnce(TimeProvider.System), TimeProvider.System));

        reader.GroupId.Should().Be("kstudio-11111111222233334444555555555555");
    }

    [Fact]
    public async Task The_resolvers_default_margin_comes_from_the_snapshot()
    {
        FakeKafkaCluster cluster = new(TimeProvider.System);
        cluster.CreateTopic("a", 1);
        await using EngineHarness harness = EngineHarness.ForWire(new FakeKafkaWire(cluster, EngineHarness.ClusterKey), configure: x => x.Limits.CreateTimeMargin = TimeSpan.FromMinutes(7));

        // The registry validates when it is first resolved (in a host, at start).
        _ = harness.Provider.GetRequiredService<KafkaClusterRegistry>();
        harness.Options.Limits.CreateTimeMargin = TimeSpan.FromHours(30);

        ResolvedWindow resolved = await harness.Resolver.ResolveAsync(
            EngineHarness.ClusterKey,
            [new KafkaTopicName("a")],
            new KafkaWindow(DateTimeOffset.FromUnixTimeSeconds(0), DateTimeOffset.FromUnixTimeSeconds(10)),
            KafkaIsolation.Committed,
            null,
            Token);

        resolved.Margin.Should().Be(TimeSpan.FromMinutes(7), "the 30 h written after validation (and past the 1 h bound) is not seen");
    }

    [Fact]
    public void Only_the_registry_and_the_snapshot_hold_the_options_everything_else_takes_the_snapshot()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        Type options = typeof(KafkaEngineOptions);
        List<string> holders = [];

        foreach (Type type in typeof(KafkaEngineOptions).Assembly.GetTypes())
        {
            if (type == options || type.IsSubclassOf(options))
            {
                continue;
            }

            IEnumerable<string> fields = type.GetFields(all).Where(x => x.FieldType == options).Select(static x => x.Name);
            IEnumerable<string> properties = type.GetProperties(all).Where(x => x.PropertyType == options).Select(static x => x.Name);
            IEnumerable<string> parameters = type.GetConstructors(all).SelectMany(static x => x.GetParameters()).Where(x => x.ParameterType == options).Select(static x => x.Name!);

            foreach (string name in fields.Concat(properties).Concat(parameters))
            {
                holders.Add(type.Name + "." + name);
            }
        }

        holders.Select(static x => x[..x.IndexOf('.', StringComparison.Ordinal)]).Distinct().Should().BeEquivalentTo(
            ["KafkaClusterRegistry", "KafkaEngineSnapshot"],
            "a type that reads RequestTimeout, GroupIdPrefix or the limits from the live options skips the validation: " + string.Join(", ", holders));
    }
}
