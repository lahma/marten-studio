using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;

using Marten;

namespace MartenStudio.Services.Projections;

/// <summary>What a row of <c>mt_event_progression</c> is, as this store's configuration answers it.</summary>
internal enum ProgressionRowKind
{
    /// <summary>Marten's own high-water bookkeeping: never a shard (<see cref="ProjectionDataService.IsBookkeepingRow" />).</summary>
    Bookkeeping,

    /// <summary>A registered async shard - a projection's or a subscription's - by its store-global identity.</summary>
    Shard,

    /// <summary>A composite projection's member, which the composite's own agent advances in its batch.</summary>
    CompositeMember,

    /// <summary>
    /// One tenant's row of a registered shard (or member), <c>{Name}:{ShardKey}:{tenant}</c>, on a store whose events are
    /// numbered per tenant (<c>UseTenantPartitionedEvents</c>), where the daemon runs one agent per shard and tenant.
    /// </summary>
    TenantShard,

    /// <summary>A row nothing this store registers advances: an orphan, with an <see cref="OrphanReason" />.</summary>
    Orphan,
}

/// <summary>Why nothing this store registers advances an orphaned progression row.</summary>
internal enum OrphanReason
{
    /// <summary>No projection or subscription of that name is registered: it was renamed or removed.</summary>
    NoSuchProjection,

    /// <summary>The projection is registered at another version, whose rows carry their own identity.</summary>
    OtherVersion,

    /// <summary>The projection is registered at that version, and no longer has that shard.</summary>
    ShardNotRegistered,

    /// <summary>The projection is registered, inline or live: the async daemon does not run it.</summary>
    NotAsync,

    /// <summary>
    /// One tenant's row of a registered shard on a store that does not number its events per tenant
    /// (<c>UseTenantPartitionedEvents</c> off), whose daemon runs only the store-global agent.
    /// </summary>
    TenantRowWithoutPartitioning,

    /// <summary>A name no <c>ShardName</c> parses, so nothing in this store can claim it.</summary>
    NotAShardName,
}

/// <summary>One async shard this store registers.</summary>
/// <param name="Identity">The store-global <c>ShardName.Identity</c>.</param>
/// <param name="Projection">The projection or subscription that runs it.</param>
/// <param name="GatesSideEffectsBehindPriorVersion">
/// <c>AsyncOptions.GateSideEffectsBehindPriorVersion</c> (JasperFx 2.74 and 2.95): the daemon reads the highest prior
/// version's progression row each time this shard starts, and suppresses side effects up to that row's position.
/// </param>
internal sealed record RegisteredShard(string Identity, string Projection, bool GatesSideEffectsBehindPriorVersion = false);

/// <summary>One projection or subscription this store registers, of any lifecycle.</summary>
/// <param name="Name">Its name, which is the first segment of every identity it writes.</param>
/// <param name="Lifecycle">Inline, Live or Async.</param>
/// <param name="Version">Its version, which is part of the identity of every row it writes above version 1.</param>
internal sealed record RegisteredSource(string Name, ProjectionLifecycle Lifecycle, uint Version);

/// <summary>What one progression row is.</summary>
/// <param name="Kind">Which kind of row.</param>
/// <param name="Projection">
/// The registered projection or subscription it belongs to - or, for an orphan, the one its name points at where one is
/// registered; <see langword="null" /> when none is.
/// </param>
/// <param name="Reason">Why nothing advances it, for an <see cref="ProgressionRowKind.Orphan" />.</param>
/// <param name="TenantId">The tenant its name carries, or <see langword="null" />.</param>
/// <param name="RegisteredVersion">The version the projection is registered at, for <see cref="OrphanReason.OtherVersion" />.</param>
/// <param name="RowVersion">The version the row's name carries, when it parses.</param>
/// <param name="ReadByDaemon">
/// Whether the daemon may still read it: a lower version's row of a projection whose registered version gates its side
/// effects behind the prior version (<see cref="RegisteredShard.GatesSideEffectsBehindPriorVersion" />).
/// </param>
/// <param name="OfCompositeMember">Whether a tenant's row is a composite member's, which the composite's agent advances.</param>
internal sealed record ProgressionRowClass(
    ProgressionRowKind Kind,
    string? Projection = null,
    OrphanReason? Reason = null,
    string? TenantId = null,
    uint? RegisteredVersion = null,
    uint? RowVersion = null,
    bool ReadByDaemon = false,
    bool OfCompositeMember = false)
{
    /// <summary>Whether something this store registers advances the row.</summary>
    public bool IsRegistered => Kind is ProgressionRowKind.Shard or ProgressionRowKind.CompositeMember or ProgressionRowKind.TenantShard;
}

/// <summary>
/// Which projection, subscription or member a progression row belongs to - or that nothing this store registers advances
/// it, and why. Configuration only, no connection; a pure function of what it is built from.
/// </summary>
/// <remarks>
/// <para>
/// <b>The registered shards are the daemon's own list</b>, <c>ProjectionOptions.AllShards()</c>: the async projections'
/// shards and every subscription's, including one registered with <c>Events.Subscribe(...)</c>, which
/// <c>IReadOnlyEventStoreOptions.Projections()</c> does not list (<c>EventGraph</c> at <c>V9.39.0</c> and <c>v9.54.2</c>).
/// A composite's members are added from <c>CompositeProjection.AllProjections()</c>, which <c>AllShards()</c> does not
/// carry. Identities compare ignoring case, as the projections screen keys them.
/// </para>
/// <para>
/// <b>A tenant's row is registered only under <c>UseTenantPartitionedEvents</c></b>, where the coordinator's distributors
/// expand each store-global shard into one per tenant (jasperfx#489, marten#4862, in <c>ProjectionCoordinator</c> at
/// <c>V9.39.0</c> and <c>v9.54.2</c>) and each agent writes <c>{Name}:{ShardKey}:{tenant}</c>
/// (<c>ShardName.ForTenant</c>). On any other store the daemon runs the store-global agent alone, so a tenant's row
/// there is left over from when the flag was on, and nothing advances it.
/// </para>
/// <para>
/// <b>A lower version's row may still be read.</b> With <c>GateSideEffectsBehindPriorVersion</c> on, the daemon reads
/// the highest prior version's row of the same shard and tenant on every start of the new version and suppresses side
/// effects up to its position (<c>resolvePriorVersionProgressAsync</c>, <c>JasperFxAsyncDaemon</c> at <c>V2.74.0</c> and
/// <c>V2.95.1</c>). Such a row is still an orphan - nothing advances it - but it is marked <see cref="ProgressionRowClass.ReadByDaemon" />,
/// and nothing offers a statement that removes it.
/// </para>
/// </remarks>
internal sealed class ProgressionRowClassifier
{
    private readonly Dictionary<string, RegisteredShard> shards;
    private readonly Dictionary<string, string> members;
    private readonly Dictionary<string, RegisteredSource> sources;
    private readonly bool tenantPartitionedEvents;

    /// <param name="shards">Every async shard the store registers, by store-global identity.</param>
    /// <param name="sources">Every projection and subscription the store registers, of any lifecycle, and every composite member.</param>
    /// <param name="compositeMembers">Each composite member's store-global identity, and the composite that advances it.</param>
    /// <param name="tenantPartitionedEvents"><c>Events.UseTenantPartitionedEvents</c>.</param>
    public ProgressionRowClassifier(
        IEnumerable<RegisteredShard> shards,
        IEnumerable<RegisteredSource> sources,
        IReadOnlyDictionary<string, string> compositeMembers,
        bool tenantPartitionedEvents)
    {
        ArgumentNullException.ThrowIfNull(shards);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(compositeMembers);

        this.shards = new Dictionary<string, RegisteredShard>(StringComparer.OrdinalIgnoreCase);
        foreach (RegisteredShard shard in shards)
        {
            this.shards.TryAdd(shard.Identity, shard);
        }

        members = new Dictionary<string, string>(compositeMembers, StringComparer.OrdinalIgnoreCase);

        this.sources = new Dictionary<string, RegisteredSource>(StringComparer.OrdinalIgnoreCase);
        foreach (RegisteredSource source in sources)
        {
            this.sources.TryAdd(source.Name, source);
        }

        this.tenantPartitionedEvents = tenantPartitionedEvents;
    }

    /// <summary>Every store-global identity it calls registered: the shards and the composite members.</summary>
    public IReadOnlyList<string> RegisteredIdentities => [.. shards.Keys.Concat(members.Keys).Order(StringComparer.Ordinal)];

    /// <summary>
    /// <c>{identity}:</c> for every registered identity whose tenants' rows are registered too - every one under
    /// <c>UseTenantPartitionedEvents</c>, none otherwise.
    /// </summary>
    public IReadOnlyList<string> TenantPrefixes => tenantPartitionedEvents ? [.. RegisteredIdentities.Select(static x => x + ":")] : [];

    /// <summary>
    /// The classifier for <paramref name="store" />'s configuration: <c>AllShards()</c>, the registered projections and
    /// their composites' members.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="projections">The projections the screen lists, with each composite's members.</param>
    public static ProgressionRowClassifier For(IDocumentStore store, IReadOnlyList<ProjectionInfo> projections)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(projections);

        List<RegisteredShard> registered = [];
        List<RegisteredSource> sources = [];
        Dictionary<string, string> compositeMembers = new(StringComparer.OrdinalIgnoreCase);

        foreach (ProjectionInfo projection in projections)
        {
            sources.Add(new RegisteredSource(projection.Name, projection.Lifecycle, projection.Version));

            if (projection.IsAsync)
            {
                registered.AddRange(projection.ShardNames.Select(x => new RegisteredShard(x, projection.Name)));

                foreach (string member in projection.MemberShardNames)
                {
                    compositeMembers.TryAdd(member, projection.Name);

                    if (ShardName.TryParse(member, out ShardName? parsed) && parsed is not null)
                    {
                        sources.Add(new RegisteredSource(parsed.Name, ProjectionLifecycle.Async, parsed.Version));
                    }
                }
            }
        }

        // The daemon's own list last, so a projection's own row above keeps its name, and every subscription and every
        // shard's gate comes from here.
        foreach ((ShardName name, bool gates) in AsyncShardsOf(store))
        {
            registered.Add(new RegisteredShard(name.Identity, name.Name, gates));
            sources.Add(new RegisteredSource(name.Name, ProjectionLifecycle.Async, name.Version));
        }

        // The gate is a property of the shard, so the daemon's answer replaces the projection list's for the same identity.
        Dictionary<string, RegisteredShard> byIdentity = new(StringComparer.OrdinalIgnoreCase);
        foreach (RegisteredShard shard in registered)
        {
            byIdentity[shard.Identity] = byIdentity.TryGetValue(shard.Identity, out RegisteredShard? earlier)
                ? earlier with { GatesSideEffectsBehindPriorVersion = earlier.GatesSideEffectsBehindPriorVersion || shard.GatesSideEffectsBehindPriorVersion }
                : shard;
        }

        bool partitioned;
        try
        {
            partitioned = store.Options.Events.UseTenantPartitionedEvents;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            partitioned = false;
        }

        return new ProgressionRowClassifier(byIdentity.Values, sources, compositeMembers, partitioned);
    }

    /// <summary>
    /// Every async shard <c>ProjectionOptions.AllShards()</c> lists, with whether it gates its side effects behind a prior
    /// version - none for an options object of another shape, whose projections the screen's own list already carries.
    /// </summary>
    internal static IReadOnlyList<(ShardName Name, bool GatesSideEffects)> AsyncShardsOf(IDocumentStore store)
    {
        try
        {
            if (store.Options is StoreOptions concrete)
            {
                return [.. concrete.Projections.AllShards().Select(static x => (x.Name, x.Options.GateSideEffectsBehindPriorVersion))];
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A store whose shard list cannot be read is classified by its projection list alone.
        }

        return [];
    }

    /// <summary>
    /// The subscriptions <c>ProjectionOptions.AllShards()</c> runs that <c>Projections()</c> does not list - each one
    /// registered with <c>Events.Subscribe(...)</c> - as rows of the projections screen: async, one row per subscription,
    /// with its shards.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="listed">The names <c>Projections()</c> already lists, and every composite member's.</param>
    internal static IReadOnlyList<ProjectionInfo> SubscriptionsOf(IDocumentStore store, IReadOnlyCollection<string> listed)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(listed);

        HashSet<string> known = new(listed, StringComparer.OrdinalIgnoreCase);

        try
        {
            if (store.Options is not StoreOptions concrete)
            {
                return [];
            }

            return
            [
                .. concrete.Projections.AllShards()
                    .Where(x => !known.Contains(x.Name.Name))
                    .GroupBy(static x => x.Name.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(static group =>
                    {
                        var first = group.First();
                        ISubscriptionSource? source = first.Factory as ISubscriptionSource;

                        return new ProjectionInfo(
                            group.Key,
                            ProjectionLifecycle.Async,
                            source?.Type.ToString() ?? "Subscription",
                            first.Name.Version,
                            source?.ImplementationType?.Name ?? first.Factory.GetType().Name,
                            [.. group.Select(static x => x.Name.Identity).Distinct(StringComparer.OrdinalIgnoreCase)])
                        {
                            IsSubscription = true,
                        };
                    }),
            ];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>What the row named <paramref name="progressionName" /> is.</summary>
    /// <param name="progressionName">The row's <c>name</c>.</param>
    public ProgressionRowClass Classify(string progressionName)
    {
        if (ProjectionDataService.IsBookkeepingRow(progressionName))
        {
            return new ProgressionRowClass(ProgressionRowKind.Bookkeeping);
        }

        if (shards.TryGetValue(progressionName, out RegisteredShard? shard))
        {
            return new ProgressionRowClass(ProgressionRowKind.Shard, shard.Projection);
        }

        if (members.TryGetValue(progressionName, out string? composite))
        {
            return new ProgressionRowClass(ProgressionRowKind.CompositeMember, composite);
        }

        if (!ShardName.TryParse(progressionName, out ShardName? parsed) || parsed is null)
        {
            return new ProgressionRowClass(ProgressionRowKind.Orphan, Reason: OrphanReason.NotAShardName);
        }

        if (parsed.TenantId is { } tenant)
        {
            string neutral = ShardName.Compose(parsed.Name, parsed.ShardKey, null, parsed.Version).Identity;
            bool member = !shards.ContainsKey(neutral) && members.ContainsKey(neutral);
            string? owner = shards.TryGetValue(neutral, out RegisteredShard? baseShard)
                ? baseShard.Projection
                : members.GetValueOrDefault(neutral);

            if (owner is not null)
            {
                return tenantPartitionedEvents
                    ? new ProgressionRowClass(ProgressionRowKind.TenantShard, owner, TenantId: tenant, RowVersion: parsed.Version, OfCompositeMember: member)
                    : new ProgressionRowClass(ProgressionRowKind.Orphan, owner, OrphanReason.TenantRowWithoutPartitioning, tenant, RowVersion: parsed.Version);
            }
        }

        if (!sources.TryGetValue(parsed.Name, out RegisteredSource? source))
        {
            return new ProgressionRowClass(ProgressionRowKind.Orphan, null, OrphanReason.NoSuchProjection, parsed.TenantId, RowVersion: parsed.Version);
        }

        if (source.Lifecycle != ProjectionLifecycle.Async)
        {
            return new ProgressionRowClass(ProgressionRowKind.Orphan, source.Name, OrphanReason.NotAsync, parsed.TenantId, RowVersion: parsed.Version);
        }

        if (parsed.Version != source.Version)
        {
            // The daemon reads a prior version only from a newer one that gates its side effects behind it.
            bool read = parsed.Version < source.Version
                && shards.TryGetValue(ShardName.Compose(source.Name, parsed.ShardKey, null, source.Version).Identity, out RegisteredShard? current)
                && current.GatesSideEffectsBehindPriorVersion;

            return new ProgressionRowClass(
                ProgressionRowKind.Orphan, source.Name, OrphanReason.OtherVersion, parsed.TenantId, source.Version, parsed.Version, read);
        }

        return new ProgressionRowClass(ProgressionRowKind.Orphan, source.Name, OrphanReason.ShardNotRegistered, parsed.TenantId, RowVersion: parsed.Version);
    }
}
