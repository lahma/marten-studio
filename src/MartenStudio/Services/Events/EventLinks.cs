using System.Globalization;
using System.Text;

using JasperFx.Events;

using MartenStudio.Components;
using MartenStudio.Services.Query;
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

    /// <summary>The event flow screen (1.2): which projections read each event type, and what they write.</summary>
    public const string EventFlowRoute = "events/flow";

    /// <summary>The projections screen, which lists every registration by name.</summary>
    public const string ProjectionsRoute = "projections";

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
    /// A link to one stream's detail page from a caller that knows a store and a tenant and no database (D-CORR 7.2): it carries
    /// <c>store</c> and <c>tenant</c> and never <c>db</c>, and the page resolves the rest and authorizes it again.
    /// </summary>
    /// <param name="options">The studio's options.</param>
    /// <param name="storeKey">The store, or <see langword="null" /> for the visitor's current one.</param>
    /// <param name="tenantId">The tenant, or <see langword="null" /> for none.</param>
    /// <param name="streamId">The stream's id or key.</param>
    public static string ToStream(MartenStudioOptions options, string? storeKey, string? tenantId, string streamId) =>
        To(options, StreamDetailRoute, null, [new("id", streamId), new(StoreKey, storeKey), new(TenantKey, tenantId)]);

    /// <summary>
    /// A link to one stream's detail page at one of its events (1.2, Find by id): <c>events/streams/s?id=&amp;v=</c>, which the page
    /// selects and scrolls to. The version is the event's own within the stream.
    /// </summary>
    /// <param name="options">The studio's options.</param>
    /// <param name="scope">The scope to carry.</param>
    /// <param name="streamId">The stream.</param>
    /// <param name="version">The event's version in the stream.</param>
    public static string ToStreamAt(MartenStudioOptions options, StudioScope? scope, string streamId, long version) =>
        To(options, StreamDetailRoute, scope, [new("id", streamId), new("v", Number(version))]);

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

    /// <summary>
    /// A link to the event flow (1.2), optionally filtered to one projection and/or one event type, and optionally drawing
    /// Marten's own events too. A blank value is left out, so a link carries only what is set.
    /// </summary>
    public static string ToFlow(MartenStudioOptions options, StudioScope? scope, string? projection = null, string? eventType = null, bool includeOwn = false) =>
        To(options, EventFlowRoute, scope,
        [
            new("projection", projection),
            new("type", eventType),
            new("own", includeOwn ? "1" : null),
        ]);

    /// <summary>
    /// The anchor of a projection's row on the Projections page (1.2): <c>projection-</c> and the name, with every character an
    /// element id may not hold made a <c>-</c>. A name is never decoded back from it, so two names that collide only ever share a
    /// scroll target.
    /// </summary>
    public static string ProjectionAnchor(string projectionName)
    {
        ArgumentNullException.ThrowIfNull(projectionName);

        var builder = new StringBuilder("projection-", 11 + projectionName.Length);
        foreach (char c in projectionName)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' ? c : '-');
        }

        return builder.ToString();
    }

    /// <summary>
    /// The projection-row anchor an address's fragment names, or <see langword="null" /> when it names none: <c>#projection-</c> only,
    /// as every row's id starts, bounded so an address cannot make the page carry a long string around.
    /// </summary>
    public static string? ProjectionFragment(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        int hash = uri.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0 || uri.Length - hash > 256)
        {
            return null;
        }

        string fragment = Uri.UnescapeDataString(uri[(hash + 1)..]);

        return fragment.StartsWith("projection-", StringComparison.Ordinal) ? fragment : null;
    }

    /// <summary>
    /// A link to the Projections page that names one projection's row (1.2). The browser alone cannot scroll to it, because the rows
    /// are drawn after the circuit reads them; the page scrolls once they are, from this fragment.
    /// </summary>
    public static string ToProjection(MartenStudioOptions options, StudioScope? scope, string projectionName) =>
        To(options, ProjectionsRoute, scope, []) + "#" + ProjectionAnchor(projectionName);

    /// <summary>A link to the feed, filtered to one stream.</summary>
    public static string ToFeedForStream(MartenStudioOptions options, StudioScope? scope, string streamId) =>
        To(options, FeedRoute, scope, [new("stream", streamId)]);

    /// <summary>A link to the feed, filtered to one event type.</summary>
    public static string ToFeedForType(MartenStudioOptions options, StudioScope? scope, string eventType) =>
        To(options, FeedRoute, scope, [new("types", eventType)]);

    /// <summary>A link to the feed, filtered to one DCB tag: <c>?tag=type=value</c>.</summary>
    public static string ToFeedForTag(MartenStudioOptions options, StudioScope? scope, string tagType, string value) =>
        To(options, FeedRoute, scope, [new("tag", EventTagFilter.Format(tagType, value))]);

    /// <summary>The Query page, relative to the studio root.</summary>
    public const string QueryRoute = "query";

    /// <summary>
    /// A link to the Query page's LINQ tab over the event log (1.1): <c>?mode=linq&amp;target=events&amp;predicate=…</c>. It fills
    /// the boxes and never runs (D9), so the visitor reads the predicate before it does anything.
    /// </summary>
    /// <param name="options">The studio's options.</param>
    /// <param name="scope">The scope to carry.</param>
    /// <param name="predicate">The predicate to prefill, or <see langword="null" /> for none.</param>
    /// <param name="archived">What to do about archived events.</param>
    public static string ToLinqQuery(MartenStudioOptions options, StudioScope? scope, string? predicate, ArchivedFilter archived = ArchivedFilter.Exclude) =>
        To(options, QueryRoute, scope,
        [
            new("mode", "linq"),
            new("target", "events"),
            new("predicate", predicate),
            new("archived", ArchivedToken(archived)),
        ]);

    /// <summary>The archived selector as <c>?archived=</c> spells it, or <see langword="null" /> for the default.</summary>
    public static string? ArchivedToken(ArchivedFilter filter) => filter switch
    {
        ArchivedFilter.Include => "include",
        ArchivedFilter.Only => "only",
        _ => null,
    };

    /// <summary>Reads <c>?archived=</c> back, defaulting to live events only.</summary>
    public static ArchivedFilter ParseArchived(string? token) => token?.ToLowerInvariant() switch
    {
        "include" => ArchivedFilter.Include,
        "only" => ArchivedFilter.Only,
        _ => ArchivedFilter.Exclude,
    };

    /// <summary>
    /// The LINQ predicate that says what the Feed's filters say (1.1), or <see langword="null" /> when they say nothing.
    /// </summary>
    /// <remarks>
    /// A clause whose value holds a <c>"</c> or a <c>\</c> is left out, because the text screen would refuse the literal; the tag
    /// filter has no LINQ equivalent and is left out. The result never runs anything - it is only text in a box.
    /// </remarks>
    /// <param name="identity">How this store identifies streams: <c>StreamId</c> for a Guid, <c>StreamKey</c> for a string.</param>
    /// <param name="stream">The stream filter.</param>
    /// <param name="eventTypes">The event types ticked.</param>
    /// <param name="from">The start of the time range, or <see langword="null" />.</param>
    public static string? FeedPredicate(StreamIdentity identity, string? stream, IReadOnlyList<string> eventTypes, DateTimeOffset? from)
    {
        List<string> clauses = [];

        if (Quotable(stream))
        {
            clauses.Add((identity == StreamIdentity.AsString ? "StreamKey" : "StreamId") + " == \"" + stream!.Trim() + "\"");
        }

        List<string> types = [.. eventTypes.Where(Quotable).Select(static x => "\"" + x + "\"")];

        if (types.Count == 1)
        {
            clauses.Add("EventTypeName == " + types[0]);
        }
        else if (types.Count > 1)
        {
            clauses.Add("EventTypeName in (" + string.Join(", ", types) + ")");
        }

        if (from is { } start)
        {
            clauses.Add("Timestamp >= \"" + start.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\"");
        }

        string text = string.Join(" and ", clauses);

        return text.Length == 0 || text.Length > LinqQueryLimits.MaxWhereLength ? null : text;

        static bool Quotable(string? value) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= LinqQueryLimits.MaxLiteralLength && value.IndexOfAny(['"', '\\']) < 0;
    }

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
