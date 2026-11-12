using System.Globalization;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
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
    public sealed record Read(KafkaRecordRead Value) : KafkaRecordPageResult;
}

/// <summary>
/// The record page's read (D-UI decision 14), scoped to a circuit: the capability, the cluster policy and then both policies for the one topic - a
/// refusal reads like a missing topic and is recorded - and then the engine's read by offset, which is a scan of at most 21 offsets that takes one of the
/// owner's slots. A view is audited as 9604 (<c>ViewRecord</c>), once per circuit per <c>(cluster, topic, partition, offset)</c>, so a re-render or a
/// return visit is not a second entry; a read that was refused or failed is always recorded.
/// </summary>
/// <param name="capabilities">The capability switch.</param>
/// <param name="authorization">The cluster and capability policies.</param>
/// <param name="scanner">The engine's scanner.</param>
/// <param name="owners">Who the circuit's scans belong to.</param>
/// <param name="actionLog">The audit.</param>
/// <param name="logger">Where event 9604 goes.</param>
internal sealed class KafkaRecordService(
    KafkaCapabilityGuard capabilities,
    KafkaAuthorization authorization,
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

        try
        {
            KafkaScanOwner owner = await owners.OwnerAsync().ConfigureAwait(false);
            KafkaRecordRead read = await scanner
                .ReadRecordAsync(new KafkaRecordRequest(cluster.Key, name, partition, offset, Neighbours, KafkaReadGrant.Envelope, KafkaIsolation.Committed), owner, cancellationToken)
                .ConfigureAwait(false);

            Record(cluster, name.Value, partition, offset, target, user, read);
            return new KafkaRecordPageResult.Read(read);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "KStudio could not read a record");
            actionLog.Record(cluster, topics, Action, target, succeeded: false, KafkaStudioText.ReadFailed, KafkaCapability.SearchRecords);
            return new KafkaRecordPageResult.Read(KafkaRecordRead.Without(KafkaRecordReadStatus.Stopped, KafkaStudioText.ReadFailed));
        }
    }

    private void Record(KafkaClusterEntry cluster, string topic, int partition, long offset, string target, string user, KafkaRecordRead read)
    {
        if (read.Status is not (KafkaRecordReadStatus.Found or KafkaRecordReadStatus.NoRecordAtOffset))
        {
            actionLog.Record(cluster, [topic], Action, target, succeeded: false, read.Text, KafkaCapability.SearchRecords);
            return;
        }

        // Once per circuit per record: a re-render, or a return to the same link, is not a second opening.
        string key = string.Join('\n', cluster.Key, cluster.TenantId ?? string.Empty, topic, partition.ToString(CultureInfo.InvariantCulture), offset.ToString(CultureInfo.InvariantCulture));
        lock (viewed)
        {
            if (viewed.Count >= MaxRememberedViews)
            {
                viewed.Clear();
            }

            if (!viewed.Add(key))
            {
                return;
            }
        }

        int neighbours = read.Before.Count + read.After.Count;
        string message = string.Create(
            CultureInfo.InvariantCulture,
            $"key and headers, with {neighbours} neighbours ({read.RecordsRead:N0} records and {KafkaDisplay.Bytes(read.BytesRead)} read)");

        actionLog.Record(cluster, [topic], Action, target, succeeded: true, message, KafkaCapability.SearchRecords);

        // A view of an offset with no record (a compacted gap, a marker) is a view all the same: the neighbours were read and shown.
        logger.RecordOpened(user, topic, partition, offset, cluster.Key, cluster.TenantId);
    }
}
