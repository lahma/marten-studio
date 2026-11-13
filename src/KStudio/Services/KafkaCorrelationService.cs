using System.Globalization;

using KStudio.Core.Clusters;
using KStudio.Core.Correlation;
using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Scanning;
using KStudio.Core.Windows;

using Microsoft.Extensions.Logging;

namespace KStudio.Services;

/// <summary>What a related-records search is about: a record the visitor opened, or a value a link named (a Marten document or stream id).</summary>
/// <param name="AnchorTopic">The anchor's topic; <see langword="null" /> for a seed.</param>
/// <param name="AnchorPartition">The anchor's partition.</param>
/// <param name="AnchorOffset">The anchor's offset.</param>
/// <param name="SeedLabel">For a seed, what the value is (<c>document</c> or <c>stream</c>); <see langword="null" /> for an anchor.</param>
/// <param name="SeedStore">For a seed, the store the link names, or <see langword="null" /> for any: a field that names another store is not searched.</param>
internal sealed record KafkaRelatedSubject(string? AnchorTopic, int AnchorPartition, long AnchorOffset, string? SeedLabel, string? SeedStore = null)
{
    /// <summary>Whether this is a seed: there is no record to anchor to.</summary>
    public bool IsSeed => SeedLabel is not null;

    /// <summary>The subject of a record.</summary>
    /// <param name="topic">The topic.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="offset">The offset.</param>
    public static KafkaRelatedSubject Anchor(string topic, int partition, long offset) => new(topic, partition, offset, null);

    /// <summary>The subject of a value a link named.</summary>
    /// <param name="label">What the value is: <c>document</c> or <c>stream</c>.</param>
    /// <param name="store">The store the link names, or <see langword="null" />.</param>
    public static KafkaRelatedSubject Seed(string label, string? store = null) => new(null, 0, 0, label, store);
}

/// <summary>A value a link names (decision 8 of D-CORR): a Marten document or stream id, read from the address's fragment.</summary>
/// <param name="Kind"><c>document</c> or <c>stream</c>.</param>
/// <param name="Type">The document type; <see langword="null" /> for a stream.</param>
/// <param name="Store">The store the link names, or <see langword="null" /> for any.</param>
/// <param name="Value">The id.</param>
internal sealed record KafkaRelatedSeed(string Kind, string? Type, string? Store, string Value)
{
    /// <summary>The correlation kind a field must have to be searched by this value: <c>document:{type}</c> or <c>stream</c>.</summary>
    public string CorrelationKind => string.Equals(Kind, KafkaAddress.SeedDocument, StringComparison.Ordinal) ? CorrelationDefaults.DocumentPrefix + Type : CorrelationDefaults.StreamKind;

    /// <summary>The key set a search around this value uses.</summary>
    public CorrelationKeySet Keys => CorrelationKeySet.Seed(CorrelationKind, Value);

    /// <summary>The subject of the audit.</summary>
    public KafkaRelatedSubject Subject => KafkaRelatedSubject.Seed(Kind, Store);
}

/// <summary>What the visitor asked to search around: the form's content, before any gate has looked at it.</summary>
/// <param name="Cluster">The cluster entry in scope.</param>
/// <param name="Subject">What the search is about.</param>
/// <param name="Keys">The correlation values to search for: an anchor's visible values, or one seed value.</param>
/// <param name="Topics">The topics to search, by name; the anchor's topic first.</param>
/// <param name="From">The window's start (UTC).</param>
/// <param name="To">The window's end (UTC).</param>
/// <param name="IncludeUncommitted">Whether uncommitted records are included.</param>
/// <param name="ContinueFrom">The cursor of the search this one continues, or <see langword="null" />.</param>
/// <param name="SearchAllPartitions">Whether to read every partition even where a verified partitioner would allow reading fewer.</param>
internal sealed record KafkaRelatedInput(
    KafkaClusterEntry Cluster,
    KafkaRelatedSubject Subject,
    CorrelationKeySet Keys,
    IReadOnlyList<string> Topics,
    DateTimeOffset From,
    DateTimeOffset To,
    bool IncludeUncommitted = false,
    KafkaScanCursor? ContinueFrom = null,
    bool SearchAllPartitions = false);

/// <summary>One topic of a related-records search: its own query, its own grant, and what the page shows of both.</summary>
/// <param name="Topic">The topic.</param>
/// <param name="Query">The query the topic is searched with; <see cref="CorrelationTopicQuery.HasQuery" /> is false for a topic with nothing to compare.</param>
/// <param name="Grant">What the scan may read on this topic.</param>
/// <param name="ShownText">The query as the page may show it: a literal on a field any topic of the search redacts is <c>&lt;redacted&gt;</c>.</param>
internal sealed record KafkaRelatedLane(KafkaTopicName Topic, CorrelationTopicQuery Query, KafkaReadGrant Grant, string? ShownText)
{
    /// <summary>Whether the topic is scanned at all.</summary>
    public bool IsScanned => Query.HasQuery;
}

/// <summary>The lanes of a search, after every gate has passed and before the engine is asked.</summary>
/// <param name="Input">What was asked.</param>
/// <param name="Lanes">One lane per topic, in the order asked.</param>
/// <param name="Grant">The most any topic may read: the request's grant.</param>
internal sealed record KafkaRelatedPrepared(KafkaRelatedInput Input, IReadOnlyList<KafkaRelatedLane> Lanes, KafkaReadGrant Grant);

/// <summary>What <see cref="KafkaCorrelationService.PrepareAsync" /> returns.</summary>
internal abstract record KafkaRelatedPrepareResult
{
    private KafkaRelatedPrepareResult()
    {
    }

    /// <summary>A gate refused the search.</summary>
    /// <param name="Text">The sentence the page shows.</param>
    public sealed record Refused(string Text) : KafkaRelatedPrepareResult;

    /// <summary>The lanes.</summary>
    /// <param name="Value">The prepared search.</param>
    public sealed record Prepared(KafkaRelatedPrepared Value) : KafkaRelatedPrepareResult;
}

/// <summary>A plan the visitor has seen: the lanes, and the engine's plan over them.</summary>
/// <param name="Input">What was asked.</param>
/// <param name="Lanes">One lane per topic.</param>
/// <param name="Plan">The engine's plan.</param>
internal sealed record KafkaRelatedPlanned(KafkaRelatedInput Input, IReadOnlyList<KafkaRelatedLane> Lanes, KafkaScanPlan Plan);

/// <summary>What <see cref="KafkaCorrelationService.PlanAsync" /> returns.</summary>
internal abstract record KafkaRelatedPlanResult
{
    private KafkaRelatedPlanResult()
    {
    }

    /// <summary>The plan was refused before the engine was asked, or the engine could not plan.</summary>
    /// <param name="Text">The sentence the page shows.</param>
    public sealed record Refused(string Text) : KafkaRelatedPlanResult;

    /// <summary>The engine planned it.</summary>
    /// <param name="Value">The plan.</param>
    public sealed record Planned(KafkaRelatedPlanned Value) : KafkaRelatedPlanResult;
}

/// <summary>What <see cref="KafkaCorrelationService.StartAsync" /> returns.</summary>
internal abstract record KafkaRelatedStartResult
{
    private KafkaRelatedStartResult()
    {
    }

    /// <summary>The search did not start.</summary>
    /// <param name="Text">The sentence the page shows.</param>
    public sealed record Refused(string Text) : KafkaRelatedStartResult;

    /// <summary>The search runs.</summary>
    /// <param name="Run">The run the page reads from and disposes.</param>
    public sealed record Running(KafkaSearchRun Run) : KafkaRelatedStartResult;
}

/// <summary>What <see cref="KafkaCorrelationService.OpenAnchorAsync" /> returns.</summary>
internal abstract record KafkaRelatedAnchorResult
{
    private KafkaRelatedAnchorResult()
    {
    }

    /// <summary>The capability is off, or the read failed: the sentence the record page would show.</summary>
    /// <param name="Text">The sentence.</param>
    public sealed record Refused(string Text) : KafkaRelatedAnchorResult;

    /// <summary>The topic is invalid, missing or refused by a policy: indistinguishable (D35).</summary>
    public sealed record NotAvailable : KafkaRelatedAnchorResult;

    /// <summary>The record, and what can be searched for around it.</summary>
    /// <param name="Match">The record, already redacted inside the engine (D40).</param>
    /// <param name="Fields">The correlation fields of the anchor's topic that were usable: a JSON path only where the value was shown.</param>
    /// <param name="Keys">The values; each was already visible on the anchor.</param>
    public sealed record Opened(KafkaMatch Match, IReadOnlyList<CorrelationField> Fields, CorrelationKeySet Keys) : KafkaRelatedAnchorResult;
}

/// <summary>
/// The related-records page's operations (D42, decision 3 of D-CORR), scoped to a circuit. <b>The order of every call</b> is hard rule 5's and the
/// search's: the capability, the input's own bounds, the cluster policy and then both policies for every topic named - any refusal refuses the whole
/// request, never narrows it - the per-topic <c>ReadPayloads</c> question, the engine, and the audit, on failure as well as success, <b>once for the
/// scan</b>. <see cref="StartAsync" /> runs all of it again: a policy or the capability may have changed while the confirmation dialog waited.
/// </summary>
/// <remarks>
/// No new event id: a timeline is a search, under the search's capability and the search's events 9600 and 9601. A value reaches the audit and the
/// log only inside <see cref="KafkaScanPlan.AuditText" />, where a literal on a field any topic redacts is <c>&lt;redacted&gt;</c>.
/// </remarks>
/// <param name="capabilities">The capability switch.</param>
/// <param name="authorization">The cluster and capability policies.</param>
/// <param name="grants">What a visitor may read of a record: the envelope, or the payload as well.</param>
/// <param name="records">The record page's read, for the anchor.</param>
/// <param name="registry">The engine's clusters, for each topic's correlation fields and redaction rules.</param>
/// <param name="scanner">The engine's scanner.</param>
/// <param name="owners">Who the circuit's scans belong to.</param>
/// <param name="actionLog">The audit.</param>
/// <param name="logger">Where events 9600 and 9601 go.</param>
internal sealed class KafkaCorrelationService(
    KafkaCapabilityGuard capabilities,
    KafkaAuthorization authorization,
    KafkaReadGrants grants,
    KafkaRecordService records,
    KafkaClusterRegistry registry,
    IKafkaScanner scanner,
    KafkaOwnerKey owners,
    KafkaActionLog actionLog,
    ILogger<KafkaCorrelationService> logger)
{
    /// <summary>The default window: this long either side of the anchor (OPEN-9).</summary>
    public static readonly TimeSpan DefaultHalfWindow = TimeSpan.FromMinutes(30);

    /// <summary>The longest value a seed may hold: the parser's literal bound.</summary>
    public const int MaxSeedValueLength = PredicateParser.MaxLiteralLength;

    private const string Action = "SearchRecords";

    /// <summary>
    /// Opens the anchor: the record page's read, with its gates and its 9604 audit, and, for a visitor with <c>ReadPayloads</c> on the topic, the
    /// recorded view of the value (9605) before any JSON-path key is taken from it. The keys come only from what the page shows.
    /// </summary>
    /// <param name="cluster">The cluster entry in scope.</param>
    /// <param name="topic">The anchor's topic as the address named it.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="offset">The offset.</param>
    /// <param name="cancellationToken">Abandons the read.</param>
    public async Task<KafkaRelatedAnchorResult> OpenAnchorAsync(KafkaClusterEntry cluster, string? topic, int partition, long offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        KafkaRecordPageResult page = await records.ReadAsync(cluster, topic, partition, offset, cancellationToken).ConfigureAwait(false);

        switch (page)
        {
            case KafkaRecordPageResult.CapabilityOff off:
                return new KafkaRelatedAnchorResult.Refused(off.Text);
            case KafkaRecordPageResult.Read { Value.Record: { } match } read:
            {
                IReadOnlyList<CorrelationField> fields = registry.Get(cluster.Key).Descriptor(match.Topic).CorrelationFields;

                // A JSON-path key is taken from a value the visitor was shown: that is a view, recorded and judged first (9605). A refused view,
                // or a record read without its value, leaves the path fields out - never the other fields.
                // Only when the topic has a path field: a view nobody takes a key from is not recorded for this page.
                bool valueShown = match.ReadWithPayload
                    && read.Grant.HasPayload
                    && fields.Any(static x => x.Source == CorrelationSource.JsonPath && !x.IsTrace)
                    && await records.RecordValueViewAsync(cluster, match, cancellationToken).ConfigureAwait(false);

                IReadOnlyList<CorrelationField> usable = valueShown ? fields : [.. fields.Where(static x => x.Source != CorrelationSource.JsonPath)];
                return new KafkaRelatedAnchorResult.Opened(match, usable, CorrelationExtractor.Extract(match, usable));
            }

            case KafkaRecordPageResult.Read read:
                return new KafkaRelatedAnchorResult.Refused(read.Value.Text);
            default:
                return new KafkaRelatedAnchorResult.NotAvailable();
        }
    }

    /// <summary>
    /// The declared topics of the cluster the visitor may search, in ordinal order, the anchor's topic first (it was just authorized by the read). A
    /// topic the policy refuses is absent: this is a list. For a seed, only the topics with a correlation field of its kind (and its store) are listed.
    /// <see langword="null" /> when the cluster itself is refused.
    /// </summary>
    /// <param name="cluster">The cluster entry in scope.</param>
    /// <param name="anchorTopic">The anchor's topic, or <see langword="null" /> for a seed.</param>
    /// <param name="seedKind">The seed's kind (<c>document:Order</c> or <c>stream</c>), or <see langword="null" /> for an anchor.</param>
    /// <param name="seedStore">The seed's store, or <see langword="null" /> for any.</param>
    /// <param name="cancellationToken">Abandons the question.</param>
    public async Task<IReadOnlyList<string>?> ReadableTopicsAsync(
        KafkaClusterEntry cluster, string? anchorTopic, string? seedKind, string? seedStore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        if (!capabilities.IsEnabled(KafkaCapability.SearchRecords))
        {
            return [];
        }

        if (!await authorization.IsAuthorizedAsync(cluster, null, null, cancellationToken).ConfigureAwait(false))
        {
            string user = await authorization.UserNameAsync().ConfigureAwait(false);
            logger.ClusterRefused(user, cluster.Key, cluster.TenantId, null, authorization.PolicyFor(null) ?? "(none)");
            return null;
        }

        KafkaClusterDefinition definition = registry.Get(cluster.Key);
        IEnumerable<string> declared = definition.Topics.Values
            .Where(static x => x.Declared)
            .Where(x => seedKind is null || HasSeedField(x, seedKind, seedStore))
            .Select(static x => x.Name)
            .Where(x => !string.Equals(x, anchorTopic, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

        IReadOnlyList<string> allowed = await authorization.FilterTopicsAsync(cluster, declared, null, KafkaCapability.SearchRecords, cancellationToken).ConfigureAwait(false);

        return anchorTopic is null ? allowed : [anchorTopic, .. allowed];
    }

    /// <summary>A topic's correlation fields: what the host declared and the defaults. Declarations only; nothing is read from the cluster.</summary>
    /// <param name="cluster">The cluster entry in scope.</param>
    /// <param name="topic">The topic.</param>
    public IReadOnlyList<CorrelationField> FieldsOf(KafkaClusterEntry cluster, string topic)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        return registry.Get(cluster.Key).Descriptor(topic).CorrelationFields;
    }

    /// <summary>
    /// Runs every gate and builds each topic's query and grant, without asking the engine: what the page shows before the visitor presses the
    /// button. A refusal is audited like any other.
    /// </summary>
    /// <param name="input">What the visitor asked.</param>
    /// <param name="cancellationToken">Abandons the question.</param>
    public async Task<KafkaRelatedPrepareResult> PrepareAsync(KafkaRelatedInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        (string? refusal, KafkaRelatedPrepared? prepared) = await GateAsync(input, cancellationToken).ConfigureAwait(false);

        return refusal is not null ? new KafkaRelatedPrepareResult.Refused(refusal) : new KafkaRelatedPrepareResult.Prepared(prepared!);
    }

    /// <summary>
    /// Asks the engine for a plan over the prepared lanes: the verdicts, the notes about the window and an estimate, all before anything is
    /// read beyond the sampling the estimate does.
    /// </summary>
    /// <param name="input">What the visitor asked.</param>
    /// <param name="cancellationToken">Abandons the plan.</param>
    public async Task<KafkaRelatedPlanResult> PlanAsync(KafkaRelatedInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        (string? refusal, KafkaRelatedPrepared? prepared) = await GateAsync(input, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return new KafkaRelatedPlanResult.Refused(refusal);
        }

        KafkaTopicQuery[] queries = QueriesOf(prepared!);
        if (queries.Length == 0)
        {
            await NotRunAsync(input, KafkaStudioText.RelatedNothingReason, NothingRecorded).ConfigureAwait(false);
            return new KafkaRelatedPlanResult.Refused(KafkaStudioText.RelatedNothingToSearch);
        }

        try
        {
            KafkaScanOwner owner = await owners.OwnerAsync().ConfigureAwait(false);
            KafkaScanPlan plan = await scanner.PlanAsync(RequestFor(input, queries, prepared!.Grant), owner, cancellationToken).ConfigureAwait(false);

            if (!plan.IsValid)
            {
                // The builder writes only texts that parse and compile: this is a guard, and it records nothing a visitor typed.
                logger.LogWarning("KStudio's related-records search was not valid for the engine ({Errors} errors)", plan.Errors.Count);
                await NotRunAsync(input, KafkaStudioText.SearchFailed, NothingRecorded).ConfigureAwait(false);
                return new KafkaRelatedPlanResult.Refused(KafkaStudioText.SearchFailed);
            }

            return new KafkaRelatedPlanResult.Planned(new KafkaRelatedPlanned(input, prepared.Lanes, plan));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "KStudio could not plan a related-records search");
            await NotRunAsync(input, KafkaStudioText.SearchFailed, NothingRecorded).ConfigureAwait(false);
            return new KafkaRelatedPlanResult.Refused(KafkaStudioText.SearchFailed);
        }
    }

    /// <summary>
    /// Starts a planned search. Every gate runs again (the capability, the bounds, the cluster policy, both policies for each topic and the
    /// <c>ReadPayloads</c> question); the engine then checks the confirmation, the slots and the allowance itself and answers with a value.
    /// </summary>
    /// <param name="planned">The plan the visitor saw.</param>
    /// <param name="confirmed">Whether the visitor confirmed the estimate.</param>
    /// <param name="cancellationToken">Abandons the start.</param>
    public async Task<KafkaRelatedStartResult> StartAsync(KafkaRelatedPlanned planned, bool confirmed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(planned);

        KafkaRelatedInput input = planned.Input;
        (string? refusal, KafkaRelatedPrepared? prepared) = await GateAsync(input, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return new KafkaRelatedStartResult.Refused(refusal);
        }

        // A grant lower than the one the visitor was planned under refuses the start; a higher one starts the planned (lower) grant.
        if (!GrantHeld(planned, prepared!))
        {
            await NotRunAsync(input, KafkaStudioText.GrantChanged, planned.Plan.AuditText).ConfigureAwait(false);
            return new KafkaRelatedStartResult.Refused(KafkaStudioText.GrantChanged);
        }

        KafkaScanPlan plan = planned.Plan;
        string user = await authorization.UserNameAsync().ConfigureAwait(false);

        try
        {
            KafkaScanOwner owner = await owners.OwnerAsync().ConfigureAwait(false);
            KafkaScanStart start = scanner.Start(plan, owner, confirmed);

            if (start.Handle is not { } handle)
            {
                string text = KafkaStudioText.Refusal(start.Refusal ?? KafkaScanRefusal.ShuttingDown, start.SpentBudget);
                await NotRunAsync(input, text, plan.AuditText).ConfigureAwait(false);
                return new KafkaRelatedStartResult.Refused(text);
            }

            return new KafkaRelatedStartResult.Running(new KafkaSearchRun(handle, plan, result => RecordScan(input, plan, user, result)));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "KStudio could not start a related-records search");
            await NotRunAsync(input, KafkaStudioText.SearchFailed, plan.AuditText).ConfigureAwait(false);
            return new KafkaRelatedStartResult.Refused(KafkaStudioText.SearchFailed);
        }
    }

    /// <summary>The visitor cancelled the confirmation: the search did not run, and the estimate had already read record sizes (9601).</summary>
    /// <param name="planned">The plan the visitor saw.</param>
    public async Task NotConfirmedAsync(KafkaRelatedPlanned planned)
    {
        ArgumentNullException.ThrowIfNull(planned);

        await NotRunAsync(planned.Input, KafkaStudioText.NotConfirmed, planned.Plan.AuditText).ConfigureAwait(false);
    }

    private const string NothingRecorded = "(no search text was built)";

    /// <summary>Everything that does not need the engine: the capability, the bounds, the policies, the payload question and the queries.</summary>
    private async Task<(string? Refusal, KafkaRelatedPrepared? Prepared)> GateAsync(KafkaRelatedInput input, CancellationToken cancellationToken)
    {
        KafkaClusterEntry cluster = input.Cluster;
        IReadOnlyList<string> topics = input.Topics;
        string target = KafkaStudioText.RelatedTarget(input.Subject, topics.Count, input.From, input.To);

        // 1. The capability.
        try
        {
            capabilities.Require(KafkaCapability.SearchRecords);
        }
        catch (KafkaCapabilityDeniedException denial)
        {
            actionLog.RecordCapabilityDenied(cluster, topics, denial, Action, target);
            return (denial.VisitorText, null);
        }

        // 2. The input's own bounds.
        string? bounds = Bounds(input, out KafkaTopicName[] names);
        if (bounds is not null)
        {
            await NotRunAsync(input, bounds, NothingRecorded).ConfigureAwait(false);
            return (bounds, null);
        }

        // 3. The cluster policy, then both policies for every topic: any refusal refuses the whole request and names nothing.
        if (!await authorization.IsAuthorizedAsync(cluster, null, null, cancellationToken).ConfigureAwait(false))
        {
            actionLog.RecordClusterDenied(cluster, null, topics, authorization.PolicyFor(null) ?? "(none)", Action, target, KafkaCapability.SearchRecords);
            return (KafkaStudioText.SearchNamesRefusedTopic, null);
        }

        foreach (KafkaTopicName name in names)
        {
            if (!await authorization.IsAuthorizedAsync(cluster, name.Value, null, cancellationToken).ConfigureAwait(false)
                || !await authorization.IsAuthorizedAsync(cluster, name.Value, KafkaCapability.SearchRecords, cancellationToken).ConfigureAwait(false))
            {
                actionLog.RecordClusterDenied(cluster, name.Value, topics, authorization.PolicyFor(KafkaCapability.SearchRecords) ?? "(none)", Action, target, KafkaCapability.SearchRecords);
                return (KafkaStudioText.SearchNamesRefusedTopic, null);
            }
        }

        // 4. What the visitor may read, per topic, and each topic's own query. A path is searched only where the payload may be read; a refusal here
        // is never an error: the topic is searched by its other fields, and the lane says so.
        KafkaClusterDefinition definition = registry.Get(cluster.Key);
        List<(KafkaTopicName Name, KafkaTopicDescriptor Descriptor, IKafkaRedactor Redactor, CorrelationTopicQuery Query, KafkaGrantDecision Decision)> built = [];

        foreach (KafkaTopicName name in names)
        {
            KafkaTopicDescriptor descriptor = definition.Descriptor(name.Value);
            IKafkaRedactor redactor = new KafkaRedactor(descriptor.Settings.Rules);

            bool hasPath = descriptor.CorrelationFields.Any(static x => x.Source == CorrelationSource.JsonPath && !x.IsTrace);
            KafkaGrantDecision decision = hasPath
                ? await grants.ForRecordAsync(cluster, name, cancellationToken).ConfigureAwait(false)
                : KafkaGrantDecision.EnvelopeOnly;

            CorrelationTopicQuery query = CorrelationQueryBuilder.Build(input.Keys, descriptor, redactor, payloadGranted: decision.HasPayload, seedStore: input.Subject.SeedStore);
            built.Add((name, descriptor, redactor, query, decision));
        }

        IKafkaRedactor[] redactors = [.. built.Select(static x => x.Redactor)];
        List<KafkaRelatedLane> lanes = [];

        foreach ((KafkaTopicName name, _, _, CorrelationTopicQuery query, KafkaGrantDecision decision) in built)
        {
            // Payload only on a topic whose query reads a value: every other topic is read as the envelope, whatever the visitor may do elsewhere.
            KafkaReadGrant laneGrant = query.HasQuery && query.ReadsValues && decision.HasPayload ? KafkaReadGrant.Envelope | KafkaReadGrant.Payload : KafkaReadGrant.Envelope;

            lanes.Add(new KafkaRelatedLane(name, query, laneGrant, query.PredicateText is { } text ? Shown(text, redactors) : null));
        }

        KafkaReadGrant requestGrant = lanes.Any(static x => x.IsScanned && x.Grant.HasFlag(KafkaReadGrant.Payload)) ? KafkaReadGrant.Envelope | KafkaReadGrant.Payload : KafkaReadGrant.Envelope;
        return (null, new KafkaRelatedPrepared(input, lanes, requestGrant));
    }

    /// <summary>The query as the page shows it: a literal on a field any topic of the search redacts is <c>&lt;redacted&gt;</c>, as in the audit.</summary>
    private static string Shown(string text, IKafkaRedactor[] redactors) =>
        PredicateFormatter.Format(PredicateParser.Parse(text).Tree, clause => redactors.Any(x => PredicateCompiler.IsRedacted(clause.Field, x)));

    private static KafkaTopicQuery[] QueriesOf(KafkaRelatedPrepared prepared) =>
        [.. prepared.Lanes.Where(static x => x.IsScanned).Select(static x => new KafkaTopicQuery(x.Topic, x.Query.PredicateText!, x.Grant))];

    /// <summary>Whether every topic the plan scanned still has a query and at least the grant it was planned with.</summary>
    private static bool GrantHeld(KafkaRelatedPlanned planned, KafkaRelatedPrepared now)
    {
        foreach (KafkaRelatedLane was in planned.Lanes.Where(static x => x.IsScanned))
        {
            KafkaRelatedLane? current = now.Lanes.FirstOrDefault(x => x.Topic.Equals(was.Topic));
            if (current is not { IsScanned: true } || (current.Grant & was.Grant) != was.Grant)
            {
                return false;
            }
        }

        return true;
    }

    private static string? Bounds(KafkaRelatedInput input, out KafkaTopicName[] names)
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

        if (input.Keys.IsEmpty)
        {
            return KafkaStudioText.RelatedNoKeys;
        }

        if (input.Keys.Values.Any(static x => x.Value.Length > MaxSeedValueLength))
        {
            return KafkaStudioText.RelatedValueTooLong;
        }

        names = [.. parsed];
        return null;
    }

    private static KafkaScanRequest RequestFor(KafkaRelatedInput input, KafkaTopicQuery[] queries, KafkaReadGrant grant) =>
        new(
            input.Cluster.Key,
            [.. queries.Select(static x => x.Topic)],
            new KafkaWindow(input.From, input.To),
            null,
            grant,
            input.IncludeUncommitted ? KafkaIsolation.IncludeUncommitted : KafkaIsolation.Committed,
            ContinueFrom: input.ContinueFrom,
            DisablePruning: input.SearchAllPartitions,
            PerTopic: queries);

    /// <summary>Records a search that did not run: the ring entry and event 9601.</summary>
    private async Task NotRunAsync(KafkaRelatedInput input, string reason, string predicate)
    {
        string user = await authorization.UserNameAsync().ConfigureAwait(false);
        string target = KafkaStudioText.RelatedTarget(input.Subject, input.Topics.Count, input.From, input.To);

        actionLog.Record(input.Cluster, input.Topics, Action, target, succeeded: false, reason, KafkaCapability.SearchRecords);
        logger.SearchNotRun(user, string.Join(", ", input.Topics), input.Cluster.Key, input.Cluster.TenantId, reason, predicate);
    }

    /// <summary>Records a search that ran, once, when it ended: the ring entry and event 9600. Needs nothing from a scope that may be disposing.</summary>
    private void RecordScan(KafkaRelatedInput input, KafkaScanPlan plan, string user, KafkaScanResult? result)
    {
        string target = KafkaStudioText.RelatedTarget(input.Subject, input.Topics.Count, input.From, input.To);

        if (result is null)
        {
            const string Reason = "The search ended without a result.";
            actionLog.Record(input.Cluster, input.Topics, Action, target, succeeded: false, Reason, KafkaCapability.SearchRecords);
            logger.SearchNotRun(user, string.Join(", ", input.Topics), input.Cluster.Key, input.Cluster.TenantId, Reason, plan.AuditText);
            return;
        }

        bool succeeded = result.StopReason is not (KafkaScanStopReason.Failed or KafkaScanStopReason.Unavailable or KafkaScanStopReason.NotAuthorized);
        bool readValues = plan.ReadsValues && plan.Request.Grant.HasFlag(KafkaReadGrant.Payload);
        string message = string.Create(
            CultureInfo.InvariantCulture,
            $"{KafkaDisplay.Count(result.Records, "record")} and {KafkaDisplay.Bytes(result.Bytes)} read; stopped by {result.StopReason}{result.PruningAuditText}; predicate: {plan.AuditText}{(readValues ? "; values read" : string.Empty)}");

        actionLog.Record(input.Cluster, input.Topics, Action, target, succeeded, message, readValues ? KafkaCapability.ReadPayloads : KafkaCapability.SearchRecords);
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

    /// <summary>Whether a topic has a correlation field of the seed's kind and store.</summary>
    private static bool HasSeedField(KafkaTopicDescriptor topic, string kind, string? store) =>
        topic.CorrelationFields.Any(x =>
            string.Equals(x.Kind, kind, StringComparison.Ordinal)
            && (store is null || x.Store is null || string.Equals(x.Store, store, StringComparison.OrdinalIgnoreCase)));
}
