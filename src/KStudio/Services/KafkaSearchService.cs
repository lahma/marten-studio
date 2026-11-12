using System.Globalization;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Core.Windows;

using Microsoft.Extensions.Logging;

namespace KStudio.Services;

/// <summary>What the visitor asked to search: the form's content, before any gate has looked at it.</summary>
/// <param name="Cluster">The cluster entry in scope.</param>
/// <param name="Topics">The topics, by name.</param>
/// <param name="From">The window's start (UTC).</param>
/// <param name="To">The window's end (UTC).</param>
/// <param name="PredicateText">The visitor's typed text; it is parsed by the engine and never recorded.</param>
/// <param name="IncludeUncommitted">Whether uncommitted records are included.</param>
/// <param name="ContinueFrom">The cursor of the search this one continues, or <see langword="null" />.</param>
internal sealed record KafkaSearchInput(
    KafkaClusterEntry Cluster,
    IReadOnlyList<string> Topics,
    DateTimeOffset From,
    DateTimeOffset To,
    string? PredicateText,
    bool IncludeUncommitted,
    KafkaScanCursor? ContinueFrom = null);

/// <summary>A plan the visitor has seen: the input it was made from, and the engine's plan.</summary>
/// <param name="Input">What was asked.</param>
/// <param name="Plan">The engine's plan: verdicts, notes and estimate, or the errors of the text.</param>
internal sealed record KafkaSearchPlanned(KafkaSearchInput Input, KafkaScanPlan Plan);

/// <summary>What <see cref="KafkaSearchService.PlanAsync" /> returns: a refusal sentence, or a plan.</summary>
internal abstract record KafkaSearchPlanResult
{
    private KafkaSearchPlanResult()
    {
    }

    /// <summary>The plan was refused before the engine was asked.</summary>
    /// <param name="Text">The sentence the page shows.</param>
    public sealed record Refused(string Text) : KafkaSearchPlanResult;

    /// <summary>The engine planned it (the plan may still hold errors in the text: see <see cref="KafkaScanPlan.IsValid" />).</summary>
    /// <param name="Value">The plan.</param>
    public sealed record Planned(KafkaSearchPlanned Value) : KafkaSearchPlanResult;
}

/// <summary>What <see cref="KafkaSearchService.StartAsync" /> returns: a refusal sentence, or a running search.</summary>
internal abstract record KafkaSearchStartResult
{
    private KafkaSearchStartResult()
    {
    }

    /// <summary>The search did not start.</summary>
    /// <param name="Text">The sentence the page shows.</param>
    public sealed record Refused(string Text) : KafkaSearchStartResult;

    /// <summary>The search runs.</summary>
    /// <param name="Run">The run the page reads from and disposes.</param>
    public sealed record Running(KafkaSearchRun Run) : KafkaSearchStartResult;
}

/// <summary>
/// The search page's operations (D-UI decision 11), scoped to a circuit. <b>The order of every call</b> is hard rule 5's: the capability
/// (<see cref="KafkaCapabilityGuard.Require" />), the input's own bounds, the cluster policy and then both policies for every topic named - any refusal
/// refuses the whole request, never narrows it, because a search silently narrowed to the allowed topics would report a false negative - then the
/// engine, then the audit, on failure as well as success. <see cref="StartAsync" /> runs all of it again: a policy or the capability may have changed
/// while the confirmation dialog waited.
/// </summary>
/// <remarks>
/// The visitor's typed text is never recorded: the audit and the log carry <see cref="KafkaScanPlan.AuditText" /> (a literal on a redacted field is
/// <c>&lt;redacted&gt;</c>), or - when the text did not parse, so nobody can know which literal was on a redacted field - only its length.
/// </remarks>
/// <param name="capabilities">The capability switch.</param>
/// <param name="authorization">The cluster and capability policies.</param>
/// <param name="scanner">The engine's scanner.</param>
/// <param name="ownerBudget">The owner's hourly byte allowance.</param>
/// <param name="owners">Who the circuit's scans belong to.</param>
/// <param name="actionLog">The audit.</param>
/// <param name="logger">Where events 9600 and 9601 go.</param>
internal sealed class KafkaSearchService(
    KafkaCapabilityGuard capabilities,
    KafkaAuthorization authorization,
    IKafkaScanner scanner,
    OwnerByteBudget ownerBudget,
    KafkaOwnerKey owners,
    KafkaActionLog actionLog,
    ILogger<KafkaSearchService> logger)
{
    private const string Action = "SearchRecords";

    /// <summary>The bytes the visitor may still read this hour, for the form's line.</summary>
    public async ValueTask<long> OwnerBytesLeftThisHourAsync()
    {
        KafkaScanOwner owner = await owners.OwnerAsync().ConfigureAwait(false);
        return ownerBudget.Remaining(owner.Key);
    }

    /// <summary>The most a visitor reads in an hour, for the form's line.</summary>
    public long OwnerBytesPerHour => ownerBudget.BytesPerHour;

    /// <summary>
    /// Asks the engine for a plan: the verdicts of each clause on each topic, every note about the window, and an estimate - all before anything is
    /// read. A plan whose text has errors is returned, not refused: the page draws the errors where the text is.
    /// </summary>
    /// <param name="input">What the visitor asked.</param>
    /// <param name="cancellationToken">Abandons the plan.</param>
    public async Task<KafkaSearchPlanResult> PlanAsync(KafkaSearchInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        (string? refusal, KafkaTopicName[] names) = await GateAsync(input, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return new KafkaSearchPlanResult.Refused(refusal);
        }

        try
        {
            KafkaScanOwner owner = await owners.OwnerAsync().ConfigureAwait(false);
            KafkaScanPlan plan = await scanner.PlanAsync(RequestFor(input, names), owner, cancellationToken).ConfigureAwait(false);

            if (!plan.IsValid)
            {
                await NotRunAsync(input, KafkaStudioText.PredicateInvalid, KafkaStudioText.PredicateDidNotParse(input.PredicateText?.Length ?? 0)).ConfigureAwait(false);
            }

            return new KafkaSearchPlanResult.Planned(new KafkaSearchPlanned(input, plan));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "KStudio could not plan a search");
            await NotRunAsync(input, KafkaStudioText.SearchFailed, PredicateNotRecorded(input)).ConfigureAwait(false);
            return new KafkaSearchPlanResult.Refused(KafkaStudioText.SearchFailed);
        }
    }

    /// <summary>
    /// Starts a planned search. Every gate runs again (the capability, the bounds, the cluster policy and both policies for each topic); the engine then
    /// checks the confirmation, the slots and the allowance itself and answers with a value.
    /// </summary>
    /// <param name="planned">The plan the visitor saw.</param>
    /// <param name="confirmed">Whether the visitor confirmed the estimate.</param>
    /// <param name="cancellationToken">Abandons the start.</param>
    public async Task<KafkaSearchStartResult> StartAsync(KafkaSearchPlanned planned, bool confirmed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(planned);

        KafkaSearchInput input = planned.Input;
        (string? refusal, _) = await GateAsync(input, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return new KafkaSearchStartResult.Refused(refusal);
        }

        KafkaScanPlan plan = planned.Plan;
        string user = await authorization.UserNameAsync().ConfigureAwait(false);

        if (!plan.IsValid)
        {
            await NotRunAsync(input, KafkaStudioText.PredicateInvalid, KafkaStudioText.PredicateDidNotParse(input.PredicateText?.Length ?? 0)).ConfigureAwait(false);
            return new KafkaSearchStartResult.Refused(KafkaStudioText.PredicateInvalid);
        }

        try
        {
            KafkaScanOwner owner = await owners.OwnerAsync().ConfigureAwait(false);
            KafkaScanStart start = scanner.Start(plan, owner, confirmed);

            if (start.Handle is not { } handle)
            {
                string text = KafkaStudioText.Refusal(start.Refusal ?? KafkaScanRefusal.ShuttingDown, plan.Limits);
                await NotRunAsync(input, text, plan.AuditText).ConfigureAwait(false);
                return new KafkaSearchStartResult.Refused(text);
            }

            return new KafkaSearchStartResult.Running(new KafkaSearchRun(handle, plan, result => RecordScan(input, plan, user, result)));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "KStudio could not start a search");
            await NotRunAsync(input, KafkaStudioText.SearchFailed, plan.AuditText).ConfigureAwait(false);
            return new KafkaSearchStartResult.Refused(KafkaStudioText.SearchFailed);
        }
    }

    /// <summary>The visitor cancelled the confirmation: the search did not run, and the estimate had already read record sizes (9601).</summary>
    /// <param name="planned">The plan the visitor saw.</param>
    public async Task NotConfirmedAsync(KafkaSearchPlanned planned)
    {
        ArgumentNullException.ThrowIfNull(planned);

        await NotRunAsync(planned.Input, KafkaStudioText.NotConfirmed, planned.Plan.IsValid ? planned.Plan.AuditText : KafkaStudioText.PredicateDidNotParse(planned.Input.PredicateText?.Length ?? 0))
            .ConfigureAwait(false);
    }

    /// <summary>Everything that does not need the engine: the capability, the bounds and the policies. Returns the sentence of the first refusal, or <see langword="null" />.</summary>
    private async Task<(string? Refusal, KafkaTopicName[] Names)> GateAsync(KafkaSearchInput input, CancellationToken cancellationToken)
    {
        KafkaClusterEntry cluster = input.Cluster;
        IReadOnlyList<string> topics = input.Topics;
        string target = KafkaStudioText.SearchTarget(topics, input.From, input.To);

        // 1. The capability.
        try
        {
            capabilities.Require(KafkaCapability.SearchRecords);
        }
        catch (KafkaCapabilityDeniedException denial)
        {
            actionLog.RecordCapabilityDenied(cluster, topics, denial, Action, target);
            return (denial.Message, []);
        }

        // 2. The input's own bounds.
        string? bounds = Bounds(input, out KafkaTopicName[] names);
        if (bounds is not null)
        {
            await NotRunAsync(input, bounds, PredicateNotRecorded(input)).ConfigureAwait(false);
            return (bounds, []);
        }

        // 3. The cluster policy, then both policies for every topic: any refusal refuses the whole request and names nothing.
        if (!await authorization.IsAuthorizedAsync(cluster, null, null, cancellationToken).ConfigureAwait(false))
        {
            actionLog.RecordClusterDenied(cluster, null, topics, authorization.PolicyFor(null) ?? "(none)", Action, target, KafkaCapability.SearchRecords);
            return (KafkaStudioText.SearchNamesRefusedTopic, []);
        }

        foreach (KafkaTopicName name in names)
        {
            if (!await authorization.IsAuthorizedAsync(cluster, name.Value, null, cancellationToken).ConfigureAwait(false)
                || !await authorization.IsAuthorizedAsync(cluster, name.Value, KafkaCapability.SearchRecords, cancellationToken).ConfigureAwait(false))
            {
                actionLog.RecordClusterDenied(cluster, name.Value, topics, authorization.PolicyFor(KafkaCapability.SearchRecords) ?? "(none)", Action, target, KafkaCapability.SearchRecords);
                return (KafkaStudioText.SearchNamesRefusedTopic, []);
            }
        }

        return (null, names);
    }

    private static string? Bounds(KafkaSearchInput input, out KafkaTopicName[] names)
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

        if (new KafkaWindow(input.From, input.To).Problem is { } problem)
        {
            return problem;
        }

        if (input.PredicateText is { Length: > KafkaStudioText.MaxPredicateLength })
        {
            return KafkaStudioText.PredicateTooLong;
        }

        names = [.. parsed];
        return null;
    }

    private static KafkaScanRequest RequestFor(KafkaSearchInput input, KafkaTopicName[] names) =>
        new(
            input.Cluster.Key,
            names,
            new KafkaWindow(input.From, input.To),
            input.PredicateText,
            KafkaReadGrant.Envelope,
            input.IncludeUncommitted ? KafkaIsolation.IncludeUncommitted : KafkaIsolation.Committed,
            ContinueFrom: input.ContinueFrom);

    private static string PredicateNotRecorded(KafkaSearchInput input) =>
        string.Create(CultureInfo.InvariantCulture, $"(the search text is not recorded, {input.PredicateText?.Length ?? 0} characters)");

    /// <summary>Records a search that did not run: the ring entry and event 9601.</summary>
    private async Task NotRunAsync(KafkaSearchInput input, string reason, string predicate)
    {
        string user = await authorization.UserNameAsync().ConfigureAwait(false);
        string target = KafkaStudioText.SearchTarget(input.Topics, input.From, input.To);

        actionLog.Record(input.Cluster, input.Topics, Action, target, succeeded: false, reason, KafkaCapability.SearchRecords);
        logger.SearchNotRun(user, string.Join(", ", input.Topics), input.Cluster.Key, input.Cluster.TenantId, reason, predicate);
    }

    /// <summary>Records a search that ran, once, when it ended: the ring entry and event 9600. Needs nothing from a scope that may be disposing.</summary>
    private void RecordScan(KafkaSearchInput input, KafkaScanPlan plan, string user, KafkaScanResult? result)
    {
        string target = KafkaStudioText.SearchTarget(input.Topics, input.From, input.To);

        if (result is null)
        {
            const string Reason = "The search ended without a result.";
            actionLog.Record(input.Cluster, input.Topics, Action, target, succeeded: false, Reason, KafkaCapability.SearchRecords);
            logger.SearchNotRun(user, string.Join(", ", input.Topics), input.Cluster.Key, input.Cluster.TenantId, Reason, plan.AuditText);
            return;
        }

        bool succeeded = result.StopReason is not (KafkaScanStopReason.Failed or KafkaScanStopReason.Unavailable or KafkaScanStopReason.NotAuthorized);
        string message = string.Create(
            CultureInfo.InvariantCulture,
            $"{KafkaDisplay.Count(result.Records, "record")} and {KafkaDisplay.Bytes(result.Bytes)} read; stopped by {result.StopReason}; predicate: {plan.AuditText}");

        actionLog.Record(input.Cluster, input.Topics, Action, target, succeeded, message, KafkaCapability.SearchRecords);
        logger.SearchRan(
            user,
            string.Join(", ", input.Topics),
            input.Cluster.Key,
            input.Cluster.TenantId,
            input.From,
            input.To,
            result.Records,
            result.Bytes,
            result.StopReason.ToString(),
            plan.AuditText);
    }
}
