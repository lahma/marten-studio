using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// PostgresStudio's <see cref="IStudioLinks" />: it has no documents, event or schema screen, so every link is
/// <see langword="null" /> and the shared screens draw plain text.
/// </summary>
/// <remarks>A minimal body by PS-3a; PS-3b owns the implementation.</remarks>
internal sealed class PostgresStudioLinks : IStudioLinks
{
    /// <inheritdoc />
    public string? ToDocuments(StudioScope? scope) => null;

    /// <inheritdoc />
    public string? ToCollection(StudioScope? scope, string alias) => null;

    /// <inheritdoc />
    public string? ToDocument(StudioScope? scope, string alias, string id) => null;

    /// <inheritdoc />
    public string? ToStreams(StudioScope? scope) => null;

    /// <inheritdoc />
    public string? ToFeed(StudioScope? scope) => null;

    /// <inheritdoc />
    public string? ToProjections(StudioScope? scope) => null;

    /// <inheritdoc />
    public string? ToSchema(StudioScope? scope, string tab = "tables") => null;
}
