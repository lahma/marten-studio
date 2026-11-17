using DataStudio.Postgres.Services.Database;
using DataStudio.Marten.Services.Documents;
using DataStudio.Marten.Services.Events;
using DataStudio.Postgres.Services.Settings;

using DataStudio.Postgres.Services;

namespace DataStudio.Marten.Services;

/// <summary>
/// The Marten module's <see cref="IStudioLinks" />: every link the shared screens make into Documents, Streams, the feed,
/// Projections and Schema, built by the same link builders those screens' own links use.
/// </summary>
/// <remarks>
/// A singleton over the options: every link is pure and relative to the studio-rooted <c>&lt;base href&gt;</c>, and the
/// page it lands on resolves and authorizes its own scope again.
/// </remarks>
internal sealed class MartenLinks : IStudioLinks
{
    /// <inheritdoc />
    public string? ToDocuments(StudioScope? scope) => DocumentLinks.ToIndex(scope);

    /// <inheritdoc />
    public string? ToCollection(StudioScope? scope, string alias) => DocumentLinks.ToCollection(scope, alias);

    /// <inheritdoc />
    public string? ToDocument(StudioScope? scope, string alias, string id) => DocumentLinks.ToDocument(scope, alias, id);

    /// <inheritdoc />
    public string? ToStreams(StudioScope? scope) => EventLinks.To(EventLinks.StreamsRoute, scope);

    /// <inheritdoc />
    public string? ToFeed(StudioScope? scope) => EventLinks.To(EventLinks.FeedRoute, scope);

    /// <inheritdoc />
    public string? ToProjections(StudioScope? scope) => EventLinks.To(EventLinks.ProjectionsRoute, scope);

    /// <inheritdoc />
    public string? ToSchema(StudioScope? scope, string tab = "tables") =>
        EventLinks.To(MartenRoutes.Page("schema"), scope, [new KeyValuePair<string, string?>(DatabaseLinks.TabParameter, tab)]);
}
