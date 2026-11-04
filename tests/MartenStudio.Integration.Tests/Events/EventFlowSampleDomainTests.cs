using JasperFx.Events;
using JasperFx.Events.Projections;

using Marten;

using MartenStudio.SampleDomain;
using MartenStudio.SampleDomain.Events;
using MartenStudio.Services.Events;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// 1.2, M6: the event flow of the demo store, built from its real configuration (<c>SampleStore.Configure</c>) and never
/// connected to - so this needs no Docker, and proves the model against the projections a host actually writes.
/// </summary>
public class EventFlowSampleDomainTests
{
    /// <summary>A database nothing listens on: the model reads configuration, so no connection is ever tried.</summary>
    private const string RefusedConnectionString = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2";

    private static EventFlowModel Build()
    {
        using DocumentStore store = DocumentStore.For(options => SampleStore.Configure(options, RefusedConnectionString));

        return EventFlowGraph.Build(store, null);
    }

    private static string[] ReadsOf(EventFlowModel model, string source) =>
        [.. model.Reads.Where(x => x.Source == source)
            .Select(x => model.EventTypes.Single(e => e.Key == x.EventAlias))
            .Where(static x => !x.IsMartenOwn)
            .Select(static x => x.ClrName)
            .Order(StringComparer.Ordinal)];

    [Fact]
    public void The_inline_order_summary_reads_the_order_events_and_Archived_and_writes_its_document()
    {
        EventFlowModel model = Build();

        FlowSource source = model.Sources.Single(static x => x.Name == "OrderSummary");
        source.Kind.Should().Be(FlowSourceKind.SingleStream);
        source.Lifecycle.Should().Be(ProjectionLifecycle.Inline);
        source.Input.Should().Be(FlowInput.Declared);

        ReadsOf(model, "OrderSummary").Should().Equal(
            nameof(ItemAdded), nameof(OrderCancelled), nameof(OrderPlaced), nameof(OrderShipped), nameof(OrderVoided));
        model.Reads.Where(static x => x.Source == "OrderSummary").Should().OnlyContain(static x => x.Via == null);
        model.Reads.Should().Contain(x => x.Source == "OrderSummary" && model.EventTypes.Single(e => e.Key == x.EventAlias).ClrName == nameof(Archived));

        FlowOutput output = model.Outputs.Single(x => x.Key == model.Writes.Single(static w => w.Source == "OrderSummary").OutputKey);
        output.Kind.Should().Be(FlowOutputKind.Document);
        output.Subtitle.Should().Be(nameof(OrderSummary));
    }

    /// <summary>
    /// <c>Identity&lt;IOrderEvent&gt;()</c> groups the events; JasperFx's <c>AllEventTypes</c> - what <c>AppliesTo</c> and the
    /// daemon's filter read - holds the Apply methods' types, so the daemon does not feed DailySales an <c>OrderShipped</c>.
    /// </summary>
    [Fact]
    public void The_async_daily_sales_reads_the_events_it_applies_and_writes_its_document()
    {
        EventFlowModel model = Build();

        FlowSource source = model.Sources.Single(static x => x.Name == "DailySales");
        source.Kind.Should().Be(FlowSourceKind.MultiStream);
        source.Lifecycle.Should().Be(ProjectionLifecycle.Async);

        ReadsOf(model, "DailySales").Should().Equal(nameof(ItemAdded), nameof(OrderCancelled), nameof(OrderPlaced));
        model.Outputs.Single(x => x.Key == model.Writes.Single(static w => w.Source == "DailySales").OutputKey).Subtitle.Should().Be(nameof(DailySales));
    }

    [Fact]
    public void The_shipment_tracker_reads_its_IEvent_parameters_and_lists_no_output_because_it_declares_none()
    {
        EventFlowModel model = Build();

        FlowSource source = model.Sources.Single(static x => x.Name == "ShipmentTracker");
        source.Kind.Should().Be(FlowSourceKind.EventProjection);
        source.Lifecycle.Should().Be(ProjectionLifecycle.Async);

        ReadsOf(model, "ShipmentTracker").Should().Equal(nameof(ItemAdded), nameof(OrderShipped));
        model.Writes.Where(static x => x.Source == "ShipmentTracker").Should().BeEmpty(
            "it stores a Shipment from a Project method without declaring it: PublishedTypes() cannot say");
        source.UndrawnOutputs.Should().BeEmpty();
    }

    [Fact]
    public void Nothing_in_the_demo_store_goes_unread_and_Martens_own_events_are_never_unconsumed()
    {
        EventFlowModel model = Build();

        model.Unconsumed.Should().BeEmpty();
        model.HasEverythingSource.Should().BeFalse();
        model.EventTypes.Single(static x => x.ClrName == nameof(Archived)).IsMartenOwn.Should().BeTrue();
        model.SubscriptionsUnavailable.Should().BeNull();
        model.Sources.Select(static x => x.Name).Should().BeEquivalentTo("OrderSummary", "DailySales", "ShipmentTracker");
    }

    [Fact]
    public void Every_edge_names_a_box_and_none_is_duplicated()
    {
        EventFlowModel model = Build();

        HashSet<string> events = [.. model.EventTypes.Select(static x => x.Key)];
        HashSet<string> outputs = [.. model.Outputs.Select(static x => x.Key)];

        model.Reads.Should().OnlyContain(x => events.Contains(x.EventAlias));
        model.Writes.Should().OnlyContain(x => outputs.Contains(x.OutputKey));
        model.Reads.Should().OnlyHaveUniqueItems();
        model.Writes.Should().OnlyHaveUniqueItems();
        model.EventTypes.Should().Contain(static x => x.ClrName == nameof(OrderPlaced));
    }
}
