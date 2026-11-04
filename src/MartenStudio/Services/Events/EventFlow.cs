using JasperFx.Events.Projections;

namespace MartenStudio.Services.Events;

/// <summary>
/// Which projections and subscriptions read each event type of a store, and what each one writes (1.2, M6): the model
/// behind Events › Event flow.
/// </summary>
/// <remarks>
/// <para>
/// Built from <c>StoreOptions</c> and the projection graph alone by <see cref="EventFlowGraph" />: no connection, no
/// audit entry, and none of the Marten or JasperFx members that describe, validate, build or migrate
/// (<c>EventFlowPurityTests</c>). Everything in it is a function of the configuration, so it is the same in every
/// process and for every visitor; what a visitor may see of the store is decided in front of the cache that holds it.
/// </para>
/// <para>
/// A hidden document type (<c>IsDocumentTypeVisible</c>) is absent from every string of the model: no output, no count, no
/// note.
/// </para>
/// </remarks>
/// <param name="EventTypes">The known event types: the user's by alias (ordinal), then Marten's own.</param>
/// <param name="Sources">Every projection, composite member and subscription, by name ignoring case.</param>
/// <param name="Reads">The event type to source edges.</param>
/// <param name="Writes">The source to output edges.</param>
/// <param name="Outputs">The document types, flat tables and live aggregates the sources write: visible ones only.</param>
/// <param name="TagTypes">The DCB tag types, with an aggregate their hiding has already removed.</param>
/// <param name="SubscriptionsUnavailable"><see langword="null" />, or why the daemon's shard list could not be read.</param>
internal sealed record EventFlowModel(
    IReadOnlyList<FlowEventType> EventTypes,
    IReadOnlyList<FlowSource> Sources,
    IReadOnlyList<FlowRead> Reads,
    IReadOnlyList<FlowWrite> Writes,
    IReadOnlyList<FlowOutput> Outputs,
    IReadOnlyList<EventTagTypeModel> TagTypes,
    string? SubscriptionsUnavailable)
{
    /// <summary>
    /// <see langword="null" />, or why no source lists what it writes: Marten would not give the store's document types, so nothing
    /// can be told from a hidden type and the model fails closed. Every document type then counts as hidden: no output is listed,
    /// and generic event types over a type, stream types, the global note, live outputs and tag aggregate names drop out. The event
    /// types the registry knows, and every source's reads of them, stay.
    /// </summary>
    public string? DocumentTypesUnavailable { get; init; }

    /// <summary>A store with nothing to draw.</summary>
    public static EventFlowModel None { get; } = new([], [], [], [], [], [], null);

    /// <summary>
    /// The event types nothing reads: known, not Marten's own, with no read edge - and none at all when some source
    /// declares no event types, because that source is fed every event.
    /// </summary>
    public IReadOnlyList<FlowEventType> Unconsumed
    {
        get
        {
            if (HasEverythingSource)
            {
                return [];
            }

            HashSet<string> read = new(Reads.Select(static x => x.EventAlias), StringComparer.Ordinal);

            return [.. EventTypes.Where(x => !x.IsMartenOwn && !read.Contains(x.Key))];
        }
    }

    /// <summary>Whether some source declares no event types and so is fed every event.</summary>
    public bool HasEverythingSource => Sources.Any(static x => x.Input == FlowInput.Everything);

    /// <summary>The first source called <paramref name="name" />, ignoring case, or <see langword="null" />.</summary>
    public FlowSource? Source(string name) =>
        Sources.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The members of the composite called <paramref name="name" />, ignoring case, and theirs when one is a composite.</summary>
    public IReadOnlyList<FlowSource> MembersOf(string name)
    {
        List<FlowSource> members = [];
        Queue<string> pending = new([name]);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase) { name };

        while (pending.TryDequeue(out string? composite))
        {
            foreach (FlowSource member in Sources.Where(x => x.Composite is not null && string.Equals(x.Composite, composite, StringComparison.OrdinalIgnoreCase)))
            {
                members.Add(member);

                if (member.IsComposite && seen.Add(member.Name))
                {
                    pending.Enqueue(member.Name);
                }
            }
        }

        return members;
    }

    private List<FlowSource> SelfAndMembers(string name)
    {
        List<FlowSource> selected = [];
        FlowSource? named = Source(name);

        if (named is not null)
        {
            selected.Add(named);
        }

        selected.AddRange(MembersOf(name));

        return selected;
    }

    /// <summary>
    /// The event types a projection reads - for a composite, the union of its members' - or <see cref="FlowInput.Everything" />.
    /// A rebuild replays every event through the shard's filter, so this is what a rebuild of it would read (the Rebuild
    /// dialog's row, 1.2).
    /// </summary>
    /// <param name="source">The projection's name, ignoring case.</param>
    public FlowReads ReadsOf(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        List<FlowSource> selected = SelfAndMembers(source);
        HashSet<string> names = new(selected.Select(static x => x.Name), StringComparer.OrdinalIgnoreCase);

        if (selected.Any(static x => x.Input == FlowInput.Everything))
        {
            return new FlowReads(FlowInput.Everything, [], []);
        }

        HashSet<string> keys = new(Reads.Where(x => names.Contains(x.Source)).Select(static x => x.EventAlias), StringComparer.Ordinal);

        return new FlowReads(
            FlowInput.Declared,
            [.. EventTypes.Where(x => !x.IsMartenOwn && keys.Contains(x.Key)).Select(static x => x.Alias)],
            [.. EventTypes.Where(x => x.IsMartenOwn && keys.Contains(x.Key)).Select(static x => x.Alias)]);
    }
}

/// <summary>One event type the store knows.</summary>
/// <param name="Alias">What <c>mt_events.type</c> holds, and what every Events link filters by.</param>
/// <param name="ClrName">The .NET simple name; a closed generic is spelled <c>Compacted&lt;OrderSummary&gt;</c>.</param>
/// <param name="DotNetTypeName">The recorded .NET type name, for a tooltip.</param>
/// <param name="IsMartenOwn">Whether the type is declared by Marten or JasperFx (<c>Archived</c>, <c>Compacted&lt;T&gt;</c>).</param>
/// <param name="IsBaseType">Whether the type is an interface or abstract: a declared base other event types are matched through.</param>
internal sealed record FlowEventType(string Alias, string ClrName, string DotNetTypeName, bool IsMartenOwn, bool IsBaseType)
{
    /// <summary>
    /// What edges name this type by: its alias, or - for a type whose alias another known type shares - the alias and the
    /// .NET type name together. The model never merges two types.
    /// </summary>
    public string Key { get; init; } = Alias;

    /// <summary>The other type's .NET name when the alias is shared, or <see langword="null" />.</summary>
    public string? SharesAliasWith { get; init; }

    /// <summary>
    /// Whether the store's event registry has not met this type yet: a projection declares it, and its alias is the one the
    /// registry will give it. The registry grows as events are appended or read, so this is the state of a fresh process.
    /// </summary>
    public bool NotRegistered { get; init; }
}

/// <summary>What kind of projection or subscription a source is.</summary>
internal enum FlowSourceKind
{
    /// <summary>A single-stream projection or <c>Snapshot&lt;T&gt;</c>.</summary>
    SingleStream,

    /// <summary>A multi-stream projection.</summary>
    MultiStream,

    /// <summary>An <c>EventProjection</c>.</summary>
    EventProjection,

    /// <summary>A <c>FlatTableProjection</c>.</summary>
    FlatTable,

    /// <summary>A composite: a group, not a box in the picture.</summary>
    Composite,

    /// <summary>A subscription registered with <c>Events.Subscribe(...)</c>.</summary>
    Subscription,

    /// <summary>A hand-written projection that is not a <c>ProjectionBase</c>.</summary>
    Custom,
}

/// <summary>Whether a source names the event types it takes in.</summary>
internal enum FlowInput
{
    /// <summary>It declares event types.</summary>
    Declared,

    /// <summary>It declares none, so the daemon feeds it every event.</summary>
    Everything,
}

/// <summary>One projection, composite member or subscription.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Lifecycle">Inline, async or live.</param>
/// <param name="Version">Its version, for blue/green.</param>
/// <param name="Shards">Its shards, by identity.</param>
/// <param name="Composite">The composite it is a member of, or <see langword="null" />.</param>
/// <param name="Input">Whether it declares event types.</param>
/// <param name="AlsoDeclares">Declared input types no known event type matches, by CLR name.</param>
/// <param name="UndrawnOutputs">Visible published types that are not document types of this store, by CLR name.</param>
/// <param name="StreamType">The stream type it is filtered to, or <see langword="null" />.</param>
/// <param name="IncludesArchived">Whether it reads archived events too.</param>
/// <param name="TagType">The DCB tag type its aggregate is keyed by, or <see langword="null" />.</param>
/// <param name="Note">What went wrong describing it, or <see langword="null" />.</param>
internal sealed record FlowSource(
    string Name,
    FlowSourceKind Kind,
    ProjectionLifecycle Lifecycle,
    uint Version,
    IReadOnlyList<string> Shards,
    string? Composite,
    FlowInput Input,
    IReadOnlyList<string> AlsoDeclares,
    IReadOnlyList<string> UndrawnOutputs,
    string? StreamType,
    bool IncludesArchived,
    string? TagType,
    string? Note)
{
    /// <summary>"global: one aggregate across tenants", or <see langword="null" />.</summary>
    public string? GlobalNote { get; init; }

    /// <summary>
    /// Why what it writes could not be listed ("could not describe what it writes: ..."), or <see langword="null" />. Apart from
    /// <see cref="Note" />, which is about what it reads, so neither overwrites the other.
    /// </summary>
    public string? WritesNote { get; init; }

    /// <summary>Whether this is a composite: a group of members, and not a box of the picture.</summary>
    public bool IsComposite => Kind == FlowSourceKind.Composite;

    /// <summary>What the source is, without its lifecycle: <c>Single stream</c>, <c>Member of Bundle</c>.</summary>
    public string KindText => Composite is { Length: > 0 } composite
        ? "Member of " + composite
        : Kind switch
        {
            FlowSourceKind.SingleStream => "Single stream",
            FlowSourceKind.MultiStream => "Multi stream",
            FlowSourceKind.EventProjection => "Event projection",
            FlowSourceKind.FlatTable => "Flat table",
            FlowSourceKind.Composite => "Composite",
            FlowSourceKind.Subscription => "Subscription",
            _ => "Custom projection",
        };

    /// <summary>
    /// The sentence the table and the box's tooltip use for what the source is: <c>Single stream · Inline</c>,
    /// <c>Member of Bundle · Async</c>.
    /// </summary>
    public string Description => KindText + " · " + Lifecycle;
}

/// <summary>An event type read by a source.</summary>
/// <param name="EventAlias">The event type's <see cref="FlowEventType.Key" /> (its alias, but for a shared alias).</param>
/// <param name="Source">The source's name.</param>
/// <param name="Via">The declared base type's CLR name when the match is through it, or <see langword="null" /> for an exact one.</param>
internal sealed record FlowRead(string EventAlias, string Source, string? Via);

/// <summary>What an output is.</summary>
internal enum FlowOutputKind
{
    /// <summary>A document type of the store.</summary>
    Document,

    /// <summary>A flat table.</summary>
    Table,

    /// <summary>An aggregate folded live, never stored.</summary>
    LiveAggregate,
}

/// <summary>A document type, flat table or live aggregate a source writes.</summary>
/// <param name="Key">What edges name it by: <c>d:alias</c>, <c>t:schema.table</c> or <c>a:full name</c>.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Title">The first line.</param>
/// <param name="Subtitle">The second line: the CLR name, the schema, or "folded live, not stored".</param>
/// <param name="Hue">The collection's colour.</param>
internal sealed record FlowOutput(string Key, FlowOutputKind Kind, string Title, string? Subtitle, int Hue)
{
    /// <summary>The document type's alias, or <see langword="null" /> for a table or live aggregate.</summary>
    public string? Alias { get; init; }

    /// <summary>A flat table's schema, or <see langword="null" />.</summary>
    public string? Schema { get; init; }

    /// <summary>A flat table's name, or <see langword="null" />.</summary>
    public string? Table { get; init; }
}

/// <summary>A source writing an output.</summary>
/// <param name="Source">The source's name.</param>
/// <param name="OutputKey">The output's <see cref="FlowOutput.Key" />.</param>
/// <param name="Subclass">The published subclass's CLR name when the node is its root, or <see langword="null" />.</param>
internal sealed record FlowWrite(string Source, string OutputKey, string? Subclass);

/// <summary>What <see cref="EventFlowModel.ReadsOf" /> answers.</summary>
/// <param name="Input">Whether the projection declares event types.</param>
/// <param name="Aliases">The user's event types it reads, by alias.</param>
/// <param name="MartenOwnAliases">Marten's own event types it reads (<c>Archived</c>, <c>Compacted&lt;T&gt;</c>).</param>
internal sealed record FlowReads(FlowInput Input, IReadOnlyList<string> Aliases, IReadOnlyList<string> MartenOwnAliases);

/// <summary>What the data service answers: the model, or why it could not be built.</summary>
/// <param name="Model">The model; <see cref="EventFlowModel.None" /> when it failed.</param>
/// <param name="Error">The failure, or <see langword="null" />.</param>
internal sealed record EventFlowResult(EventFlowModel Model, string? Error)
{
    /// <summary>A failed read.</summary>
    public static EventFlowResult Failed(string error) => new(EventFlowModel.None, error);
}
