using KStudio.Core.Clusters;
using KStudio.Core.Correlation;
using KStudio.Core.Scanning;

using PostgresStudio.Services.Settings;

namespace KStudio.Services;

/// <summary>
/// One link from a record to a record of the host studio (D44): the text, and the address when the studio shows such a record.
/// </summary>
/// <param name="Kind">What it refers to: <see cref="KafkaAddress.SeedDocument" /> or <see cref="KafkaAddress.SeedStream" />.</param>
/// <param name="Text">The link's words: <c>Document Order 4711 →</c>, with the id cut at 24 characters.</param>
/// <param name="Href">The address the studio gave, verbatim, or <see langword="null" /> when it shows no such record.</param>
/// <param name="Reason">Why there is no <paramref name="Href" />, or <see langword="null" />.</param>
internal sealed record KafkaRecordLink(string Kind, string Text, string? Href, string? Reason);

/// <summary>What <see cref="KafkaRecordLinkBuilder" /> found for one record.</summary>
/// <param name="Links">The links, in field order.</param>
/// <param name="TenantHidden">Whether the record names a tenant that is redacted or cut, so that no link was drawn: a URL would carry the hidden value.</param>
internal sealed record KafkaRecordLinkSet(IReadOnlyList<KafkaRecordLink> Links, bool TenantHidden)
{
    /// <summary>Nothing to draw.</summary>
    public static KafkaRecordLinkSet None { get; } = new([], false);

    /// <summary>Whether there is anything to draw at all.</summary>
    public bool IsEmpty => Links.Count == 0 && !TenantHidden;
}

/// <summary>
/// Links from a record to the Marten documents and streams its topic's correlation fields name (decision 7.3 of D-CORR, D44). Pure: it reads only the
/// record's already-redacted fields, so a masked, cut or absent value never becomes a link, and it asks no policy, because a link grants nothing and the
/// page it opens authorizes again.
/// </summary>
internal static class KafkaRecordLinkBuilder
{
    /// <summary>The header that names a record's tenant (Wolverine's <c>tenant-id</c>).</summary>
    public const string TenantHeader = "tenant-id";

    /// <summary>How much of an id the link's words show.</summary>
    public const int IdCut = KafkaMatchView.ChipValueCut;

    /// <summary>The most links one record draws.</summary>
    public const int MaxLinks = 8;

    /// <summary>The reason a link has no address for a document.</summary>
    public const string NoDocumentReason = "not a document this studio shows";

    /// <summary>The reason a link has no address for a stream.</summary>
    public const string NoStreamReason = "not a stream this studio shows";

    /// <summary>Whether a topic has a field that names a document or a stream, so that a Links section could be drawn for it.</summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="topic">The topic.</param>
    public static bool HasLinkFields(KafkaClusterDefinition? cluster, string topic) =>
        cluster is not null && cluster.Descriptor(topic).CorrelationFields.Any(IsLinkField);

    /// <summary>The links of a record.</summary>
    /// <param name="routes">The studio's seam; a host without one gets no link.</param>
    /// <param name="cluster">The record's cluster, for its fields and for the entry's tenant, or <see langword="null" /> when it is not known.</param>
    /// <param name="match">The record.</param>
    public static KafkaRecordLinkSet Build(IStudioRouteLinks? routes, KafkaClusterDefinition? cluster, KafkaMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        if (routes is null || cluster is null)
        {
            return KafkaRecordLinkSet.None;
        }

        List<CorrelationField> fields = [.. cluster.Descriptor(match.Topic).CorrelationFields.Where(IsLinkField)];

        if (fields.Count == 0)
        {
            return KafkaRecordLinkSet.None;
        }

        // What the record holds first: a tenant that is hidden is only worth saying when there is a link it would have drawn.
        List<(CorrelationField Field, CorrelationValue Value)> held = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (CorrelationField field in fields)
        {
            foreach (CorrelationValue value in CorrelationExtractor.Extract(match, [field]).Values)
            {
                string identity = field.IsStream ? "stream|" + field.Store + "|" + value.Value : "document|" + field.Store + "|" + field.DocumentType + "|" + value.Value;

                if (held.Count < MaxLinks && seen.Add(identity))
                {
                    held.Add((field, value));
                }
            }
        }

        if (held.Count == 0)
        {
            return KafkaRecordLinkSet.None;
        }

        if (!TryTenant(match, cluster, out string? tenant))
        {
            return new KafkaRecordLinkSet([], true);
        }

        List<KafkaRecordLink> links = [];

        foreach ((CorrelationField field, CorrelationValue value) in held)
        {
            StudioLinkScope scope = new(field.Store, tenant);
            string shown = KafkaMatchView.Cut(value.Value, IdCut);

            if (field.IsStream)
            {
                string? href = routes.ToStream(scope, value.Value);
                links.Add(new KafkaRecordLink(KafkaAddress.SeedStream, href is null ? "Stream " + shown : "Stream " + shown + " →", href, href is null ? NoStreamReason : null));
            }
            else
            {
                string? href = routes.ToDocument(scope, field.DocumentType!, value.Value);
                string words = "Document " + field.DocumentType + " " + shown;
                links.Add(new KafkaRecordLink(KafkaAddress.SeedDocument, href is null ? words : words + " →", href, href is null ? NoDocumentReason : null));
            }
        }

        return new KafkaRecordLinkSet(links, false);
    }

    /// <summary>
    /// The tenant of a record, the first that applies: the <c>tenant-id</c> header when it is a literal; <b>none, and no link at all</b> when the header is
    /// present and redacted or cut, because the address would carry the hidden value; the cluster entry's own tenant (a broker-per-tenant cluster); none.
    /// </summary>
    /// <param name="match">The record.</param>
    /// <param name="cluster">The record's cluster.</param>
    /// <param name="tenant">The tenant, or <see langword="null" /> for none.</param>
    /// <returns><see langword="false" /> when the header hides the tenant.</returns>
    public static bool TryTenant(KafkaMatch match, KafkaClusterDefinition cluster, out string? tenant)
    {
        KafkaMatchHeader? header = match.Headers.FirstOrDefault(static x => string.Equals(x.Name, TenantHeader, StringComparison.OrdinalIgnoreCase));

        if (header is { } present && !present.Value.IsNull)
        {
            if (present.Value.IsLiteral)
            {
                tenant = present.Value.Text;
                return true;
            }

            tenant = null;
            return false;
        }

        tenant = cluster.TenantId;
        return true;
    }

    private static bool IsLinkField(CorrelationField field) => field.IsStream || field.DocumentType is not null;
}
