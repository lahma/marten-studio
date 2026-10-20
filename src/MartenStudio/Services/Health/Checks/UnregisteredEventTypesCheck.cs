using System.Reflection;

using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Projections;

using Marten;
using Marten.Events;

using MartenStudio.Services.Events;
using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Health.Checks;

/// <summary>An alias with no mapping here, and the .NET type name stored for it that nothing here can load.</summary>
/// <param name="Alias">The <c>type</c> value.</param>
/// <param name="DotNetType">The <c>mt_dotnet_type</c> value whose own name gives that alias.</param>
internal sealed record UnreadableAlias(string Alias, string DotNetType);

/// <summary>A projection or subscription registered here with no event-type filter, so it reads every event type.</summary>
/// <param name="Name">Its name.</param>
/// <param name="IsProjection">A projection, which a rebuild replays; otherwise a subscription, which only the daemon runs.</param>
/// <param name="IsAsync">Whether the running daemon runs it.</param>
/// <param name="Hidden">Whether it writes a document type the studio hides, so it is counted and never named.</param>
internal sealed record AllTypeReader(string Name, bool IsProjection, bool IsAsync, bool Hidden = false);

/// <summary>What E05 read.</summary>
/// <param name="Events">What the planner's statistics say about <c>mt_events</c>.</param>
/// <param name="AliasesSeen">How many distinct <c>type</c> values the statistics name.</param>
/// <param name="Unmapped">The aliases Marten has no mapping for in this process: neither registered nor an upcaster's.</param>
/// <param name="ReadableThroughDotNet">Of them, the ones a .NET type this process has loaded would be read as.</param>
/// <param name="Unreadable">
/// Of them, the ones the statistics also show the .NET type name of - a name whose own type name gives the alias under
/// this store's naming style - and that name does not load here. Only these are named as unreadable: the
/// <c>type</c> and <c>mt_dotnet_type</c> statistics are sampled and sorted separately, so an alias whose name was not
/// seen is evidence of nothing.
/// </param>
/// <param name="UnloadableDotNetTypes">The <c>mt_dotnet_type</c> values that name no type this process has loaded.</param>
/// <param name="EventsWithoutDotNetType">The estimated events with no <c>mt_dotnet_type</c> at all.</param>
/// <param name="UpcastersKnown">Whether the upcasters' aliases could be read, so an upcast alias is not taken for an unknown one.</param>
/// <param name="RebuildsSkipUnknown"><c>Projections.RebuildErrors.SkipUnknownEvents</c>.</param>
/// <param name="DaemonSkipsUnknown"><c>Projections.Errors.SkipUnknownEvents</c>.</param>
/// <param name="AllTypeReaders">
/// The inline and async projections and the subscriptions registered here that read every event type - no event-type
/// filter. A live aggregation is not among them: it reads whatever its stream holds.
/// </param>
internal sealed record UnregisteredEventTypesFacts(
    TableStatistics Events,
    int AliasesSeen,
    IReadOnlyList<string> Unmapped,
    IReadOnlyList<string> ReadableThroughDotNet,
    IReadOnlyList<UnreadableAlias> Unreadable,
    IReadOnlyList<string> UnloadableDotNetTypes,
    double EventsWithoutDotNetType,
    bool UpcastersKnown,
    bool RebuildsSkipUnknown,
    bool DaemonSkipsUnknown,
    IReadOnlyList<AllTypeReader> AllTypeReaders)
{
    /// <summary>The unmapped aliases no loaded .NET type accounts for.</summary>
    public IReadOnlyList<string> Unexplained => [.. Unmapped.Except(ReadableThroughDotNet, StringComparer.Ordinal)];

    /// <summary>The unexplained aliases the statistics show no .NET type name for, either way.</summary>
    public IReadOnlyList<string> Unmatched =>
        [.. Unexplained.Except(Unreadable.Select(static x => x.Alias), StringComparer.Ordinal)];

    /// <summary>Whether a rebuild here stops on them: a projection reads every type, and rebuilds skip nothing.</summary>
    public bool RebuildStops => !RebuildsSkipUnknown && AllTypeReaders.Any(static x => x.IsProjection);

    /// <summary>Whether the running daemon stops on them: something async reads every type, and the daemon skips nothing.</summary>
    public bool DaemonStops => !DaemonSkipsUnknown && AllTypeReaders.Any(static x => x.IsAsync);
}

/// <summary>
/// E05: <c>mt_events</c> holds events of a type this process cannot read.
/// </summary>
/// <remarks>
/// <para>
/// <b>How Marten reads an event's type</b>, at <c>V9.39.0</c> and <c>V9.50.2</c> (<c>EventDocumentStorage</c>): by
/// its <c>type</c> alias first - a registered mapping, or an upcaster's old name - and, failing that, by the
/// assembly-qualified name in <c>mt_dotnet_type</c>, which works whenever that type can still be loaded. Only when
/// both fail does it throw <c>UnknownEventTypeException</c>: a rebuild stops there (<c>RebuildErrors</c> skips nothing
/// by default), a live aggregation throws, and the running daemon skips the event and logs it
/// (<c>Errors.SkipUnknownEvents</c> is on by default). Marten registers event types lazily, on first append, so an
/// alias this process has no mapping for is ordinary.
/// </para>
/// <para>
/// <b>Named only with evidence.</b> It is a finding only for an alias whose own stored .NET type name the statistics
/// show and nothing here can load. The <c>type</c> and <c>mt_dotnet_type</c> statistics are sampled and sorted
/// separately, so an alias is paired with a type name by the name itself: the alias a type of that name would be
/// stored under in this store's naming style. An alias with no such name in the statistics "could not be matched",
/// and is said to be so in the status row rather than named as unreadable.
/// </para>
/// <para>
/// <b>A Warning only when something here stops on them.</b> A rebuild or the daemon loads an event only for a
/// projection or subscription that reads its type - the daemon's fetch filters by the shard's
/// <c>IncludedEventTypes</c>, and an aggregation fills those from its own Apply methods - so one with a filter never
/// meets an unknown type. Two services can share a store, each registering the types it appends, so the finding is
/// a Warning only when something registered in this process reads every event type <em>and</em> the policy that runs
/// it skips nothing: a projection under <c>RebuildErrors</c>, an async projection or subscription under
/// <c>Errors</c>. Otherwise it is an Info about live aggregations and another service's projections.
/// </para>
/// <para>
/// <b>Tier 1, no scan</b>, from the planner's statistics of <c>mt_events</c>, shared with E06 and E07 - which is why
/// the Event types screen's banner can read this from the regular sweep. A type rarer than the sample is not seen;
/// the Event types screen counts every type exactly, on its own. <b>Nothing is loaded</b>: a <c>mt_dotnet_type</c>
/// is resolved only among the assemblies this process has already loaded.
/// </para>
/// </remarks>
[HealthCheck("E05", HealthArea.Events, HealthCostTier.Catalog, "Unregistered event types")]
internal sealed class UnregisteredEventTypesCheck : HealthCheck<UnregisteredEventTypesFacts>
{
    /// <summary>How many names one piece of evidence lists before it says how many more there are.</summary>
    internal const int NamesListed = 10;

    /// <inheritdoc />
    public override async Task<UnregisteredEventTypesFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        EventStoreRelations shape = await EventHealth.ShapeAsync(context, cancellationToken).ConfigureAwait(false);
        EventHealth.RequireEvents(shape);

        EventStatistics statistics = await EventHealth.StatisticsAsync(context, cancellationToken).ConfigureAwait(false);
        TableStatistics events = statistics.Events;

        if (!events.Readable)
        {
            throw new HealthCheckUnavailableException(
                "The role the studio reads as may not select from mt_events, so PostgreSQL shows it none of that table's statistics.");
        }

        if (!events.HasColumn("type"))
        {
            throw new HealthCheckUnavailableException(
                "PostgreSQL holds no statistics for mt_events yet: it has not been analysed since its events arrived. Autovacuum "
                + "analyses a table once enough of it has changed.");
        }

        IReadOnlyEventStoreOptions options = context.StoreOptions.Events;
        Func<string, bool>? known = EventHealth.KnownAliases(options);
        HashSet<string> registered = new(options.AllKnownEventTypes().Select(static x => x.EventTypeName), StringComparer.Ordinal);
        Func<string, bool> isKnown = known ?? registered.Contains;

        IReadOnlySet<string> aliases = events.ValuesOf("type");
        IReadOnlySet<string> dotNetNames = events.ValuesOf("mt_dotnet_type");

        IReadOnlyDictionary<string, Assembly> loaded = EventHealth.LoadedAssemblies();
        HashSet<string> loadableAliases = new(StringComparer.Ordinal);
        List<string> unloadable = [];

        foreach (string name in dotNetNames)
        {
            if (EventHealth.ResolveLoaded(name, loaded) is { } type)
            {
                if (AliasOf(type, options.EventNamingStyle) is { } alias)
                {
                    loadableAliases.Add(alias);
                }
            }
            else
            {
                unloadable.Add(name);
            }
        }

        List<string> unmapped = [.. aliases.Where(x => !isKnown(x)).Order(StringComparer.Ordinal)];
        HashSet<string> unexplained = new(unmapped.Where(x => !loadableAliases.Contains(x)), StringComparer.Ordinal);

        List<UnreadableAlias> unreadable = [];
        foreach (string name in unloadable.Order(StringComparer.Ordinal))
        {
            if (AliasFromName(name, options.EventNamingStyle) is { } alias && unexplained.Contains(alias))
            {
                unreadable.Add(new UnreadableAlias(alias, name));
            }
        }

        (bool rebuilds, bool daemon) = context.Store.Options is StoreOptions concrete
            ? (concrete.Projections.RebuildErrors.SkipUnknownEvents, concrete.Projections.Errors.SkipUnknownEvents)
            : (false, true);

        IReadOnlyList<AllTypeReader> readers = AllTypeReaders(context);

        return new UnregisteredEventTypesFacts(
            events,
            aliases.Count,
            unmapped,
            [.. unmapped.Where(loadableAliases.Contains)],
            unreadable,
            [.. unloadable.Order(StringComparer.Ordinal)],
            events.EstimatedNulls("mt_dotnet_type"),
            known is not null,
            rebuilds,
            daemon,
            readers);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(UnregisteredEventTypesFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);

        IReadOnlyList<string> unmatched = facts.Unmatched;

        if (facts.Unreadable.Count == 0)
        {
            if (unmatched.Count > 0)
            {
                return HealthVerdict.Quiet(
                    EventHealth.Count(unmatched.Count) + " event type alias(es) have no mapping in this process, and the statistics "
                    + "show no .NET type name they could be matched to - nothing says they cannot be read: " + List(unmatched) + "."
                    + (facts.EventsWithoutDotNetType >= 1
                        ? " About " + EventHealth.Count(facts.EventsWithoutDotNetType) + " stored events have no .NET type name at all, "
                            + "which Marten cannot fall back on; the Event types screen counts every type exactly."
                        : string.Empty));
            }

            return HealthVerdict.Quiet(facts.ReadableThroughDotNet.Count == 0
                ? "Every event type the statistics of mt_events name (" + EventHealth.Count(facts.AliasesSeen) + ") is mapped in this process."
                : EventHealth.Count(facts.ReadableThroughDotNet.Count) + " of the " + EventHealth.Count(facts.AliasesSeen)
                    + " event types the statistics name are not registered here, but each is read through its .NET type, which this process has loaded.");
        }

        bool readHere = facts.AllTypeReaders.Count > 0;
        bool projections = facts.AllTypeReaders.Any(static x => x.IsProjection);
        HealthSeverity severity = facts.RebuildStops || facts.DaemonStops ? HealthSeverity.Warning : HealthSeverity.Info;

        List<HealthEvidence> evidence =
        [
            new("Aliases whose stored .NET type does not load here", List([.. facts.Unreadable.Select(static x => x.Alias + " (" + x.DotNetType + ")")])),
        ];

        if (unmatched.Count > 0)
        {
            evidence.Add(new HealthEvidence("Other aliases with no mapping, not matched to a .NET type in the statistics", List(unmatched)));
        }

        if (facts.EventsWithoutDotNetType >= 1)
        {
            evidence.Add(new HealthEvidence("Events with no mt_dotnet_type (estimated)", EventHealth.Count(facts.EventsWithoutDotNetType)));
        }

        evidence.Add(new HealthEvidence("Projections and subscriptions here that read every event type", Readers(facts.AllTypeReaders)));
        evidence.Add(new HealthEvidence("Projections.RebuildErrors.SkipUnknownEvents", facts.RebuildsSkipUnknown ? "true" : "false"));
        evidence.Add(new HealthEvidence("Projections.Errors.SkipUnknownEvents", facts.DaemonSkipsUnknown ? "true" : "false"));

        string caveat = EventHealth.StatisticsCaveat(facts.Events)
            + " A type rarer than the sample is not seen at all; the Event types screen counts every type exactly. An alias is "
            + "paired with the .NET type name that would give it in this store's naming style, and only such pairs are named. A "
            + ".NET type is looked for only among the assemblies this process has already loaded, so a type Marten could still "
            + "load by name, from an assembly nothing has touched yet, is reported as missing."
            + (facts.UpcastersKnown
                ? string.Empty
                : " This Marten does not let the studio read its upcasters' aliases, so an alias an upcaster reads is reported too.");

        const string Prefix = "Some stored events have a type this process cannot read";
        string title = facts.RebuildStops
            ? Prefix + ", so a rebuild will stop on them"
            : facts.DaemonStops
                ? Prefix + ", so the daemon will stop on them"
                : readHere
                    ? Prefix + ", and what reads them here skips them"
                    : Prefix + ", though nothing registered here reads every event type";

        string rebuild = !projections
            ? "No projection registered in this process reads every event type - each filters by the types it handles - so "
                + "a rebuild here never loads these events. "
            : facts.RebuildsSkipUnknown
                ? "Projections.RebuildErrors.SkipUnknownEvents is on, so a rebuild of a projection that reads every type skips "
                    + "them and the rebuilt projection silently lacks whatever they would have applied. "
                : "A rebuild of a projection that reads every event type throws UnknownEventTypeException and stops part-way: "
                    + "RebuildErrors skips nothing unless told to. ";

        string daemon = !facts.AllTypeReaders.Any(static x => x.IsAsync)
            ? "Nothing the daemon runs here reads every type either. "
            : facts.DaemonSkipsUnknown
                ? "The running daemon skips them and logs each one. "
                : "The running daemon stops on them, since Projections.Errors.SkipUnknownEvents is off. ";

        string elsewhere = readHere
            ? string.Empty
            : "Two services can share one store, each registering the events it appends: another service, whose projections "
                + "do read these, may be the one that appends them. ";

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "E05",
            Severity = severity,
            Title = title,
            WhyItMatters =
                "Marten reads an event by its type alias, and when it has no mapping for the alias, by the .NET type name stored "
                + "beside it. These aliases have neither here: no registration, no upcaster, and the type name stored with them "
                + "does not load in this process. "
                + rebuild
                + daemon
                + "A live aggregation of a stream that holds one throws. "
                + elsewhere
                + "Usually the type was renamed, moved or deleted, or it lives in a service this process does not reference.",
            Evidence = evidence,
            Caveat = caveat,
            Remedy = new HealthRemedy(
                """
                // Register the type each alias belongs to, in every process that rebuilds or aggregates:
                opts.Events.AddEventType<YourEventType>();

                // ...or map an old alias onto today's type, or upcast it:
                opts.Events.MapEventType<YourEventType>("the_old_alias");

                // ...or let rebuilds skip what they cannot read (the projection then lacks those events):
                opts.Projections.RebuildErrors.SkipUnknownEvents = true;
                """,
                "Since Marten 9.39 the UnknownEventTypeException's own message names the registration and the skip option. "
                + "Skipping is the last resort: a projection rebuilt without events it was written for is quietly wrong."),
            Link = new HealthLink("Open Event types, which counts every type exactly", EventLinks.EventTypesRoute),
        });
    }

    /// <summary>
    /// The alias a type of this assembly-qualified name would be stored under in <paramref name="style" />, read off the
    /// name alone - <see cref="EventTypeExtensions.GetEventTypeName(Type, EventNamingStyle)" /> without the type - or
    /// <see langword="null" /> for a generic type, whose alias cannot be told from its name.
    /// </summary>
    internal static string? AliasFromName(string assemblyQualifiedName, EventNamingStyle style)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyQualifiedName);

        int comma = assemblyQualifiedName.IndexOf(',', StringComparison.Ordinal);
        string fullName = (comma < 0 ? assemblyQualifiedName : assemblyQualifiedName[..comma]).Trim();

        if (fullName.Length == 0 || fullName.Contains('`', StringComparison.Ordinal) || fullName.Contains('[', StringComparison.Ordinal))
        {
            return null;
        }

        string[] nesting = fullName.Split('+');
        string simple = SimpleName(nesting[^1]);

        return style switch
        {
            EventNamingStyle.ClassicTypeName => simple.ToTableAlias(),
            EventNamingStyle.SmarterTypeName => nesting.Length > 1
                ? SimpleName(nesting[^2]).ToTableAlias() + "." + simple.ToTableAlias()
                : simple.ToTableAlias(),
            _ => fullName.Replace('+', '.'),
        };

        static string SimpleName(string name) => name[(name.LastIndexOf('.') + 1)..];
    }

    /// <summary>
    /// The inline and async projections and the subscriptions registered here that the daemon would load every event
    /// type for. Marten's <c>buildEventLoaderFilters</c> (read at <c>V9.39.0</c> and <c>V9.50.2</c>) filters a shard's
    /// fetch by its <c>IncludedEventTypes</c> only when the list is not empty and names no abstract type or
    /// interface; otherwise the shard is handed every event.
    /// </summary>
    private static List<AllTypeReader> AllTypeReaders(HealthCheckContext context)
    {
        List<AllTypeReader> readers = [];

        if (context.Store.Options is not StoreOptions concrete)
        {
            return readers;
        }

        try
        {
            IEnumerable<(AsyncShard<IDocumentOperations, IQuerySession> Shard, bool Async)> shards = concrete.Projections.AllShards()
                .Select(static x => (x, true))
                .Concat(concrete.Projections.All
                    .Where(static x => x.Lifecycle == ProjectionLifecycle.Inline)
                    .SelectMany(static x => x.Shards())
                    .Select(static x => (x, false)));

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach ((AsyncShard<IDocumentOperations, IQuerySession> shard, bool async) in shards)
            {
                List<Type> included = shard.Filters.IncludedEventTypes;
                bool filtered = included.Count > 0 && !included.Any(static x => x.IsAbstract || x.IsInterface);

                if (filtered || !seen.Add(shard.Name.Name))
                {
                    continue;
                }

                bool hidden = !concrete.Projections.All
                    .Where(x => string.Equals(x.Name, shard.Name.Name, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(static x => x.PublishedTypes())
                    .All(context.IsDocumentTypeVisible);

                readers.Add(new AllTypeReader(shard.Name.Name, shard.Role == ShardRole.Projection, async, hidden));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A registration that cannot be enumerated names no reader, and the finding is an Info that still names the
            // aliases: the evidence row then says nothing reads every type, which is what could be shown.
            readers.Clear();
        }

        readers.Sort(static (x, y) => StringComparer.Ordinal.Compare(x.Name, y.Name));
        return readers;
    }

    /// <summary>The readers' evidence: the visible ones by name and role, the hidden ones counted.</summary>
    private static string Readers(IReadOnlyList<AllTypeReader> readers)
    {
        if (readers.Count == 0)
        {
            return "none: each one registered here filters by the event types it handles";
        }

        List<string> named =
        [
            .. readers
                .Where(static x => !x.Hidden)
                .Select(static x => x.Name + " (" + (x.IsProjection ? x.IsAsync ? "async projection" : "inline projection" : "subscription") + ")"),
        ];
        int hidden = readers.Count(static x => x.Hidden);

        if (named.Count == 0)
        {
            return EventHealth.Count(hidden) + ", each writing a document type the studio hides";
        }

        return List(named) + (hidden > 0 ? ", and " + EventHealth.Count(hidden) + " writing a document type the studio hides" : string.Empty);
    }

    /// <summary>The alias a loaded type's events are stored under in this store's naming style, or <see langword="null" />.</summary>
    private static string? AliasOf(Type type, EventNamingStyle style)
    {
        try
        {
            return type.GetEventTypeName(style);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static string List(IReadOnlyList<string> names) =>
        names.Count <= NamesListed
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(NamesListed)) + " and " + EventHealth.Count(names.Count - NamesListed) + " more";
}
