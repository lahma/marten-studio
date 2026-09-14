using JasperFx.Events;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;

using MartenStudio.Services.Events;

namespace MartenStudio.Tests.Events;

/// <summary>
/// Which names a dead-letter rewind will accept, and what it says about the ones it will not.
/// </summary>
/// <remarks>
/// <para>
/// The check exists for its <em>message</em> as much as for its answer. Marten's own
/// <c>IEventStore.RewindSubscriptionProgressAsync</c> (9.35) refuses an unknown name with
/// <c>ArgumentOutOfRangeException</c>: <c>"Unknown subscription name 'x'. Available options are A, B,
/// C"</c> - every shard the store has, joined into the sentence. That exception's message is what the
/// studio's audit ring records and what the dead-letter screen renders, so a name typed into the studio
/// would have answered with the store's whole projection set. The refusal here repeats only the name it
/// was given (D5: a refusal says nothing about what exists).
/// </para>
/// <para>
/// The predicate is a pure function of <c>IReadOnlyEventStoreOptions.Projections()</c>, so these are
/// hand-written <see cref="ISubscriptionSource" />s rather than a built store: the thing under test is
/// which registrations count, and a Marten store would add a projection compiler and a connection string
/// to a question that has neither in it.
/// </para>
/// </remarks>
public class RewindProjectionNameTests
{
    [Fact]
    public void An_async_projection_is_the_one_thing_that_can_be_rewound()
    {
        EventDataService.HasAsyncProjection(Registered(), "OrderSummary").Should().BeTrue();
    }

    /// <summary>
    /// Case-insensitively, because Marten's own match is - and because the name arrives from a dead
    /// letter that a person may have retyped into the query string.
    /// </summary>
    [Theory]
    [InlineData("ordersummary")]
    [InlineData("ORDERSUMMARY")]
    [InlineData("OrderSummary")]
    public void The_name_is_matched_the_way_Marten_matches_it(string name)
    {
        EventDataService.HasAsyncProjection(Registered(), name).Should().BeTrue();
    }

    /// <summary>
    /// An inline or live projection has no shard for the daemon to restart, so there is no progression to
    /// rewind however the name is spelt.
    /// </summary>
    [Theory]
    [InlineData("Ledger")]
    [InlineData("Basket")]
    public void A_projection_that_the_daemon_does_not_run_is_refused(string name)
    {
        EventDataService.HasAsyncProjection(Registered(), name).Should().BeFalse();
    }

    [Theory]
    [InlineData("NoSuchProjection")]
    [InlineData("OrderSummary:All")]
    [InlineData("")]
    public void A_name_this_store_does_not_have_is_refused(string name)
    {
        EventDataService.HasAsyncProjection(Registered(), name).Should().BeFalse();
    }

    /// <summary>
    /// The refusal names only what the caller supplied.
    /// </summary>
    /// <remarks>
    /// The regression this pins is the one the adversarial review found: letting Marten answer instead,
    /// whose message lists every projection the store has.
    /// </remarks>
    [Fact]
    public void The_refusal_discloses_nothing_but_the_name_it_was_given()
    {
        KeyNotFoundException refusal = EventDataService.UnknownAsyncProjection("NoSuchProjection");

        refusal.Message.Should().Be("This store has no async projection named 'NoSuchProjection'.");

        refusal.Message.Should().NotContain("Available options", "that is Marten's message, and it lists the store");
        refusal.Message.Should().NotContain("OrderSummary").And.NotContain("Ledger").And.NotContain("Basket");
    }

    /// <summary>One async projection, one inline, one live - the three lifecycles a store can register.</summary>
    private static IReadOnlyList<ISubscriptionSource> Registered() =>
    [
        new FakeSubscriptionSource("OrderSummary", ProjectionLifecycle.Async),
        new FakeSubscriptionSource("Ledger", ProjectionLifecycle.Inline),
        new FakeSubscriptionSource("Basket", ProjectionLifecycle.Live),
    ];

    /// <summary>
    /// One registration, as <c>Projections()</c> hands it over.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than mocked: the package budget has no mocking library, and the only two
    /// members this question has anything to do with are the name and the lifecycle.
    /// </remarks>
    private sealed class FakeSubscriptionSource(string name, ProjectionLifecycle lifecycle) : ISubscriptionSource
    {
        public string Name => name;

        public uint Version => 1;

        public SubscriptionType Type => SubscriptionType.SingleStreamProjection;

        public ProjectionLifecycle Lifecycle => lifecycle;

        public Type ImplementationType => typeof(FakeSubscriptionSource);

        public ShardName[] ShardNames() => [new ShardName(name)];

        public SubscriptionDescriptor Describe(IEventStore store) =>
            throw new NotSupportedException("Nothing in this test describes a projection.");
    }
}
