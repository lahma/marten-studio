using System.Globalization;

using KStudio.Core.Clusters;
using KStudio.Core.Scanning;
using KStudio.Core.Windows;

using Microsoft.Extensions.Logging;

namespace KStudio.Services;

/// <summary>What the record page gets: the read, one value for "missing" and "refused", or the capability being off.</summary>
internal abstract record KafkaRecordPageResult
{
    private KafkaRecordPageResult()
    {
    }

    /// <summary>The capability is off: the sentence names the option a host would have to set.</summary>
    /// <param name="Text">The refusal.</param>
    public sealed record CapabilityOff(string Text) : KafkaRecordPageResult;

    /// <summary>The topic is invalid, missing from the cluster's listing or refused by a policy: indistinguishable (D35).</summary>
    public sealed record NotAvailable : KafkaRecordPageResult;

    /// <summary>The engine's read, whatever its status.</summary>
    /// <param name="Value">The record, its neighbours and the status with its sentence.</param>
    /// <param name="Grant">What the read was allowed to keep of the values: the page says why a value is absent from it.</param>
    public sealed record Read(KafkaRecordRead Value, KafkaGrantDecision Grant) : KafkaRecordPageResult;
}

/// <summary>
/// The record page's read (D-UI decision 14), scoped to a circuit: the capability, the cluster policy and then both policies for the one topic - a
/// refusal reads like a missing topic and is recorded - and then the engine's read by offset, which is a scan of at most 21 offsets that takes one of the
/// owner's slots. A view is audited as 9604 (<c>ViewRecord</c>), once per circuit per <c>(cluster, topic, partition, offset)</c>, so a re-render or a
/// return visit is not a second entry; a read that was refused or failed is always recorded. A view that shows the record's value is a different read
/// and is audited as 9605 under <c>ReadPayloads</c> instead, once per circuit per record as well (<see cref="RecordValueViewAsync" />).
/// </summary>
/// <param name="capabilities">The capability switch.</param>
/// <param name="authorization">The cluster and capability policies.</param>
/// <param name="grants">What a visitor may read of a record's value.</param>
/// <param name="engine">The engine's settings, whose decode limit is the value the record asked for may keep.</param>
/// <param name="scanner">The engine's scanner.</param>
/// <param name="owners">Who the circuit's scans belong to.</param>
/// <param name="actionLog">The audit.</param>
/// <param name="logger">Where events 9604 and 9605 go.</param>
internal sealed class KafkaRecordService(
    KafkaCapabilityGuard capabilities,
    KafkaAuthorization authorization,
    KafkaReadGrants grants,
    KafkaEngineSnapshot engine,
    IKafkaScanner scanner,
    KafkaOwnerKey owners,
    KafkaActionLog actionLog,
    ILogger<KafkaRecordService> logger)
{
    /// <summary>How many records on each side of the one asked for are read (D-UI OPEN-4: 11 records fit one screen and one fetch).</summary>
    public const int Neighbours = 5;

    private const string Action = "ViewRecord";
    private const int MaxRememberedViews = 1_000;

    private readonly HashSet<string> viewed = new(StringComparer.Ordinal);

    /// <summary>Reads one record and its neighbours.</summary>
    /// <param name="cluster">The cluster entry in scope.</param>
    /// <param name="topic">The topic as the address named it.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="offset">The offset.</param>
    /// <param name="cancellationToken">Abandons the read.</param>
    public async Task<KafkaRecordPageResult> ReadAsync(KafkaClusterEntry cluster, string? topic, int partition, long offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        string target = (topic ?? string.Empty) + "/" + partition.ToString(CultureInfo.InvariantCulture) + "/" + offset.ToString(CultureInfo.InvariantCulture);
        IReadOnlyList<string> topics = topic is null ? [] : [topic];

        try
        {
            capabilities.Require(KafkaCapability.SearchRecords);
        }
        catch (KafkaCapabilityDeniedException denial)
        {
            actionLog.RecordCapabilityDenied(cluster, topics, denial, Action, target);
            return new KafkaRecordPageResult.CapabilityOff(denial.Message);
        }

        if (!KafkaTopicName.TryCreate(topic, out KafkaTopicName name) || partition < 0 || offset < 0)
        {
            return new KafkaRecordPageResult.NotAvailable();
        }

        if (!await authorization.IsAuthorizedAsync(cluster, null, null, cancellationToken).ConfigureAwait(false))
        {
            actionLog.RecordClusterDenied(cluster, null, topics, authorization.PolicyFor(null) ?? "(none)", Action, target, KafkaCapability.SearchRecords);
            return new KafkaRecordPageResult.NotAvailable();
        }

        if (!await authorization.IsAuthorizedAsync(cluster, name.Value, null, cancellationToken).ConfigureAwait(false)
            || !await authorization.IsAuthorizedAsync(cluster, name.Value, KafkaCapability.SearchRecords, cancellationToken).ConfigureAwait(false))
        {
            actionLog.RecordClusterDenied(cluster, name.Value, topics, authorization.PolicyFor(KafkaCapability.SearchRecords) ?? "(none)", Action, target, KafkaCapability.SearchRecords);
            return new KafkaRecordPageResult.NotAvailable();
        }

        string user = await authorization.UserNameAsync().ConfigureAwait(false);

        // What the visitor may read of the value (D-PAYLOAD decision 1.2): asking, and being told no, refuses nothing and records nothing.
        KafkaGrantDecision grant = await grants.ForRecordAsync(cluster, name, cancellationToken).ConfigureAwait(false);

        try
        {
            KafkaScanOwner owner = await owners.OwnerAsync().ConfigureAwait(false);
            KafkaRecordRequest request = new(
                cluster.Key,
                name,
                partition,
                offset,
                Neighbours,
                grant.Grant,
                KafkaIsolation.Committed,
                FocusValueBytes: grant.HasPayload ? engine.Limits.MaxDecodeBytes : 0);

            KafkaRecordRead read = await scanner.ReadRecordAsync(request, owner, cancellationToken).ConfigureAwait(false);

            Record(cluster, name.Value, partition, offset, target, user, read, grant);
            return new KafkaRecordPageResult.Read(read, grant);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "KStudio could not read a record");
            actionLog.Record(cluster, topics, Action, target, succeeded: false, KafkaStudioText.ReadFailed, KafkaCapability.SearchRecords);
            return new KafkaRecordPageResult.Read(KafkaRecordRead.Without(KafkaRecordReadStatus.Stopped, KafkaStudioText.ReadFailed), grant);
        }
    }

    /// <summary>
    /// Records that the visitor was shown <paramref name="match" />'s value - in a search's detail or in a compare - as 9605 under <c>ReadPayloads</c>,
    /// once per circuit per record. Judged here and not by the page: the capability and the topic's capability policy are asked again, and a value
    /// is shown only when this answers <see langword="true" />. Nothing of the value reaches the entry: its format, its length and the number of
    /// masked parts only.
    /// </summary>
    /// <param name="cluster">The cluster entry in scope.</param>
    /// <param name="match">The record whose value is about to be shown.</param>
    /// <param name="cancellationToken">Abandons the question.</param>
    public async Task<bool> RecordValueViewAsync(KafkaClusterEntry cluster, KafkaMatch match, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(match);

        // A match read under the envelope alone holds no value: showing it is no view of one, and nothing is recorded as read.
        if (!match.ReadWithPayload)
        {
            return false;
        }

        string target = match.Topic + "/" + match.Partition.ToString(CultureInfo.InvariantCulture) + "/" + match.Offset.ToString(CultureInfo.InvariantCulture);

        try
        {
            // ReadPayloads is judged with its prerequisite: a value is read only as part of a record SearchRecords opened.
            capabilities.Require(KafkaCapability.SearchRecords);
            capabilities.Require(KafkaCapability.ReadPayloads);
        }
        catch (KafkaCapabilityDeniedException denial)
        {
            actionLog.RecordCapabilityDenied(cluster, [match.Topic], denial, Action, target);
            return false;
        }

        if (!await authorization.IsAuthorizedAsync(cluster, null, null, cancellationToken).ConfigureAwait(false)
            || !await authorization.IsAuthorizedAsync(cluster, match.Topic, null, cancellationToken).ConfigureAwait(false)
            || !await authorization.IsAuthorizedAsync(cluster, match.Topic, KafkaCapability.SearchRecords, cancellationToken).ConfigureAwait(false)
            || !await authorization.IsAuthorizedAsync(cluster, match.Topic, KafkaCapability.ReadPayloads, cancellationToken).ConfigureAwait(false))
        {
            actionLog.RecordClusterDenied(cluster, match.Topic, [match.Topic], authorization.PolicyFor(KafkaCapability.ReadPayloads) ?? "(none)", Action, target, KafkaCapability.ReadPayloads);
            return false;
        }

        if (!Remember(cluster, match.Topic, match.Partition, match.Offset, withValue: true))
        {
            return true;
        }

        string user = await authorization.UserNameAsync().ConfigureAwait(false);
        string message = string.Create(CultureInfo.InvariantCulture, $"value ({match.ValueFormat}, {KafkaDisplay.Bytes(match.ValueLength)}){MaskedSuffix(match)}");

        actionLog.Record(cluster, [match.Topic], Action, target, succeeded: true, message, KafkaCapability.ReadPayloads);
        logger.RecordValueRead(user, match.Topic, match.Partition, match.Offset, cluster.Key, cluster.TenantId);
        return true;
    }

    /// <summary>"; 2 parts masked", or nothing: the count of masked parts, never which or what.</summary>
    private static string MaskedSuffix(KafkaMatch match)
    {
        int masked = match.MaskedPaths.Count + match.MaskedPathsOmitted + match.ValueMaskedBy.Count(static x => x.StartsWith("pattern ", StringComparison.Ordinal));

        return masked == 0 ? string.Empty : "; " + KafkaDisplay.Count(masked, "part") + " masked";
    }

    /// <summary>Whether this is the first time this circuit records this view; the set is bounded.</summary>
    private bool Remember(KafkaClusterEntry cluster, string topic, int partition, long offset, bool withValue)
    {
        string key = string.Join(
            '\n',
            cluster.Key,
            cluster.TenantId ?? string.Empty,
            topic,
            partition.ToString(CultureInfo.InvariantCulture),
            offset.ToString(CultureInfo.InvariantCulture),
            withValue ? "value" : "key");

        lock (viewed)
        {
            if (viewed.Count >= MaxRememberedViews)
            {
                viewed.Clear();
            }

            return viewed.Add(key);
        }
    }

    private void Record(KafkaClusterEntry cluster, string topic, int partition, long offset, string target, string user, KafkaRecordRead read, KafkaGrantDecision grant)
    {
        if (read.Status is not (KafkaRecordReadStatus.Found or KafkaRecordReadStatus.NoRecordAtOffset))
        {
            actionLog.Record(cluster, [topic], Action, target, succeeded: false, read.Text, KafkaCapability.SearchRecords);
            return;
        }

        // Once per circuit per record: a re-render, or a return to the same link, is not a second opening. A page that shows the value is a
        // different read (9605 instead of 9604), recorded once of its own.
        bool withValue = grant.HasPayload && read.Record is not null;

        if (!Remember(cluster, topic, partition, offset, withValue))
        {
            return;
        }

        int neighbours = read.Before.Count + read.After.Count;
        string reads = string.Create(
            CultureInfo.InvariantCulture,
            $"with {KafkaDisplay.Count(neighbours, "neighbour")} ({KafkaDisplay.Count(read.RecordsRead, "record")} and {KafkaDisplay.Bytes(read.BytesRead)} read)");

        if (withValue)
        {
            KafkaMatch record = read.Record!;
            string valueMessage = string.Create(
                CultureInfo.InvariantCulture,
                $"key, headers and value ({record.ValueFormat}, {KafkaDisplay.Bytes(record.ValueLength)}){MaskedSuffix(record)}, {reads}");

            actionLog.Record(cluster, [topic], Action, target, succeeded: true, valueMessage, KafkaCapability.ReadPayloads);
            logger.RecordValueRead(user, topic, partition, offset, cluster.Key, cluster.TenantId);
            return;
        }

        actionLog.Record(cluster, [topic], Action, target, succeeded: true, $"key and headers, {reads}", KafkaCapability.SearchRecords);

        // A view of an offset with no record (a compacted gap, a marker) is a view all the same: the neighbours were read and shown.
        logger.RecordOpened(user, topic, partition, offset, cluster.Key, cluster.TenantId);
    }
}
