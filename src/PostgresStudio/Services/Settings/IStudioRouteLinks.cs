namespace PostgresStudio.Services.Settings;

/// <summary>
/// Links from a module's page into the studio's own record screens. Implemented by MartenStudio; absent in Postgres Studio,
/// where the consumer gets <see langword="null" /> from <c>GetService</c> and draws plain text.
/// </summary>
/// <remarks>
/// Pure, under <see cref="IStudioLinks" />' rules: no IO, no policy, every id escaped, and a link grants nothing. The page a
/// link lands on resolves its own scope and asks its own policies again, so a link holds only values the visitor already sees.
/// </remarks>
internal interface IStudioRouteLinks
{
    /// <summary>
    /// A document by its type (an alias, a CLR type's full name or its name) and id, or <see langword="null" /> when no visible
    /// store declares the type, or the link would be ambiguous or false.
    /// </summary>
    /// <param name="scope">The store and tenant the caller knows.</param>
    /// <param name="documentType">The type, as an alias, a CLR full name or a CLR name.</param>
    /// <param name="id">The document id, as the record carries it.</param>
    string? ToDocument(StudioLinkScope scope, string documentType, string id);

    /// <summary>
    /// A stream by its id or key, or <see langword="null" /> when the value is not one the store's stream identity reads.
    /// </summary>
    /// <param name="scope">The store and tenant the caller knows.</param>
    /// <param name="streamIdOrKey">The stream id (a Guid) or key (a string), as the record carries it.</param>
    string? ToStream(StudioLinkScope scope, string streamIdOrKey);
}

/// <summary>
/// The store (<see langword="null" />: the one store that declares the type, or the default store among several) and the tenant a
/// route link is for. Never a database: the page a link opens resolves that itself.
/// </summary>
/// <param name="StoreKey">The store's key, or <see langword="null" />.</param>
/// <param name="TenantId">The tenant, or <see langword="null" />.</param>
internal sealed record StudioLinkScope(string? StoreKey, string? TenantId);
