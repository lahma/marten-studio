using Bunit;

using JasperFx.Events;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;

namespace MartenStudio.Tests.Events;

/// <summary>The streams list: its rows, its states, and the links each row carries.</summary>
public class StreamsPageTests
{
    private static readonly string StreamId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Each_row_carries_the_id_the_type_the_version_and_the_two_links()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.Streams = new StreamPage([FakeEventDataService.Stream(StreamId)], null, false);

        IRenderedComponent<Streams> page = context.Render<Streams>();

        page.Find("tbody tr a").GetAttribute("href").Should()
            .StartWith("events/streams/s?id=" + StreamId);
        page.TextOfAll(".ms-event-chip").Should().Contain("Order");
        page.Markup.Should().Contain("Open in feed");
        page.FindAll("a").Select(x => x.GetAttribute("href")).Should()
            .Contain(x => x!.StartsWith("events/feed?stream=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_archived_stream_wears_the_badge()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.Streams = new StreamPage([FakeEventDataService.Stream(StreamId, archived: true)], null, false);

        IRenderedComponent<Streams> page = context.Render<Streams>();

        page.FindAll(".ms-event-flag-archived").Should().NotBeEmpty();
    }

    /// <summary>
    /// The tenant column exists only where the store has a tenant column - a column of dashes on a
    /// single-tenant store is noise that looks like missing data.
    /// </summary>
    [Fact]
    public async Task The_tenant_column_appears_only_when_the_store_has_one()
    {
        using StudioComponentContext single = await NewContextAsync();
        single.EventData.Streams = new StreamPage([FakeEventDataService.Stream(StreamId)], null, false);

        single.Render<Streams>().TextOfAll("thead th").Should().NotContain("Tenant");

        using StudioComponentContext conjoined = await NewContextAsync();
        conjoined.EventData.Shape = conjoined.EventData.Shape with { HasTenantId = true };
        conjoined.EventData.Streams = new StreamPage(
            [FakeEventDataService.Stream(StreamId, tenantId: "acme")], null, false);

        IRenderedComponent<Streams> page = conjoined.Render<Streams>();

        page.TextOfAll("thead th").Should().Contain("Tenant");
        page.Markup.Should().Contain("acme");
    }

    [Fact]
    public async Task An_empty_store_says_so_rather_than_rendering_an_empty_table()
    {
        using StudioComponentContext context = await NewContextAsync();

        IRenderedComponent<Streams> page = context.Render<Streams>();

        page.Find(".ms-empty-title").TextContent.Should().Be("No streams");
    }

    /// <summary>A store whose event tables were never created is a different empty from no streams.</summary>
    [Fact]
    public async Task A_store_with_no_event_tables_says_that_instead()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.Shape = context.EventData.Shape with { EventTablesExist = false };

        IRenderedComponent<Streams> page = context.Render<Streams>();

        page.Find(".ms-empty-title").TextContent.Should().Be("This store has no event storage yet");
    }

    /// <summary>A failed read is a value, drawn as an alert with the SQLSTATE, not an empty list.</summary>
    [Fact]
    public async Task A_failed_read_renders_the_sql_state_and_offers_a_retry_only_when_retrying_could_work()
    {
        using StudioComponentContext retryable = await NewContextAsync();
        retryable.EventData.Streams = StreamPage.Failed(new EventDataError("connection failure", "08006", true));

        IRenderedComponent<Streams> page = retryable.Render<Streams>();

        page.Find(".ms-error-alert").TextContent.Should().Contain("[08006] connection failure");
        page.FindAll(".ms-error-alert button").Should().NotBeEmpty();

        using StudioComponentContext fatal = await NewContextAsync();
        fatal.EventData.Streams = StreamPage.Failed(new EventDataError("relation does not exist", "42P01", false));

        fatal.Render<Streams>().FindAll(".ms-error-alert button").Should().BeEmpty();
    }

    /// <summary>The id box has to say what kind of id this store's streams have.</summary>
    [Fact]
    public async Task The_id_box_asks_for_whatever_this_store_identifies_streams_by()
    {
        using StudioComponentContext guids = await NewContextAsync();
        guids.Render<Streams>().Find("#ms-streams-id").GetAttribute("placeholder")
            .Should().Be("any stream GUID");

        using StudioComponentContext keys = await NewContextAsync();
        keys.EventData.Shape = keys.EventData.Shape with { StreamIdentity = StreamIdentity.AsString };

        keys.Render<Streams>().Find("#ms-streams-id").GetAttribute("placeholder")
            .Should().Be("any stream key");
    }

    /// <summary>The "recently active" ordering reaches the service, which is what decides the SQL.</summary>
    [Fact]
    public async Task The_recently_active_ordering_is_asked_for_by_the_url()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.Navigate("marten/events/streams?order=recent&archived=1");

        context.Render<Streams>();

        StreamListRequest request = context.EventData.StreamRequests[^1];
        request.Order.Should().Be(StreamListOrder.RecentlyActive);
        request.IncludeArchived.Should().BeTrue();
    }

    private static async Task<StudioComponentContext> NewContextAsync()
    {
        var context = new StudioComponentContext();
        await context.ReadyAsync();
        return context;
    }
}
