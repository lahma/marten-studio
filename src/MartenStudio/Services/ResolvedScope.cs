using Marten;
using Marten.Storage;

using PostgresStudio.Services.Sources;
using PostgresStudio.Services;

namespace MartenStudio.Services;

/// <summary>
/// A scope that passed every check, with the Marten objects it names.
/// </summary>
/// <remarks>
/// <para>
/// MartenStudio's <see cref="ResolvedSource" />: every Marten-only service keeps reading <see cref="Registration" />,
/// <see cref="Store" /> and <see cref="Database" />, and shared code reads the base record's <c>Source</c> and
/// <c>Target</c>, which are the same registration and the same database seen through the shared seam.
/// </para>
/// <para>
/// <b>One view, whatever is copied.</b> The base record's <c>Source</c> and <c>Target</c> are computed from
/// <see cref="Registration" /> and <see cref="Database" /> when the record is built, so all four are get-only: a
/// <c>with { Database = … }</c> that left shared code reading the old instance does not compile. <c>with { Scope = … }</c>
/// and <c>with { Store = … }</c> still do - neither feeds the base record.
/// </para>
/// </remarks>
/// <param name="Scope">The scope that was resolved.</param>
/// <param name="Registration">The store's registration.</param>
/// <param name="Store">The built store.</param>
/// <param name="Database">The database the scope resolved to - for a tenant, the instance that tenant was built from.</param>
internal sealed record ResolvedScope(
    StudioScope Scope,
    MartenStoreRegistration Registration,
    IDocumentStore Store,
    IMartenDatabase Database)
    : ResolvedSource(Scope, Registration.AsSource(), MartenStudioDatabase.Of(Database))
{
    /// <summary>The store's registration; get-only, because the base record's <c>Source</c> is built from it.</summary>
    public MartenStoreRegistration Registration { get; } = Registration;

    /// <summary>The database the scope resolved to; get-only, because the base record's <c>Target</c> is built from it.</summary>
    public IMartenDatabase Database { get; } = Database;

    /// <summary>
    /// Every database the resolver's one enumeration listed, in the order <c>AllDatabases()</c> answered (1.2): what the Tenants
    /// overview places its rows with, so it enumerates nothing of its own. Empty for the unattended resolution and for a test that
    /// builds a scope by hand; the overview then uses <see cref="Database" /> alone.
    /// </summary>
    public IReadOnlyList<IMartenDatabase> ListedDatabases { get; init; } = [];

    /// <summary>
    /// Two scopes over the same registration, store, database and scope are equal: <see cref="ListedDatabases" /> is what the
    /// resolution saw, not what the scope is, and a wrapper of one instance is equal to another of it.
    /// </summary>
    /// <param name="other">The other scope.</param>
    public bool Equals(ResolvedScope? other) =>
        other is not null
        && base.Equals((ResolvedSource) other)
        && EqualityComparer<MartenStoreRegistration>.Default.Equals(Registration, other.Registration)
        && ReferenceEquals(Store, other.Store)
        && Equals(Database, other.Database);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Registration, Store, Database);
}

/// <summary>The Marten spellings of the shared scope types.</summary>
internal static class MartenScopeResources
{
    /// <summary>
    /// <paramref name="scope" /> as the resource an authorization handler is asked about: MartenStudio's public
    /// <see cref="MartenStoreResource" />.
    /// </summary>
    /// <param name="scope">The scope.</param>
    /// <param name="capability">
    /// The capability being exercised (for example <c>EditDocuments</c>), or <see langword="null" /> for a
    /// read - which is what lets one handler answer both the store policy and the write policy.
    /// </param>
    public static MartenStoreResource ToResource(this StudioScope scope, string? capability)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new MartenStoreResource(scope.StoreKey, scope.DatabaseId, scope.TenantId, capability);
    }

    /// <summary>This registration as the shared seam names it.</summary>
    public static StudioSourceRegistration AsSource(this MartenStoreRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return new StudioSourceRegistration(registration.Key, registration.DisplayName, StudioSourceKind.MartenStore);
    }
}
