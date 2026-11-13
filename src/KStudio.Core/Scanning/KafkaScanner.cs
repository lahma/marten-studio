using System.Collections.Concurrent;
using System.Globalization;

using KStudio.Core.Catalog;
using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Index;
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

    /// <summary>
    /// Reads one record by its place, with some neighbours on each side, through the scan machinery (D-UI decision 1): a record permalink names
    /// an offset and not a time. It takes a scan slot, counts against the owner's budget, is redacted by the match factory like every
    /// match, and never throws for a state of the cluster: the status says what happened.
    /// </summary>
    /// <param name="request">Which record.</param>
    /// <param name="owner">Whose read it is.</param>
    /// <param name="cancellationToken">Stops the read and throws <see cref="OperationCanceledException" />.</param>
    Task<KafkaRecordRead> ReadRecordAsync(KafkaRecordRequest request, KafkaScanOwner owner, CancellationToken cancellationToken);

    /// <summary>
    /// Plans a live tail (D-M5 decision 3.1): compiles the search per topic exactly as <see cref="PlanAsync" /> does and fixes where each partition
    /// is read from, which is its end (the last stable offset under <see cref="KafkaIsolation.Committed" />) or a paused tail's cursor. It reads
    /// the metadata, the configs and the offsets, and nothing else.
    /// </summary>
    /// <param name="request">What to tail.</param>
    /// <param name="owner">Whose tail it is.</param>
    /// <param name="cancellationToken">Abandons the planning.</param>
    Task<KafkaScanPlan> PlanTailAsync(KafkaTailRequest request, KafkaScanOwner owner, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a planned tail, or refuses: the cluster's profile may forbid it, and it holds a scan slot while it reads, exactly like a scan. It never
    /// queues. A scan's plan is a defect here, as a tail's is for <see cref="Start" />.
    /// </summary>
    /// <param name="plan">The tail plan.</param>
    /// <param name="owner">Whose tail it is.</param>
    KafkaScanStart StartTail(KafkaScanPlan plan, KafkaScanOwner owner);

    /// <summary>
    /// The limits that apply on one cluster: the host's, capped by the cluster's profile when it has one (AGENTS.md D47). A new copy on every call.
    /// A plan's <c>Limits</c> are these with the request's lowering applied; the export reads <c>MaxExportBytes</c> here.
    /// </summary>
    /// <param name="cluster">The cluster's entry key.</param>
    KafkaScanLimits LimitsFor(string cluster);
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

        // PublicationOnly: a factory that throws is tried again on the next scan and is never cached as the answer (the default mode caches
        // the exception for the life of the host).
        return resolved
            .GetOrAdd(descriptor, _ => new Lazy<IKafkaRecordDecoder?>(() => source(services), LazyThreadSafetyMode.PublicationOnly))
            .Value;
    }
}

/// <summary>The scan engine (decisions 9.1, 9.7, 9.10 and 9.11).</summary>
internal sealed partial class KafkaScanner(
    KafkaClusterRegistry registry,
    KafkaWires wires,
    KafkaWireReads reads,
    WindowResolver resolver,
    RecordSizeSampler sampler,
    PartitionerVerifier verifier,
    KafkaDecoderResolver decoders,
    KafkaScanLimiter limiter,
    OwnerByteBudget ownerBudget,
    KafkaByteRateGovernor governor,
    KafkaClusterCostControls clusterCosts,
    KafkaScanCoordinator coordinator,
    KafkaEngineSnapshot settings,
    TimeProvider time,
    KStudioLoggers loggers,
    KafkaLogOnce once,
    KafkaIndexPlanner indexes,
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

        ValidatePerTopic(request);

        KafkaClusterDefinition definition = registry.Get(request.Cluster);

        // The cluster's own allowance is charged by the planning reads too (the size sample and the partitioner verification).
        KafkaClusterCost cost = clusterCosts.For(definition);

        // The cluster's profile caps the host's limits first, in the engine, so that no page can skip it; a lowering may then only go below that.
        KafkaScanLimits profiled = KafkaProfileCeilings.Cap(definition.Profile, settings.Limits);
        KafkaScanLimits limits = request.Lower?.Apply(profiled) ?? profiled;

        // One search for every topic, or (D42) one for each, each under its own grant.
        bool perTopic = request.PerTopic is not null;
        PredicateParseResult shared = perTopic ? new PredicateParseResult(null, []) : PredicateParser.Parse(request.PredicateText);
        Dictionary<string, (PredicateParseResult Parsed, KafkaReadGrant Grant)> searches = new(StringComparer.Ordinal);
        List<PredicateError> parseErrors = [];

        if (request.PerTopic is { } queries)
        {
            foreach (KafkaTopicQuery query in queries)
            {
                PredicateParseResult parsedQuery = PredicateParser.Parse(query.PredicateText);
                searches[query.Topic.Value] = (parsedQuery, query.Grant);
                parseErrors.AddRange(parsedQuery.Errors.Select(x => OnTopic(query.Topic.Value, x)));
            }
        }
        else if (!shared.IsValid)
        {
            parseErrors.AddRange(shared.Errors);
        }

        if (parseErrors.Count > 0)
        {
            return new KafkaScanPlan(request, null, [], new Dictionary<string, TopicScanContext>(), parseErrors, [], Unknown(0, 0), limits, string.Empty);
        }

        KafkaTopicName[] topics = [.. request.Topics.DistinctBy(static x => x.Value)];
        ResolvedWindow window = await resolver
            .ResolveAsync(request.Cluster, topics, request.Window, request.Isolation, request.CreateTimeMargin, cancellationToken)
            .ConfigureAwait(false);

        Compiled search = Compile(definition, topics, perTopic, shared, request.Grant, searches);
        Dictionary<string, TopicScanContext> contexts = search.Contexts;
        List<PredicateError> errors = search.Errors;
        List<ClauseVerdict> verdicts = search.Verdicts;
        IKafkaRedactor[] redactors = search.Redactors;

        // A literal is shown only where no topic of the scan redacts the field it is on: one that is plain here and masked there is
        // <redacted> everywhere in the entry.
        string audit = perTopic ? PerTopicAudit(topics, searches, redactors) : PredicateFormatter.Format(shared.Tree, clause => redactors.Any(x => PredicateCompiler.IsRedacted(clause.Field, x)));

        IReadOnlyList<PartitionRange> ranges = Continue(window.Ranges, request.ContinueFrom);

        if (errors.Count > 0)
        {
            return new KafkaScanPlan(request, window, ranges, contexts, errors, verdicts, Unknown(ranges.Sum(static x => Math.Max(0, x.End - x.Start)), ranges.Count(static x => !x.IsEmpty)), limits, audit);
        }

        // A key search may read fewer partitions, but only after sampling the window has proved the topic's declared partitioner
        // (D-PAYLOAD decision 7): the ranges, the estimate and the cursor then cover exactly what is read.
        IReadOnlyList<KafkaTopicPruning> pruning = await PruneAsync(request, shared.Tree, perTopic ? searches : null, topics, contexts, window, ranges, verdicts, owner, cost, cancellationToken).ConfigureAwait(false);
        ranges = Pruned(ranges, pruning);
        verdicts = ApplyPruning(verdicts, pruning);

        // The host's record index (D48, D-M5 decision 5) is asked once, after every gate has passed and the partitions are pruned, and only narrows what is
        // read inside the ranges the plan already has. A continuation reads the rest of the first scan's ranges and never asks again; it keeps what the
        // first scan was narrowed by, because its negatives are still about those ranges only.
        KafkaIndexNarrowing? narrowing = request.ContinueFrom?.Index;

        if (request.ContinueFrom is null && !request.IgnoreIndex && !perTopic)
        {
            IndexAsk ask = new(
                definition,
                [.. topics.Select(static x => x.Value)],
                request.Window,
                shared.Tree,
                [.. topics.Select(x => definition.Descriptor(x.Value))],
                redactors,
                ranges);

            (ranges, narrowing) = await indexes.NarrowAsync(ask, cancellationToken).ConfigureAwait(false);
        }

        KafkaScanEstimate estimate = await EstimateAsync(request, ranges, owner, cost, limits, cancellationToken).ConfigureAwait(false);
        return new KafkaScanPlan(request, window, ranges, contexts, [], verdicts, estimate, limits, audit, pruning, index: narrowing);
    }

    /// <summary>What <see cref="Compile" /> made of a request's search: a context for each topic that compiled, and what is wrong with the rest.</summary>
    private sealed record Compiled(
        Dictionary<string, TopicScanContext> Contexts,
        List<PredicateError> Errors,
        List<ClauseVerdict> Verdicts,
        IKafkaRedactor[] Redactors);

    /// <summary>
    /// Compiles the search once per topic, because key formats, redaction, decoders and grants differ per topic. A scan, a tail and a
    /// correlation scan (a search for each topic, D42) all go through here, so a tail judges a record exactly as a search does.
    /// </summary>
    private Compiled Compile(
        KafkaClusterDefinition definition,
        KafkaTopicName[] topics,
        bool perTopic,
        PredicateParseResult shared,
        KafkaReadGrant sharedGrant,
        Dictionary<string, (PredicateParseResult Parsed, KafkaReadGrant Grant)> searches)
    {
        Dictionary<string, TopicScanContext> contexts = new(StringComparer.Ordinal);
        List<PredicateError> errors = [];
        List<ClauseVerdict> verdicts = [];

        // Every topic's redactor is built before anything is compiled, and the audit text asks all of them: a topic whose compilation was
        // refused (a deny-listed header, a rule that cannot be searched) has no context, and its refused literal must still not reach a sink.
        IKafkaRedactor[] redactors = [.. topics.Select(topic => (IKafkaRedactor)new KafkaRedactor(definition.Descriptor(topic.Value).Settings.Rules))];

        for (int index = 0; index < topics.Length; index++)
        {
            KafkaTopicName topic = topics[index];
            KafkaTopicDescriptor descriptor = definition.Descriptor(topic.Value);
            IKafkaRedactor redactor = redactors[index];
            (PredicateParseResult parsed, KafkaReadGrant grant) = perTopic ? searches[topic.Value] : (shared, sharedGrant);

            // A host decoder runs only for a visitor who may read payloads (decision 8.6): under Envelope it is not even created. The
            // built-in kinds (CloudEvents among them) are instances the host handed over, never made by its factory, so they still count.
            // The grant is the topic's own: a topic searched under Envelope reads no value though another topic of the scan does.
            IKafkaRecordDecoder? decoder = grant.HasFlag(KafkaReadGrant.Payload) || KafkaDecoders.BuiltInKind(descriptor.Settings.DecoderInstance) is not null
                ? decoders.For(descriptor)
                : null;
            bool structured = KafkaDecoders.BuiltInKind(decoder) == KafkaBuiltInDecoder.CloudEvents;

            PredicateCompilation compiled = PredicateCompiler.Compile(
                parsed.Tree,
                new PredicateCompileContext(topic.Value, descriptor.KeyFormat, descriptor.KeyFormatDeclared, redactor, grant, structured));

            foreach (PredicateError error in compiled.Errors)
            {
                PredicateError shown = perTopic ? OnTopic(topic.Value, error) : error;

                if (!errors.Any(x => x.Start == shown.Start && x.Message == shown.Message))
                {
                    errors.Add(shown);
                }
            }

            if (compiled.Predicate is { } predicate)
            {
                contexts[topic.Value] = new TopicScanContext(topic.Value, descriptor, redactor, predicate, decoder, grant);
                verdicts.AddRange(predicate.Verdicts);
            }
        }

        return new Compiled(contexts, errors, verdicts, redactors);
    }

    /// <inheritdoc />
    public async Task<KafkaScanPlan> PlanTailAsync(KafkaTailRequest request, KafkaScanOwner owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(owner);

        if (!request.Grant.HasFlag(KafkaReadGrant.Envelope))
        {
            throw new ArgumentException("A tail needs at least the Envelope grant (the SearchRecords capability).", nameof(request));
        }

        if (request.Topics.Count == 0)
        {
            throw new ArgumentException("A tail needs at least one topic.", nameof(request));
        }

        KafkaClusterDefinition definition = registry.Get(request.Cluster);

        // The cluster's profile caps the host's limits in the engine, as for a scan; a tail has no lowering of its own.
        KafkaScanLimits limits = KafkaProfileCeilings.Cap(definition.Profile, settings.Limits);
        DateTimeOffset now = time.GetUtcNow();

        // The scan machinery's view of the tail: the cluster, topics, search, grant and isolation are the request's, and nothing reads the window.
        KafkaScanRequest image = new(
            request.Cluster,
            request.Topics,
            new KafkaWindow(now, now.AddMilliseconds(1)),
            request.PredicateText,
            request.Grant,
            request.Isolation);

        PredicateParseResult shared = PredicateParser.Parse(request.PredicateText);

        if (!shared.IsValid)
        {
            return new KafkaScanPlan(image, null, [], new Dictionary<string, TopicScanContext>(), shared.Errors, [], TailEstimate(0), limits, string.Empty, tail: request);
        }

        KafkaTopicName[] topics = [.. request.Topics.DistinctBy(static x => x.Value)];

        // One read of the offsets: the window resolver's own read of the log start, the high watermark and (read_committed) the last stable offset.
        // The window it is asked for is a placeholder at the planning time; only its offsets, refusals and notes are used.
        ResolvedWindow window = await resolver
            .ResolveAsync(request.Cluster, topics, image.Window, request.Isolation, TimeSpan.Zero, cancellationToken)
            .ConfigureAwait(false);

        // A tail has no window: the resolver's notes about one (a margin, a compaction that hides superseded keys from a time window, a partition list
        // fixed at a time) are not the tail's to show. What stays is what is true of any read: uncommitted records are included, and a configuration that
        // could not be read.
        window = window with
        {
            Notes = [.. window.Notes.OfType<WindowNote.IncludesUncommitted>()],
            Topics = [.. window.Topics.Select(static x => x with { Notes = [.. x.Notes.OfType<WindowNote.ConfigNotReadable>()] })],
        };

        Compiled compiled = Compile(definition, topics, perTopic: false, shared, request.Grant, new Dictionary<string, (PredicateParseResult Parsed, KafkaReadGrant Grant)>());
        string audit = PredicateFormatter.Format(shared.Tree, clause => compiled.Redactors.Any(x => PredicateCompiler.IsRedacted(clause.Field, x)));
        (IReadOnlyList<PartitionRange> ranges, List<KafkaScanGap> gaps) = TailRanges(window.Ranges, request.ResumeFrom, request.Isolation);

        return new KafkaScanPlan(image, window, ranges, compiled.Contexts, compiled.Errors, compiled.Verdicts, TailEstimate(ranges.Count), limits, audit, tail: request, gaps: gaps);
    }

    private static KafkaScanEstimate TailEstimate(int partitions) => new(0, null, null, partitions, false, "A live tail reads what arrives.");

    /// <summary>
    /// Where each partition of a tail is read from (D-M5 decision 3.1): its end, which is the last stable offset under <c>read_committed</c> (a
    /// transaction open now holds records below the high watermark that commit later, and starting at the high watermark would skip them without
    /// a word) and the high watermark otherwise; or, for a partition the cursor of a paused tail names, where that stopped. A cursor below the log start
    /// is moved to it and the records in between are a gap; a partition the cursor does not name was added since and is read from its end.
    /// </summary>
    private static (IReadOnlyList<PartitionRange> Ranges, List<KafkaScanGap> Gaps) TailRanges(IReadOnlyList<PartitionRange> resolved, KafkaScanCursor? resume, KafkaIsolation isolation)
    {
        Dictionary<(string, int), KafkaCursorPosition>? positions = resume?.Positions.ToDictionary(static x => (x.Topic, x.Partition));
        List<PartitionRange> ranges = [];
        List<KafkaScanGap> gaps = [];

        foreach (PartitionRange range in resolved)
        {
            long end = isolation == KafkaIsolation.Committed ? Math.Min(range.Lso ?? range.HighWatermark, range.HighWatermark) : range.HighWatermark;
            long start = end;
            List<WindowNote> notes = [];

            if (positions is not null)
            {
                if (positions.TryGetValue((range.Topic, range.Partition), out KafkaCursorPosition? position))
                {
                    if (position.Next < range.LogStart)
                    {
                        gaps.Add(new KafkaScanGap(range.Topic, range.Partition, position.Next, range.LogStart - 1, range.LogStart - position.Next, range.LogStart));
                        start = range.LogStart;
                    }
                    else if (position.Next > range.HighWatermark)
                    {
                        // Recreated or truncated: what the paused tail had read is not in this partition. It is read from the end, and the plan says so.
                        start = range.HighWatermark;
                        notes.Add(new WindowNote.TailPositionPastEnd(range.Topic, range.Partition, range.HighWatermark));
                    }
                    else
                    {
                        start = position.Next;
                    }
                }
                else
                {
                    notes.Add(new WindowNote.PartitionAddedToTail(range.Topic, range.Partition));
                }
            }

            ranges.Add(range with { Start = start, End = KafkaScanCursor.OpenEnd, Notes = notes });
        }

        return (ranges, gaps);
    }

    /// <summary>The rules of a request with a search for each topic (D42); every violation is a caller's bug and throws.</summary>
    private static void ValidatePerTopic(KafkaScanRequest request)
    {
        if (request.PerTopic is not { } queries)
        {
            return;
        }

        if (request.PredicateText is not null)
        {
            throw new ArgumentException("A scan with a search for each topic has no PredicateText.", nameof(request));
        }

        if (!request.Topics.Select(static x => x.Value).SequenceEqual(queries.Select(static x => x.Topic.Value), StringComparer.Ordinal))
        {
            throw new ArgumentException("A scan with a search for each topic scans exactly those topics, in that order.", nameof(request));
        }

        if (queries.Select(static x => x.Topic.Value).Distinct(StringComparer.Ordinal).Count() != queries.Count)
        {
            throw new ArgumentException("A topic has one search in a scan.", nameof(request));
        }

        foreach (KafkaTopicQuery query in queries)
        {
            if (string.IsNullOrWhiteSpace(query.PredicateText))
            {
                throw new ArgumentException("A topic's search is a text that is not empty: a topic with no search is left out of the request.", nameof(request));
            }

            if (!query.Grant.HasFlag(KafkaReadGrant.Envelope))
            {
                throw new ArgumentException("A topic needs at least the Envelope grant.", nameof(request));
            }

            if ((query.Grant & ~request.Grant) != 0)
            {
                throw new ArgumentException("A topic cannot be read under more than the request's grant.", nameof(request));
            }
        }
    }

    /// <summary>
    /// The audit text of a scan with a search for each topic: the topics whose formatted text is equal are named together, so that a scan of
    /// 20 topics with one search is not 20 copies of it (the log cuts a long text). A literal is shown only where no topic redacts its field.
    /// </summary>
    private static string PerTopicAudit(KafkaTopicName[] topics, Dictionary<string, (PredicateParseResult Parsed, KafkaReadGrant Grant)> searches, IKafkaRedactor[] redactors)
    {
        Dictionary<string, string> formatted = topics.ToDictionary(
            static x => x.Value,
            x => PredicateFormatter.Format(searches[x.Value].Parsed.Tree, clause => redactors.Any(r => PredicateCompiler.IsRedacted(clause.Field, r))),
            StringComparer.Ordinal);

        return string.Join("; ", topics.GroupBy(x => formatted[x.Value], StringComparer.Ordinal).Select(g => string.Join(", ", g.Select(static x => x.Value)) + ": " + g.Key));
    }

    private static PredicateError OnTopic(string topic, PredicateError error) => error with { Message = "On " + topic + ": " + error.Message };

    /// <summary>
    /// Asks the verifier about every topic that has a key clause to explain: a topic that declared a partitioner, or one whose search fixes
    /// the key (its verdict then says why it is not pruned). A topic with neither says nothing.
    /// </summary>
    private async Task<IReadOnlyList<KafkaTopicPruning>> PruneAsync(
        KafkaScanRequest request,
        PredicateNode? sharedTree,
        Dictionary<string, (PredicateParseResult Parsed, KafkaReadGrant Grant)>? searches,
        KafkaTopicName[] topics,
        Dictionary<string, TopicScanContext> contexts,
        ResolvedWindow window,
        IReadOnlyList<PartitionRange> ranges,
        List<ClauseVerdict> verdicts,
        KafkaScanOwner owner,
        KafkaClusterCost cost,
        CancellationToken cancellationToken)
    {
        WireIsolation isolation = request.Isolation == KafkaIsolation.Committed ? WireIsolation.ReadCommitted : WireIsolation.ReadUncommitted;
        List<KafkaTopicPruning> pruning = [];

        foreach (KafkaTopicName topic in topics)
        {
            if (!contexts.TryGetValue(topic.Value, out TopicScanContext? context)
                || !verdicts.Any(x => x.Topic == topic.Value && x.Field == "key"))
            {
                continue;
            }

            // A topic with a search of its own is pruned by that search's key clauses and by no other topic's.
            IReadOnlyList<string>? keySet = KeyPruning.KeySet(searches is null ? sharedTree : searches[topic.Value].Parsed.Tree);
            bool declared = context.Descriptor.Partitioner is not null;

            if (!declared && keySet is null)
            {
                continue;
            }

            int partitionCount = window.Ranges.Count(x => x.Topic == topic.Value);
            PruneRequest ask = new(
                request.Cluster,
                topic.Value,
                context.Descriptor,
                keySet,
                context.Redactor.ForKey() is not null,
                request.DisablePruning,
                partitionCount,
                [.. ranges.Where(x => x.Topic == topic.Value)],
                isolation,
                request.ContinueFrom?.Pruning.FirstOrDefault(x => x.Topic == topic.Value && x.IsPruned));

            pruning.Add(await verifier.DecideAsync(ask, owner, cost, cancellationToken).ConfigureAwait(false));
        }

        return pruning;
    }

    /// <summary>The ranges a scan reads: a pruned topic keeps only its pruned partitions.</summary>
    private static IReadOnlyList<PartitionRange> Pruned(IReadOnlyList<PartitionRange> ranges, IReadOnlyList<KafkaTopicPruning> pruning)
    {
        if (!pruning.Any(static x => x.IsPruned))
        {
            return ranges;
        }

        Dictionary<string, IReadOnlyList<int>> keep = pruning.Where(static x => x.IsPruned).ToDictionary(static x => x.Topic, static x => x.Partitions, StringComparer.Ordinal);
        return [.. ranges.Where(x => !keep.TryGetValue(x.Topic, out IReadOnlyList<int>? partitions) || partitions.Contains(x.Partition))];
    }

    /// <summary>Replaces the verdict of a key <c>=</c> or <c>in</c> with the pruning's, or says on it why the topic is not pruned (decision 7.5).</summary>
    private static List<ClauseVerdict> ApplyPruning(List<ClauseVerdict> verdicts, IReadOnlyList<KafkaTopicPruning> pruning)
    {
        if (pruning.Count == 0)
        {
            return verdicts;
        }

        Dictionary<string, KafkaTopicPruning> byTopic = pruning.ToDictionary(static x => x.Topic, StringComparer.Ordinal);
        List<ClauseVerdict> result = [];

        foreach (ClauseVerdict verdict in verdicts)
        {
            if (verdict.Field != "key" || !byTopic.TryGetValue(verdict.Topic, out KafkaTopicPruning? topic))
            {
                result.Add(verdict);
                continue;
            }

            bool fixesKey = verdict.Operator is PredicateOperator.Equals or PredicateOperator.In;

            if (topic.IsPruned)
            {
                // Only the clause that fixes the key is pruned; a `~` beside it is still a scan of what is read.
                result.Add(fixesKey ? verdict with { Kind = topic.VerdictKind, Description = topic.VerdictText! } : verdict);
            }
            else if (fixesKey || topic.NotPrunedReason == "the search does not fix the key with = or in")
            {
                result.Add(verdict with { Description = verdict.Description + "; not pruned: " + topic.NotPrunedReason });
            }
            else
            {
                result.Add(verdict);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public KafkaScanLimits LimitsFor(string cluster) => KafkaProfileCeilings.Cap(registry.Get(cluster).Profile, settings.Limits);

    /// <inheritdoc />
    public KafkaScanStart Start(KafkaScanPlan plan, KafkaScanOwner owner, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(owner);

        if (plan.IsTail)
        {
            throw new ArgumentException("A live tail's plan is started with StartTail, not Start.", nameof(plan));
        }

        if (coordinator.IsShuttingDown)
        {
            return Refuse(plan, KafkaScanRefusal.ShuttingDown);
        }

        if (!plan.IsValid)
        {
            return Refuse(plan, KafkaScanRefusal.PredicateInvalid);
        }

        Guid scanId = Guid.NewGuid();
        KafkaClusterCost cost = clusterCosts.For(registry.Get(plan.Request.Cluster));
        ScanRunInputs inputs = new(
            scanId,
            plan,
            owner,
            wires.For(plan.Request.Cluster),
            settings,
            governor,
            ownerBudget,
            time,
            loggers,
            once,
            observer,
            ClusterGovernor: cost.Governor,
            ClusterOwnerBudget: cost.OwnerBudget);
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

        if (cost.Remaining(owner.Key, ownerBudget) <= 0)
        {
            return Refuse(plan, KafkaScanRefusal.OwnerBudgetSpent, cost.Spent(owner.Key, ownerBudget));
        }

        if (limiter.TryAcquire(owner.Key, out IDisposable? lease) is { } refusal)
        {
            return Refuse(plan, refusal);
        }

        return Launch(plan, inputs, lease, offsets, isolation);
    }

    /// <inheritdoc />
    public KafkaScanStart StartTail(KafkaScanPlan plan, KafkaScanOwner owner)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(owner);

        if (!plan.IsTail)
        {
            throw new ArgumentException("A scan's plan is started with Start, not StartTail.", nameof(plan));
        }

        if (coordinator.IsShuttingDown)
        {
            return Refuse(plan, KafkaScanRefusal.ShuttingDown);
        }

        if (!plan.IsValid)
        {
            return Refuse(plan, KafkaScanRefusal.PredicateInvalid);
        }

        // The profile forbids before anything is spent or taken: a Production cluster is never tailed, whatever the capability says (D47).
        KafkaClusterDefinition definition = registry.Get(plan.Request.Cluster);

        if (KafkaProfileCeilings.ForbidsTail(definition.Profile))
        {
            return Refuse(plan, KafkaScanRefusal.ProfileForbids);
        }

        Guid scanId = Guid.NewGuid();
        KafkaClusterCost cost = clusterCosts.For(definition);
        ScanRunInputs inputs = new(
            scanId,
            plan,
            owner,
            wires.For(plan.Request.Cluster),
            settings,
            governor,
            ownerBudget,
            time,
            loggers,
            once,
            observer,
            ClusterGovernor: cost.Governor,
            ClusterOwnerBudget: cost.OwnerBudget,
            Tail: true);
        string isolation = plan.Request.Isolation.ToString();

        // A plan with no partition to follow (every topic refused) starts no consumer and takes no slot; the run says why it ended.
        if (plan.Ranges.Count == 0)
        {
            ScanRun empty = new(inputs);
            KafkaScanResult result = empty.Execute();
            empty.Dispose();
            return KafkaScanStart.Of(new KafkaScanHandle(empty, Task.FromResult(result), time));
        }

        if (cost.Remaining(owner.Key, ownerBudget) <= 0)
        {
            return Refuse(plan, KafkaScanRefusal.OwnerBudgetSpent, cost.Spent(owner.Key, ownerBudget));
        }

        // A tail holds one of MaxConcurrentScans and one of MaxConcurrentScansPerOwner while it reads (a tail is no heavier than a scan: its ring
        // is at most MaxMatches), and releases both when its segment ends.
        if (limiter.TryAcquire(owner.Key, out IDisposable? lease) is { } refusal)
        {
            return Refuse(plan, refusal);
        }

        return Launch(plan, inputs, lease, 0, isolation);
    }

    /// <summary>
    /// Runs a scan or a tail on a thread of its own. From here the run holds both slots; until the thread that releases them has started, a
    /// failure must release them here, or the slots are gone for the life of the process.
    /// </summary>
    private KafkaScanStart Launch(KafkaScanPlan plan, ScanRunInputs inputs, IDisposable? lease, long offsets, string isolation)
    {
        Guid scanId = inputs.ScanId;
        ScanRun? run = null;

        try
        {
            run = new ScanRun(inputs);
            ScanRun started = run;
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
                        return started.Execute();
                    }
                    finally
                    {
                        started.Dispose();
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
        catch
        {
            run?.Dispose();
            lease?.Dispose();
            throw;
        }
    }

    private KafkaScanStart Refuse(KafkaScanPlan plan, KafkaScanRefusal refusal, KafkaSpentBudget? spentBudget = null)
    {
        string cluster = KafkaLogValue.Cap(plan.Request.Cluster)!;
        string text = refusal.ToString();
        KStudioLog.ScanRefused(loggers.Scans, cluster, text);
        return KafkaScanStart.Refused(refusal, spentBudget);
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
        KafkaClusterCost cost,
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
                .MeanRecordBytesAsync(request.Cluster, topic.Key, [.. topic], isolation, owner, cost, cancellationToken)
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
internal sealed class KafkaScanCoordinator(TimeProvider time, KStudioLoggers loggers) : IAsyncDisposable, IDisposable
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

    /// <summary>
    /// The synchronous form, for a container that is disposed with <c>Dispose()</c> (a host that never awaits its provider): the same stop,
    /// the same wait. A scan runs on a thread of its own and nothing here captures a context, so blocking cannot deadlock.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

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
        services.TryAddSingleton(static provider => new KafkaClusterCostControls(provider.GetRequiredService<KafkaEngineSnapshot>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(static provider => new KafkaScanLimiter(provider.GetRequiredService<KafkaEngineSnapshot>()));
        services.TryAddSingleton(static provider => new OwnerByteBudget(provider.GetRequiredService<KafkaEngineSnapshot>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<RecordSizeSampler>();
        services.TryAddSingleton<PartitionerVerifier>();
        services.TryAddSingleton<KafkaDecoderResolver>();
        services.TryAddSingleton<KafkaRecordIndexResolver>();
        services.TryAddSingleton<KafkaIndexPlanner>();
        services.TryAddSingleton<KafkaScanCoordinator>();
        services.TryAddSingleton<IKafkaScanner, KafkaScanner>();
        return services;
    }
}
