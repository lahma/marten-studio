using MartenStudio.Services;
using MartenStudio.Services.Events;

namespace MartenStudio.Tests.Events;

/// <summary>
/// The Events area's links: the id and every filter in the query string (D9), the scope on the end of
/// each of them, and nothing that was not set.
/// </summary>
public class EventLinksTests
{
    private static readonly MartenStudioOptions DefaultPath = new();

    private static readonly StudioScope Scope = new("default", "localhost.marten", "acme");

    [Fact]
    public void A_stream_link_carries_the_id_in_the_query_string_and_never_in_a_segment()
    {
        // A Marten stream key is frequently a string containing '/', which a route segment cannot hold.
        string link = EventLinks.ToStream(DefaultPath, Scope, "orders/2026/17");

        link.Should().StartWith("marten/events/streams/s?id=");
        link.Should().Contain("id=orders%2F2026%2F17");
    }

    [Fact]
    public void Every_link_carries_the_active_scope()
    {
        string link = EventLinks.ToStream(DefaultPath, Scope, "abc");

        link.Should().Contain("store=default");
        link.Should().Contain("db=localhost.marten");
        link.Should().Contain("tenant=acme");
    }

    [Fact]
    public void A_scope_with_no_tenant_does_not_put_an_empty_tenant_in_the_url()
    {
        string link = EventLinks.ToStream(DefaultPath, new StudioScope("default", "localhost.marten", null), "abc");

        link.Should().NotContain("tenant=");
    }

    [Fact]
    public void A_filter_that_is_not_set_is_left_out_entirely()
    {
        string link = EventLinks.To(
            DefaultPath,
            EventLinks.FeedRoute,
            scope: null,
            [new("types", "OrderPlaced"), new("stream", null), new("range", "   ")]);

        link.Should().Be("marten/events/feed?types=OrderPlaced");
    }

    [Fact]
    public void A_custom_path_makes_links_relative_to_the_studio_root()
    {
        var options = new MartenStudioOptions { Path = "/ops/marten" };

        EventLinks.To(options, EventLinks.FeedRoute, scope: null).Should().Be("events/feed");
    }

    [Fact]
    public void A_value_with_an_ampersand_cannot_become_a_second_parameter()
    {
        string link = EventLinks.To(
            DefaultPath, EventLinks.FeedRoute, scope: null, [new("stream", "a&follow=1")]);

        link.Should().Be("marten/events/feed?stream=a%26follow%3D1");
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("", true, true)]
    [InlineData("1", false, true)]
    [InlineData("0", true, false)]
    public void A_flag_defaults_to_what_the_page_would_have_done_anyway(string? value, bool fallback, bool expected)
    {
        EventLinks.ReadFlag(value, fallback).Should().Be(expected);
    }

    [Fact]
    public void A_stream_cursor_survives_a_round_trip_through_a_url()
    {
        var cursor = new StreamCursor(
            new DateTimeOffset(2026, 9, 14, 10, 30, 0, TimeSpan.Zero), "orders/2026/17");

        StreamCursor? read = StreamCursor.FromToken(cursor.ToToken());

        read.Should().NotBeNull();
        read!.Id.Should().Be("orders/2026/17");
        read.Timestamp.Should().Be(cursor.Timestamp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("|missing-timestamp")]
    [InlineData("123|")]
    public void A_cursor_token_somebody_typed_is_no_cursor_rather_than_an_exception(string? token)
    {
        StreamCursor.FromToken(token).Should().BeNull();
    }

    /// <summary>
    /// The four states plan §4.8 names are the ones worth a Retry button; everything else is a failure a
    /// person has to act on rather than repeat.
    /// </summary>
    [Theory]
    [InlineData("28P01", true)]
    [InlineData("3D000", true)]
    [InlineData("08006", true)]
    [InlineData("57014", true)]
    [InlineData("42P01", false)]
    [InlineData(null, false)]
    public void Only_the_environmental_sql_states_offer_a_retry(string? sqlState, bool retryable)
    {
        EventDataError.IsRetryable(sqlState).Should().Be(retryable);
    }

    [Fact]
    public void An_error_shows_the_sql_state_in_front_of_the_message()
    {
        new EventDataError("relation does not exist", "42P01", false).Display
            .Should().Be("[42P01] relation does not exist");

        new EventDataError("something else", null, false).Display.Should().Be("something else");
    }
}
