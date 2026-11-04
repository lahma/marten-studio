using JasperFx.Events.Projections;

using MartenStudio.Services.Events;

namespace MartenStudio.Tests.Events;

/// <summary>
/// Hand-built event flow models for the diagram and page tests: no store, so a test says exactly what it is about.
/// </summary>
internal static class EventFlowFixtures
{
    /// <summary>Everything a model needs of an event type, with the alias doubling as the key.</summary>
    public static FlowEventType Event(string alias, bool own = false, bool baseType = false) =>
        new(alias, alias.ToUpperInvariant(), "Sample." + alias + ", Sample", own, baseType);

    /// <summary>A projection or subscription that declares its event types.</summary>
    public static FlowSource Source(
        string name,
        FlowSourceKind kind = FlowSourceKind.SingleStream,
        ProjectionLifecycle lifecycle = ProjectionLifecycle.Async,
        FlowInput input = FlowInput.Declared,
        string? composite = null) =>
        new(name, kind, lifecycle, 1, [name + ":All"], composite, input, [], [], null, false, null, null);

    /// <summary>A document type output.</summary>
    public static FlowOutput Document(string alias, string clr) =>
        new("d:" + alias, FlowOutputKind.Document, alias, clr, 120) { Alias = alias };

    /// <summary>A flat table output.</summary>
    public static FlowOutput Table(string schema, string name) =>
        new("t:" + schema + "." + name, FlowOutputKind.Table, name, schema, 200) { Schema = schema, Table = name };

    /// <summary>A live aggregate output.</summary>
    public static FlowOutput Live(string clr) =>
        new("a:Sample." + clr, FlowOutputKind.LiveAggregate, clr, "folded live, not stored", 40);

    /// <summary>
    /// A store shaped like the sample domain: an inline single-stream projection, a multi-stream projection that reads
    /// <c>shipped</c> through the base type <c>iorder</c>, an event projection that writes nothing it declares, and one event type
    /// nothing reads.
    /// </summary>
    public static EventFlowModel Sample() => new(
        [
            Event("cancelled"),
            Event("iorder", baseType: true),
            Event("item_added"),
            Event("ledger_opened"),
            Event("order_placed"),
            Event("shipped"),
            Event("archived", own: true),
        ],
        [
            Source("Daily", FlowSourceKind.MultiStream),
            Source("Orders", FlowSourceKind.SingleStream, ProjectionLifecycle.Inline),
            Source("Tracker", FlowSourceKind.EventProjection),
        ],
        [
            new("order_placed", "Orders", null),
            new("item_added", "Orders", null),
            new("cancelled", "Orders", null),
            new("shipped", "Orders", null),
            new("archived", "Orders", null),
            new("order_placed", "Daily", null),
            new("item_added", "Daily", null),
            new("cancelled", "Daily", null),
            new("iorder", "Daily", null),
            new("shipped", "Daily", "IORDER"),
            new("shipped", "Tracker", null),
            new("item_added", "Tracker", null),
        ],
        [
            new("Orders", "d:order", null),
            new("Daily", "d:day", null),
        ],
        [Document("order", "Order"), Document("day", "Day")],
        [],
        null);
}
