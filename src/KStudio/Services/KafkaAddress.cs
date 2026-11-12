using Microsoft.AspNetCore.WebUtilities;

using PostgresStudio.Components;
using PostgresStudio.Services.Settings;

namespace KStudio.Services;

/// <summary>
/// The Kafka pages' addresses (D9): every id travels in the query string, and a link is relative to the studio's base so that it honours the
/// mount. Reading is tolerant - a malformed value is absent - and writing never produces a value the reader would not give back.
/// </summary>
internal static class KafkaAddress
{
    /// <summary>The home.</summary>
    public const string TopicsRoute = "kafka";

    /// <summary>A topic's overview.</summary>
    public const string TopicRoute = "kafka/topic";

    /// <summary>Cluster settings.</summary>
    public const string SettingsRoute = "kafka/settings";

    /// <summary>Search.</summary>
    public const string SearchRoute = "kafka/search";

    /// <summary>A record's page, the permalink (D-UI decision 14).</summary>
    public const string RecordRoute = "kafka/record";

    /// <summary>Related records, the timeline (D42): around a record, or around a value a link names.</summary>
    public const string RelatedRoute = "kafka/related";

    /// <summary>The query-string name of the topics a timeline searches, comma separated (topic names hold no comma).</summary>
    public const string TopicsParameter = "topics";

    /// <summary>The fragment key of a seed's kind: <c>document</c> or <c>stream</c>.</summary>
    public const string SeedKindKey = "link";

    /// <summary>The fragment key of a seed document's type.</summary>
    public const string SeedTypeKey = "type";

    /// <summary>The fragment key of a seed's store.</summary>
    public const string SeedStoreKey = "store";

    /// <summary>The fragment key of a seed's value: a document or stream id, which may be personal data and so never travels in the query string.</summary>
    public const string SeedValueKey = "v";

    /// <summary>The seed kind of a Marten document.</summary>
    public const string SeedDocument = "document";

    /// <summary>The seed kind of a Marten stream.</summary>
    public const string SeedStream = "stream";

    /// <summary>The query-string name of a topic.</summary>
    public const string TopicParameter = "topic";

    /// <summary>The query-string name of a partition.</summary>
    public const string PartitionParameter = "partition";

    /// <summary>The query-string name of an offset.</summary>
    public const string OffsetParameter = "offset";

    /// <summary>The query-string name of a search window's start, <c>yyyy-MM-ddTHH:mm:ssZ</c> (UTC).</summary>
    public const string FromParameter = "from";

    /// <summary>The query-string name of a search window's end, <c>yyyy-MM-ddTHH:mm:ssZ</c> (UTC).</summary>
    public const string ToParameter = "to";

    /// <summary>The query-string name of the isolation: <c>uncommitted</c> includes uncommitted records.</summary>
    public const string IsolationParameter = "iso";

    /// <summary>The value of <see cref="IsolationParameter" /> that includes uncommitted records.</summary>
    public const string UncommittedValue = "uncommitted";

    /// <summary>The fragment key that holds a search's predicate: <c>#q=…</c>. A browser never sends a fragment to a server (D-UI decision 10).</summary>
    public const string PredicateFragmentKey = "q";

    /// <summary>The format of <see cref="FromParameter" /> and <see cref="ToParameter" />.</summary>
    public const string WindowFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>A UTC instant as the address writes it.</summary>
    /// <param name="value">The instant.</param>
    public static string FormatWindow(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(WindowFormat, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Reads an address's window time; a value that does not parse is ignored.</summary>
    /// <param name="text">The text.</param>
    /// <param name="value">The instant, in UTC.</param>
    public static bool TryParseWindow(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(
            text,
            WindowFormat,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out value);

    /// <summary>The search text of an address's fragment (<c>#q=…</c>), unescaped and cut at <paramref name="maxLength" />; <see langword="null" /> when there is none.</summary>
    /// <param name="uri">An absolute address.</param>
    /// <param name="maxLength">The longest text read.</param>
    public static string? ReadPredicate(string uri, int maxLength)
    {
        int hash = uri.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0)
        {
            return null;
        }

        string prefix = PredicateFragmentKey + "=";
        foreach (string part in uri[(hash + 1)..].Split('&'))
        {
            if (part.StartsWith(prefix, StringComparison.Ordinal))
            {
                string text = Uri.UnescapeDataString(part[prefix.Length..]);
                return text.Length > maxLength ? text[..maxLength] : text;
            }
        }

        return null;
    }

    /// <summary>
    /// The seed of an address's fragment (<c>#link=document&amp;type=Order&amp;store=main&amp;v=42</c>), unescaped; <see langword="null" /> when the fragment
    /// names no kind, or a kind other than <c>document</c> and <c>stream</c>, or no value. Each part is cut at <paramref name="maxLength" /> + 1, so a
    /// longer value can be told from one that fits.
    /// </summary>
    /// <param name="uri">An absolute address.</param>
    /// <param name="maxLength">The longest value kept whole.</param>
    public static KafkaRelatedSeed? ReadSeed(string uri, int maxLength)
    {
        int hash = uri.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0)
        {
            return null;
        }

        Dictionary<string, string> parts = new(StringComparer.Ordinal);

        foreach (string part in uri[(hash + 1)..].Split('&'))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            string text = Uri.UnescapeDataString(part[(equals + 1)..]);
            parts[part[..equals]] = text.Length > maxLength + 1 ? text[..(maxLength + 1)] : text;
        }

        if (!parts.TryGetValue(SeedKindKey, out string? kind) || !parts.TryGetValue(SeedValueKey, out string? value) || value.Length == 0)
        {
            return null;
        }

        parts.TryGetValue(SeedTypeKey, out string? type);
        parts.TryGetValue(SeedStoreKey, out string? store);
        store = string.IsNullOrEmpty(store) ? null : store;

        return kind switch
        {
            SeedDocument when !string.IsNullOrEmpty(type) => new KafkaRelatedSeed(SeedDocument, type, store, value),
            SeedStream => new KafkaRelatedSeed(SeedStream, null, store, value),
            _ => null,
        };
    }

    /// <summary>
    /// Whether an absolute address is the page at <paramref name="route" /> (<c>kafka/topic</c>), below any mount. A page's location handler uses it to
    /// ignore the address of the page the router is already taking the visitor to: its own query parameters are not that page's business.
    /// </summary>
    /// <param name="uri">An absolute address.</param>
    /// <param name="route">The route below the studio root.</param>
    public static bool IsPage(string uri, string route) =>
        Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed)
        && parsed.AbsolutePath.TrimEnd('/').EndsWith("/" + route, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first value of the query-string parameter <paramref name="name" /> of an absolute address, or <see langword="null" />.</summary>
    /// <param name="uri">An absolute address.</param>
    /// <param name="name">The parameter.</param>
    public static string? Get(string uri, string name) =>
        Query(uri).TryGetValue(name, out var values) && values.Count > 0 && !string.IsNullOrEmpty(values[0]) ? values[0] : null;

    /// <summary>Every value of the query-string parameter <paramref name="name" /> of an absolute address.</summary>
    /// <param name="uri">An absolute address.</param>
    /// <param name="name">The parameter.</param>
    public static IReadOnlyList<string> GetAll(string uri, string name) =>
        Query(uri).TryGetValue(name, out var values) ? [.. values.Where(static x => !string.IsNullOrEmpty(x)).Select(static x => x!)] : [];

    /// <summary>A link to <paramref name="route" /> below the studio, naming the cluster and the given parameters; a <see langword="null" /> value is left out.</summary>
    /// <param name="settings">The studio's settings.</param>
    /// <param name="route">The route below the studio root (<c>kafka/topic</c>).</param>
    /// <param name="cluster">The cluster entry's key, or <see langword="null" /> to name none.</param>
    /// <param name="parameters">Further parameters in order.</param>
    public static string Href(IStudioSettings settings, string route, string? cluster, params (string Name, string? Value)[] parameters)
    {
        string href = StudioLink.To(settings, route);

        if (cluster is not null)
        {
            href = QueryHelpers.AddQueryString(href, KafkaState.ClusterParameter, cluster);
        }

        foreach ((string name, string? value) in parameters)
        {
            if (!string.IsNullOrEmpty(value))
            {
                href = QueryHelpers.AddQueryString(href, name, value);
            }
        }

        return href;
    }

    private static Dictionary<string, Microsoft.Extensions.Primitives.StringValues> Query(string uri)
    {
        int query = uri.IndexOf('?', StringComparison.Ordinal);
        if (query < 0)
        {
            return [];
        }

        int fragment = uri.IndexOf('#', query);
        return QueryHelpers.ParseQuery(fragment < 0 ? uri[query..] : uri[query..fragment]);
    }
}
