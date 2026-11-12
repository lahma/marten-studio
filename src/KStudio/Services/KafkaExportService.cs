using KStudio.Core.Clusters;
using KStudio.Core.Export;
using KStudio.Core.Redaction;
using KStudio.Core.Scanning;

using Microsoft.Extensions.Logging;

using PostgresStudio.Services.Live;

namespace KStudio.Services;

/// <summary>Where the records of an export came from (D-M5 decision 2.1): the records are the page's, already read, redacted and audited.</summary>
internal enum KafkaExportSource
{
    /// <summary>The matches of a search on screen, finished or stopped.</summary>
    Search,

    /// <summary>The records a live tail shows.</summary>
    Tail,

    /// <summary>The one record the record page shows.</summary>
    Record,
}

/// <summary>What an export saves: the records already in memory, and what the source that read them says about itself.</summary>
/// <param name="Cluster">The cluster entry in scope.</param>
/// <param name="Topics">The source's topics: a search's, a tail's, the record's.</param>
/// <param name="Source">Where the records came from.</param>
/// <param name="From">The search's window start, or <see langword="null" /> for a tail and a record.</param>
/// <param name="To">The search's window end, or <see langword="null" /> for a tail and a record.</param>
/// <param name="AuditText">The source plan's audit text: a literal on a redacted field is already <c>&lt;redacted&gt;</c>.</param>
/// <param name="SourceRecords">How many records the source read.</param>
/// <param name="SourceBytes">How many bytes the source read.</param>
/// <param name="Matches">The records to save.</param>
/// <param name="Format">The format.</param>
internal sealed record KafkaExportInput(
    KafkaClusterEntry Cluster,
    IReadOnlyList<string> Topics,
    KafkaExportSource Source,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string AuditText,
    long SourceRecords,
    long SourceBytes,
    IReadOnlyList<KafkaMatch> Matches,
    KafkaExportFormat Format);

/// <summary>Hands a finished file to whoever saves it; <see langword="false" /> when it did not arrive. The stream is the delivery's to dispose.</summary>
/// <param name="fileName">The file's name.</param>
/// <param name="mime">Its MIME type.</param>
/// <param name="content">Its bytes.</param>
/// <param name="cancellationToken">Abandons the delivery.</param>
internal delegate Task<bool> KafkaExportDelivery(string fileName, string mime, Stream content, CancellationToken cancellationToken);

/// <summary>What an export came to.</summary>
/// <param name="Delivered">Whether the file reached the browser.</param>
/// <param name="Text">The sentence the page shows.</param>
/// <param name="FileName">The file's name, when one was written.</param>
/// <param name="Written">How many records the file holds.</param>
/// <param name="LeftOut">How many records were left out at the size limit.</param>
internal sealed record KafkaExportOutcome(bool Delivered, string Text, string? FileName = null, int Written = 0, int LeftOut = 0);

/// <summary>
/// Two exports at a time, process-wide (D-M5 decision 2.6): a file is built in memory before it is sent, so the memory an export holds is bounded by
/// bounding how many are in flight. A third is refused, never queued.
/// </summary>
internal sealed class KafkaExportGate
{
    /// <summary>How many exports run at once.</summary>
    public const int MaxInFlight = 2;

    private int inFlight;

    /// <summary>Takes a place, or returns <see langword="null" /> when two exports are already running. Dispose the lease to give it back.</summary>
    public IDisposable? TryEnter()
    {
        while (true)
        {
            int current = Volatile.Read(ref inFlight);

            if (current >= MaxInFlight)
            {
                return null;
            }

            if (Interlocked.CompareExchange(ref inFlight, current + 1, current) == current)
            {
                return new Lease(this);
            }
        }
    }

    private sealed class Lease(KafkaExportGate owner) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                Interlocked.Decrement(ref owner.inFlight);
            }
        }
    }
}

/// <summary>
/// Saves the records a search, a live tail or the record page holds as a file (AGENTS.md D45, D-M5 decision 2). It reads nothing from Kafka: the
/// records are <see cref="KafkaMatch" /> values, which hold only what the engine redacted, so a mask in a file is the engine's. <b>The order of the
/// call is hard rule 5's</b>: the capability (and the capability it needs), the cluster's profile, the input's own bounds, the cluster policy and then
/// the cluster and capability policies for every topic - any refusal refuses the whole export, never narrows it - the visitor's payload grant again,
/// the file, its delivery, and the audit on every path from the profile on, exactly once per call.
/// </summary>
/// <param name="capabilities">The capability switch.</param>
/// <param name="authorization">The cluster and capability policies.</param>
/// <param name="grants">What a visitor may read of a record's value.</param>
/// <param name="registry">The clusters, whose profile decides whether anything is exported at all.</param>
/// <param name="scanner">The engine, for the cluster's capped limits.</param>
/// <param name="gate">How many exports run at once.</param>
/// <param name="actionLog">The audit.</param>
/// <param name="time">The clock for the file's name.</param>
/// <param name="logger">Where events 9606 and 9609 go.</param>
internal sealed class KafkaExportService(
    KafkaCapabilityGuard capabilities,
    KafkaAuthorization authorization,
    KafkaReadGrants grants,
    KafkaClusterRegistry registry,
    IKafkaScanner scanner,
    KafkaExportGate gate,
    KafkaActionLog actionLog,
    TimeProvider time,
    ILogger<KafkaExportService> logger)
{
    /// <summary>Whether a cluster's profile forbids export, whatever the capability says: the controls use this to say so before a click.</summary>
    /// <param name="cluster">The cluster entry.</param>
    public bool ForbiddenByProfile(KafkaClusterEntry cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        return registry.TryGet(cluster.Key, out KafkaClusterDefinition? definition) && KafkaProfileCeilings.ForbidsExport(definition.Profile);
    }

    /// <summary>Writes <paramref name="input" />'s records as a file and hands it to <paramref name="deliver" />. Refusals are values; the audit is written once.</summary>
    /// <param name="input">What to save.</param>
    /// <param name="deliver">Hands the finished file to the browser.</param>
    /// <param name="cancellationToken">Abandons the export.</param>
    public async Task<KafkaExportOutcome> ExportAsync(KafkaExportInput input, KafkaExportDelivery deliver, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(deliver);

        KafkaClusterEntry cluster = input.Cluster;
        IReadOnlyList<string> topics = input.Topics;
        string? place = input.Source == KafkaExportSource.Record && input.Matches.Count == 1
            ? $"{input.Matches[0].Topic}/{input.Matches[0].Partition}/{input.Matches[0].Offset}"
            : null;
        string target = KafkaStudioText.ExportTarget(input.Source, topics, input.From, input.To, place);

        // 1. The capability, and the one it needs.
        try
        {
            capabilities.Require(KafkaCapability.ExportRecords);
        }
        catch (KafkaCapabilityDeniedException denial)
        {
            actionLog.RecordCapabilityDenied(cluster, topics, denial, KafkaStudioText.ExportAction, target);
            return new KafkaExportOutcome(false, denial.Message);
        }

        // 2. The cluster's profile: Production never exports, even when the capability is granted.
        if (!registry.TryGet(cluster.Key, out KafkaClusterDefinition? definition))
        {
            return await NotRunAsync(input, target, KafkaStudioText.RequestRefused).ConfigureAwait(false);
        }

        if (KafkaProfileCeilings.ForbidsExport(definition.Profile))
        {
            return await NotRunAsync(input, target, KafkaStudioText.ProfileForbids(KafkaStudioText.ExportingRecords, cluster)).ConfigureAwait(false);
        }

        // 3. The input's own bounds.
        if (Bounds(input) is { } bounds)
        {
            return await NotRunAsync(input, target, bounds).ConfigureAwait(false);
        }

        // 4. The cluster policy, then both policies for every topic: any refusal refuses the whole export and names nothing.
        if (!await authorization.IsAuthorizedAsync(cluster, null, null, cancellationToken).ConfigureAwait(false))
        {
            actionLog.RecordClusterDenied(cluster, null, topics, authorization.PolicyFor(null) ?? "(none)", KafkaStudioText.ExportAction, target, KafkaCapability.ExportRecords);
            return new KafkaExportOutcome(false, KafkaStudioText.RequestRefused);
        }

        foreach (string topic in topics)
        {
            if (!await authorization.IsAuthorizedAsync(cluster, topic, null, cancellationToken).ConfigureAwait(false)
                || !await authorization.IsAuthorizedAsync(cluster, topic, KafkaCapability.SearchRecords, cancellationToken).ConfigureAwait(false)
                || !await authorization.IsAuthorizedAsync(cluster, topic, KafkaCapability.ExportRecords, cancellationToken).ConfigureAwait(false))
            {
                actionLog.RecordClusterDenied(
                    cluster,
                    topic,
                    topics,
                    authorization.PolicyFor(KafkaCapability.ExportRecords) ?? "(none)",
                    KafkaStudioText.ExportAction,
                    target,
                    KafkaCapability.ExportRecords);
                return new KafkaExportOutcome(false, KafkaStudioText.RequestRefused);
            }
        }

        // 5. Values: a match read with the payload grant carries its value, so the visitor must still be allowed to read it. If not, the export is
        // refused whole; it is never silently written as envelope rows (D35).
        KafkaTopicName[] valueTopics =
        [
            .. input.Matches
                .Where(static x => x.ReadWithPayload)
                .Select(static x => x.Topic)
                .Distinct(StringComparer.Ordinal)
                .Select(static x => new KafkaTopicName(x)),
        ];

        if (valueTopics.Length > 0)
        {
            KafkaGrantDecision grant = await grants.ForSearchAsync(cluster, valueTopics, readsValues: false, cancellationToken).ConfigureAwait(false);

            if (!grant.HasPayload)
            {
                return await NotRunAsync(input, target, KafkaStudioText.ExportGrantChanged).ConfigureAwait(false);
            }
        }

        // 6. The file, two at a time.
        using IDisposable? lease = gate.TryEnter();
        if (lease is null)
        {
            return await NotRunAsync(input, target, KafkaStudioText.ExportsBusy).ConfigureAwait(false);
        }

        string user = await authorization.UserNameAsync().ConfigureAwait(false);
        KafkaExportFile file;
        string fileName;

        try
        {
            long maxBytes = scanner.LimitsFor(cluster.Key).MaxExportBytes;
            file = KafkaExportWriter.Write(input.Matches, input.Format, maxBytes);

            if (file.Refusal is { } refusal)
            {
                return await NotRunAsync(input, target, refusal).ConfigureAwait(false);
            }

            // A single value larger than the limit: nothing fits, and an empty file is not an export.
            if (file.Written == 0)
            {
                return await NotRunAsync(input, target, KafkaStudioText.ExportLimited(0, file.LeftOut, maxBytes)).ConfigureAwait(false);
            }

            KafkaMatch first = input.Matches[0];
            string part = input.Source == KafkaExportSource.Record
                ? KafkaExportFileName.RecordPart(first.Topic, first.Partition, first.Offset)
                : KafkaExportFileName.TopicsPart(topics);
            fileName = KafkaExportFileName.For(cluster.Key, part, time.GetUtcNow(), file.Extension);

            // 7. The delivery. Interop loss is a file that did not arrive, not a failure of the export.
            bool delivered;

            try
            {
                delivered = await deliver(fileName, file.ContentType, new MemoryStream(file.Content, writable: false), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (StudioLiveUpdates.IsInteropUnavailable(e))
            {
                delivered = false;
            }

            // 8. The audit, once.
            return Record(input, target, user, file, fileName, delivered);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "KStudio could not export records");
            actionLog.Record(cluster, topics, KafkaStudioText.ExportAction, target, succeeded: false, KafkaStudioText.ExportFailed, KafkaCapability.ExportRecords);
            logger.ActionNotRun(user, "export", string.Join(", ", topics), cluster.Cluster, cluster.TenantId, KafkaStudioText.ExportFailed, input.AuditText);
            return new KafkaExportOutcome(false, KafkaStudioText.ExportFailed);
        }
    }

    /// <summary>The extension the record page saves a value with (<c>json</c> or <c>txt</c>), or <see langword="null" /> when the value has no text to save.</summary>
    /// <param name="match">The record.</param>
    public static string? ValueExtensionOf(KafkaMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        return match.ReadWithPayload && match.ValueWithheldBy is null && match.ValueText is not null
            ? match.ValueTextKind == KafkaValueTextKind.Json ? "json" : "txt"
            : null;
    }

    private static string? Bounds(KafkaExportInput input)
    {
        if (input.Topics.Count is < 1 or > KafkaStudioText.MaxSearchTopics)
        {
            return KafkaStudioText.SearchTopicCount;
        }

        if (input.Matches.Count == 0)
        {
            return KafkaStudioText.ExportNothing;
        }

        HashSet<string> named = new(input.Topics, StringComparer.Ordinal);

        if (input.Matches.Any(x => !named.Contains(x.Topic)))
        {
            return KafkaStudioText.ExportRecordsNotFromTopics;
        }

        if (input.Format == KafkaExportFormat.Value && (input.Matches.Count != 1 || input.Source != KafkaExportSource.Record))
        {
            return KafkaStudioText.ExportValueFromOneRecord;
        }

        return null;
    }

    private async Task<KafkaExportOutcome> NotRunAsync(KafkaExportInput input, string target, string reason)
    {
        string user = await authorization.UserNameAsync().ConfigureAwait(false);

        actionLog.Record(input.Cluster, input.Topics, KafkaStudioText.ExportAction, target, succeeded: false, reason, KafkaCapability.ExportRecords);
        logger.ActionNotRun(user, "export", string.Join(", ", input.Topics), input.Cluster.Cluster, input.Cluster.TenantId, reason, input.AuditText);

        return new KafkaExportOutcome(false, reason);
    }

    private KafkaExportOutcome Record(KafkaExportInput input, string target, string user, KafkaExportFile file, string fileName, bool delivered)
    {
        string format = FormatName(file.Format);

        if (!delivered)
        {
            actionLog.Record(input.Cluster, input.Topics, KafkaStudioText.ExportAction, target, succeeded: false, KafkaStudioText.ExportNotDelivered, KafkaCapability.ExportRecords);
            logger.ActionNotRun(user, "export", string.Join(", ", input.Topics), input.Cluster.Cluster, input.Cluster.TenantId, KafkaStudioText.ExportNotDelivered, input.AuditText);
            return new KafkaExportOutcome(false, KafkaStudioText.ExportNotDelivered, fileName, file.Written, file.LeftOut);
        }

        string message = KafkaStudioText.ExportMessage(
            file.Written,
            file.Bytes,
            format,
            file.IncludesValues,
            file.LeftOut,
            input.Source,
            input.SourceRecords,
            input.SourceBytes,
            input.AuditText);

        actionLog.Record(input.Cluster, input.Topics, KafkaStudioText.ExportAction, target, succeeded: true, message, KafkaCapability.ExportRecords);
        logger.RecordsExported(
            user,
            file.Written,
            string.Join(", ", input.Topics),
            input.Cluster.Cluster,
            input.Cluster.TenantId,
            format,
            file.Bytes,
            file.IncludesValues,
            input.AuditText);

        string text = file.LeftOut > 0
            ? KafkaStudioText.ExportLimited(file.Written, file.Written + file.LeftOut, scanner.LimitsFor(input.Cluster.Key).MaxExportBytes)
            : KafkaStudioText.ExportSaved(fileName);
        return new KafkaExportOutcome(true, text, fileName, file.Written, file.LeftOut);
    }

    private static string FormatName(KafkaExportFormat format) => format switch
    {
        KafkaExportFormat.JsonLines => "JSON Lines",
        KafkaExportFormat.Csv => "CSV",
        _ => "a single value",
    };
}
