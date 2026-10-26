using System.Diagnostics.CodeAnalysis;

namespace PostgresStudio.Services.Sources;

/// <summary>What kind of thing a source is, which decides which studio-specific screens and checks apply to it.</summary>
internal enum StudioSourceKind
{
    /// <summary>A host's own <c>NpgsqlDataSource</c>, found in its container or named by it.</summary>
    DataSource,

    /// <summary>A Marten store - its databases, its declarations, its tenancy.</summary>
    MartenStore,

    /// <summary>
    /// A host-registered provider that enumerates its databases while the process runs - one per tenant plus an admin
    /// database, say (issue #8, G11). Plain PostgreSQL, like <see cref="DataSource" />, but many databases, enumerated.
    /// </summary>
    HostProvider,
}

/// <summary>One source a studio offers, as the picker names it.</summary>
/// <param name="Key">
/// What travels in the URL's source parameter and in the resource a policy is shown: <c>default</c>, or an ancillary
/// store's marker type name. Stable across processes, compared ignoring case.
/// </param>
/// <param name="DisplayName">What a person reads in the picker.</param>
/// <param name="Kind">What it is.</param>
internal sealed record StudioSourceRegistration(string Key, string DisplayName, StudioSourceKind Kind);

/// <summary>
/// One source that resolved: its databases, its tenancy, and how a scope on it is completed.
/// </summary>
/// <remarks>
/// <para>
/// Returned by <see cref="IStudioSourceProvider.TryResolve" /> and held for one operation - a listing, a resolution -
/// never cached by a caller across operations: the provider owns its own caching, and a source can stop resolving.
/// </para>
/// <para>
/// <b>Nothing here asks a policy.</b> Authorization is the caller's and comes first (<see cref="StudioSourceResolver" />
/// asks before it touches a provider; <c>StudioScopeCatalog</c> filters every listing it gets from here).
/// </para>
/// </remarks>
internal interface IStudioSource
{
    /// <summary>The registration this resolved from.</summary>
    StudioSourceRegistration Registration { get; }

    /// <summary>How many databases it spans. Read from configuration: no IO.</summary>
    StudioDatabaseCardinality Cardinality { get; }

    /// <summary>
    /// Whether a tenant selector means anything for this source - whether anything in it is tenanted per row that the
    /// visitor could see. Read from configuration: no IO. A source with no tenancy answers <see langword="false" />, and
    /// then <see cref="DiscoverTenantsAsync" /> is never asked and a scope naming a tenant is refused by
    /// <see cref="CompleteAsync" />.
    /// </summary>
    bool ShowsTenantSelector { get; }

    /// <summary>
    /// Every database instance the source holds, in the source's own order - two instances may share one
    /// <see cref="IStudioDatabase.Identity" />, and the first of them is the one a scope without a tenant resolves to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called on navigation, so it must not create, migrate or apply anything it can avoid; where the underlying
    /// enumeration is not read-only (a Marten dynamic tenancy's <c>BuildDatabases()</c>, D27) that is the source's
    /// documented known limit and the call sites are fenced (<c>TenancyEnumerationCallTests</c>).
    /// </para>
    /// <para>
    /// A host-supplied provider that enumerates many databases (issue #8, G11) answers the whole list here; the picker
    /// filters it by policy. A reader with no visitor goes through <see cref="IStudioSourceProvider.ListUnattendedAsync" />,
    /// which streams names, and resolves only the databases it reaches.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    ValueTask<IReadOnlyList<IStudioDatabase>> DatabasesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The tenants of <paramref name="database" />, unfiltered by policy (the catalog filters), cached by the source as
    /// it sees fit. Only asked when <see cref="ShowsTenantSelector" /> is <see langword="true" />.
    /// </summary>
    /// <param name="database">The database, as matched in <paramref name="all" />.</param>
    /// <param name="all">What <see cref="DatabasesAsync" /> has just answered, so same-identity twins answer for their tenants.</param>
    /// <param name="cancellationToken">Cancels the discovery.</param>
    /// <exception cref="Exception">Whatever the discovery throws; the catalog logs it (9219) and lists no tenant.</exception>
    ValueTask<TenantList> DiscoverTenantsAsync(IStudioDatabase database, IReadOnlyList<IStudioDatabase> all, CancellationToken cancellationToken);

    /// <summary>
    /// The last steps of a resolution, after the policy said yes and the database was matched: whether the scope's
    /// tenant is one this database answers for, and which same-identity instance that tenant was built from.
    /// </summary>
    /// <remarks>
    /// MartenStudio answers a <c>ResolvedScope</c>, the <see cref="ResolvedSource" /> subtype its own screens take. A source
    /// without tenancy refuses any tenant with <see cref="KeyNotFoundException" /> - the same answer as an unknown one.
    /// </remarks>
    /// <param name="scope">The scope being resolved.</param>
    /// <param name="database">The first instance in <paramref name="all" /> with the scope's identity.</param>
    /// <param name="all">What <see cref="DatabasesAsync" /> has just answered.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <exception cref="KeyNotFoundException">The tenant is not one this database answers for.</exception>
    ValueTask<ResolvedSource> CompleteAsync(
        StudioScope scope,
        IStudioDatabase database,
        IReadOnlyList<IStudioDatabase> all,
        CancellationToken cancellationToken);
}

/// <summary>What one step of a listing of every source's databases found: a database, or why there is none.</summary>
internal enum StudioSweepTargetOutcome
{
    /// <summary><see cref="StudioListedDatabase.DatabaseId" /> names a database.</summary>
    Database,

    /// <summary>The source did not resolve, or its databases could not be listed (already logged by the provider).</summary>
    SourceNotReached,

    /// <summary>The source holds no database it can name without building one - an expected state, not a failure.</summary>
    SourceNotEnumerated,

    /// <summary><see cref="StudioListedDatabase.Count" /> databases are held under ids the source cannot look up.</summary>
    Unnamed,
}

/// <summary>
/// One step of <see cref="IStudioSourceProvider.ListUnattendedAsync" />: a database's name, and nothing opened - or why a
/// source has none to name.
/// </summary>
/// <param name="Source">The source this step is about.</param>
/// <param name="Outcome">What was found.</param>
/// <param name="DatabaseId">
/// For <see cref="StudioSweepTargetOutcome.Database" />: the database's identity, which
/// <see cref="IStudioSourceProvider.ResolveUnattendedAsync" /> takes.
/// </param>
/// <param name="Count">How many databases the step stands for (1, or the unnamed count).</param>
/// <param name="Detail">A Debug-log detail - the tenancy's shape for a source left out - never shown to anyone.</param>
internal sealed record StudioListedDatabase(
    StudioSourceRegistration Source,
    StudioSweepTargetOutcome Outcome,
    string? DatabaseId = null,
    int Count = 1,
    string? Detail = null)
{
    /// <summary>
    /// Whether the source is one database by its nature - a data source, a store whose tenancy is one database - rather than a
    /// set a host or a tenancy lists, which may grow: the health export sweeps every one-database source in every run, and
    /// rotates through the others (API design §3.4).
    /// </summary>
    public bool SingleDatabaseSource { get; init; }
}

/// <summary>How <see cref="IStudioSourceProvider.ResolveUnattendedAsync" /> ended.</summary>
internal enum StudioUnattendedOutcome
{
    /// <summary><see cref="StudioUnattendedResolution.Resolved" /> is the database.</summary>
    Resolved,

    /// <summary>No such source, or no such database in what it lists or holds.</summary>
    NotFound,

    /// <summary>The source would not build, or the database could not be opened (already logged by the provider).</summary>
    NotReached,
}

/// <summary>One database resolved with no visitor, or why not - a value, never an exception.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Resolved">For <see cref="StudioUnattendedOutcome.Resolved" />: the database, resolved with no tenant.</param>
/// <param name="Reason">
/// Otherwise, the studio's own sentence - naming the source and the database as they were asked for, and an exception's
/// type, never its words, which may be a host's or a driver's and carry anything.
/// </param>
internal sealed record StudioUnattendedResolution(StudioUnattendedOutcome Outcome, ResolvedSource? Resolved = null, string? Reason = null)
{
    /// <summary>The database, resolved.</summary>
    public static StudioUnattendedResolution Of(ResolvedSource resolved) => new(StudioUnattendedOutcome.Resolved, resolved);

    /// <summary>No such source or database.</summary>
    public static StudioUnattendedResolution NotFound(string reason) => new(StudioUnattendedOutcome.NotFound, null, reason);

    /// <summary>A source or database that could not be reached.</summary>
    public static StudioUnattendedResolution NotReached(string reason) => new(StudioUnattendedOutcome.NotReached, null, reason);
}

/// <summary>
/// The sources a studio offers: which exist, how each resolves, and every database a sweep with no visitor may cover.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoped.</b> A source's tenant discovery is cached per circuit, and a refresh button clears this circuit's view
/// (<see cref="Invalidate" />); everything process-wide underneath - which sources exist, a failure cache - is the
/// implementation's own singleton. A caller with no circuit - the opt-in health export, a sweeper running with no
/// visitor - creates an <c>AsyncServiceScope</c> per run (<c>IServiceScopeFactory.CreateAsyncScope()</c>), resolves the
/// provider from it, and disposes the scope when the run ends; it never resolves the provider from the root container,
/// which would keep one run's discovered tenants for the life of the process.
/// </para>
/// <para>
/// <b>Never asks a policy, never discovers anything by connection string.</b> Sources are what the application
/// registered (D3's "not a connection-string tool"), found lazily on first use so that registering the studio before
/// the sources is fine. Every listing this answers is filtered by the caller, and every resolution is authorized by the
/// caller before it gets here: a source the visitor may not see and a source that does not exist must be one answer.
/// </para>
/// <para>
/// <b>Identity is the source's.</b> Each source defines its databases' <see cref="IStudioDatabase.Identity" />: a Marten
/// store the database's <c>Id.Identity</c>, unchanged; a DI-registered <c>NpgsqlDataSource</c> its registration name; a
/// host-supplied provider the name the host lists the database under. Whatever it is, it is stable across processes and
/// restarts, because URLs, policies, caches and the sweep history key on it.
/// </para>
/// <para>
/// <b>Adapting a host's own source (issue #8, G11).</b> A later public interface for a host's database provider - a
/// name, and an asynchronous enumeration of databases each with a stable name and a data source - adapts onto this as one
/// <see cref="StudioSourceRegistration" /> of kind <see cref="StudioSourceKind.HostProvider" /> whose
/// <see cref="IStudioSource.DatabasesAsync" /> materializes the enumeration and whose databases carry the name the host
/// lists in <see cref="IStudioDatabase.Identity" />; <see cref="ListUnattendedAsync" /> streams the same names and opens
/// nothing, so a sweep with no visitor resolves only the databases it reaches (<see cref="ResolveUnattendedAsync" />).
/// </para>
/// </remarks>
internal interface IStudioSourceProvider
{
    /// <summary>Every source this studio offers, the default first. Found on first use.</summary>
    IReadOnlyList<StudioSourceRegistration> Registrations();

    /// <summary>The registration with this key, ignoring case, or <see langword="null" />.</summary>
    /// <param name="key">The key the URL carried.</param>
    StudioSourceRegistration? Find(string? key);

    /// <summary>
    /// Builds <paramref name="registration" />, or says why it could not be built. A failure is cached briefly and logged
    /// throttled (9210) by the implementation, so a page listing five broken sources does not rebuild each on every render.
    /// </summary>
    /// <param name="registration">A registration this provider answered.</param>
    /// <param name="source">The resolved source, when available.</param>
    StoreAvailability TryResolve(StudioSourceRegistration registration, [NotNullWhen(true)] out IStudioSource? source);

    /// <summary>Forgets what this circuit's view of the sources cached - discovered tenants - for the refresh button.</summary>
    void Invalidate();

    /// <summary>
    /// How many times <see cref="Invalidate" /> has run on this circuit. A caller that remembers an answer it took from a
    /// source's tenant discovery keeps the generation it was read under and stops trusting the answer when it moves.
    /// </summary>
    long Generation { get; }

    /// <summary>
    /// Every database of every source, by name, for a reader with no visitor and no policy - the opt-in health check (D29's
    /// second exception) and the diagnostics API (D31) - streamed, in registration order, with the sources that could not
    /// be listed said as values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Names, never connections</b> (API design S1). Nothing is opened, no data source is built and no tenancy that
    /// builds is enumerated: a Marten store's databases are the ones its tenancy already holds (<c>HeldDatabases</c>, D27),
    /// a data source is its one database by its key, and a host's database source is the names it lists (its own
    /// minute-long cache). A database is named by an identity <see cref="ResolveUnattendedAsync" /> looks up again.
    /// </para>
    /// <para>
    /// The caller has no circuit, so it resolves this provider from an <c>AsyncServiceScope</c> of its own run.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    IAsyncEnumerable<StudioListedDatabase> ListUnattendedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One database, resolved with no tenant and no visitor (API design S4): every step of <see cref="StudioSourceResolver" />
    /// after authorization - the registration, the source, the database, opened through the host only for this operation -
    /// and only a database <see cref="ListUnattendedAsync" /> would name. Never throws but for cancellation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No policy is asked</b>, so this is for the two readers whose output needs none - the health export's codes and
    /// counts, and host code through the diagnostics API - and never for a visitor.
    /// </para>
    /// <para>
    /// An empty <paramref name="databaseId" /> is the source's first database, as the visitor's resolver treats it.
    /// </para>
    /// </remarks>
    /// <param name="sourceKey">The source's key, compared ignoring case.</param>
    /// <param name="databaseId">The database's identity, compared ignoring case; empty for the source's first.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    ValueTask<StudioUnattendedResolution> ResolveUnattendedAsync(string sourceKey, string databaseId, CancellationToken cancellationToken);
}
