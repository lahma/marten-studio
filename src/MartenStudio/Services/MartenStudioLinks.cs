using MartenStudio.Services.Database;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Settings;

using Microsoft.Extensions.Options;

namespace MartenStudio.Services;

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
    private readonly IOptions<MartenStudioOptions> options;

    public MartenStudioLinks(IOptions<MartenStudioOptions> options)
    {
        this.options = options;
    }

    /// <inheritdoc />
    public string? ToDocuments(StudioScope? scope) => DocumentLinks.ToIndex(options.Value, scope);

    /// <inheritdoc />
    public string? ToCollection(StudioScope? scope, string alias) => DocumentLinks.ToCollection(options.Value, scope, alias);

    /// <inheritdoc />
    public string? ToDocument(StudioScope? scope, string alias, string id) => DocumentLinks.ToDocument(options.Value, scope, alias, id);

    /// <inheritdoc />
    public string? ToStreams(StudioScope? scope) => DatabaseLinks.ToStreams(options.Value, scope);

    /// <inheritdoc />
    public string? ToFeed(StudioScope? scope) => DatabaseLinks.ToFeed(options.Value, scope);

    /// <inheritdoc />
    public string? ToProjections(StudioScope? scope) => DatabaseLinks.ToProjections(options.Value, scope);

    /// <inheritdoc />
    public string? ToSchema(StudioScope? scope, string tab = "tables") => DatabaseLinks.ToSchema(options.Value, scope, tab);
}
