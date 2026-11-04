using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using JasperFx.Events.Projections.Composite;
using JasperFx.Events.Subscriptions;

using Marten;
using Marten.Events;
using Marten.Events.Projections.Flattened;
using Marten.Schema;

using MartenStudio.Services.Documents;

using PostgresStudio.Services;

namespace MartenStudio.Services.Events;

/// <summary>
/// Builds the <see cref="EventFlowModel" /> (1.2, M6): which projections and subscriptions read each event type, and what
/// each writes, from <c>StoreOptions</c> and the projection graph alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule is JasperFx's own, restated over public members.</b> A source takes in the event types its
/// <c>IAggregateProjection.AllEventTypes</c> (an aggregation) or its <c>EventFilterable.IncludedEventTypes</c> (anything else)
/// declares - <c>SubscriptionDescriptor.appliedEventsFor</c>, which is private - and an event type is matched when it equals a
/// declared type or can be cast to one (<c>AppliesTo</c>'s <c>CanBeCastTo</c>, which this calls). Empty is a legitimate
/// answer and means "did not narrow", not "applies nothing": that source is fed every event.
/// </para>
/// <para>
/// <b>What it never calls</b> (<c>EventFlowPurityTests</c> holds the list): <c>Describe</c>, <c>TryCreateUsage</c>,
/// <c>AggregatorFor</c>, <c>AssembleAndAssertValidity</c>, <c>CreateSchemaObjects</c>, <c>Shards()</c>,
/// <c>BuildExecution</c> and <c>EventMappingFor</c>. Each registers, builds, validates, enumerates the tenancy or touches
/// storage, and a screen that only reads the configuration may do none of them (hard rule 14).
/// </para>
/// <para>
/// <b>Hidden is absent, never counted.</b> A document type <c>IsDocumentTypeVisible</c> hides is no output, no node, no count
/// and no word; neither is an event type or a declared input that names one as a type argument (Marten's
/// <c>Compacted&lt;T&gt;</c>). A delegate that throws hides.
/// </para>
/// </remarks>
internal static class EventFlowGraph
{
    /// <summary>How deep a composite of composites is followed.</summary>
    private const int MaxCompositeDepth = 8;

    /// <summary>What a source whose event types the studio cannot read is told.</summary>
    private const string UnreadableInputNote = "The studio cannot read which event types this declares, so it is shown as fed every event.";

    /// <summary>Why the daemon's shard list could not be read.</summary>
    internal const string SubscriptionsUnavailableText = "Subscriptions could not be read from this store's options.";

    /// <summary>Why no source lists what it writes: Marten would not give the store's document types.</summary>
    internal const string DocumentTypesUnavailableText = "This store's document types could not be read, so what each projection writes is not listed.";

    /// <summary>Builds the model for a store.</summary>
    /// <param name="store">The store; only its options are read.</param>
    /// <param name="isVisible"><c>MartenStudioOptions.IsDocumentTypeVisible</c>, or <see langword="null" /> to hide nothing.</param>
    public static EventFlowModel Build(IDocumentStore store, Func<Type, bool>? isVisible)
    {
        ArgumentNullException.ThrowIfNull(store);

        return new Builder(store.Options, SafeVisible(isVisible)).Build();
    }

    /// <summary>
    /// The shard count that, with the event types and the projections, tells the cache the configuration moved; <c>-1</c> when
    /// the store's options are not the concrete ones and the list cannot be read.
    /// </summary>
    internal static int ShardCountOf(IDocumentStore store)
    {
        return AllShardsOf(store.Options)?.Count ?? -1;
    }

    /// <summary>
    /// The daemon's shards - the only place a subscription is listed - or <see langword="null" /> for an options object
    /// of another shape, or one that will not enumerate them.
    /// </summary>
    private static IReadOnlyList<AsyncShard<IDocumentOperations, IQuerySession>>? AllShardsOf(IReadOnlyStoreOptions options)
    {
        try
        {
            return options is StoreOptions concrete ? concrete.Projections.AllShards() : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static Func<Type, bool> SafeVisible(Func<Type, bool>? isVisible) => type =>
    {
        if (isVisible is null)
        {
            return true;
        }

        try
        {
            return isVisible(type);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fail closed, per call: a delegate that throws hides the type (DeadLetterVisibility.IsVisible does the same).
            return false;
        }
    };

    /// <summary>The CLR simple name, a closed generic spelled <c>Compacted&lt;OrderSummary&gt;</c>.</summary>
    internal static string ClrName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        string name = type.Name;
        int tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick > 0)
        {
            name = name[..tick];
        }

        return name + "<" + string.Join(", ", type.GetGenericArguments().Select(ClrName)) + ">";
    }

    /// <summary>Whether a type is declared by Marten or JasperFx: <c>Archived</c>, <c>Compacted&lt;T&gt;</c>, <c>Tombstone</c>.</summary>
    internal static bool IsMartenOwn(Type type)
    {
        Type declared = type.IsGenericType ? type.GetGenericTypeDefinition() : type;

        return declared.Assembly == typeof(Archived).Assembly || declared.Assembly == typeof(IDocumentStore).Assembly;
    }

    private sealed class Builder(IReadOnlyStoreOptions options, Func<Type, bool> visible)
    {
        private readonly List<FlowSource> sources = [];
        private readonly List<FlowRead> reads = [];
        private readonly List<FlowWrite> writes = [];
        private readonly Dictionary<string, FlowOutput> outputs = new(StringComparer.Ordinal);
        private readonly HashSet<object> visited = new(ReferenceEqualityComparer.Instance);

        private List<FlowEventType> eventTypes = [];
        private readonly Dictionary<string, Type> clrByKey = new(StringComparer.Ordinal);
        private Dictionary<Type, (IDocumentType Root, bool IsSubclass)> documents = [];

        // Hidden is a property of the document type's whole hierarchy: a hidden root hides every subclass it has, as the rest of the
        // studio treats them (LinqQueryService.HiddenDocumentTypes), and a visible root's subclass the delegate hides is hidden too.
        // Filled by ReadDocumentTypes, so it comes first. With the registry unreadable the hierarchy is unknown, so it fails closed:
        // every document type counts as hidden. Generic event types over a type, stream types, the global note, live outputs and tag
        // aggregate names then drop out; non-generic event types the registry knows, and every source's reads of them, stay.
        private readonly HashSet<Type> hidden = [];
        private bool documentsReadable = true;

        // The event types the registry knows, before any type a projection declares is added to them.
        private HashSet<Type> registeredClr = [];

        /// <summary>
        /// The question asked of a document type - a stream type, a live or tag aggregate, a type argument: the delegate's answer,
        /// the hierarchy's, and yes for everything when the registry could not be read.
        /// </summary>
        private bool IsHidden(Type type) => !documentsReadable || IsHiddenByHost(type);

        /// <summary>What the host and the hierarchy hide, without the fail-closed answer: what can still be asked of an event type.</summary>
        private bool IsHiddenByHost(Type type) => !visible(type) || hidden.Contains(type);

        /// <summary>
        /// Whether an event type, or a declared input, is left out: it is a hidden type itself or over one, or - with the registry
        /// unreadable - it is not one the registry knows, because nothing declared-but-unregistered can then be told from a document type.
        /// </summary>
        private bool LeaveOut(Type type) =>
            IsHiddenByHost(type) || MentionsHidden(type) || (!documentsReadable && !registeredClr.Contains(type));

        public EventFlowModel Build()
        {
            ReadDocumentTypes();
            ReadEventTypes();

            HashSet<string> listed = new(StringComparer.OrdinalIgnoreCase);

            foreach (ISubscriptionSource source in options.Events.Projections())
            {
                AddSource(source, null, 0, listed);
            }

            string? unavailable = AddSubscriptions(listed);

            List<FlowSource> ordered = [.. sources.OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(static x => x.Name, StringComparer.Ordinal)];

            return new EventFlowModel(
                eventTypes,
                ordered,
                [.. reads.Distinct().OrderBy(static x => x.EventAlias, StringComparer.Ordinal).ThenBy(static x => x.Source, StringComparer.Ordinal)],
                [.. writes.Distinct().OrderBy(static x => x.Source, StringComparer.Ordinal).ThenBy(static x => x.OutputKey, StringComparer.Ordinal)],
                [.. outputs.Values.OrderBy(static x => x.Title, StringComparer.OrdinalIgnoreCase).ThenBy(static x => x.Key, StringComparer.Ordinal)],
                EventTagModel.From(options, type => !IsHidden(type)).Types,
                unavailable)
            {
                DocumentTypesUnavailable = documentsReadable ? null : DocumentTypesUnavailableText,
            };
        }

        // ------------------------------------------------------------------------------------------
        // Event types and document types
        // ------------------------------------------------------------------------------------------

        private void ReadEventTypes()
        {
            List<IEventType> known = [.. options.Events.AllKnownEventTypes().Where(x => !IsHiddenByHost(x.EventType) && !MentionsHidden(x.EventType))];
            registeredClr = [.. known.Select(static x => x.EventType)];

            Dictionary<string, List<IEventType>> byAlias = new(StringComparer.Ordinal);
            foreach (IEventType type in known)
            {
                if (!byAlias.TryGetValue(type.EventTypeName, out List<IEventType>? list))
                {
                    byAlias[type.EventTypeName] = list = [];
                }

                list.Add(type);
            }

            List<FlowEventType> result = [];
            HashSet<string> keys = new(StringComparer.Ordinal);

            foreach (IEventType type in known)
            {
                List<IEventType> sharing = byAlias[type.EventTypeName];
                bool shared = sharing.Count > 1;
                string key = shared ? type.EventTypeName + "|" + type.DotNetTypeName : type.EventTypeName;

                if (!keys.Add(key))
                {
                    continue;
                }

                Type clr = type.EventType;
                IEventType? other = shared ? sharing.FirstOrDefault(x => !ReferenceEquals(x, type)) : null;

                result.Add(new FlowEventType(type.EventTypeName, ClrName(clr), type.DotNetTypeName, IsMartenOwn(clr), clr.IsInterface || clr.IsAbstract)
                {
                    Key = key,
                    SharesAliasWith = other is null ? null : ClrName(other.EventType),
                });

                clrByKey[key] = clr;
            }

            // A type a projection declares is an event type of the flow whether or not this process has met it yet: the registry
            // grows as events are appended or read, so a process that has not yet is the common case of a fresh start, and a
            // picture that waited for it would be empty exactly when somebody wants to see what the configuration says. The
            // alias is the one the registry will use (EventNamingStyle), and the row says it is not registered yet.
            EventNamingStyle style = (options.Events as EventGraph)?.EventNamingStyle ?? EventNamingStyle.ClassicTypeName;
            HashSet<Type> registered = [.. known.Select(static x => x.EventType)];

            foreach (Type declared in DeclaredByAnySource())
            {
                if (registered.Contains(declared) || LeaveOut(declared))
                {
                    continue;
                }

                string alias;
                try
                {
                    alias = declared.GetEventTypeName(style);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    continue;
                }

                registered.Add(declared);
                string key = keys.Contains(alias) ? alias + "|" + declared.FullName : alias;

                if (!keys.Add(key))
                {
                    continue;
                }

                result.Add(new FlowEventType(alias, ClrName(declared), (declared.FullName ?? declared.Name) + ", " + declared.Assembly.GetName().Name, IsMartenOwn(declared), declared.IsInterface || declared.IsAbstract)
                {
                    Key = key,
                    NotRegistered = true,
                });

                clrByKey[key] = declared;
            }

            eventTypes =
            [
                .. result.OrderBy(static x => x.IsMartenOwn)
                    .ThenBy(static x => x.Alias, StringComparer.Ordinal)
                    .ThenBy(static x => x.Key, StringComparer.Ordinal),
            ];
        }

        /// <summary>Every type any source declares it takes in: projections, composite members and the daemon's shard filters.</summary>
        private List<Type> DeclaredByAnySource()
        {
            List<Type> declared = [];
            HashSet<object> seen = new(ReferenceEqualityComparer.Instance);

            void Walk(ISubscriptionSource source, int depth)
            {
                if (!seen.Add(source))
                {
                    return;
                }

                try
                {
                    declared.AddRange(DeclaredInputs(source).Declared);

                    if (source is CompositeProjection<IDocumentOperations, IQuerySession> composite && depth < MaxCompositeDepth)
                    {
                        foreach (IProjectionSource<IDocumentOperations, IQuerySession> member in composite.AllProjections())
                        {
                            Walk(member, depth + 1);
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The source's own row carries the note.
                }
            }

            foreach (ISubscriptionSource source in options.Events.Projections())
            {
                Walk(source, 0);
            }

            foreach (AsyncShard<IDocumentOperations, IQuerySession> shard in AllShardsOf(options) ?? [])
            {
                declared.AddRange(shard.Filters.IncludedEventTypes);
            }

            return declared;
        }

        private void ReadDocumentTypes()
        {
            documents = [];

            IReadOnlyList<IDocumentType> roots;
            try
            {
                roots = options.AllKnownDocumentTypes();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Marten asks every projection for PublishedTypes() to build its mappings, so one that throws fails this too. Fail
                // closed: without the registry no published type can be told from a hidden one's, so no source lists what it writes,
                // and the model says so once.
                documentsReadable = false;
                return;
            }

            foreach (IDocumentType root in roots)
            {
                if (CollectionAliases.IsMartenInfrastructure(root.DocumentType) || !visible(root.DocumentType))
                {
                    hidden.Add(root.DocumentType);

                    foreach (SubClassMapping subclass in root.SubClasses)
                    {
                        hidden.Add(subclass.DocumentType);
                    }

                    continue;
                }

                documents.TryAdd(root.DocumentType, (root, false));

                // A subclass has no table of its own: the root is what it resolves to (MartenRelationshipGraph.DeclaredKeys).
                foreach (SubClassMapping subclass in root.SubClasses)
                {
                    if (visible(subclass.DocumentType))
                    {
                        documents.TryAdd(subclass.DocumentType, (root, true));
                    }
                    else
                    {
                        hidden.Add(subclass.DocumentType);
                    }
                }
            }
        }

        /// <summary>Whether a type, or any of its type arguments, is one the host hides.</summary>
        private bool MentionsHidden(Type type)
        {
            if (!type.IsGenericType)
            {
                return false;
            }

            foreach (Type argument in type.GetGenericArguments())
            {
                if (IsHidden(argument) || MentionsHidden(argument))
                {
                    return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------------------------------
        // Sources
        // ------------------------------------------------------------------------------------------

        private void AddSource(ISubscriptionSource source, string? composite, int depth, HashSet<string> listed)
        {
            if (!visited.Add(source))
            {
                return;
            }

            string name = source.GetType().Name;
            FlowSourceKind kind = FlowSourceKind.Custom;
            ProjectionLifecycle lifecycle = ProjectionLifecycle.Async;
            uint version = 0;

            try
            {
                name = source.Name;
                listed.Add(name);
                lifecycle = source.Lifecycle;
                version = source.Version;
                kind = KindOf(source);

                IReadOnlyList<string> shards = composite is null ? ShardIdentities(source) : [ShardName.Compose(name, version: version).Identity];

                (List<Type> declared, bool readable) = DeclaredInputs(source);
                bool isComposite = kind == FlowSourceKind.Composite;
                FlowInput input = !isComposite && (declared.Count == 0 || !readable) ? FlowInput.Everything : FlowInput.Declared;

                List<string> also = isComposite ? [] : MatchReads(name, declared, input);
                List<string> undrawn = [];

                string? note = readable || isComposite ? null : UnreadableInputNote;
                string? writesNote = null;

                // A composite's PublishedTypes() is its members' own: it lists only what it adds, in AddMembers.
                if (!isComposite)
                {
                    MapOutputs(source, name, undrawn, ref writesNote);
                }

                EventFilterable? filter = source as EventFilterable;
                string? streamType = filter?.StreamType is { } stream && !IsHidden(stream) ? ClrName(stream) : null;

                string? tagType = null;
                string? global = null;

                if (source is IAggregateProjection aggregate)
                {
                    if (options.Events is EventGraph graph)
                    {
                        tagType = graph.TagTypes.FirstOrDefault(x => x.TagType == aggregate.IdentityType)?.TagType.Name;

                        if (graph.GlobalAggregates.Contains(aggregate.AggregateType) && !IsHidden(aggregate.AggregateType))
                        {
                            global = "global: one aggregate across tenants";
                        }
                    }
                }

                if (isComposite && depth < MaxCompositeDepth)
                {
                    AddMembers(source, name, depth, listed, undrawn, ref note, ref writesNote);
                }

                sources.Add(new FlowSource(
                    name, kind, lifecycle, version, shards, composite, input,
                    [.. also.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                    [.. undrawn.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                    streamType, filter?.IncludeArchivedEvents ?? false, tagType, note)
                {
                    GlobalNote = global,
                    WritesNote = writesNote,
                });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One bad PublishedTypes() override, or a composite that will not list its stages, never blanks the page.
                sources.Add(new FlowSource(
                    name, kind, lifecycle, version, [], composite, FlowInput.Declared, [], [], null, false, null,
                    "could not describe its event types: " + exception.GetType().Name));
            }
        }

        /// <summary>A composite's members become sources of their own; a note says when it will not list them or what it publishes.</summary>
        private void AddMembers(ISubscriptionSource source, string name, int depth, HashSet<string> listed, List<string> undrawn, ref string? note, ref string? writesNote)
        {
            if (source is not CompositeProjection<IDocumentOperations, IQuerySession> composite)
            {
                return;
            }

            IReadOnlyList<IProjectionSource<IDocumentOperations, IQuerySession>> all;
            try
            {
                all = composite.AllProjections();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                note = "could not list its members: " + exception.GetType().Name;
                return;
            }

            HashSet<Type> memberTypes = [];

            foreach (IProjectionSource<IDocumentOperations, IQuerySession> member in all)
            {
                AddSource(member, name, depth + 1, listed);

                try
                {
                    foreach (Type published in member.PublishedTypes())
                    {
                        memberTypes.Add(published);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The member's own row carries the note.
                }
            }

            // What the composite itself publishes beyond its members' is listed on its row, mapped as any source's output is, and
            // never drawn: a box between the events and the members would break the columns.
            try
            {
                MapPublished(name, [.. composite.PublishedTypes().Where(x => !memberTypes.Contains(x))], null, undrawn);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                writesNote = "could not describe what it writes: " + exception.GetType().Name;
            }
        }

        private static FlowSourceKind KindOf(ISubscriptionSource source)
        {
            if (source is IProjectionWrapper { InnerProjection: not ProjectionBase })
            {
                return FlowSourceKind.Custom;
            }

            return source.Type switch
            {
                SubscriptionType.SingleStreamProjection => FlowSourceKind.SingleStream,
                SubscriptionType.MultiStreamProjection => FlowSourceKind.MultiStream,
                SubscriptionType.FlatTableProjection => FlowSourceKind.FlatTable,
                SubscriptionType.EventProjection => FlowSourceKind.EventProjection,
                SubscriptionType.CompositeProjection => FlowSourceKind.Composite,
                SubscriptionType.Subscription => FlowSourceKind.Subscription,
                _ => FlowSourceKind.Custom,
            };
        }

        /// <summary>
        /// The one input rule of <c>SubscriptionDescriptor.appliedEventsFor</c>: an aggregation's <c>AllEventTypes</c>, any other
        /// filterable's <c>IncludedEventTypes</c>, and nothing the studio can read otherwise.
        /// </summary>
        private (List<Type> Declared, bool Readable) DeclaredInputs(object source)
        {
            IEnumerable<Type>? types = source switch
            {
                IAggregateProjection aggregation => aggregation.AllEventTypes,
                EventFilterable filterable => filterable.IncludedEventTypes,
                _ => null,
            };

            return types is null ? ([], false) : ([.. types.Where(x => !LeaveOut(x)).Distinct()], true);
        }

        /// <summary>
        /// Adds a read for every known event type a declared type matches, and returns the declared types nothing matched.
        /// </summary>
        private List<string> MatchReads(string source, List<Type> declared, FlowInput input)
        {
            if (input == FlowInput.Everything)
            {
                return [];
            }

            // Declaration order must not change the picture: a type matched through two bases is shown through the first by name.
            List<Type> ordered = [.. declared.OrderBy(static x => x.FullName ?? x.Name, StringComparer.Ordinal)];
            HashSet<Type> matched = [];

            foreach (FlowEventType flow in eventTypes)
            {
                Type clr = clrByKey[flow.Key];
                Type? exact = ordered.FirstOrDefault(x => x == clr);

                if (exact is not null)
                {
                    matched.Add(exact);
                    reads.Add(new FlowRead(flow.Key, source, null));
                    continue;
                }

                // JasperFx's own call, the very one AppliesTo makes, so the two cannot disagree.
                Type? via = ordered.FirstOrDefault(x => clr.CanBeCastTo(x));

                if (via is not null)
                {
                    matched.Add(via);
                    reads.Add(new FlowRead(flow.Key, source, ClrName(via)));
                }
            }

            return [.. ordered.Where(x => !matched.Contains(x)).Select(ClrName)];
        }

        // ------------------------------------------------------------------------------------------
        // Outputs
        // ------------------------------------------------------------------------------------------

        private void MapOutputs(ISubscriptionSource source, string name, List<string> undrawn, ref string? writesNote)
        {
            Type? live = source is IAggregateProjection { Lifecycle: ProjectionLifecycle.Live } aggregation ? aggregation.AggregateType : null;

            if (live is not null && !IsHidden(live) && !CollectionAliases.IsMartenInfrastructure(live))
            {
                // Folded on read and never written by this projection, though the type usually is a document type with no table.
                FlowOutput output = new("a:" + (live.FullName ?? live.Name), FlowOutputKind.LiveAggregate, ClrName(live), "folded live, not stored", CollectionColorizer.HueFor(ClrName(live)));
                outputs.TryAdd(output.Key, output);
                writes.Add(new FlowWrite(name, output.Key, null));
            }

            if (source is FlatTableProjection flat)
            {
                AddTable(name, flat);
            }

            Type[] published;
            try
            {
                published = source is IPublishesTypes publishes ? [.. publishes.PublishedTypes()] : [];
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                writesNote = "could not describe what it writes: " + exception.GetType().Name;
                return;
            }

            MapPublished(name, published, live, undrawn);
        }

        /// <summary>
        /// Maps published types to the document types they are (a subclass to its root), and lists the rest by name - unless the
        /// store's document types could not be read, when nothing can be told from a hidden type and nothing is listed.
        /// </summary>
        private void MapPublished(string name, IEnumerable<Type> published, Type? live, List<string> undrawn)
        {
            if (!documentsReadable)
            {
                return;
            }

            foreach (Type type in published.Distinct())
            {
                if (type == live || CollectionAliases.IsMartenInfrastructure(type))
                {
                    continue;
                }

                // Hidden is absent: no edge, no node, no count and no text - and a subclass of a hidden root is hidden with it.
                if (IsHidden(type) || MentionsHidden(type))
                {
                    continue;
                }

                if (documents.TryGetValue(type, out (IDocumentType Root, bool IsSubclass) found))
                {
                    string key = "d:" + found.Root.Alias;
                    outputs.TryAdd(key, new FlowOutput(key, FlowOutputKind.Document, found.Root.Alias, ClrName(found.Root.DocumentType), CollectionColorizer.HueFor(found.Root.Alias))
                    {
                        Alias = found.Root.Alias,
                    });
                    writes.Add(new FlowWrite(name, key, found.IsSubclass ? ClrName(type) : null));
                }
                else
                {
                    undrawn.Add(ClrName(type));
                }
            }
        }

        private void AddTable(string source, FlatTableProjection flat)
        {
            // Never CreateSchemaObjects: it moves the schema, and deletes data on teardown. SchemaNameSource says where it goes.
            string schema = flat.SchemaNameSource switch
            {
                SchemaNameSource.DocumentSchema => options.DatabaseSchemaName,
                SchemaNameSource.EventSchema => options.Events.DatabaseSchemaName,
                _ => flat.Table.Identifier.Schema,
            };

            string table = flat.Table.Identifier.Name;
            string key = "t:" + schema + "." + table;

            outputs.TryAdd(key, new FlowOutput(key, FlowOutputKind.Table, table, schema, CollectionColorizer.HueFor(schema))
            {
                Schema = schema,
                Table = table,
            });
            writes.Add(new FlowWrite(source, key, null));
        }

        // ------------------------------------------------------------------------------------------
        // Subscriptions
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// A subscription is not in <c>Projections()</c>: it is only a shard of the daemon's <c>AllShards()</c>, whose filter is the
        /// subscription's own. The same rule the Projections page lists them by.
        /// </summary>
        private string? AddSubscriptions(HashSet<string> listed)
        {
            IReadOnlyList<AsyncShard<IDocumentOperations, IQuerySession>>? shards = AllShardsOf(options);
            if (shards is null)
            {
                return SubscriptionsUnavailableText;
            }

            try
            {
                foreach (IGrouping<string, AsyncShard<IDocumentOperations, IQuerySession>> group in shards
                             .Where(x => !listed.Contains(x.Name.Name))
                             .GroupBy(static x => x.Name.Name, StringComparer.OrdinalIgnoreCase))
                {
                    AsyncShard<IDocumentOperations, IQuerySession> first = group.First();
                    EventFilterable filters = first.Filters;
                    List<Type> declared = [.. filters.IncludedEventTypes.Where(x => !LeaveOut(x)).Distinct()];

                    FlowInput input = declared.Count == 0 ? FlowInput.Everything : FlowInput.Declared;
                    List<string> also = MatchReads(group.Key, declared, input);

                    string? stream = filters.StreamType is { } type && !IsHidden(type) ? ClrName(type) : null;

                    sources.Add(new FlowSource(
                        group.Key, FlowSourceKind.Subscription, ProjectionLifecycle.Async, first.Name.Version,
                        [.. group.Select(static x => x.Name.Identity).Distinct(StringComparer.OrdinalIgnoreCase)],
                        null, input, [.. also.Order(StringComparer.Ordinal)], [], stream, filters.IncludeArchivedEvents, null, null));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return SubscriptionsUnavailableText;
            }

            return null;
        }

        private static IReadOnlyList<string> ShardIdentities(ISubscriptionSource source)
        {
            ShardName[] shards;
            try
            {
                shards = source.ShardNames();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return [source.Name + ":All"];
            }

            return shards.Length == 0 ? [source.Name + ":All"] : [.. shards.Select(static x => x.Identity)];
        }
    }
}
