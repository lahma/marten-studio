using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// Postgres Studio's <see cref="IStudioLinks" />: it has no documents, event store, projections or schema screen, so every
/// link is <see langword="null" /> and the shared screens draw what they would have linked as plain text.
/// </summary>
/// <remarks>
/// That is the honest answer for an object the <c>mt_</c> rule calls Marten's: its rows are read in Marten Studio, where
/// its tenancy and visibility rules apply, and there is nowhere here to send anybody. The database page says so once
/// (<see cref="StudioText.MartenStoreNotice" />). Pure, like every <see cref="IStudioLinks" />: no IO, no scope.
/// </remarks>
internal sealed class PostgresStudioLinks : IStudioLinks
{
    /// <summary>The one instance: it holds nothing.</summary>
    public static PostgresStudioLinks Instance { get; } = new();

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
