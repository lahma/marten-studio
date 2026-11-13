using System.Globalization;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Scanning;
using KStudio.Core.Windows;

using Microsoft.Extensions.Logging;

namespace KStudio.Services;

/// <summary>A plan the visitor has seen: the input it was made from, the engine's tail plan, and what the visitor may read of the records it finds.</summary>
/// <param name="Input">What was asked.</param>
/// <param name="Plan">The engine's plan: verdicts and notes, or the errors of the text.</param>
/// <param name="Grant">What the tail will read: the envelope, or the payload as well (D-PAYLOAD decision 1.2).</param>
internal sealed record KafkaTailPlanned(KafkaTailInput Input, KafkaScanPlan Plan, KafkaGrantDecision Grant);

/// <summary>What <see cref="KafkaTailService.PlanAsync" /> returns: a refusal sentence, or a plan.</summary>
internal abstract record KafkaTailPlanResult
{
    private KafkaTailPlanResult()
    {
    }

    /// <summary>The plan was refused before the engine was asked.</summary>
    /// <param name="Text">The sentence the page shows.</param>
    public sealed record Refused(string Text) : KafkaTailPlanResult;

    /// <summary>The engine planned it (the plan may still hold errors in the text: see <see cref="KafkaScanPlan.IsValid" />).</summary>
    /// <param name="Value">The plan.</param>
    public sealed record Planned(KafkaTailPlanned Value) : KafkaTailPlanResult;
}

/// <summary>What <see cref="KafkaTailService.StartAsync" /> returns: a refusal sentence, or a running segment.</summary>
internal abstract record KafkaTailStartResult
{
    private KafkaTailStartResult()
    {
    }

    /// <summary>The segment did not start.</summary>
    /// <param name="Text">The sentence the page shows.</param>
    public sealed record Refused(string Text) : KafkaTailStartResult;

    /// <summary>The segment runs; the session is the one it belongs to (new, for a tail's first segment).</summary>
    /// <param name="Segment">The segment the page reads from and disposes.</param>
    public sealed record Running(KafkaTailSegment Segment) : KafkaTailStartResult;
}

/// <summary>
/// The live tail page's operations (D-M5 decision 3.8, AGENTS.md D46), scoped to a circuit. <b>The order of every call</b> is hard rule 5's: the capability
/// (<see cref="KafkaCapabilityGuard.Require" />, with <c>SearchRecords</c> first), the cluster's profile, the input's own bounds, the cluster policy and
/// then the policies for every topic named - any refusal refuses the whole tail, never narrows it - then the grant, then the engine, then the audit. A
/// plan asks all of it, and <b>so does the start of every segment</b>: a resume is a new read, and a policy that changed while the tail was paused
/// takes effect at the pause (at most <c>TailPauseAfter</c>), never mid-segment.
/// </summary>
/// <remarks>
/// One audit entry per tail <i>session</i> (the ring entry and event 9608, written when the session ends), the start logged as 9607; a tail whose first
/// segment was refused or failed to start is one entry and event 9609 and leaves no session; a refused resume is counted in the session's entry. The
/// visitor's typed text reaches neither: the audit carries <see cref="KafkaScanPlan.AuditText" />.
/// </remarks>
/// <param name="capabilities">The capability switch.</param>
/// <param name="authorization">The cluster and capability policies.</param>
/// <param name="grants">What a visitor may read of a record: the envelope, or the payload as well.</param>
/// <param name="scanner">The engine's scanner.</param>
/// <param name="registry">The clusters, whose profile may forbid a tail.</param>
/// <param name="owners">Who the circuit's scans belong to.</param>
/// <param name="actionLog">The audit.</param>
/// <param name="logger">Where events 9607 to 9609 go.</param>
internal sealed class KafkaTailService(
    KafkaCapabilityGuard capabilities,
    KafkaAuthorization authorization,
    KafkaReadGrants grants,
    IKafkaScanner scanner,
    KafkaClusterRegistry registry,
    KafkaOwnerKey owners,
    KafkaActionLog actionLog,
    ILogger<KafkaTailService> logger)
{
    /// <summary>Whether a cluster's profile forbids a live tail whatever the capability says: the page uses this to say so before a click.</summary>
    /// <param name="cluster">The cluster entry.</param>
    public bool ForbiddenByProfile(KafkaClusterEntry cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        return registry.TryGet(cluster.Key, out KafkaClusterDefinition? definition) && KafkaProfileCeilings.ForbidsTail(definition.Profile);
    }

    /// <summary>
    /// Asks the engine for a tail plan: the verdicts of each clause on each topic and the notes - all before anything is read. A plan whose text has
    /// errors is returned, not refused: the page draws the errors where the text is.
    /// </summary>
    /// <param name="input">What the visitor asked; with a cursor, a resume.</param>
    /// <param name="session">The session a resume belongs to, or <see langword="null" /> for a tail's first segment: a refusal of a resume is counted in the session's entry.</param>
    /// <param name="cancellationToken">Abandons the plan.</param>
    public async Task<KafkaTailPlanResult> PlanAsync(KafkaTailInput input, KafkaTailSession? session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        (string? refusal, KafkaTopicName[] names, KafkaGrantDecision grant) = await GateAsync(input, session, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return new KafkaTailPlanResult.Refused(refusal);
        }

        try
        {
            KafkaScanOwner owner = await owners.OwnerAsync().ConfigureAwait(false);
            KafkaScanPlan plan = await scanner.PlanTailAsync(RequestFor(input, names, grant.Grant), owner, cancellationToken).ConfigureAwait(false);

            if (!plan.IsValid)
            {
                await NotRunAsync(input, session, KafkaStudioText.PredicateInvalid, KafkaStudioText.PredicateDidNotParse(input.PredicateText?.Length ?? 0)).ConfigureAwait(false);
            }

            return new KafkaTailPlanResult.Planned(new KafkaTailPlanned(input, plan, grant));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "KStudio could not plan a live tail");
            await NotRunAsync(input, session, KafkaStudioText.TailFailed, PredicateNotRecorded(input)).ConfigureAwait(false);
            return new KafkaTailPlanResult.Refused(KafkaStudioText.TailFailed);
        }
    }

    /// <summary>
    /// Starts a planned segment of <paramref name="session" />, or the first segment of a new session when it is <see langword="null" />. Every gate runs
    /// again; the engine then checks the profile, the slots and the allowance itself and answers with a value.
    /// </summary>
    /// <param name="session">The session this segment goes on, or <see langword="null" /> for a new tail.</param>
    /// <param name="planned">The plan the visitor saw.</param>
    /// <param name="cancellationToken">Abandons the start.</param>
    public async Task<KafkaTailStartResult> StartAsync(KafkaTailSession? session, KafkaTailPlanned planned, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(planned);

        KafkaTailInput input = planned.Input;
        (string? refusal, _, KafkaGrantDecision grant) = await GateAsync(input, session, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return new KafkaTailStartResult.Refused(refusal);
        }

        // A grant lower than the one the segment was planned under refuses it (the plan's compiled predicates were judged under that grant), and so does one
        // lower than the tail's first segment had; a higher one starts with the planned (lower) grant.
        if (!grant.AtLeast(planned.Grant) || (session is not null && !planned.Grant.AtLeast(session.Grant)))
        {
            await NotRunAsync(input, session, KafkaStudioText.TailGrantChanged, AuditTextOf(input, planned.Plan)).ConfigureAwait(false);
            return new KafkaTailStartResult.Refused(KafkaStudioText.TailGrantChanged);
        }

        KafkaScanPlan plan = planned.Plan;

        if (!plan.IsValid)
        {
            await NotRunAsync(input, session, KafkaStudioText.PredicateInvalid, KafkaStudioText.PredicateDidNotParse(input.PredicateText?.Length ?? 0)).ConfigureAwait(false);
            return new KafkaTailStartResult.Refused(KafkaStudioText.PredicateInvalid);
        }

        try
        {
            KafkaScanOwner owner = await owners.OwnerAsync().ConfigureAwait(false);

            // The page was left while the gates were being asked: no segment starts for a page that is gone (it would hold a place nobody reads).
            cancellationToken.ThrowIfCancellationRequested();
            KafkaScanStart start = scanner.StartTail(plan, owner);

            if (start.Handle is not { } handle)
            {
                string text = KafkaTailText.Refusal(start.Refusal ?? KafkaScanRefusal.ShuttingDown, start.SpentBudget);
                await NotRunAsync(input, session, text, plan.AuditText).ConfigureAwait(false);
                return new KafkaTailStartResult.Refused(text);
            }

            if (session is null)
            {
                string user = await authorization.UserNameAsync().ConfigureAwait(false);
                session = new KafkaTailSession(input with { ResumeFrom = null }, plan, grant, user, RecordEnd);
                logger.TailStarted(user, string.Join(", ", input.Topics), input.Cluster.Cluster, input.Cluster.TenantId, plan.AuditText);
            }

            return new KafkaTailStartResult.Running(session.BeginSegment(handle, plan));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "KStudio could not start a live tail");
            await NotRunAsync(input, session, KafkaStudioText.TailFailed, plan.AuditText).ConfigureAwait(false);
            return new KafkaTailStartResult.Refused(KafkaStudioText.TailFailed);
        }
    }

    /// <summary>
    /// How many offsets a jump to now skips: for each partition the new plan follows and the paused tail had a place in, what was produced since that
    /// place. A partition the tail did not follow before (added since) skips nothing.
    /// </summary>
    /// <param name="session">The paused session, whose cursor says where each partition stopped.</param>
    /// <param name="fromEnd">The plan of a tail that starts at the end of each partition.</param>
    public static long SkippedByJumping(KafkaTailSession session, KafkaScanPlan fromEnd)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(fromEnd);

        if (session.Cursor is not { } cursor)
        {
            return 0;
        }

        Dictionary<(string, int), long> next = cursor.Positions.ToDictionary(static x => (x.Topic, x.Partition), static x => x.Next);
        long skipped = 0;

        foreach (PartitionRange range in fromEnd.Ranges)
        {
            if (next.TryGetValue((range.Topic, range.Partition), out long position) && range.Start > position)
            {
                skipped += range.Start - position;
            }
        }

        return skipped;
    }

    /// <summary>Everything that does not need the engine: the capability, the profile, the bounds and the policies. Returns the sentence of the first refusal, or <see langword="null" />.</summary>
    private async Task<(string? Refusal, KafkaTopicName[] Names, KafkaGrantDecision Grant)> GateAsync(KafkaTailInput input, KafkaTailSession? session, CancellationToken cancellationToken)
    {
        KafkaClusterEntry cluster = input.Cluster;
        IReadOnlyList<string> topics = input.Topics;
        string target = KafkaStudioText.TailTarget(topics);

        // 1. The capability, and the one it needs.
        try
        {
            capabilities.Require(KafkaCapability.LiveTail);
        }
        catch (KafkaCapabilityDeniedException denial)
        {
            actionLog.RecordCapabilityDenied(cluster, topics, denial, KafkaStudioText.TailAction, target);
            session?.CountRefusedResume(denial.Message);
            return (denial.Message, [], KafkaGrantDecision.EnvelopeOnly);
        }

        // 2. The cluster's profile: Production never tails, even when the capability is granted.
        if (ForbiddenByProfile(cluster))
        {
            string text = KafkaStudioText.ProfileForbids(KafkaStudioText.LiveTailAction, cluster);
            await NotRunAsync(input, session, text, PredicateNotRecorded(input)).ConfigureAwait(false);
            return (text, [], KafkaGrantDecision.EnvelopeOnly);
        }

        // 3. The input's own bounds.
        string? bounds = Bounds(input, out KafkaTopicName[] names);
        if (bounds is not null)
        {
            await NotRunAsync(input, session, bounds, PredicateNotRecorded(input)).ConfigureAwait(false);
            return (bounds, [], KafkaGrantDecision.EnvelopeOnly);
        }

        // 4. The cluster policy, then the policies for every topic: any refusal refuses the whole tail and names nothing.
        if (!await authorization.IsAuthorizedAsync(cluster, null, null, cancellationToken).ConfigureAwait(false))
        {
            actionLog.RecordClusterDenied(cluster, null, topics, authorization.PolicyFor(null) ?? "(none)", KafkaStudioText.TailAction, target, KafkaCapability.LiveTail);
            session?.CountRefusedResume(KafkaStudioText.RequestRefused);
            return (KafkaStudioText.RequestRefused, [], KafkaGrantDecision.EnvelopeOnly);
        }

        foreach (KafkaTopicName name in names)
        {
            if (!await authorization.IsAuthorizedAsync(cluster, name.Value, null, cancellationToken).ConfigureAwait(false)
                || !await authorization.IsAuthorizedAsync(cluster, name.Value, KafkaCapability.SearchRecords, cancellationToken).ConfigureAwait(false)
                || !await authorization.IsAuthorizedAsync(cluster, name.Value, KafkaCapability.LiveTail, cancellationToken).ConfigureAwait(false))
            {
                actionLog.RecordClusterDenied(cluster, name.Value, topics, authorization.PolicyFor(KafkaCapability.LiveTail) ?? "(none)", KafkaStudioText.TailAction, target, KafkaCapability.LiveTail);
                session?.CountRefusedResume(KafkaStudioText.RequestRefused);
                return (KafkaStudioText.RequestRefused, [], KafkaGrantDecision.EnvelopeOnly);
            }
        }

        // 5. What the visitor may read of the records the tail finds (D-PAYLOAD decision 1.2): only a tail whose predicate reads values is refused for it.
        bool readsValues = PredicateTree.ReadsValues(PredicateParser.Parse(input.PredicateText).Tree);
        KafkaGrantDecision grant = await grants.ForSearchAsync(cluster, names, readsValues, cancellationToken).ConfigureAwait(false);

        if (grant.Denied is { } denied)
        {
            actionLog.RecordCapabilityDenied(cluster, topics, denied, KafkaStudioText.TailAction, target);
            session?.CountRefusedResume(denied.Message);
            return (denied.Message, [], grant);
        }

        if (grant.Refusal is not null)
        {
            actionLog.RecordClusterDenied(cluster, grant.RefusedTopic, topics, authorization.PolicyFor(KafkaCapability.ReadPayloads) ?? "(none)", KafkaStudioText.TailAction, target, KafkaCapability.ReadPayloads);
            session?.CountRefusedResume(grant.Refusal);
            return (grant.Refusal, [], grant);
        }

        return (null, names, grant);
    }

    private static string? Bounds(KafkaTailInput input, out KafkaTopicName[] names)
    {
        names = [];

        if (input.Topics.Count is < 1 or > KafkaStudioText.MaxSearchTopics)
        {
            return KafkaStudioText.SearchTopicCount;
        }

        List<KafkaTopicName> parsed = [];
        foreach (string topic in input.Topics)
        {
            if (!KafkaTopicName.TryCreate(topic, out KafkaTopicName name))
            {
                return KafkaStudioText.SearchTopicInvalid;
            }

            if (!parsed.Contains(name))
            {
                parsed.Add(name);
            }
        }

        if (input.PredicateText is { Length: > KafkaStudioText.MaxPredicateLength })
        {
            return KafkaStudioText.PredicateTooLong;
        }

        names = [.. parsed];
        return null;
    }

    private static KafkaTailRequest RequestFor(KafkaTailInput input, KafkaTopicName[] names, KafkaReadGrant grant) =>
        new(
            input.Cluster.Key,
            names,
            input.PredicateText,
            grant,
            input.IncludeUncommitted ? KafkaIsolation.IncludeUncommitted : KafkaIsolation.Committed,
            input.ResumeFrom);

    private static string PredicateNotRecorded(KafkaTailInput input) =>
        string.Create(CultureInfo.InvariantCulture, $"(the tail text is not recorded, {input.PredicateText?.Length ?? 0} characters)");

    private static string AuditTextOf(KafkaTailInput input, KafkaScanPlan plan) =>
        plan.IsValid ? plan.AuditText : KafkaStudioText.PredicateDidNotParse(input.PredicateText?.Length ?? 0);

    /// <summary>
    /// Records a segment that did not run. For a tail's first segment that is the ring entry and event 9609; for a resume it is counted in the session's
    /// one entry (D-M5 decision 3.9), so a visitor who clicks Resume through a spent allowance leaves one line, not one per click.
    /// </summary>
    private async Task NotRunAsync(KafkaTailInput input, KafkaTailSession? session, string reason, string predicate)
    {
        if (session is not null)
        {
            session.CountRefusedResume(reason);
            return;
        }

        string user = await authorization.UserNameAsync().ConfigureAwait(false);

        actionLog.Record(input.Cluster, input.Topics, KafkaStudioText.TailAction, KafkaStudioText.TailTarget(input.Topics), succeeded: false, reason, KafkaCapability.LiveTail);
        logger.ActionNotRun(user, KafkaStudioText.LiveTailAction.ToLowerInvariant(), string.Join(", ", input.Topics), input.Cluster.Cluster, input.Cluster.TenantId, reason, predicate);
    }

    /// <summary>Records a session that ended, once: the ring entry and event 9608. Needs nothing from a scope that may be disposing.</summary>
    private void RecordEnd(KafkaTailSession session, KafkaScanStopReason reason)
    {
        KafkaTailInput input = session.Input;
        bool succeeded = reason is not (KafkaScanStopReason.Failed or KafkaScanStopReason.Unavailable or KafkaScanStopReason.NotAuthorized);

        actionLog.Record(
            input.Cluster,
            input.Topics,
            KafkaStudioText.TailAction,
            KafkaStudioText.TailTarget(input.Topics),
            succeeded,
            session.AuditMessage(reason),
            KafkaCapability.LiveTail);
        logger.TailEnded(
            session.User,
            string.Join(", ", input.Topics),
            input.Cluster.Cluster,
            input.Cluster.TenantId,
            KafkaTailText.Elapsed(session.CompletedElapsed),
            session.Segments,
            session.CompletedRecords,
            session.CompletedBytes,
            session.CompletedMatches,
            reason.ToString(),
            session.AuditText);
    }
}
