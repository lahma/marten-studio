namespace PostgresStudio.Services.Settings;

/// <summary>
/// From a studio's record screen to a module's search over that record. Implemented by a module; absent otherwise.
/// </summary>
/// <remarks>
/// Pure, under <see cref="IStudioLinks" />' rules: the request holds only values the screen already shows the visitor, the answer
/// is a relative URL, and nothing is read or asked of a policy for it. The module's page authorizes what it reads, again.
/// </remarks>
internal interface IStudioRecordSearchLinks
{
    /// <summary>The button's text. The consumer never spells it, so a studio names no module.</summary>
    string Label { get; }

    /// <summary>A link that searches for this record's references, or <see langword="null" /> when nothing the module knows refers to such a record.</summary>
    /// <param name="request">The record, its scope and the window to search.</param>
    string? ToSearch(StudioRecordSearchRequest request);
}

/// <summary>What a record screen asks a module to search for.</summary>
/// <param name="StoreKey">The store the screen is on, or <see langword="null" />.</param>
/// <param name="TenantId">The tenant the screen is on, or <see langword="null" />.</param>
/// <param name="Reference">The record.</param>
/// <param name="From">The start of the window to search, or <see langword="null" /> when the screen knows none.</param>
/// <param name="To">The end of the window to search, or <see langword="null" />.</param>
internal sealed record StudioRecordSearchRequest(
    string? StoreKey,
    string? TenantId,
    StudioRecordReference Reference,
    DateTimeOffset? From,
    DateTimeOffset? To);

/// <summary>A document (<paramref name="TypeNames" />: the alias, the CLR full name and the CLR name) or a stream (none), by id.</summary>
/// <param name="Kind">Which kind of record.</param>
/// <param name="TypeNames">The names the record's type goes by; empty for a stream.</param>
/// <param name="Id">The id, as the screen shows it.</param>
internal sealed record StudioRecordReference(StudioRecordKind Kind, IReadOnlyList<string> TypeNames, string Id);

/// <summary>What a <see cref="StudioRecordReference" /> refers to.</summary>
internal enum StudioRecordKind
{
    /// <summary>A document.</summary>
    Document,

    /// <summary>An event stream.</summary>
    Stream,
}
