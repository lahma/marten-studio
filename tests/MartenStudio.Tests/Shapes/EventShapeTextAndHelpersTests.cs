using System.Text.Json;

using MartenStudio.Services.Shapes;

namespace MartenStudio.Tests.Shapes;

/// <summary>The events' sentences (D-SHAPE S6, S7) and the two pure helpers of the events check.</summary>
public class EventShapeTextAndHelpersTests
{
    private static ShapeItemKey Key => new("stream-1", null, 1042, 3);

    [Fact]
    public void Dropped_says_nothing_is_lost_from_the_log()
    {
        ShapeCheckText.EventDropped(12, 200, "$.couponCode", "OrderPlaced").Should().Be(
            "12 of 200 sampled `OrderPlaced` events carry `$.couponCode`, which `OrderPlaced` no longer declares: projections never see it. "
            + "Events are never rewritten, so nothing is lost from the log.");
    }

    [Fact]
    public void The_member_sentences_say_what_projections_see()
    {
        ShapeCheckText.EventNulled(1, 10, "$.a", "T").Should().EndWith("projections see it as empty.");
        ShapeCheckText.EventRetyped(1, 10, "$.a", "T", JsonValueKind.String, JsonValueKind.Number).Should()
            .Contain("store `$.a` as a string, and `T` reads it as a number").And.EndWith("projections see it as a number.");
        ShapeCheckText.EventRewritten(1, 10, "$.a", "T").Should().EndWith("projections see a different value from the one stored.");
        ShapeCheckText.EventRespelled(1, 10, "$.A", "$.a", "T").Should().EndWith("the value reaches projections under the new spelling.");
        ShapeCheckText.EventDefaulted(3, 10, "$.a", "T").Should().Be("`$.a` is absent from 3 of 10 sampled `T` events: projections read its default.");
        ShapeCheckText.EventDefaulted(10, 10, "$.a", "T").Should().Be("`$.a` is declared by `T` and set in none of the 10 sampled events.");
    }

    [Fact]
    public void An_unreadable_event_names_its_path_the_stored_kind_and_the_exception_type_and_never_a_message()
    {
        ShapeFailureGroup failure = new(ShapeFailureKind.Unreadable, "JsonException", "$.total", JsonValueKind.String, null, 3, Key, 1042);

        ShapeCheckText.EventFailure(failure, 200, "OrderPlaced").Should().Be(
            "3 of 200 sampled `OrderPlaced` events cannot be read: `$.total` is stored as a string (`JsonException`).");
    }

    [Fact]
    public void An_unknown_event_type_names_the_alias_and_the_stored_dotnet_type()
    {
        ShapeFailureGroup failure = new(
            ShapeFailureKind.UnknownEventType, "UnknownEventTypeException", null, null, "order_placed_v1 / Shop.OrderPlacedV1, Shop", 4, Key, 7);

        ShapeCheckText.EventFailure(failure, 4, "order_placed_v1").Should().Be(
            "4 of 4 `order_placed_v1` events cannot be read: no type is registered for `order_placed_v1`, and `Shop.OrderPlacedV1, Shop` cannot be loaded.");
        ShapeCheckText.EventFailure(failure with { Detail = "order_placed_v1 / (none)" }, 4, "order_placed_v1").Should()
            .EndWith("and the row names no .NET type to load.");
    }

    [Fact]
    public void The_rebuild_sentence_names_the_first_sequence_and_what_the_option_does()
    {
        ShapeCheckText.RebuildSerialization("OrderPlaced", 1042, rebuildSkips: false, daemonSkips: false).Should().Be(
            "A rebuild of a projection that reads `OrderPlaced` stops at sequence 1,042, the first of them, because `RebuildErrors.SkipSerializationErrors` is off. "
            + "The running daemon pauses the shard at the first of them (`Errors.SkipSerializationErrors` is off).");
        ShapeCheckText.RebuildSerialization("OrderPlaced", 1042, rebuildSkips: true, daemonSkips: true).Should().Be(
            "A rebuild skips them and records each as a dead letter (`RebuildErrors.SkipSerializationErrors` is on): its projections leave them out. "
            + "The running daemon skips them and records each as a dead letter (`Errors.SkipSerializationErrors` is on).");
        ShapeCheckText.RebuildUnknown(7, rebuildSkips: false, daemonSkips: true).Should().StartWith(
            "A rebuild of a projection that reads every event type stops at sequence 7, the first of them, because `RebuildErrors.SkipUnknownEvents` is off.")
            .And.EndWith("The running daemon skips them (`Errors.SkipUnknownEvents` is on).");
    }

    [Fact]
    public void The_other_event_sentences()
    {
        ShapeCheckText.EventsReadAsOtherType(80, 200, "OrderPlaced").Should().Be(
            "80 of 200 sampled `OrderPlaced` events were written as another type and are read as `OrderPlaced`: an upcaster or a renamed type maps them, so their members are not compared.");
        ShapeCheckText.EventsBinary(2, 5, "T").Should().Be("2 of 5 sampled `T` events have a binary body: they are read, and not compared.");
        ShapeCheckText.NoSuchEventType("ghost").Should().Be("There is no event type called 'ghost' to check.");
        ShapeCheckText.EveryEventFits("T").Should().Be("Every sampled event fits `T`.").And.NotContain("healthy");
        ShapeCheckText.NoTypeIndex.Should().Be("Without an index on type, only the oldest and newest events of each type were read.");
        ShapeCheckText.TypeIndexRemedy.Should().Be("opts.Events.EnableEventTypeIndex = true;");
        ShapeCheckText.ArchivedLeftOut.Should().Be("Archived events are not checked: a rebuild skips them.");
        ShapeCheckText.EventsAllTenantsEdgesOnly.Should().Contain("choose a tenant for a spread sample");
        ShapeCheckText.UpcasterRemedy.Should().Contain("opts.Events.Upcast(...)");
    }

    [Fact]
    public void The_event_summary_of_a_complete_a_sampled_and_a_partial_check()
    {
        ShapeCheckResult Result(ShapeCoverage coverage, int examined, int lowest = 0, int highest = 0, int spread = 0, string? reason = null) =>
            new("t", coverage, DateTimeOffset.UnixEpoch, TimeSpan.Zero, examined, 0, lowest, highest, spread, reason, null, [], [], null, null, []);

        ShapeCheckText.EventSummary(Result(ShapeCoverage.Complete, 37), "10:42").Should().Be("Checked all 37 events at 10:42.");
        ShapeCheckText.EventSummary(Result(ShapeCoverage.Complete, 0), "10:42").Should().Be("There are no events of this type in scope.");
        ShapeCheckText.EventSummary(Result(ShapeCoverage.Sampled, 200, 25, 25, 150), "10:42").Should().Be(
            "Checked 200 events at 10:42: the 25 oldest and 25 newest and 150 spread between them.");
        ShapeCheckText.EventSummary(Result(ShapeCoverage.Sampled, 200, 100, 100), "10:42").Should().EndWith("the 100 oldest and 100 newest.");
        ShapeCheckText.EventSummary(Result(ShapeCoverage.Partial, 5, reason: "the 15-second budget ran out."), "10:42").Should()
            .Be("Stopped after 5 events: the 15-second budget ran out.");
    }

    [Theory]
    [InlineData("Shop.Orders.OrderPlaced, Shop", true)]
    [InlineData("Shop.Orders.OrderPlaced, Shop.Renamed", true)]
    [InlineData("Shop.Orders.OrderPlacedV1, Shop", false)]
    [InlineData("Shop.Orders.OrderPlaced", false)]
    public void An_event_is_the_same_type_when_only_the_assembly_part_differs(string stored, bool same)
    {
        EventShapeCheckService.IsSameType(stored, typeof(Shop.Orders.OrderPlaced)).Should().Be(same);
    }

    [Fact]
    public void A_closed_generics_name_with_commas_is_not_split_at_the_first_comma()
    {
        Type generic = typeof(Dictionary<string, int>);
        string stored = generic.FullName + ", " + generic.Assembly.GetName().Name;

        EventShapeCheckService.IsSameType(stored, generic).Should().BeTrue();
        EventShapeCheckService.IsSameType(stored, typeof(Shop.Orders.OrderPlaced)).Should().BeFalse();
    }

    [Fact]
    public void The_probes_are_evenly_spaced_between_the_two_edges_and_none_when_the_edges_touch()
    {
        List<EventShapeCheckService.KeyRow> edges =
        [
            new(10, null, 1, Lowest),
            new(25, null, 1, Lowest),
            new(9_000, null, 1, Highest),
            new(9_999, null, 1, Highest),
        ];

        long[] probes = EventShapeCheckService.Probes(edges);

        probes.Should().HaveCount(ShapeCheckLimits.Spread).And.BeInAscendingOrder().And.OnlyContain(static x => x > 25 && x < 9_000);
        EventShapeCheckService.Probes([new(1, null, 1, Lowest), new(2, null, 1, Highest)]).Should().BeEmpty();
        EventShapeCheckService.Probes([new(1, null, 1, Lowest)]).Should().BeEmpty();
    }

    private const int Lowest = MartenStudio.Internal.Sql.EventShapeQueries.PartLowest;
    private const int Highest = MartenStudio.Internal.Sql.EventShapeQueries.PartHighest;
}
