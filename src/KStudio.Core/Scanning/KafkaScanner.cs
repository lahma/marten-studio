using System.Collections.Concurrent;
using System.Globalization;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Logging;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Wire;
using KStudio.Core.Windows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KStudio.Core.Scanning;

/// <summary>
/// The scan engine's API (decision 9.1). <see cref="PlanAsync" /> resolves the window, compiles the search per topic and estimates, and
/// reads nothing but list offsets, configs and a small sample of record sizes. <see cref="Start" /> turns a plan into a running scan, or
/// says why not, as a value: it never queues, and it checks the confirmation itself, so a dialog is not the only thing standing between a
/// visitor and a large read.
/// </summary>
internal interface IKafkaScanner
{
    /// <summary>Plans a scan.</summary>
    /// <param name="request">What to scan.</param>
    /// <param name="owner">Whose scan it is: the sampling read counts against the owner's budget.</param>
    /// <param name="cancellationToken">Abandons the planning.</param>
    Task<KafkaScanPlan> PlanAsync(KafkaScanRequest request, KafkaScanOwner owner, CancellationToken cancellationToken);

    /// <summary>Starts a planned scan, or refuses.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="owner">Whose scan it is.</param>
    /// <param name="confirmed">Whether the visitor confirmed a scan above the confirmation threshold; the engine checks it.</param>
    KafkaScanStart Start(KafkaScanPlan plan, KafkaScanOwner owner, bool confirmed);
}

/// <summary>Resolves a topic's declared decoder once and keeps it: a singleton per topic descriptor (decision 2.3).</summary>
internal sealed class KafkaDecoderResolver(IServiceProvider services)
{
    private readonly ConcurrentDictionary<KafkaTopicDescriptor, Lazy<IKafkaRecordDecoder?>> resolved = new(ReferenceEqualityComparer.Instance);

    /// <summary>The decoder a topic declared, activated on first use; <see langword="null" /> when it declared none.</summary>
    /// <param name="descriptor">The topic's descriptor.</param>
    public IKafkaRecordDecoder? For(KafkaTopicDescriptor descriptor)
    {
        if (descriptor.Settings.DecoderSource is not { } source)
        {
            return null;
        }

        return resolved.GetOrAdd(descriptor, _ => new Lazy<IKafkaRecordDecoder?>(() => source(services))).Value;
    }
}

/// <summary>The scan engine (decisions 9.1, 9.7, 9.10 and 9.11).</summary>
internal sealed class KafkaScanner(
    KafkaClusterRegistry registry,
    KafkaWires wires,
    WindowResolver resolver,
    RecordSizeSampler sampler,
    KafkaDecoderResolver decoders,
    KafkaScanLimiter limiter,
    OwnerByteBudget ownerBudget,
    KafkaByteRateGovernor governor,
    KafkaScanCoordinator coordinator,
    KafkaEngineSnapshot settings,
    TimeProvider time,
    KStudioLoggers loggers,
    KafkaLogOnce once,
    IScanObserver? observer = null) : IKafkaScanner
{
    /// <inheritdoc />
    public async Task<KafkaScanPlan> PlanAsync(KafkaScanRequest request, KafkaScanOwner owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(owner);

        if (!request.Grant.HasFlag(KafkaReadGrant.Envelope))
        {
            throw new ArgumentException("A scan needs at least the Envelope grant (the SearchRecords capability).", nameof(request));
        }

        if (request.Topics.Count == 0)
        {
            throw new ArgumentException("A scan needs at least one topic.", nameof(request));
        }

        KafkaClusterDefinition definition = registry.Get(request.Cluster);
        KafkaScanLimits limits = request.Lower?.Apply(settings.Limits) ?? settings.Limits;
        PredicateParseResult parsed = PredicateParser.Parse(request.PredicateText);

        if (!parsed.IsValid)
        {
            return new KafkaScanPlan(request, null, [], new Dictionary<string, TopicScanContext>(), parsed.Errors, [], Unknown(0, 0), limits, string.Empty);
        }

        KafkaTopicName[] topics = [.. request.Topics.DistinctBy(static x => x.Value)];
        ResolvedWindow window = await resolver
            .ResolveAsync(request.Cluster, topics, request.Window, request.Isolation, request.CreateTimeMargin, cancellationToken)
            .ConfigureAwait(false);

        // Compile once per topic: key formats and redaction differ per topic.
        Dictionary<string, TopicScanContext> contexts = new(StringComparer.Ordinal);
        List<PredicateError> errors = [];
        List<ClauseVerdict> verdicts = [];

        foreach (KafkaTopicName topic in topics)
        {
            KafkaTopicDescriptor descriptor = definition.Descriptor(topic.Value);
            IKafkaRedactor redactor = new KafkaRedactor(descriptor.Settings.Rules);
            IKafkaRecordDecoder? decoder = decoders.For(descriptor);
            bool structured = KafkaDecoders.BuiltInKind(decoder) == KafkaBuiltInDecoder.CloudEvents;

            PredicateCompilation compiled = PredicateCompiler.Compile(
                parsed.Tree,
                new PredicateCompileContext(topic.Value, descriptor.KeyFormat, descriptor.KeyFormatDeclared, redactor, request.Grant, structured));

            foreach (PredicateError error in compiled.Errors)
            {
                if (!errors.Any(x => x.Start == error.Start && x.Message == error.Message))
                {
                    errors.Add(error);
                }
            }

            if (compiled.Predicate is { } predicate)
            {
                contexts[topic.Value] = new TopicScanContext(topic.Value, descriptor, redactor, predicate, decoder);
                verdicts.AddRange(predicate.Verdicts);
            }
        }

        string audit = PredicateFormatter.Format(
            parsed.Tree,
            clause => contexts.Values.Any(x => PredicateCompiler.IsRedacted(clause.Field, x.Redactor)));

        IReadOnlyList<PartitionRange> ranges = Continue(window.Ranges, request.ContinueFrom);

        if (errors.Count > 0)
        {
            return new KafkaScanPlan(request, window, ranges, contexts, errors, verdicts, Unknown(ranges.Sum(static x => Math.Max(0, x.End - x.Start)), ranges.Count(static x => !x.IsEmpty)), limits, audit);
        }

        KafkaScanEstimate estimate = await EstimateAsync(request, ranges, owner, limits, cancellationToken).ConfigureAwait(false);
        return new KafkaScanPlan(request, window, ranges, contexts, [], verdicts, estimate, limits, audit);
    }

    /// <inheritdoc />
    public KafkaScanStart Start(KafkaScanPlan plan, KafkaScanOwner owner, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(owner);

        if (coordinator.IsShuttingDown)
        {
            return Refuse(plan, KafkaScanRefusal.ShuttingDown);
        }

        if (!plan.IsValid)
        {
            return Refuse(plan, KafkaScanRefusal.PredicateInvalid);
        }

        Guid scanId = Guid.NewGuid();
        ScanRunInputs inputs = new(scanId, plan, owner, wires.For(plan.Request.Cluster), settings, governor, ownerBudget, time, loggers, once, observer);
        long offsets = plan.Estimate.Offsets;
        string isolation = plan.Request.Isolation.ToString();

        // A plan with nothing to read starts no consumer, takes no slot, and completes at once.
        if (plan.Ranges.All(static x => x.IsEmpty))
        {
            ScanRun empty = new(inputs);
            KafkaScanResult result = empty.Execute();
            empty.Dispose();
            return KafkaScanStart.Of(new KafkaScanHandle(empty, Task.FromResult(result), time));
        }

        if (plan.Estimate.NeedsConfirmation && !confirmed)
        {
            return Refuse(plan, KafkaScanRefusal.NeedsConfirmation);
        }

        if (ownerBudget.Remaining(owner.Key) <= 0)
        {
            return Refuse(plan, KafkaScanRefusal.OwnerBudgetSpent);
        }

        if (limiter.TryAcquire(owner.Key, out IDisposable? lease) is { } refusal)
        {
            return Refuse(plan, refusal);
        }

        ScanRun run = new(inputs);
        string cluster = KafkaLogValue.Cap(plan.Request.Cluster)!;
        int topicCount = plan.Request.Topics.Count;
        int partitionCount = plan.Ranges.Count(static x => !x.IsEmpty);
        KStudioLog.ScanStarted(loggers.Scans, scanId, cluster, topicCount, partitionCount, offsets, isolation);

        // One dedicated thread per scan; nothing in it awaits and the consumer lives and dies on it (decision 4.2).
        Task<KafkaScanResult> task = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    return run.Execute();
                }
                finally
                {
                    run.Dispose();
                    lease?.Dispose();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        KafkaScanHandle handle = new(run, task, time);
        coordinator.Register(handle);
        return KafkaScanStart.Of(handle);
    }

    private KafkaScanStart Refuse(KafkaScanPlan plan, KafkaScanRefusal refusal)
    {
        string cluster = KafkaLogValue.Cap(plan.Request.Cluster)!;
        string text = refusal.ToString();
        KStudioLog.ScanRefused(loggers.Scans, cluster, text);
        return KafkaScanStart.Refused(refusal);
    }

    /// <summary>"Continue from here" reads exactly the offsets the first scan left: its positions are the starts and its ends the ends, not re-resolved (decision 9.9).</summary>
    private static IReadOnlyList<PartitionRange> Continue(IReadOnlyList<PartitionRange> ranges, KafkaScanCursor? cursor)
    {
        if (cursor is null)
        {
            return ranges;
        }

        Dictionary<(string, int), KafkaCursorPosition> positions = cursor.Positions.ToDictionary(static x => (x.Topic, x.Partition));

        return
        [
            .. ranges
                .Where(x => positions.ContainsKey((x.Topic, x.Partition)))
                .Select(x =>
                {
                    KafkaCursorPosition position = positions[(x.Topic, x.Partition)];
                    return x with { Start = position.Next, End = position.End };
                }),
        ];
    }

    private async Task<KafkaScanEstimate> EstimateAsync(
        KafkaScanRequest request,
        IReadOnlyList<PartitionRange> ranges,
        KafkaScanOwner owner,
        KafkaScanLimits limits,
        CancellationToken cancellationToken)
    {
        long offsets = ranges.Sum(static x => Math.Max(0, x.End - x.Start));
        int partitions = ranges.Count(static x => !x.IsEmpty);

        if (offsets == 0)
        {
            return new KafkaScanEstimate(0, null, 0, 0, false, "Nothing to read.");
        }

        WireIsolation isolation = request.Isolation == KafkaIsolation.Committed ? WireIsolation.ReadCommitted : WireIsolation.ReadUncommitted;
        long known = 0;
        long knownOffsets = 0;
        int unknownTopics = 0;

        foreach (IGrouping<string, PartitionRange> topic in ranges.Where(static x => !x.IsEmpty).GroupBy(static x => x.Topic))
        {
            long topicOffsets = topic.Sum(static x => x.End - x.Start);
            long? mean = await sampler
                .MeanRecordBytesAsync(request.Cluster, topic.Key, [.. topic], isolation, owner, cancellationToken)
                .ConfigureAwait(false);

            if (mean is { } size)
            {
                known += topicOffsets * size;
                knownOffsets += topicOffsets;
            }
            else
            {
                unknownTopics++;
            }
        }

        long? bytes = knownOffsets == 0 ? null : known;
        long? overall = knownOffsets == 0 ? null : known / knownOffsets;
        bool needs = (bytes is { } b && b > limits.ConfirmAboveBytes) || offsets > limits.ConfirmAboveRecords;

        string basis = knownOffsets == 0
            ? string.Create(CultureInfo.InvariantCulture, $"About {offsets:N0} offsets (an upper bound: offsets are not records); the record size is unknown.")
            : unknownTopics == 0
                ? string.Create(CultureInfo.InvariantCulture, $"About {offsets:N0} offsets (an upper bound: offsets are not records) of about {overall:N0} bytes each, from a sample of at most {RecordSizeSampler.MaxRecords} records.")
                : string.Create(CultureInfo.InvariantCulture, $"About {offsets:N0} offsets (an upper bound: offsets are not records); the size of {unknownTopics} topic(s) is unknown, so the bytes are a lower bound.");

        return new KafkaScanEstimate(offsets, overall, bytes, partitions, needs, basis);
    }

    private static KafkaScanEstimate Unknown(long offsets, int partitions) =>
        new(offsets, null, null, partitions, false, "Not estimated: the search text has errors.");
}

/// <summary>Holds every running scan, and stops them when the host stops (decision 4.3). No hosted service: disposing the container is the trigger.</summary>
internal sealed class KafkaScanCoordinator(TimeProvider time, KStudioLoggers loggers) : IAsyncDisposable
{
    /// <summary>How long shutdown waits for running scans.</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<Guid, KafkaScanHandle> running = new();
    private int shuttingDown;

    /// <summary>Whether the host is stopping: a start is then refused.</summary>
    public bool IsShuttingDown => Volatile.Read(ref shuttingDown) != 0;

    /// <summary>How many scans run now.</summary>
    public int Count => running.Count;

    /// <summary>Remembers a running scan until it ends.</summary>
    /// <param name="handle">The scan.</param>
    public void Register(KafkaScanHandle handle)
    {
        running[handle.ScanId] = handle;
        _ = handle.Result.ContinueWith(
            _ => running.TryRemove(handle.ScanId, out KafkaScanHandle? _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // A start that raced the shutdown: stop it at once.
        if (IsShuttingDown)
        {
            handle.Stop(KafkaScanStopReason.Shutdown);
        }
    }

    /// <summary>Stops every scan with <see cref="KafkaScanStopReason.Shutdown" /> and waits for them for at most ten seconds.</summary>
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref shuttingDown, 1);
        KafkaScanHandle[] handles = [.. running.Values];

        if (handles.Length == 0)
        {
            return;
        }

        KStudioLog.ShutdownStarted(loggers.Scans, handles.Length);

        foreach (KafkaScanHandle handle in handles)
        {
            handle.Stop(KafkaScanStopReason.Shutdown);
        }

        try
        {
            await Task.WhenAll(handles.Select(static x => x.Result)).WaitAsync(ShutdownTimeout, time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            KStudioLog.ShutdownTimedOut(loggers.Scans, handles.Count(static x => x.IsRunning));
        }
    }
}

/// <summary>Registers the scan engine's singletons (decision 2.5). No hosted service, no startup filter, nothing resolved here.</summary>
internal static class ScanningRegistration
{
    /// <summary>Adds the scan engine to <paramref name="services" />.</summary>
    /// <param name="services">The container.</param>
    public static IServiceCollection Add(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(static provider => new KafkaByteRateGovernor(provider.GetRequiredService<KafkaEngineSnapshot>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(static provider => new KafkaScanLimiter(provider.GetRequiredService<KafkaEngineSnapshot>()));
        services.TryAddSingleton(static provider => new OwnerByteBudget(provider.GetRequiredService<KafkaEngineSnapshot>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<RecordSizeSampler>();
        services.TryAddSingleton<KafkaDecoderResolver>();
        services.TryAddSingleton<KafkaScanCoordinator>();
        services.TryAddSingleton<IKafkaScanner, KafkaScanner>();
        return services;
    }
}
