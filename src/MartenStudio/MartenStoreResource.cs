namespace MartenStudio;

/// <summary>
/// The resource Marten Studio passes to <c>IAuthorizationService.AuthorizeAsync(user, resource, policy)</c>
/// when evaluating <see cref="MartenStudioOptions.StoreAuthorizationPolicy"/> and
/// <see cref="MartenStudioOptions.WriteAuthorizationPolicy"/>. A host writes one
/// <c>AuthorizationHandler&lt;TRequirement, MartenStoreResource&gt;</c> and it answers for the store selector,
/// the page frame and every data call.
/// </summary>
/// <remarks>
/// <para>
/// A handler is always asked the registered names before anything is read. A link's store and database match in any
/// case, and a link without a database means the store's first one, so the first question about a link can carry its
/// spelling. Whenever that spelling differs, the studio asks again with <paramref name="StoreName" /> and
/// <paramref name="DatabaseIdentifier" /> exactly as registered and as Marten names the database, and reads nothing
/// unless that answer is yes too - so a handler that compares ordinally, a deny-list included, decides about the names
/// it knows.
/// </para>
/// <para>
/// <b>An empty <paramref name="DatabaseIdentifier" /> is not "any database".</b> The studio asks with an empty one only
/// to decide whether to name a store that would not build, or whose databases could not be listed: the store selector, the
/// Overview and, since 1.2, the Stores page. It reads nothing on such an answer: every scope it resolves for a read names
/// its database, and one that names none is refused. A handler must not treat an empty identifier as permission for every
/// database of the store.
/// </para>
/// </remarks>
/// <param name="StoreName"><c>default</c> for the host's <c>IDocumentStore</c>, or the marker interface name of an ancillary store registered with <c>AddMartenStore&lt;T&gt;()</c>.</param>
/// <param name="DatabaseIdentifier">Marten's database identity (server and database name), never a connection string. Empty only when the studio asks whether to name a store that would not build, or whose databases could not be listed (see the remarks); nothing is read on that answer.</param>
/// <param name="TenantId">The selected tenant, or <see langword="null"/> for a view that is not filtered to one tenant.</param>
/// <param name="Capability">The name of the capability being exercised (for example <c>EditDocuments</c>), or <see langword="null"/> for a read.</param>
public sealed record MartenStoreResource(string StoreName, string DatabaseIdentifier, string? TenantId, string? Capability);
