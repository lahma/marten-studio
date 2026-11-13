using KStudio.Core.Clusters;
using KStudio.Core.Logging;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Windows;

namespace KStudio.Core.Index;

/// <summary>
/// The host's record index, activated on first use (AGENTS.md D48): once per engine, from the root provider, behind a lazy that is
/// <see cref="LazyThreadSafetyMode.PublicationOnly" /> exactly as a topic's decoder is, so a constructor that throws is tried again on the next plan and
/// is never cached as the answer.
/// </summary>
internal sealed class KafkaRecordIndexResolver
{
    private readonly Lazy<IKafkaRecordIndex>? index;

    /// <summary>Creates the resolver.</summary>
    /// <param name="settings">The engine's options as validated: they say whether an index was registered, and how to make it.</param>
    /// <param name="services">The root provider the index is created from.</param>
    public KafkaRecordIndexResolver(KafkaEngineSnapshot settings, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(services);

        Name = settings.RecordIndexName;

        if (settings.RecordIndexSource is { } source)
        {
            index = new Lazy<IKafkaRecordIndex>(() => source(services), LazyThreadSafetyMode.PublicationOnly);
        }
    }

    /// <summary>Whether the host registered an index.</summary>
    public bool IsRegistered => index is not null;

    /// <summary>The registered index's type name, or <see langword="null" /> when there is none: for the Settings page and the log.</summary>
    public string? Name { get; }

    /// <summary>The index, created on first call; <see langword="null" /> when none is registered. Throws what the index's constructor throws.</summary>
    public IKafkaRecordIndex? Get() => index?.Value;
}

/// <summary>What a search plan asks of the index: the plan's own facts, nothing the index can reach back through.</summary>
/// <param name="Definition">The cluster.</param>
/// <param name="Topics">The topics the search names; every one has passed every gate.</param>
/// <param name="Window">The window the query carries.</param>
/// <param name="Tree">The parsed search.</param>
/// <param name="Descriptors">The topics' descriptors, in the order of <paramref name="Topics" />.</param>
/// <param name="Redactors">Every topic's redactor.</param>
/// <param name="Ranges">The ranges the plan would read: the window's, after pruning.</param>
internal sealed record IndexAsk(
    KafkaClusterDefinition Definition,
    IReadOnlyList<string> Topics,
    KafkaWindow Window,
    PredicateNode? Tree,
    IReadOnlyList<KafkaTopicDescriptor> Descriptors,
    IReadOnlyList<IKafkaRedactor> Redactors,
    IReadOnlyList<PartitionRange> Ranges);

/// <summary>
/// Asks the host's record index once while a search is planned, and narrows the plan to what it names (AGENTS.md D48). It is asked only after every
/// gate of the search passed, about topics the visitor may search and a field no rule redacts; it is given the request timeout; and whatever it does
/// wrong (throws, hangs, cannot be created, names things that are not there) costs nothing but the narrowing: the whole window is read.
/// </summary>
internal sealed class KafkaIndexPlanner(
    KafkaRecordIndexResolver resolver,
    KafkaEngineSnapshot settings,
    TimeProvider time,
    KStudioLoggers loggers,
    KafkaLogOnce once)
{
    /// <summary>Narrows <paramref name="ask" />'s ranges, or says why not; <see langword="null" /> when the index was not asked at all (none registered, or no clause to ask).</summary>
    /// <param name="ask">The plan so far.</param>
    /// <param name="cancellationToken">Abandons the planning; the index's own timeout is not this.</param>
    public async Task<(IReadOnlyList<PartitionRange> Ranges, KafkaIndexNarrowing? Narrowing)> NarrowAsync(IndexAsk ask, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ask);

        if (!resolver.IsRegistered || IndexClauseSelector.Choose(ask.Tree, ask.Descriptors, ask.Redactors) is not { } clause)
        {
            return (ask.Ranges, null);
        }

        long window = ask.Ranges.Sum(static x => Math.Max(0, x.End - x.Start));
        KafkaIndexAnswer? answer;
        string? failure;

        try
        {
            IKafkaRecordIndex index = resolver.Get()!;
            KafkaIndexQuery query = new(
                ask.Definition.Key,
                ask.Definition.TenantId,
                ask.Topics,
                ask.Window.From,
                ask.Window.To,
                clause.Field,
                clause.FieldName,
                clause.CorrelationKind,
                clause.Value);

            (answer, failure) = await AskAsync(index, query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // The index could not be created.
            (answer, failure) = (null, e.GetType().Name);
        }

        if (answer is null)
        {
            LogNotAnswered(ask.Definition.Key, failure!);
            return (ask.Ranges, KafkaIndexNarrowing.DidNotAnswer(window));
        }

        (IReadOnlyList<PartitionRange> narrowed, KafkaIndexNarrowing narrowing) = IndexNarrower.Narrow(ask.Ranges, answer, ask.Topics);
        return (narrowed, narrowing);
    }

    private async Task<(KafkaIndexAnswer? Answer, string? Failure)> AskAsync(IKafkaRecordIndex index, KafkaIndexQuery query, CancellationToken cancellationToken)
    {
        // The request timeout bounds the wait even for an index that ignores its token: the abandoned call is not awaited again.
        using CancellationTokenSource timeout = new(settings.RequestTimeout, time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            KafkaIndexAnswer? answer = await index.FindAsync(query, linked.Token).AsTask().WaitAsync(linked.Token).ConfigureAwait(false);
            return (answer ?? KafkaIndexAnswer.None, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, "Timeout");
        }
        catch (Exception e)
        {
            return (null, e.GetType().Name);
        }
    }

    /// <summary>Event 9679 (Debug): the index's type, the cluster and the kind of failure, once per cluster per window. Never the query, a literal or the message.</summary>
    private void LogNotAnswered(string cluster, string kind)
    {
        string clusterText = KafkaLogValue.Cap(cluster)!;

        string indexText = KafkaLogValue.Cap(resolver.Name ?? "index")!;
        string kindText = KafkaLogValue.Cap(kind)!;

        if (once.ShouldLog($"index|{clusterText}"))
        {
            KStudioLog.RecordIndexNotAnswered(loggers.Scans, indexText, clusterText, kindText);
        }
    }
}
