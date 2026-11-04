using System.Globalization;

using PostgresStudio.Services;
using PostgresStudio.Services.Relationships;

namespace MartenStudio.Services.Events;

/// <summary>What the event flow picture is narrowed to (the URL's <c>?projection=</c>, <c>?type=</c> and <c>?own=1</c>, D9).</summary>
/// <param name="Projection">A projection, subscription or composite by name (ignoring case), or <see langword="null" />.</param>
/// <param name="Type">An event type by alias, or <see langword="null" />.</param>
/// <param name="IncludeOwn">Whether Marten's own event types (<c>Archived</c>, <c>Compacted&lt;T&gt;</c>) are drawn too.</param>
internal sealed record EventFlowFilter(string? Projection, string? Type, bool IncludeOwn)
{
    /// <summary>No filter at all.</summary>
    public static EventFlowFilter None { get; } = new(null, null, false);

    /// <summary>Whether the picture is narrowed to anything.</summary>
    public bool IsFiltered => !string.IsNullOrWhiteSpace(Projection) || !string.IsNullOrWhiteSpace(Type);
}

/// <summary>Where each kind of box links to: already carrying the scope, built through the studio's own link builders.</summary>
/// <param name="Feed">An event type's alias to the Feed filtered to it.</param>
/// <param name="FeedAll">The unfiltered Feed.</param>
/// <param name="Source">A projection's name to the Projections page.</param>
/// <param name="Collection">A document type's alias to its collection list.</param>
/// <param name="Table">A flat table's schema and name to the database browser's object page.</param>
/// <param name="Streams">The Streams page, where a live aggregate is folded from a stream's detail page.</param>
internal sealed record EventFlowHrefs(
    Func<string, string> Feed,
    string FeedAll,
    Func<string, string> Source,
    Func<string, string> Collection,
    Func<string, string, string> Table,
    string Streams);

/// <summary>What the picture of one filter is, and what the page needs to say about it.</summary>
/// <param name="Drawn">The picture.</param>
/// <param name="DrawnNodes">How many boxes it has.</param>
/// <param name="OverCap">Whether that is more than the diagram draws (<see cref="RelationshipViews.NodeCap" />).</param>
/// <param name="UnknownFilter">Whether the filter named a projection or event type this store does not have.</param>
/// <param name="Styles">The edge styles drawn, in legend order.</param>
/// <param name="SourceCount">How many projections and subscriptions are drawn.</param>
internal sealed record EventFlowDrawing(
    GraphDiagramModel Drawn,
    int DrawnNodes,
    bool OverCap,
    bool UnknownFilter,
    IReadOnlyList<string> Styles,
    int SourceCount)
{
    /// <summary>Nothing selected.</summary>
    public static EventFlowDrawing Empty { get; } = new(GraphDiagramModel.Empty, 0, false, false, [], 0);
}

/// <summary>
/// Turns an <see cref="EventFlowModel" /> into the neutral diagram's model (1.2, M6): event types on the left, projections
/// and subscriptions in the middle, and what they write on the right. Pure, so the same model draws the same picture in every
/// process.
/// </summary>
internal static class EventFlowDiagram
{
    /// <summary>The pseudo-node of the sources that declare no event types.</summary>
    internal const string EveryKey = "e:*";

    /// <summary>The edge styles, in the order the legend lists them.</summary>
    internal static IReadOnlyList<string> AllStyles { get; } = ["reads", "via", "every", "writes", "folds"];

    /// <summary>The diagram model of <paramref name="filter" />: the picture, its size and what it was narrowed to.</summary>
    public static EventFlowDrawing Project(EventFlowModel model, EventFlowFilter filter, EventFlowHrefs hrefs)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(hrefs);

        Selection selection = Select(model, filter);

        if (selection.Unknown)
        {
            return EventFlowDrawing.Empty with { UnknownFilter = true };
        }

        Dictionary<string, FlowEventType> eventsByKey = model.EventTypes.ToDictionary(static x => x.Key, StringComparer.Ordinal);
        Dictionary<string, FlowOutput> outputsByKey = model.Outputs.ToDictionary(static x => x.Key, StringComparer.Ordinal);

        List<GraphDiagramNode> nodes = [];
        List<GraphDiagramEdge> edges = [];
        HashSet<string> styles = new(StringComparer.Ordinal);

        if (selection.EverySources.Count > 0)
        {
            nodes.Add(new GraphDiagramNode(
                EveryKey,
                "Every event type",
                "for sources that declare none",
                null,
                210,
                GraphNodeShape.Pill,
                hrefs.FeedAll,
                "A projection or subscription that declares no event types is fed every event the daemon reads.",
                CssClass: "ms-flow-node-every"));
        }

        foreach (FlowEventType type in model.EventTypes.Where(x => selection.Events.Contains(x.Key))
                     .OrderBy(static x => x.IsMartenOwn)
                     .ThenBy(static x => x.Alias, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static x => x.Key, StringComparer.Ordinal))
        {
            nodes.Add(new GraphDiagramNode(
                "e:" + type.Key,
                type.Alias,
                type.ClrName,
                type.IsBaseType ? "base type" : type.SharesAliasWith is { } other ? "alias shared with " + other : null,
                CollectionColorizer.EventTypeHueFor(type.Alias),
                GraphNodeShape.Pill,
                hrefs.Feed(type.Alias),
                type.DotNetTypeName,
                CssClass: type.IsMartenOwn ? "ms-flow-node-own" : null));
        }

        foreach (FlowSource source in model.Sources.Where(x => selection.Sources.Contains(x.Name))
                     .OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static x => x.Name, StringComparer.Ordinal))
        {
            nodes.Add(new GraphDiagramNode(
                "p:" + source.Name,
                source.Name,
                source.KindText,
                // The lifecycle on the third line: "Event projection · Async" does not fit a box fitted to its title.
                source.Input == FlowInput.Everything ? source.Lifecycle + " · every event" : source.Lifecycle.ToString(),
                CollectionColorizer.HueFor(source.Name),
                GraphNodeShape.Rounded,
                hrefs.Source(source.Name),
                source.Name + " - " + source.Description,
                CssClass: "ms-flow-node-source"));
        }

        foreach (FlowOutput output in selection.Outputs.Select(x => outputsByKey[x])
                     .OrderBy(static x => x.Title, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static x => x.Key, StringComparer.Ordinal))
        {
            nodes.Add(OutputNode(output, hrefs));
        }

        foreach (FlowSource every in model.Sources.Where(x => selection.EverySources.Contains(x.Name)))
        {
            styles.Add("every");
            edges.Add(new GraphDiagramEdge(
                EveryKey,
                "p:" + every.Name,
                "ms-flow-edge-every",
                $"{every.Name} declares no event types, so the daemon feeds it every event",
                null,
                EveryEdgeId(every.Name)));
        }

        foreach (FlowRead read in selection.Reads)
        {
            FlowEventType type = eventsByKey[read.EventAlias];
            bool via = read.Via is not null;
            styles.Add(via ? "via" : "reads");

            edges.Add(new GraphDiagramEdge(
                "e:" + type.Key,
                "p:" + read.Source,
                via ? "ms-flow-edge-via" : "ms-flow-edge-reads",
                via ? $"{type.Alias} → {read.Source}, as {read.Via}" : $"{type.Alias} → {read.Source}",
                via ? "as " + read.Via : null,
                ReadEdgeId(type.Key, read.Source)));
        }

        foreach (IGrouping<(string Source, string Output), FlowWrite> group in selection.Writes
                     .GroupBy(static x => (x.Source, Output: x.OutputKey)))
        {
            FlowOutput output = outputsByKey[group.Key.Output];
            bool folds = output.Kind == FlowOutputKind.LiveAggregate;
            string[] subclasses = [.. group.Select(static x => x.Subclass).OfType<string>().Order(StringComparer.Ordinal)];
            styles.Add(folds ? "folds" : "writes");

            edges.Add(new GraphDiagramEdge(
                "p:" + group.Key.Source,
                output.Key,
                folds ? "ms-flow-edge-folds" : "ms-flow-edge-writes",
                folds
                    ? $"{group.Key.Source} folds {output.Title} live; it is not stored"
                    : $"{group.Key.Source} → {output.Title}" + (subclasses.Length > 0 ? " (as " + string.Join(", ", subclasses) + ")" : string.Empty),
                subclasses.Length > 0 ? "as " + string.Join(", ", subclasses) : null,
                WriteEdgeId(group.Key.Source, output.Key)));
        }

        int eventCount = nodes.Count(static x => x.Key.StartsWith("e:", StringComparison.Ordinal) && x.Key != EveryKey);
        int sourceCount = nodes.Count(static x => x.Key.StartsWith("p:", StringComparison.Ordinal));
        int outputCount = nodes.Count - eventCount - sourceCount - (selection.EverySources.Count > 0 ? 1 : 0);

        GraphDiagramModel drawn = new(nodes, edges, Summary(eventCount, sourceCount, outputCount));

        return new EventFlowDrawing(
            drawn,
            nodes.Count,
            nodes.Count > RelationshipViews.NodeCap,
            false,
            [.. AllStyles.Where(styles.Contains)],
            sourceCount);
    }

    /// <summary>The diagram model of <paramref name="filter" /> alone.</summary>
    public static GraphDiagramModel Build(EventFlowModel model, EventFlowFilter filter, EventFlowHrefs hrefs) =>
        Project(model, filter, hrefs).Drawn;

    /// <summary>An event-to-source edge's identity, which the by-projection table carries too as <c>data-edge</c>.</summary>
    public static string ReadEdgeId(string eventKey, string source) => "r:" + eventKey + "|" + source;

    /// <summary>The identity of the edge from the "every event" box to a source.</summary>
    public static string EveryEdgeId(string source) => "r:*|" + source;

    /// <summary>A source-to-output edge's identity.</summary>
    public static string WriteEdgeId(string source, string outputKey) => "w:" + source + "|" + outputKey;

    private static GraphDiagramNode OutputNode(FlowOutput output, EventFlowHrefs hrefs) => output.Kind switch
    {
        FlowOutputKind.Table => new GraphDiagramNode(
            output.Key,
            output.Title,
            output.Subtitle,
            "flat table",
            output.Hue,
            GraphNodeShape.Square,
            hrefs.Table(output.Schema ?? string.Empty, output.Table ?? output.Title),
            $"{output.Schema}.{output.Table} - a flat table"),
        FlowOutputKind.LiveAggregate => new GraphDiagramNode(
            output.Key,
            output.Title,
            output.Subtitle,
            null,
            output.Hue,
            GraphNodeShape.Rounded,
            hrefs.Streams,
            output.Title + " - folded live from a stream's events, never stored. Fold a stream of it from its detail page.",
            CssClass: "ms-flow-node-live"),
        _ => new GraphDiagramNode(
            output.Key,
            output.Title,
            output.Subtitle,
            "document type",
            output.Hue,
            GraphNodeShape.Rounded,
            hrefs.Collection(output.Alias ?? output.Title),
            $"{output.Title} - {output.Subtitle}"),
    };

    private static string Summary(int events, int sources, int outputs) =>
        string.Create(CultureInfo.InvariantCulture, $"{events} event {(events == 1 ? "type feeds" : "types feed")} {sources} {(sources == 1 ? "projection or subscription" : "projections and subscriptions")}, which {(sources == 1 ? "writes" : "write")} {outputs} {(outputs == 1 ? "document type, table or live aggregate" : "document types, tables and live aggregates")}.");

    /// <summary>
    /// What a filter selects: the sources, then everything those sources touch. Unconsumed event types are never drawn - a
    /// column of unconnected boxes - and Marten's own are drawn only on request.
    /// </summary>
    private static Selection Select(EventFlowModel model, EventFlowFilter filter)
    {
        IEnumerable<FlowSource> candidates = model.Sources.Where(static x => !x.IsComposite);
        HashSet<string> typeKeys = new(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(filter.Projection))
        {
            string wanted = filter.Projection.Trim();
            FlowSource? named = model.Source(wanted);
            IReadOnlyList<FlowSource> members = model.MembersOf(wanted);

            if (named is null && members.Count == 0)
            {
                return Selection.Missing;
            }

            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            if (named is { IsComposite: false })
            {
                names.Add(named.Name);
            }

            foreach (FlowSource member in members.Where(static x => !x.IsComposite))
            {
                names.Add(member.Name);
            }

            candidates = candidates.Where(x => names.Contains(x.Name));
        }

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            string wanted = filter.Type.Trim();

            foreach (FlowEventType type in model.EventTypes)
            {
                if (string.Equals(type.Alias, wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(type.Key, wanted, StringComparison.Ordinal))
                {
                    typeKeys.Add(type.Key);
                }
            }

            if (typeKeys.Count == 0)
            {
                return Selection.Missing;
            }

            HashSet<string> readers = new(
                model.Reads.Where(x => typeKeys.Contains(x.EventAlias)).Select(static x => x.Source),
                StringComparer.OrdinalIgnoreCase);

            candidates = candidates.Where(x => x.Input == FlowInput.Everything || readers.Contains(x.Name));
        }

        List<FlowSource> chosen = [.. candidates];
        HashSet<string> chosenNames = new(chosen.Select(static x => x.Name), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FlowEventType> byKey = model.EventTypes.ToDictionary(static x => x.Key, StringComparer.Ordinal);

        // Marten's own events are noise on a whole-store picture (every single-stream projection reads Archived), so they are
        // drawn on request - or when the filter names one.
        bool Shown(FlowEventType type) => !type.IsMartenOwn || filter.IncludeOwn || typeKeys.Contains(type.Key);

        List<FlowRead> reads =
        [
            .. model.Reads.Where(x => chosenNames.Contains(x.Source)
                                      && byKey.TryGetValue(x.EventAlias, out FlowEventType? type)
                                      && Shown(type)
                                      && (typeKeys.Count == 0 || typeKeys.Contains(x.EventAlias))),
        ];

        HashSet<string> events = new(reads.Select(static x => x.EventAlias), StringComparer.Ordinal);
        events.UnionWith(typeKeys);

        HashSet<string> every = new(chosen.Where(static x => x.Input == FlowInput.Everything).Select(static x => x.Name), StringComparer.OrdinalIgnoreCase);

        List<FlowWrite> writes = [.. model.Writes.Where(x => chosenNames.Contains(x.Source))];

        return new Selection(false, events, chosenNames, every, reads, writes, [.. writes.Select(static x => x.OutputKey).Distinct(StringComparer.Ordinal)]);
    }

    private sealed record Selection(
        bool Unknown,
        HashSet<string> Events,
        HashSet<string> Sources,
        HashSet<string> EverySources,
        List<FlowRead> Reads,
        List<FlowWrite> Writes,
        List<string> Outputs)
    {
        public static Selection Missing { get; } = new(true, [], [], [], [], [], []);
    }
}
