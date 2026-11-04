using System.Runtime.CompilerServices;

using Marten;

namespace MartenStudio.Services.Events;

/// <summary>
/// The event flow model of each store, built once and rebuilt only when the configuration has visibly moved (1.2, M6).
/// </summary>
/// <remarks>
/// <para>
/// A singleton. The model is a pure function of <c>StoreOptions</c> and the visibility delegate, both process-wide, so it is
/// the same for every visitor and nothing visitor-specific is in it: authorization is the scope resolution in front of this
/// cache (<see cref="EventDataService.GetEventFlowAsync" />), never inside it.
/// </para>
/// <para>
/// Why a stamp, and not "once per store build": Marten's event-type registry grows at run time the first time the process
/// appends or maps a type it had not registered, and the Event types page sees that growth, so the flow has to agree with it.
/// The stamp is three cheap list reads - the known event types, the projections and the daemon's shards - and a request that
/// finds it unchanged does nothing else. The entries sit in a <see cref="ConditionalWeakTable{TKey, TValue}" />, so a disposed
/// store's goes with it. Two concurrent requests may both rebuild; the entry is an immutable record replaced atomically and
/// both answers are equal.
/// </para>
/// </remarks>
internal sealed class EventFlowCache
{
    private readonly ConditionalWeakTable<IDocumentStore, Entry> entries = new();

    /// <summary>The model for <paramref name="store" />, rebuilt when its configuration moved or the delegate changed.</summary>
    /// <param name="store">The store; only its options are read.</param>
    /// <param name="isVisible"><c>MartenStudioOptions.IsDocumentTypeVisible</c>, or <see langword="null" />.</param>
    public EventFlowModel Get(IDocumentStore store, Func<Type, bool>? isVisible)
    {
        ArgumentNullException.ThrowIfNull(store);

        Stamp stamp = new(
            store.Options.Events.AllKnownEventTypes().Count,
            store.Options.Events.Projections().Count,
            EventFlowGraph.ShardCountOf(store));

        if (entries.TryGetValue(store, out Entry? entry) && entry.Stamp == stamp && ReferenceEquals(entry.Visible, isVisible))
        {
            return entry.Model;
        }

        EventFlowModel model = EventFlowGraph.Build(store, isVisible);
        entries.AddOrUpdate(store, new Entry(stamp, isVisible, model));

        return model;
    }

    private readonly record struct Stamp(int EventTypes, int Projections, int Shards);

    private sealed record Entry(Stamp Stamp, Func<Type, bool>? Visible, EventFlowModel Model);
}
