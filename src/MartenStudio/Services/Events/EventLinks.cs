using System.Globalization;
using System.Text;

using MartenStudio.Components;
using PostgresStudio.Components;
using PostgresStudio.Services;

namespace MartenStudio.Services.Events;

/// <summary>
/// Builds the Events area's links: the route, the filters and the cursor in the query string, and the
/// active scope on the end of every one of them.
/// </summary>
/// <remarks>
/// <para>
/// D9: ids travel in the query string rather than in a route segment. The hand-rolled router has no
/// catch-all segment, and a Marten stream key is frequently a string containing <c>/</c> - an id in a
/// segment would be two segments as soon as somebody used a natural key.
/// </para>
/// <para>
/// The scope goes on for the same reason the id does. A link that opens a different store than the one
/// the person who sent it was looking at is worse than no link, so <c>store</c>, <c>db</c> and
/// <c>tenant</c> ride along and the page puts them back through <see cref="StudioState.SetScopeAsync" />
/// - which validates them against the listings the visitor is actually authorized for, so a pasted link
/// naming a store they may not see simply opens the one they may.
/// </para>
/// </remarks>
internal static class EventLinks
{
    /// <summary>The streams list, relative to the studio root.</summary>
    public const string StreamsRoute = "events/streams";

    /// <summary>One stream's detail page.</summary>
    public const string StreamDetailRoute = "events/streams/s";

    /// <summary>The projection stepper over one stream (M1).</summary>
    public const string StreamStepperRoute = "events/streams/step";

    /// <summary>The global feed.</summary>
    public const string FeedRoute = "events/feed";

    /// <summary>The event types screen.</summary>
    public const string EventTypesRoute = "events/types";

    /// <summary>The dead letters screen.</summary>
    public const string DeadLettersRoute = "events/dead-letters";

    /// <summary>The query-string key the store travels under.</summary>
    public const string StoreKey = "store";

    /// <summary>The query-string key the database travels under.</summary>
    public const string DatabaseKey = "db";

    /// <summary>The query-string key the tenant travels under.</summary>
    public const string TenantKey = "tenant";

    /// <summary>A link to one of the area's pages, with the query parameters and the scope appended.</summary>
    /// <param name="options">The studio's options, which decide what the studio root is.</param>
    /// <param name="route">One of the <c>*Route</c> constants.</param>
    /// <param name="scope">The scope to carry, or <see langword="null" /> to carry none.</param>
    /// <param name="parameters">
    /// The page's own parameters. An entry whose value is null or blank is left out, so a link only ever
    /// carries the filters that are actually set.
    /// </param>
    public static string To(
        MartenStudioOptions options,
        string route,
        StudioScope? scope,
        params IReadOnlyList<KeyValuePair<string, string?>> parameters)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = new StringBuilder(StudioLink.To(options, route));
        var first = true;

        foreach (KeyValuePair<string, string?> parameter in parameters)
        {
            Append(builder, ref first, parameter.Key, parameter.Value);
        }

        if (scope is not null)
        {
            Append(builder, ref first, StoreKey, scope.StoreKey);
            Append(builder, ref first, DatabaseKey, scope.DatabaseId);
            Append(builder, ref first, TenantKey, scope.TenantId);
        }

        return builder.ToString();
    }

    /// <summary>A link to one stream's detail page.</summary>
    public static string ToStream(MartenStudioOptions options, StudioScope? scope, string streamId) =>
        To(options, StreamDetailRoute, scope, [new("id", streamId)]);

    /// <summary>
    /// A link to the stepper: the stream, the aggregate type, the step and the watched path all in the query string, so a
    /// pasted link reopens the same step (D9).
    /// </summary>
    /// <param name="options">The studio's options.</param>
    /// <param name="scope">The scope to carry.</param>
    /// <param name="streamId">The stream.</param>
    /// <param name="aggregate">The aggregate type's full name, or <see langword="null" /> to let the visitor pick.</param>
    /// <param name="version">The step, or <see langword="null" /> for the first.</param>
    /// <param name="projection">
    /// A projection's name for the page to pick the aggregate by, when there is no <paramref name="aggregate" />: what a
    /// dead letter names.
    /// </param>
    /// <param name="path">The value of the aggregate the next-change search watches, or <see langword="null" />.</param>
    public static string ToStep(
        MartenStudioOptions options,
        StudioScope? scope,
        string streamId,
        string? aggregate = null,
        long? version = null,
        string? projection = null,
        string? path = null) =>
        To(options, StreamStepperRoute, scope,
        [
            new("id", streamId),
            new("agg", aggregate),
            new("v", Number(version)),
            new("projection", projection),
            new("path", path),
        ]);

    /// <summary>A link to the dead letters that name one event.</summary>
    public static string ToDeadLettersForEvent(MartenStudioOptions options, StudioScope? scope, long sequence) =>
        To(options, DeadLettersRoute, scope, [new("seq", Number(sequence))]);

    /// <summary>A link to the feed, filtered to one stream.</summary>
    public static string ToFeedForStream(MartenStudioOptions options, StudioScope? scope, string streamId) =>
        To(options, FeedRoute, scope, [new("stream", streamId)]);

    /// <summary>A link to the feed, filtered to one event type.</summary>
    public static string ToFeedForType(MartenStudioOptions options, StudioScope? scope, string eventType) =>
        To(options, FeedRoute, scope, [new("types", eventType)]);

    /// <summary>A link to the feed, filtered to one DCB tag: <c>?tag=type=value</c>.</summary>
    public static string ToFeedForTag(MartenStudioOptions options, StudioScope? scope, string tagType, string value) =>
        To(options, FeedRoute, scope, [new("tag", EventTagFilter.Format(tagType, value))]);

    /// <summary>A number as a query-string value, or null when it is the default the page would use anyway.</summary>
    public static string? Number(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture);

    /// <summary>A flag as a query-string value, or null when it is off and would be noise in the URL.</summary>
    public static string? Flag(bool value) => value ? "1" : null;

    /// <summary>Reads a flag back, defaulting to <paramref name="fallback" /> when it is absent.</summary>
    public static bool ReadFlag(string? value, bool fallback = false) => value switch
    {
        null or "" => fallback,
        "1" or "true" or "True" => true,
        _ => false,
    };

    private static void Append(StringBuilder builder, ref bool first, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append(first ? '?' : '&')
            .Append(Uri.EscapeDataString(key))
            .Append('=')
            .Append(Uri.EscapeDataString(value));

        first = false;
    }
}
