namespace MartenStudio;

/// <summary>
/// The resource Marten Studio passes to <c>IAuthorizationService.AuthorizeAsync(user, resource, policy)</c>
/// when evaluating <see cref="MartenStudioOptions.StoreAuthorizationPolicy"/> and
/// <see cref="MartenStudioOptions.WriteAuthorizationPolicy"/>. A host writes one
/// <c>AuthorizationHandler&lt;TRequirement, MartenStoreResource&gt;</c> and it answers for the store selector,
/// the page frame and every data call.
/// </summary>
/// <remarks>
/// A handler is always asked the registered names before anything is read. A link's store and database match in any
/// case, and a link without a database means the store's first one, so the first question about a link can carry its
/// spelling. Whenever that spelling differs, the studio asks again with <paramref name="StoreName" /> and
/// <paramref name="DatabaseIdentifier" /> exactly as registered and as Marten names the database, and reads nothing
/// unless that answer is yes too - so a handler that compares ordinally, a deny-list included, decides about the names
/// it knows.
/// </remarks>
/// <param name="StoreName"><c>default</c> for the host's <c>IDocumentStore</c>, or the marker interface name of an ancillary store registered with <c>AddMartenStore&lt;T&gt;()</c>.</param>
/// <param name="DatabaseIdentifier">Marten's database identity (server and database name), never a connection string.</param>
/// <param name="TenantId">The selected tenant, or <see langword="null"/> for a view that is not filtered to one tenant.</param>
/// <param name="Capability">The name of the capability being exercised (for example <c>EditDocuments</c>), or <see langword="null"/> for a read.</param>
public sealed record MartenStoreResource(string StoreName, string DatabaseIdentifier, string? TenantId, string? Capability);
