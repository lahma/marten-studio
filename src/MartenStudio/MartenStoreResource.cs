namespace MartenStudio;

/// <summary>
/// The resource Marten Studio passes to <c>IAuthorizationService.AuthorizeAsync(user, resource, policy)</c>
/// when evaluating <see cref="MartenStudioOptions.StoreAuthorizationPolicy"/> and
/// <see cref="MartenStudioOptions.WriteAuthorizationPolicy"/>. A host writes one
/// <c>AuthorizationHandler&lt;TRequirement, MartenStoreResource&gt;</c> and it answers for the store selector,
/// the page frame and every data call.
/// </summary>
/// <param name="StoreName"><c>default</c> for the host's <c>IDocumentStore</c>, or the marker interface name of an ancillary store registered with <c>AddMartenStore&lt;T&gt;()</c>.</param>
/// <param name="DatabaseIdentifier">Marten's database identity (server and database name), never a connection string.</param>
/// <param name="TenantId">The selected tenant, or <see langword="null"/> for a view that is not filtered to one tenant.</param>
/// <param name="Capability">The name of the capability being exercised (for example <c>EditDocuments</c>), or <see langword="null"/> for a read.</param>
public sealed record MartenStoreResource(string StoreName, string DatabaseIdentifier, string? TenantId, string? Capability);
