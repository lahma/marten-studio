using System.Globalization;

using Bunit;

using DataStudio.Components.Layout;
using DataStudio.Kafka.Components.Pages;
using DataStudio.Kafka.Tests.Support;
using DataStudio.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace DataStudio.Kafka.Tests.Components;

/// <summary>
/// A change of the visitor's time zone redraws the Kafka module's pages (F5): the pages of a module with no scope gate are not drawn again by the layout
/// (a page has no parameters, so the router leaves it as it is), so every component that spells a time takes the layout's display version. Rendered through the
/// real layout over the real engine on the fake broker, with the browser answering UTC+14 at circuit start and the visitor picking another zone after it; the
/// engine is never asked again, and a running tail is not restarted.
/// </summary>
public class KafkaZoneChangeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private const string Browser = "Pacific/Kiritimati";

    /// <summary>The instant of the record at this offset in <see cref="KafkaSearchRig.Produce" />'s terms: <see cref="KafkaSearchRig.T0" /> plus its seconds.</summary>
    private static DateTimeOffset At(int seconds) => KafkaSearchRig.T0.AddSeconds(seconds);

    private static string Spelt(DateTimeOffset instant, string zoneId) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(zoneId)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static void Seed(KafkaSearchRig rig, int count)
    {
        rig.Topic("orders");

        for (int i = 0; i < count; i++)
        {
            rig.Produce("orders", 0, i, "k" + i, "v" + i, ("correlation-id", "c-" + i));
        }
    }

    /// <summary>The layout over a page the address names, no zone stored and the browser answering <see cref="Browser" />, waited until circuit start has read it.</summary>
    private static async Task<IRenderedComponent<StudioLayout>> OpenAsync<TPage>(KafkaSearchRig rig, string address)
        where TPage : IComponent
    {
        BunitContext context = rig.Studio.Context;
        await rig.Studio.Context.ReadyAsync();

        context.JSInterop.Setup<string?>("martenStudio.prefs.get", StudioPreferences.ThemePreferenceKey).SetResult(null);
        context.JSInterop.Setup<string?>("martenStudio.prefs.get", StudioPreferences.TimeZonePreferenceKey).SetResult(null);
        context.JSInterop.Setup<string?>("martenStudio.timeZone.get").SetResult(Browser);

        rig.Studio.Navigation.Simulate(address, intercepted: false);

        RenderFragment body = static builder =>
        {
            builder.OpenComponent<TPage>(0);
            builder.CloseComponent();
        };

        IRenderedComponent<StudioLayout> layout = context.Render<StudioLayout>(parameters => parameters.Add(x => x.Body, body));
        layout.WaitForState(() => layout.Instance.PreferencesLoaded);
        return layout;
    }

    /// <summary>A zone the Preferences select lists, whose offset at <paramref name="instant" /> is not the browser's, picked as the select is used.</summary>
    private static string PickAnotherZone(IRenderedComponent<StudioLayout> layout, DateTimeOffset instant)
    {
        string other = layout.FindAll("#ms-timezone-select option").Select(static x => x.GetAttribute("value")!).First(id =>
            TimeZoneInfo.TryFindSystemTimeZoneById(id, out TimeZoneInfo? zone) && zone is not null && zone.GetUtcOffset(instant) != TimeSpan.FromHours(14));

        layout.Find("#ms-timezone-select").Change(other);
        return other;
    }

    [Fact]
    public async Task A_record_page_already_drawn_spells_its_times_again_when_the_zone_changes_and_the_engine_is_not_asked()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig, 20);

        IRenderedComponent<StudioLayout> layout = await OpenAsync<KafkaRecordPage>(rig, "kafka/record?cluster=test&topic=orders&partition=0&offset=10");

        // The browser's zone, UTC+14: the record's own time and its neighbours'.
        layout.WaitForAssertion(() => layout.FindAll("table.ms-k-neighbours tbody tr").Should().HaveCount(11), Wait);
        string Marked() => layout.Find("table.ms-k-neighbours tbody tr[aria-current='true']").TextContent;
        string Detail() => layout.Find(".ms-k-record-detail").TextContent;
        layout.WaitForAssertion(() => Marked().Should().Contain(Spelt(At(10), Browser)), Wait);
        Detail().Should().Contain(Spelt(At(10), Browser));
        int calls = rig.BrokerCalls;

        string picked = PickAnotherZone(layout, At(10));

        layout.WaitForAssertion(() => Marked().Should().Contain(Spelt(At(10), picked)), Wait);
        Detail().Should().Contain(Spelt(At(10), picked)).And.NotContain(Spelt(At(10), Browser));
        layout.Find("table.ms-k-neighbours tbody tr:first-child").TextContent.Should().Contain(Spelt(At(5), picked));
        rig.BrokerCalls.Should().Be(calls, "a zone is spelt again from the record the page holds: the broker is not asked");
    }

    [Fact]
    public async Task A_search_result_spells_its_times_again_when_the_zone_changes_and_the_engine_is_not_asked()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig, 3);

        IRenderedComponent<StudioLayout> layout = await OpenAsync<KafkaSearchPage>(
            rig, "kafka/search?cluster=test&topic=orders&from=2026-11-08T11%3A00%3A00Z&to=2026-11-08T13%3A00%3A00Z");
        layout.WaitForAssertion(() => layout.FindAll("label.ms-k-topic-option input:checked, label.ms-k-topic-option input[checked]").Should().HaveCount(1), Wait);

        await layout.Find(".ms-k-search-actions button").ClickAsync(new MouseEventArgs());

        string[] Times() => [.. layout.FindAll("table.ms-k-matches > tbody > tr.ms-table-row").Select(static x => x.QuerySelectorAll("td")[0].TextContent.Trim())];
        layout.WaitForAssertion(() => Times().Should().HaveCount(3), Wait);
        Times().Should().BeEquivalentTo([Spelt(At(0), Browser), Spelt(At(1), Browser), Spelt(At(2), Browser)]);
        layout.WaitForAssertion(() => layout.Find(".ms-k-result-banner").TextContent.Should().NotBeEmpty(), Wait);
        int calls = rig.BrokerCalls;

        string picked = PickAnotherZone(layout, At(0));

        layout.WaitForAssertion(() => Times().Should().BeEquivalentTo([Spelt(At(0), picked), Spelt(At(1), picked), Spelt(At(2), picked)]), Wait);
        rig.BrokerCalls.Should().Be(calls, "the matches the page holds are spelt again: nothing is read and no scan is planned");
        rig.Ring.Should().ContainSingle("the one search is the one audit entry: the zone's change is neither");
    }

    /// <summary>
    /// A window typed in the visitor's zone keeps its instants when that zone moves, as it does when the visitor changes the window's own zone choice: what
    /// the label says the box means is what a search reads.
    /// </summary>
    [Fact]
    public async Task An_absolute_window_in_the_visitors_zone_keeps_its_instants_when_the_zone_changes()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig, 3);

        IRenderedComponent<StudioLayout> layout = await OpenAsync<KafkaSearchPage>(
            rig, "kafka/search?cluster=test&topic=orders&from=2026-11-08T11%3A00%3A00Z&to=2026-11-08T13%3A00%3A00Z");
        layout.WaitForAssertion(() => layout.FindAll("input[type=datetime-local]").Should().HaveCount(2), Wait);
        layout.Find("input[type=datetime-local]").GetAttribute("value").Should().Be("2026-11-08T11:00", "a link's window is UTC");

        // The window in the visitor's zone (the browser's, UTC+14): the same instants, spelt there.
        layout.FindAll("select").Single(static x => x.QuerySelector("option[value=preference]") is not null).Change("preference");
        layout.WaitForAssertion(() => layout.Find("input[type=datetime-local]").GetAttribute("value").Should().Be("2026-11-09T01:00"), Wait);

        string picked = PickAnotherZone(layout, KafkaSearchRig.T0);

        string expected = TimeZoneInfo.ConvertTime(new DateTimeOffset(2026, 11, 8, 11, 0, 0, TimeSpan.Zero), TimeZoneInfo.FindSystemTimeZoneById(picked))
            .ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
        layout.WaitForAssertion(() => layout.Find("input[type=datetime-local]").GetAttribute("value").Should().Be(expected), Wait);
        layout.Find(".ms-k-window-text").TextContent.Should().EndWith("11:00Z–13:00Z", "the window line still says the same instants, in UTC");
    }

    [Fact]
    public async Task A_running_tail_spells_its_times_again_and_is_not_restarted()
    {
        await using KafkaSearchRig rig = new(
            studio: static o =>
            {
                o.Capabilities.LiveTail = true;
            },
            traceCircuit: true);
        Seed(rig, 3);

        IRenderedComponent<StudioLayout> layout = await OpenAsync<KafkaTailPage>(rig, "kafka/tail?cluster=test&topics=orders");
        layout.WaitForAssertion(() => layout.FindAll("textarea.ms-k-predicate").Should().HaveCount(1), Wait);

        Task start = layout.FindAll(".ms-k-tail-actions button").Single(static x => x.TextContent.Trim() == "Start").ClickAsync(new MouseEventArgs());
        layout.WaitForAssertion(() => layout.Find(".ms-k-tail-chip-live").TextContent.Should().Be("1 live"), Wait);

        rig.Produce("orders", 0, 10, "n1", "x");
        string[] Times() => [.. layout.FindAll("table.ms-k-tail-matches > tbody > tr.ms-table-row").Select(static x => x.QuerySelectorAll("td")[0].TextContent.Trim())];
        layout.WaitForAssertion(() => Times().Should().Equal(Spelt(At(10), Browser)), Wait);

        string picked = PickAnotherZone(layout, At(10));

        layout.WaitForAssertion(() => Times().Should().Equal(Spelt(At(10), picked)), Wait);
        layout.Find(".ms-k-tail-chip-live").TextContent.Should().Be("1 live", "a zone change does not pause or restart a tail");
        rig.CircuitLogs.Entries.Count(static x => x.EventId.Id == 9607).Should().Be(1, "the tail was started once");
        rig.Ring.Should().BeEmpty("a tail is one audit entry, written when it ends");

        await layout.FindAll(".ms-k-tail-actions button").Single(static x => x.TextContent.Trim() == "Stop").ClickAsync(new MouseEventArgs());
        await start.WaitAsync(Wait, Xunit.TestContext.Current.CancellationToken);

        rig.Ring.Should().ContainSingle();
        rig.CircuitLogs.Entries.Count(static x => x.EventId.Id == 9607).Should().Be(1);
    }
}
