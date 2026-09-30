using Marten.Storage;

using Microsoft.Extensions.Logging;

namespace MartenStudio.Services;

/// <summary>
/// Asks the host's policies about what an operation <em>reaches</em>, when that is wider than the scope
/// in the URL the resolver has already authorized.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StudioScopeResolver" /> authorizes the store, database and tenant a page is on. Several
/// operations act on more than that - a whole database from a tenant scope, or every database of the
/// store from one of them - and <see cref="DatabaseReach" /> says which and where in Marten that was read.
/// This is the one place those wider questions are asked, so the projections screen's corrections,
/// rebuilds and daemon controls and the dead-letter page's rewind cannot drift apart on what "the whole
/// database" means, which policies are asked about it, or what the refusal carries.
/// </para>
/// <para>
/// Every refusal is a <see cref="StudioNotAuthorizedException" /> carrying the scope that was refused -
/// <c>(store, database, null)</c> for a whole-database question - which the caller's catch records as
/// <c>denied.Scope</c>, so the audit entry says what the visitor was not allowed to reach rather than the
/// tenant they asked from.
/// </para>
/// <para>
/// Constructed by the service that uses it rather than registered: it holds nothing but that service's
/// <see cref="StudioAuthorization" /> - the circuit's visitor - and its logger.
/// </para>
/// </remarks>
internal sealed class DatabaseReachAuthorization
{
    private readonly StudioAuthorization authorization;
    private readonly ILogger logger;

    public DatabaseReachAuthorization(StudioAuthorization authorization, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(logger);

        this.authorization = authorization;
        this.logger = logger;
    }

    /// <summary>Whether any policy would be asked about <paramref name="capability" /> at all.</summary>
    /// <remarks>
    /// The store policy answers reads, and the write policy - or the store policy when there is no write
    /// policy - answers the capability. Only when neither is configured is every question a yes, and only
    /// then is enumerating the store's databases (a query, on a master-table tenancy) not worth doing.
    /// </remarks>
    public bool AnyPolicyAnswersFor(StudioCapability capability) =>
        !string.IsNullOrWhiteSpace(authorization.PolicyFor(null))
        || !string.IsNullOrWhiteSpace(authorization.PolicyFor(capability.ToString()));

    /// <summary>
    /// Refuses unless the visitor may address <paramref name="databaseId" /> as
    /// <paramref name="tenantId" /> - with the same two questions <see cref="StudioScopeResolver" /> asks
    /// for the scope in the URL.
    /// </summary>
    /// <remarks>
    /// The store policy with no capability, then the write policy with it: a database the visitor may
    /// not even read is not one they may change, and asking only the write policy - which is what the
    /// coordinator check once did - let a host that configured a write policy and no store policy skip
    /// the per-database question entirely, because the old guard short-circuited on the store policy
    /// alone.
    /// </remarks>
    /// <exception cref="StudioNotAuthorizedException">
    /// Either policy refuses, carrying the refused scope for the caller's audit entry.
    /// </exception>
    public async Task RequireDatabaseAsync(
        ResolvedScope resolved,
        string databaseId,
        string? tenantId,
        StudioCapability capability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        var reached = new StudioScope(resolved.Registration.Key, databaseId, tenantId);

        bool allowed =
            await authorization.IsAuthorizedAsync(reached, null, cancellationToken).ConfigureAwait(false)
            && await authorization.IsAuthorizedAsync(reached, capability.ToString(), cancellationToken).ConfigureAwait(false);

        if (!allowed)
        {
            throw new StudioNotAuthorizedException(reached);
        }
    }

    /// <summary>
    /// Refuses an operation that acts on the whole of <paramref name="database" /> unless the visitor may
    /// address that database as a whole - or it is exclusively the tenant in scope's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With a tenant in scope, both questions are asked, tenant first: the one the scope names, and the
    /// one the operation reaches. The second is left out only when the database is exclusively that
    /// tenant's (<see cref="DatabaseReach.IsExclusivelyTenantsAsync" />), which is database-per-tenant:
    /// there, the two questions are about the same data and the tenant one is the equivalent. Without a
    /// tenant, the whole-database question is the only one - and it is the one the resolver already
    /// asked, which a policy answers the same way twice.
    /// </para>
    /// <para>
    /// <paramref name="database" /> is the database the operation actually reaches: the scope's own for a
    /// rebuild, a daemon control or a rewind (the daemon the accessor found is provably that database's),
    /// and for a tenant correction the tenant's own - found through
    /// <see cref="DatabaseReach.FindTenantDatabaseAsync" />, and not provably the one in the URL.
    /// </para>
    /// </remarks>
    /// <exception cref="StudioNotAuthorizedException">
    /// Either question is refused; the refused scope is on the exception for the audit entry.
    /// </exception>
    public async Task RequireWholeDatabaseAsync(
        ResolvedScope resolved,
        IMartenDatabase database,
        StudioCapability capability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(database);

        if (!AnyPolicyAnswersFor(capability))
        {
            return;
        }

        string databaseId = database.Id.Identity;

        if (resolved.TenantId is { Length: > 0 } tenantId)
        {
            await RequireDatabaseAsync(resolved, databaseId, tenantId, capability, cancellationToken).ConfigureAwait(false);

            if (await DatabaseReach.IsExclusivelyTenantsAsync(resolved.Store, database, tenantId, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        await RequireDatabaseAsync(resolved, databaseId, tenantId: null, capability, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses an operation that reaches every database of the store unless the visitor may address
    /// <em>every</em> one of them as a whole.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two operations are that wide: a coordinator pause or resume, and a progression correction asked
    /// for without a tenant, which Marten 9.31 runs over <c>Tenancy.BuildDatabases()</c>.
    /// </para>
    /// <para>
    /// The databases come from <c>IMartenStorage.AllDatabases()</c> - verified in Marten 9.31 to be
    /// <c>Tenancy.BuildDatabases()</c>, the same set both of those operations walk - never
    /// <c>AllSchemaNames()</c> or <c>AllObjects()</c>, which apply migrations (hard rule 14). A store that
    /// cannot enumerate its databases is refused rather than allowed: the whole point of the check is that
    /// the operation is wider than the scope, so not knowing how wide is not a reason to proceed.
    /// </para>
    /// </remarks>
    /// <exception cref="StudioNotAuthorizedException">
    /// A policy refuses one of them, or the databases cannot be listed.
    /// </exception>
    public async Task RequireEveryDatabaseOfTheStoreAsync(
        ResolvedScope resolved,
        StudioCapability capability,
        string action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        if (!AnyPolicyAnswersFor(capability))
        {
            return;
        }

        IReadOnlyList<IMartenDatabase> databases;

        try
        {
            databases = await resolved.Store.Storage.AllDatabases().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Marten Studio could not enumerate the databases of store {StoreKey} before {Action}, so it refused it",
                resolved.Registration.Key,
                action);

            throw new StudioNotAuthorizedException(resolved.Scope);
        }

        foreach (IMartenDatabase database in databases)
        {
            await RequireDatabaseAsync(resolved, database.Id.Identity, tenantId: null, capability, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
