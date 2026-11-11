using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Core.Windows;
using KStudio.Tests.Contract;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Integration.Tests.Support;

/// <summary>
/// The engine's real container over a real broker, as one principal, for the scan tests: the same registration the host studio uses, the real
/// Confluent wire, and (for the tests that need it) the scan observer seam. Seeding is always done by <see cref="LiveContractCluster" />, as
/// <c>admin</c>.
/// </summary>
internal sealed class LiveScanEngine : IAsyncDisposable
{
    /// <summary>The start of the seeded time; every record is stamped after it, and all of it is in the past (the broker refuses the future).</summary>
    public static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private LiveScanEngine(ServiceProvider provider, CapturingLoggerProvider logs)
    {
        Provider = provider;
        Logs = logs;
    }

    /// <summary>
    /// A window end later than anything the broker has written, control records included: a transaction's marker carries the broker's clock, so
    /// a window that ends in the past would end at the first marker written after it, ahead of records that were stamped earlier.
    /// </summary>
    public static DateTimeOffset Beyond => DateTimeOffset.UtcNow.AddDays(1);

    public ServiceProvider Provider { get; }

    public CapturingLoggerProvider Logs { get; }

    public IKafkaScanner Scanner => Provider.GetRequiredService<IKafkaScanner>();

    public KafkaScanOwner Owner { get; } = new("live-owner");

    /// <summary>Builds the engine for a principal of the fixture's broker.</summary>
    public static LiveScanEngine Create(KafkaFixture fixture, string user, Action<KafkaEngineOptions>? configure = null, IScanObserver? observer = null)
    {
        Assert.SkipUnless(DockerAvailability.IsAvailable, DockerAvailability.SkipReason);
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        CapturingLoggerProvider logs = new();
        KafkaEngineOptions options = new();
        options.AddCluster(EngineHarness.ClusterKey, fixture.Client(user));
        configure?.Invoke(options);

        ServiceCollection services = new();
        services.AddSingleton(logs.CreateFactory());

        if (observer is not null)
        {
            services.AddSingleton(observer);
        }

        KStudioEngineRegistration.Add(services, _ => options, _ => new TestEngineSettings(TimeSpan.Zero));
        return new LiveScanEngine(services.BuildServiceProvider(), logs);
    }

    public static KafkaScanRequest Request(
        string[] topics,
        string? predicate = null,
        int fromSeconds = 0,
        int? toSeconds = null,
        KafkaReadGrant grant = KafkaReadGrant.Envelope,
        KafkaIsolation isolation = KafkaIsolation.Committed,
        KafkaScanCursor? cursor = null,
        KafkaScanLowering? lower = null,
        TimeSpan? margin = null) =>
        new(
            EngineHarness.ClusterKey,
            [.. topics.Select(static x => new KafkaTopicName(x))],
            new KafkaWindow(T0.AddSeconds(fromSeconds), toSeconds is { } end ? T0.AddSeconds(end) : Beyond),
            predicate,
            grant,
            isolation,
            margin ?? TimeSpan.Zero,
            cursor,
            lower);

    public Task<KafkaScanPlan> PlanAsync(KafkaScanRequest request, CancellationToken cancellationToken) => Scanner.PlanAsync(request, Owner, cancellationToken);

    /// <summary>Plans, starts and waits for the result; the scan must start.</summary>
    public async Task<(KafkaScanPlan Plan, KafkaScanResult Result, IReadOnlyList<KafkaMatch> Matches)> RunAsync(KafkaScanRequest request, CancellationToken cancellationToken, bool confirmed = true)
    {
        KafkaScanPlan plan = await PlanAsync(request, cancellationToken);
        plan.Errors.Should().BeEmpty();

        KafkaScanStart started = Scanner.Start(plan, Owner, confirmed);
        started.Refusal.Should().BeNull();

        await using KafkaScanHandle handle = started.Handle!;
        KafkaScanResult result = await handle.Result.WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
        return (plan, result, handle.Matches);
    }

    public ValueTask DisposeAsync() => Provider.DisposeAsync();
}

/// <summary>Helpers for seeding.</summary>
internal static class LiveRecords
{
    /// <summary>A record stamped <paramref name="seconds" /> after <see cref="LiveScanEngine.T0" />.</summary>
    public static ContractRecord At(int seconds, string? key, string? value, params (string Name, string Value)[] headers) =>
        new(key, value, LiveScanEngine.T0.ToUnixTimeMilliseconds() + (seconds * 1000L), headers.Length == 0 ? null : [.. headers.Select(static x => KeyValuePair.Create(x.Name, x.Value))]);
}
