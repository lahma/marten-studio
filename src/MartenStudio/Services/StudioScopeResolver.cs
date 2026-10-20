using Marten.Storage;

using PostgresStudio.Services.Sources;
using PostgresStudio.Services;

namespace MartenStudio.Services;

/// <summary>
/// Turns the three strings in a URL into the Marten objects behind them, refusing before it looks
/// anything up - MartenStudio's typed face of the shared <see cref="StudioSourceResolver" />.
/// </summary>
/// <remarks>
/// <para>
/// The order is the security property, and it is the shared resolver's: authorization runs <em>first</em>, so a
/// store the visitor may not see and a store that was never registered produce the same refusal; only then is the
/// registration looked up, the store built, the database matched and the tenant checked.
/// </para>
/// <para>
/// The database is matched by <c>Id.Identity</c> against what <c>Storage.AllDatabases()</c> answers, and
/// never passed to <c>FindOrCreateDatabase</c>: that method's name is literal, and handing it a string a
/// browser supplied is how a UI creates a database nobody asked for.
/// </para>
/// <para>
/// Every Marten-only service resolves through here and gets a <see cref="ResolvedScope" />. A source that is not a
/// Marten store - which no source is today, and which a studio listing plain data sources beside its stores would
/// offer - is refused with <see cref="KeyNotFoundException" />, the same answer as a store that does not exist, so a
/// Documents URL naming one is "not found" rather than a cast exception.
/// </para>
/// </remarks>
internal sealed class StudioScopeResolver
{
    private readonly StudioSourceResolver resolver;

    public StudioScopeResolver(StudioSourceResolver resolver)
    {
        this.resolver = resolver;
    }

    /// <summary>
    /// Resolves <paramref name="scope" />, or throws.
    /// </summary>
    /// <param name="scope">The store, database and tenant to reach.</param>
    /// <param name="capability">
    /// The capability being exercised, for a mutating call. Non-null adds a second check against
    /// <see cref="MartenStudioOptions.WriteAuthorizationPolicy" /> with the capability named on the
    /// resource; the store policy has already had to pass.
    /// </param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <exception cref="StudioNotAuthorizedException">The visitor may not have this scope.</exception>
    /// <exception cref="KeyNotFoundException">No such store, or no such database in it.</exception>
    /// <exception cref="StudioStoreUnavailableException">The store is registered but will not build.</exception>
    public async ValueTask<ResolvedScope> ResolveAsync(
        StudioScope scope,
        string? capability = null,
        CancellationToken cancellationToken = default)
    {
        ResolvedSource resolved = await resolver.ResolveAsync(scope, capability, cancellationToken).ConfigureAwait(false);

        return resolved as ResolvedScope
            ?? throw new KeyNotFoundException($"'{scope.StoreKey}' is not a Marten store.");
    }

    /// <summary>
    /// Of the instances that share <paramref name="database" />'s identity, the one whose own tenant list holds
    /// <paramref name="tenantId" /> - or <paramref name="database" /> itself, the first of them, when it does or none
    /// does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Twins.</b> Two master-table rows with one connection string are two <c>MartenDatabase</c> objects with one
    /// <c>Id.Identity</c>, each listing its own row's tenant (<c>MasterTableTenancy.BuildDatabases</c>). The studio scopes by
    /// identity, so the database matched above is the first of them, and tenant discovery answers for every twin's
    /// tenants (<see cref="TenantDiscovery" />). A scope naming the second row's tenant then opened every session, and
    /// every connection, on the <em>first</em> row's instance: its data source - that row's role, its
    /// <c>Options=-c</c>, its pool - and not the one Marten itself would use for that tenant. For a host that gives
    /// each tenant a role of its own, or row-level security keyed on one, that is the wrong tenant's connection under
    /// the right tenant's header (SEC1 L1). The policies were asked the right question; only the instance was wrong.
    /// </para>
    /// <para>
    /// A scope with no tenant keeps the first instance, which is what the picker lists. Matched ordinally, as
    /// <see cref="TenantList.IsAcceptable" /> matched the tenant against the same lists, each copied before it is
    /// read because a sharded tenancy reconciles them from whichever thread enumerates.
    /// </para>
    /// </remarks>
    internal static IMartenDatabase InstanceFor(IReadOnlyList<IMartenDatabase> databases, IMartenDatabase database, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(databases);
        ArgumentNullException.ThrowIfNull(database);

        if (Lists(database, tenantId))
        {
            return database;
        }

        foreach (IMartenDatabase twin in databases)
        {
            if (!ReferenceEquals(twin, database)
                && string.Equals(twin.Id.Identity, database.Id.Identity, StringComparison.OrdinalIgnoreCase)
                && Lists(twin, tenantId))
            {
                return twin;
            }
        }

        return database;

        static bool Lists(IMartenDatabase instance, string tenantId)
        {
            string[] tenants = [.. instance.TenantIds];
            return tenants.Contains(tenantId, StringComparer.Ordinal);
        }
    }
}
