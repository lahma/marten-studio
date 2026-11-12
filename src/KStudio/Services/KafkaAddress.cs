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

    /// <summary>The query-string name of a topic.</summary>
    public const string TopicParameter = "topic";

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
