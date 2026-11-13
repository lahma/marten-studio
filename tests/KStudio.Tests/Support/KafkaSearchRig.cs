using System.Text;

using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Services;
using KStudio.Tests.Contract;
using KStudio.Tests.Support.FakeKafka;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PostgresStudio.Services;

namespace KStudio.Tests.Support;

/// <summary>
/// The real engine over the fake broker behind the studio's real circuit services, for the search and record tests (D-UI decision 17): the same as
/// <see cref="KafkaCatalogRig" />, except that the clock is the system's, so the engine's batcher ticks by itself and a scan is read to its final batch
/// without a test driving time. The capability is on and a cluster policy is set by default, so every question is asked and recorded.
/// </summary>
internal sealed class KafkaSearchRig : IAsyncDisposable
{
    /// <summary>The first record's instant; every record is in the past of the system clock.</summary>
    public static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The cluster entry the studio lists.</summary>
    public KafkaClusterEntry Entry { get; }

    public KafkaSearchRig(Action<KafkaEngineOptions>? engine = null, Action<KafkaStudioOptions>? studio = null, bool searchOn = true, bool policies = true, Func<FakeKafkaWire, KStudio.Core.Wire.IKafkaWire>? wrap = null, bool payloads = false, bool traceCircuit = false, Action<IServiceCollection>? services = null, KafkaProfile? profile = null)
    {
        Entry = new KafkaClusterEntry(EngineHarness.ClusterKey, null, "Test cluster", "production", profile);
        Cluster = new FakeKafkaCluster(TimeProvider.System);
        FakeKafkaWire wire = new(Cluster, EngineHarness.ClusterKey);
        Engine = EngineHarness.ForWire(wrap?.Invoke(wire) ?? wire, TimeProvider.System, TimeSpan.Zero, engine, profile is { } p ? c => c.Profile(p) : null);
        Studio = new KafkaStudioHarness(
            options =>
            {
                options.Capabilities.SearchRecords = searchOn;
                options.Capabilities.ReadPayloads = payloads;

                if (policies)
                {
                    options.ClusterAuthorizationPolicy = "kafka-topics";
                }

                studio?.Invoke(options);
            },
            traceCircuit
                ? container =>
                {
                    container.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(CircuitLogs));
                    services?.Invoke(container);
                }
                : services,
            [Entry],
            Engine);
    }

    public FakeKafkaCluster Cluster { get; }

    public EngineHarness Engine { get; }

    public KafkaStudioHarness Studio { get; }

    /// <summary>Everything the services this rig builds by hand logged.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Everything the circuit's own services logged, at Trace, when the rig was built with <c>traceCircuit</c>.</summary>
    public CapturingLoggerProvider CircuitLogs { get; } = new();

    /// <summary>How many calls of any kind reached the fake broker.</summary>
    public int BrokerCalls => Cluster.Calls.Count;

    /// <summary>How many list-offsets calls reached the fake broker: a plan makes them, a topic list does not.</summary>
    public int ListOffsetCalls => Cluster.Calls.Count(static x => x.Call == KStudio.Core.Wire.WireCall.ListOffsets);

    /// <summary>The ring the audit writes into.</summary>
    public IReadOnlyList<StudioActionLogEntry> Ring => Studio.Context.Services.GetRequiredService<StudioActionLogService>().GetLatest();

    /// <summary>The toasts the circuit shows.</summary>
    public ToastService Toasts => Studio.Context.Services.GetRequiredService<ToastService>();

    /// <summary>A search service over the engine's scanner whose events land in <see cref="Logs" />.</summary>
    public KafkaSearchService Search()
    {
        IServiceProvider services = Studio.Context.Services;
        KafkaActionLog actionLog = new(
            Studio.Context.ActionLog,
            Logs.CreateFactory().CreateLogger<KafkaActionLog>(),
            Studio.Visitor);

        return new KafkaSearchService(
            services.GetRequiredService<KafkaCapabilityGuard>(),
            Studio.KafkaAuthorization,
            services.GetRequiredService<KafkaReadGrants>(),
            Engine.Provider.GetRequiredService<IKafkaScanner>(),
            Engine.Provider.GetRequiredService<OwnerByteBudget>(),
            Engine.Provider.GetRequiredService<KStudio.Core.Clusters.KafkaClusterRegistry>(),
            Engine.Provider.GetRequiredService<KafkaClusterCostControls>(),
            new KafkaOwnerKey(Studio.Visitor),
            actionLog,
            Logs.CreateFactory().CreateLogger<KafkaSearchService>());
    }

    /// <summary>An export service over the engine's registry and scanner whose events land in <see cref="Logs" />; two exports at a time unless <paramref name="gate" /> says otherwise.</summary>
    public KafkaExportService Export(KafkaExportGate? gate = null)
    {
        IServiceProvider services = Studio.Context.Services;
        KafkaActionLog actionLog = new(
            Studio.Context.ActionLog,
            Logs.CreateFactory().CreateLogger<KafkaActionLog>(),
            Studio.Visitor);

        return new KafkaExportService(
            services.GetRequiredService<KafkaCapabilityGuard>(),
            Studio.KafkaAuthorization,
            services.GetRequiredService<KafkaReadGrants>(),
            Engine.Provider.GetRequiredService<KStudio.Core.Clusters.KafkaClusterRegistry>(),
            Engine.Provider.GetRequiredService<IKafkaScanner>(),
            gate ?? new KafkaExportGate(),
            actionLog,
            TimeProvider.System,
            Logs.CreateFactory().CreateLogger<KafkaExportService>());
    }

    /// <summary>A live tail service over the engine's scanner and registry whose events land in <see cref="Logs" />.</summary>
    public KafkaTailService Tail()
    {
        IServiceProvider services = Studio.Context.Services;
        KafkaActionLog actionLog = new(
            Studio.Context.ActionLog,
            Logs.CreateFactory().CreateLogger<KafkaActionLog>(),
            Studio.Visitor);

        return new KafkaTailService(
            services.GetRequiredService<KafkaCapabilityGuard>(),
            Studio.KafkaAuthorization,
            services.GetRequiredService<KafkaReadGrants>(),
            Engine.Provider.GetRequiredService<IKafkaScanner>(),
            Engine.Provider.GetRequiredService<KStudio.Core.Clusters.KafkaClusterRegistry>(),
            new KafkaOwnerKey(Studio.Visitor),
            actionLog,
            Logs.CreateFactory().CreateLogger<KafkaTailService>());
    }

    /// <summary>A tail over <paramref name="topics" />, from the end of each partition.</summary>
    public KafkaTailInput TailInput(string[] topics, string? predicate = null, bool uncommitted = false, KafkaScanCursor? resumeFrom = null) =>
        new(Entry, topics, predicate, uncommitted, resumeFrom);

    /// <summary>A record service over the engine's scanner whose events land in <see cref="Logs" />.</summary>
    public KafkaRecordService Records()
    {
        IServiceProvider services = Studio.Context.Services;
        KafkaActionLog actionLog = new(
            Studio.Context.ActionLog,
            Logs.CreateFactory().CreateLogger<KafkaActionLog>(),
            Studio.Visitor);

        return new KafkaRecordService(
            services.GetRequiredService<KafkaCapabilityGuard>(),
            Studio.KafkaAuthorization,
            services.GetRequiredService<KafkaReadGrants>(),
            Engine.Provider.GetRequiredService<KStudio.Core.Clusters.KafkaEngineSnapshot>(),
            Engine.Provider.GetRequiredService<IKafkaScanner>(),
            new KafkaOwnerKey(Studio.Visitor),
            actionLog,
            Logs.CreateFactory().CreateLogger<KafkaRecordService>());
    }

    /// <summary>A related-records service over the engine's scanner and registry whose events land in <see cref="Logs" />.</summary>
    public KafkaCorrelationService Correlation()
    {
        IServiceProvider services = Studio.Context.Services;
        KafkaActionLog actionLog = new(
            Studio.Context.ActionLog,
            Logs.CreateFactory().CreateLogger<KafkaActionLog>(),
            Studio.Visitor);

        return new KafkaCorrelationService(
            services.GetRequiredService<KafkaCapabilityGuard>(),
            Studio.KafkaAuthorization,
            services.GetRequiredService<KafkaReadGrants>(),
            Records(),
            Engine.Provider.GetRequiredService<KStudio.Core.Clusters.KafkaClusterRegistry>(),
            Engine.Provider.GetRequiredService<IKafkaScanner>(),
            new KafkaOwnerKey(Studio.Visitor),
            actionLog,
            Logs.CreateFactory().CreateLogger<KafkaCorrelationService>());
    }

    /// <summary>A related-records search over <paramref name="topics" /> for the first hour of the seeded time, around <paramref name="keys" />.</summary>
    public KafkaRelatedInput RelatedInput(string[] topics, KStudio.Core.Correlation.CorrelationKeySet keys, string anchorTopic = "orders", KafkaScanCursor? cursor = null) =>
        new(Entry, KafkaRelatedSubject.Anchor(anchorTopic, 0, 0), keys, topics, T0, T0.AddHours(1), false, cursor);

    /// <summary>Creates a topic.</summary>
    public void Topic(string name, int partitions = 1) => Cluster.CreateTopic(name, partitions);

    /// <summary>Appends a record <paramref name="seconds" /> after <see cref="T0" />.</summary>
    public long Produce(string topic, int partition, int seconds, string? key, string? value, params (string Name, string? Value)[] headers) =>
        Cluster.Produce(
            topic,
            partition,
            key is null ? null : Encoding.UTF8.GetBytes(key),
            value is null ? null : Encoding.UTF8.GetBytes(value),
            [.. headers.Select(static x => new WireHeader(x.Name, x.Value is null ? null : Encoding.UTF8.GetBytes(x.Value)))],
            T0.ToUnixTimeMilliseconds() + (seconds * 1000L));

    /// <summary>A search over <paramref name="topics" /> for the first hour of the seeded time.</summary>
    public KafkaSearchInput Input(string[] topics, string? predicate = null, bool uncommitted = false, KafkaScanCursor? cursor = null) =>
        new(Entry, topics, T0, T0.AddHours(1), predicate, uncommitted, cursor);

    /// <summary>Reads a started search to its final batch.</summary>
    public static async Task<(KafkaScanResult Result, List<KafkaMatch> Matches)> DrainAsync(KafkaSearchRun run)
    {
        List<KafkaMatch> matches = [];
        KafkaScanResult? result = null;

        await foreach (KafkaScanBatch batch in run.ReadBatchesAsync(TestContext.Current.CancellationToken).WithCancellation(TestContext.Current.CancellationToken))
        {
            matches.AddRange(batch.NewMatches);

            if (batch.Final is { } final)
            {
                result = final;
                run.Complete(final);
            }
        }

        result.Should().NotBeNull("the last batch carries the result");
        return (result, matches);
    }

    public async ValueTask DisposeAsync()
    {
        Studio.Dispose();
        await Engine.DisposeAsync();
    }
}
