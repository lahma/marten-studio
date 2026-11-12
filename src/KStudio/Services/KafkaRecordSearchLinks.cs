using KStudio.Core.Clusters;
using KStudio.Core.Correlation;
using KStudio.Core.Logging;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services.Settings;

namespace KStudio.Services;

/// <summary>
/// "Find in Kafka" (D44, decision 8 of D-CORR): from a Marten Studio record screen to the timeline of records that refer to that document or stream.
/// Pure, as <see cref="IStudioRecordSearchLinks" /> requires: it reads the registered clusters' declarations and nothing from Kafka, asks no policy, and
/// grants nothing, because the timeline page authorizes everything it reads. The value travels only in the address's fragment, which a browser never
/// sends to a server, and no part of the address is built by concatenating unescaped text.
/// </summary>
/// <remarks>
/// A link is offered when some cluster has a topic with a correlation field that refers to such a record: <c>Document(type, store)</c> for a document whose
/// type goes by that name, <c>Stream(store)</c> for a stream, the field's store being unnamed or the screen's. A registry that cannot be built (the host's
/// options are invalid) answers <see langword="null" />, said once at Debug (event 9678): a Marten page never fails because of the module.
/// </remarks>
/// <param name="services">The container, so that the registry is resolved inside the failure handling.</param>
/// <param name="settings">The studio's settings, for the base-relative address.</param>
internal sealed class KafkaRecordSearchLinks(IServiceProvider services, IStudioSettings settings) : IStudioRecordSearchLinks
{
    /// <inheritdoc />
    public string Label => KafkaStudioText.FindInKafka;

    /// <inheritdoc />
    public string? ToSearch(StudioRecordSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(request.Reference.Id))
        {
            return null;
        }

        IReadOnlyList<KafkaClusterDefinition> clusters;

        try
        {
            clusters = services.GetRequiredService<KafkaClusterRegistry>().Clusters;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Say(exception);
            return null;
        }

        bool isDocument = request.Reference.Kind == StudioRecordKind.Document;
        List<(KafkaClusterDefinition Cluster, string? DocumentType)> having = [];

        foreach (KafkaClusterDefinition cluster in clusters)
        {
            if (FirstMatch(cluster, request, isDocument) is { } found)
            {
                having.Add((cluster, found.DocumentType));
            }
        }

        if (having.Count == 0)
        {
            return null;
        }

        // Exactly one cluster entry has such topics: that one. Several: the entry of the request's tenant; otherwise none, and the page uses the
        // visitor's current cluster or asks for one.
        (KafkaClusterDefinition Cluster, string? DocumentType) chosen = having[0];
        string? clusterKey = null;

        if (having.Count == 1)
        {
            clusterKey = chosen.Cluster.Key;
        }
        else if (request.TenantId is { } tenant)
        {
            foreach ((KafkaClusterDefinition Cluster, string? DocumentType) candidate in having)
            {
                if (string.Equals(candidate.Cluster.TenantId, tenant, StringComparison.Ordinal))
                {
                    chosen = candidate;
                    clusterKey = candidate.Cluster.Key;
                    break;
                }
            }
        }

        // The type in the link is the matching field's own spelling, so that the page's comparison of correlation kinds holds whatever names the screen offered.
        string href = KafkaAddress.Href(
            settings,
            KafkaAddress.RelatedRoute,
            clusterKey,
            (KafkaAddress.FromParameter, request.From is { } from && request.To is not null ? KafkaAddress.FormatWindow(from) : null),
            (KafkaAddress.ToParameter, request.To is { } to && request.From is not null ? KafkaAddress.FormatWindow(to) : null));

        List<string> fragment = [KafkaAddress.SeedKindKey + "=" + (isDocument ? KafkaAddress.SeedDocument : KafkaAddress.SeedStream)];

        if (isDocument)
        {
            fragment.Add(KafkaAddress.SeedTypeKey + "=" + Uri.EscapeDataString(chosen.DocumentType!));
        }

        if (!string.IsNullOrEmpty(request.StoreKey))
        {
            fragment.Add(KafkaAddress.SeedStoreKey + "=" + Uri.EscapeDataString(request.StoreKey));
        }

        fragment.Add(KafkaAddress.SeedValueKey + "=" + Uri.EscapeDataString(request.Reference.Id));

        return href + "#" + string.Join('&', fragment);
    }

    /// <summary>The first declared topic of a cluster with a correlation field that refers to the record, and the document type of the field's kind (<see langword="null" /> for a stream).</summary>
    private static (string Topic, string? DocumentType)? FirstMatch(KafkaClusterDefinition cluster, StudioRecordSearchRequest request, bool isDocument)
    {
        foreach (KafkaTopicDescriptor topic in cluster.Topics.Values.Where(static x => x.Declared).OrderBy(static x => x.Name, StringComparer.Ordinal))
        {
            foreach (CorrelationField field in topic.CorrelationFields)
            {
                if (!StoreMatches(field, request.StoreKey))
                {
                    continue;
                }

                // The seed's kind is what the timeline compares (document:{type} or stream), so a field whose kind was renamed with As() is not the
                // field the seed searches, and the type in the link is the part of the kind after the prefix.
                if (isDocument
                    && field.Kind.StartsWith(CorrelationDefaults.DocumentPrefix, StringComparison.Ordinal)
                    && field.Kind[CorrelationDefaults.DocumentPrefix.Length..] is { Length: > 0 } documentType
                    && request.Reference.TypeNames.Contains(documentType, StringComparer.OrdinalIgnoreCase))
                {
                    return (topic.Name, documentType);
                }

                if (!isDocument && string.Equals(field.Kind, CorrelationDefaults.StreamKind, StringComparison.Ordinal))
                {
                    return (topic.Name, null);
                }
            }
        }

        return null;
    }

    /// <summary>A field that names no store applies to the screen's; one that names a store applies only to a screen on that store.</summary>
    private static bool StoreMatches(CorrelationField field, string? storeKey) =>
        field.Store is null || (storeKey is not null && string.Equals(field.Store, storeKey, StringComparison.OrdinalIgnoreCase));

    private void Say(Exception exception)
    {
        KafkaLogOnce? once = services.GetService<KafkaLogOnce>();
        KStudioLoggers? loggers = services.GetService<KStudioLoggers>();
        string kind = exception.GetType().Name;

        if (loggers is null || (once is not null && !once.ShouldLog("find-link|" + kind)))
        {
            return;
        }

        KStudioLog.FindLinkClustersUnreadable(loggers.Catalog, kind);
    }
}
