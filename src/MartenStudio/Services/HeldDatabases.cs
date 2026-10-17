using JasperFx.Descriptors;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Storage;

using Weasel.Postgresql.Migrations;

namespace MartenStudio.Services;

/// <summary>How a store's tenancy can say which databases it has without building, migrating or querying anything.</summary>
internal enum TenancyShape
{
    /// <summary>
    /// <c>Single</c> or <c>StaticMultiple</c> - <c>DefaultTenancy</c>, its tenant-partitioned subclass,
    /// <c>StaticMultiTenancy</c>: its databases are configuration.
    /// </summary>
    Configured,

    /// <summary><c>SingleServerMultiTenancy</c>: the databases it has built so far.</summary>
    SingleServer,

    /// <summary><c>MasterTableTenancy</c> or <c>ShardedTenancy</c>: the databases it holds, each found in its own cache.</summary>
    Held,

    /// <summary>Any other tenancy - a host's own, with dynamic databases: it cannot be asked without building.</summary>
    Opaque,
}

/// <summary>A store's databases as far as its tenancy can say from configuration or memory.</summary>
/// <param name="Shape">What kind of answer this is.</param>
/// <param name="Databases">
/// The databases, one per <c>Id.Identity</c>, in the order the tenancy lists them - empty for
/// <see cref="TenancyShape.Opaque" />, and possibly empty for <see cref="TenancyShape.SingleServer" /> and
/// <see cref="TenancyShape.Held" /> when this process has loaded none yet.
/// </param>
/// <param name="Unnamed">
/// How many databases a <see cref="TenancyShape.Held" /> tenancy holds under an id its <c>TenantIdStyle</c> would
/// correct, and that were therefore not looked up: a lookup would miss the tenancy's cache and read its master
/// table. They are held, and not in <paramref name="Databases" />.
/// </param>
internal sealed record HeldDatabaseList(TenancyShape Shape, IReadOnlyList<IMartenDatabase> Databases, int Unnamed = 0)
{
    /// <summary>Whether the tenancy could say at all; <see langword="false" /> only for <see cref="TenancyShape.Opaque" />.</summary>
    public bool IsKnown => Shape != TenancyShape.Opaque;
}

/// <summary>
/// The one place the studio asks a store which databases it has on a path nobody chose to make write: a health
/// sweep, the health export, the daemon card. It never makes a tenancy apply, seed, query or create anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not simply <c>Storage.AllDatabases()</c>.</b> That is <c>Tenancy.BuildDatabases()</c>, which for the
/// dynamic tenancies is not a read (read at Marten <c>V9.39.0</c> and <c>V9.50.2</c>):
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>MasterTableTenancy</c> and <c>ShardedTenancy</c> apply their lookup tables' schema under the host's
/// <c>AutoCreate</c> on the first call in a process (<c>maybeApplyChanges</c>), upsert their configured seed
/// rows, and read the whole master table - building a data source for every tenant it names - on every call,
/// outside the studio's read-only session. A master-table tenancy that has so far only resolved tenants one at
/// a time (<c>GetTenantAsync</c>, which applies and seeds nothing) holds databases and has never been built.
/// </description></item>
/// <item><description>
/// <c>SingleServerMultiTenancy</c> runs <c>CREATE DATABASE</c> for every tenant it was configured with that
/// does not have one yet.
/// </description></item>
/// </list>
/// <para>
/// <b>So each shape is asked only what it already holds:</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="TenancyShape.Configured" />: <c>AllDatabases()</c>, whose <c>BuildDatabases()</c> returns the
/// configured databases from memory.
/// </description></item>
/// <item><description>
/// <see cref="TenancyShape.SingleServer" />: Weasel's <see cref="SingleServerDatabaseCollection{T}.AllDatabases" />,
/// the databases already built, from memory.
/// </description></item>
/// <item><description>
/// <see cref="TenancyShape.Held" />: the identifiers JasperFx's <see cref="ITenantedSource{T}.AllActive" />
/// names - the tenancy's own cache, from memory - each looked up with <see cref="ITenancy.TryFindDatabase" />
/// (#5400, the lookup Marten provides "for diagnostics and monitoring reads that must not change what exists").
/// Matched by concrete type, because only these two are known to declare a lookup of their own: the
/// interface's default body is <c>FindOrCreateDatabase</c>, which provisions.
/// </description></item>
/// <item><description><see cref="TenancyShape.Opaque" />: nothing, because nothing can be asked.</description></item>
/// </list>
/// <para>
/// Nothing here asks a tenancy for a tenant by name, which on a dynamic tenancy provisions one
/// (<see cref="DatabaseReach.FindTenantDatabaseAsync" />).
/// </para>
/// </remarks>
internal static class HeldDatabases
{
    /// <summary>
    /// The databases of <paramref name="store" /> its tenancy holds. Whatever <c>AllDatabases()</c> throws for a
    /// configured tenancy is the caller's.
    /// </summary>
    public static async Task<HeldDatabaseList> ListAsync(IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        ITenancy tenancy = store.Options.Tenancy;

        if (BuildsNothing(tenancy))
        {
            return new HeldDatabaseList(
                TenancyShape.Configured, OnePerIdentity(await store.Storage.AllDatabases().ConfigureAwait(false)));
        }

        if (tenancy is SingleServerDatabaseCollection<MartenDatabase> server)
        {
            return new HeldDatabaseList(TenancyShape.SingleServer, OnePerIdentity(server.AllDatabases()));
        }

        if (tenancy is MasterTableTenancy or ShardedTenancy && tenancy is ITenantedSource<string> source)
        {
            (List<IMartenDatabase> held, int unnamed) = await FromCacheAsync(store, tenancy, source).ConfigureAwait(false);
            return new HeldDatabaseList(TenancyShape.Held, OnePerIdentity(held), unnamed);
        }

        return new HeldDatabaseList(TenancyShape.Opaque, []);
    }

    /// <summary>The databases a master-table or sharded tenancy holds, each found in its own cache.</summary>
    /// <remarks>
    /// <para>
    /// <b>Every lookup is a cache hit, by construction.</b> Each id comes from <c>AllActive()</c>, which is the
    /// keys (sharded: <c>_databasesById</c>) or the databases' identifiers (master-table: <c>_databases</c>, whose
    /// keys are those identifiers) of the very cache <c>TryFindDatabase</c> looks in first. That lookup corrects the
    /// id with <c>TenantIdStyle</c> before it looks, so an id the correction would change - a sharded pool id like
    /// <c>Shard_A</c> under <c>ForceLowerCase</c> - would miss, and a miss is two statements against the master
    /// table and a data source built for nothing (F8). Such an id is left out instead, and counted: one held
    /// database not checked, and said so, rather than a read nobody asked for. A master-table tenancy's ids are
    /// tenant ids it has already corrected, so it never has one.
    /// </para>
    /// <para>
    /// Residual: a tenancy whose cache is emptied between the two calls (<c>ITenantedSource.RefreshAsync</c>) turns
    /// a lookup into a miss; and on a sharded tenancy, a pool id that is also a tenant id is answered with that
    /// tenant's database, which is held too, so one database may be missed rather than a wrong one checked.
    /// </para>
    /// </remarks>
    private static async Task<(List<IMartenDatabase> Held, int Unnamed)> FromCacheAsync(
        IDocumentStore store,
        ITenancy tenancy,
        ITenantedSource<string> source)
    {
        TenantIdStyle style = store.Options.TenantIdStyle;
        List<IMartenDatabase> held = [];
        int unnamed = 0;

        foreach (string id in source.AllActive())
        {
            if (!string.Equals(style.MaybeCorrectTenantId(id), id, StringComparison.Ordinal))
            {
                unnamed++;
                continue;
            }

            if (await tenancy.TryFindDatabase(id).ConfigureAwait(false) is { } database)
            {
                held.Add(database);
            }
        }

        return (held, unnamed);
    }

    /// <summary>Whether the tenancy's databases are configuration rather than something it builds.</summary>
    /// <remarks>A tenancy that cannot say is treated as one that builds, which is the cautious answer.</remarks>
    private static bool BuildsNothing(ITenancy tenancy)
    {
        try
        {
            return tenancy.Cardinality is DatabaseCardinality.Single or DatabaseCardinality.StaticMultiple;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// One database per identity. A master-table tenancy holds one object per tenant, and two tenants with one
    /// connection string are two objects over one database - which is one database, and one pool.
    /// </summary>
    private static List<IMartenDatabase> OnePerIdentity(IEnumerable<IMartenDatabase> databases)
    {
        List<IMartenDatabase> distinct = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (IMartenDatabase database in databases)
        {
            if (seen.Add(database.Id.Identity))
            {
                distinct.Add(database);
            }
        }

        return distinct;
    }
}
