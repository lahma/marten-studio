using DataStudio.Services.Settings;
using DataStudio.Postgres.Services.Database;
using DataStudio.Marten.Services.Documents;
using DataStudio.Marten.Services.Events;
using DataStudio.Postgres.Services.Settings;

using DataStudio.Postgres.Services;

namespace DataStudio.Marten.Services;

/// <summary>
/// MartenStudio's <see cref="IStudioLinks" />: every link the shared screens make into Documents, Streams, the feed,
/// Projections and Schema, built by the same link builders those screens' own links use.
/// </summary>
/// <remarks>
/// A singleton over the options: every link is pure and relative to the studio-rooted <c>&lt;base href&gt;</c>, and the
/// page it lands on resolves and authorizes its own scope again.
/// </remarks>
internal sealed class MartenStudioLinks : IStudioLinks
{
    private readonly IShellSettings settings;

    public MartenStudioLinks(IShellSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
    }

    /// <inheritdoc />
    public string? ToDocuments(StudioScope? scope) => DocumentLinks.ToIndex(settings, scope);

    /// <inheritdoc />
    public string? ToCollection(StudioScope? scope, string alias) => DocumentLinks.ToCollection(settings, scope, alias);

    /// <inheritdoc />
    public string? ToDocument(StudioScope? scope, string alias, string id) => DocumentLinks.ToDocument(settings, scope, alias, id);

    /// <inheritdoc />
    public string? ToStreams(StudioScope? scope) => EventLinks.To(settings, EventLinks.StreamsRoute, scope);

    /// <inheritdoc />
    public string? ToFeed(StudioScope? scope) => EventLinks.To(settings, EventLinks.FeedRoute, scope);

    /// <inheritdoc />
    public string? ToProjections(StudioScope? scope) => EventLinks.To(settings, "projections", scope);

    /// <inheritdoc />
    public string? ToSchema(StudioScope? scope, string tab = "tables") =>
        EventLinks.To(settings, "schema", scope, [new KeyValuePair<string, string?>(DatabaseLinks.TabParameter, tab)]);
}
