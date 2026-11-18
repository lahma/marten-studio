using System.Globalization;

using Bunit;

using DataStudio.Components.Layout;
using DataStudio.Marten.Components.Pages.Events;
using DataStudio.Marten.Services.Events;
using DataStudio.Marten.Tests.Events;
using DataStudio.Postgres.Services.Health;
using DataStudio.Services;

using Microsoft.AspNetCore.Components;

namespace DataStudio.Marten.Tests.Components;

/// <summary>
/// A change of the visitor's time zone is a change of display and nothing else: every time on screen is spelt again in the new zone, from what the
/// page already holds, and no page reads its data, the rail reads its badges or the scope moves because of it. Rendered through the real layout, which
/// is where a circuit's zone is read from the browser at its start and where the Preferences select is, with the two zones the shell's other tests use
/// (UTC+14 and UTC-11, so no machine's own zone is ever the answer) and one the Preferences select lists.
/// </summary>
public class ZoneChangeTests
{
    private const string StreamId = "11111111-1111-1111-1111-111111111111";

    private static readonly DateTimeOffset Created = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary><see cref="Created" /> spelt as the studio spells a time (<c>u</c>) in the zone with this id.</summary>
    private static string Spelt(string zoneId) =>
        TimeZoneInfo.ConvertTime(Created, TimeZoneInfo.FindSystemTimeZoneById(zoneId)).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static async Task<StudioComponentContext> NewContextAsync()
    {
        StudioComponentContext context = new();
        await context.ReadyAsync();
        context.EventData.Streams = new StreamPage([FakeEventDataService.Stream(StreamId)], null, false);
        return context;
    }

    /// <summary>
    /// The layout over a page, with no zone stored in the browser and the browser answering <paramref name="browserZone" />, waited until the first-render read
    /// of the browser has finished: circuit start.
    /// </summary>
    private static IRenderedComponent<StudioLayout> RenderOver<TPage>(StudioComponentContext context, string browserZone)
        where TPage : IComponent
    {
        context.JSInterop.Setup<string?>("martenStudio.prefs.get", StudioPreferences.ThemePreferenceKey).SetResult(null);
        context.JSInterop.Setup<string?>("martenStudio.prefs.get", StudioPreferences.TimeZonePreferenceKey).SetResult(null);
        context.JSInterop.Setup<string?>("martenStudio.timeZone.get").SetResult(browserZone);

        RenderFragment body = static builder =>
        {
            builder.OpenComponent<TPage>(0);
            builder.CloseComponent();
        };

        IRenderedComponent<StudioLayout> layout = context.Render<StudioLayout>(parameters => parameters.Add(x => x.Body, body));
        layout.WaitForState(() => layout.Instance.PreferencesLoaded);
        return layout;
    }

    private static string CreatedCell(IRenderedComponent<StudioLayout> layout) =>
        layout.Find(".ms-stream-created").TextContent.Trim();

    /// <summary>A zone the Preferences select lists, whose offset is not +14:00 (the browser's in these tests), picked by the visitor as the select is used.</summary>
    private static string PickAnotherZone(IRenderedComponent<StudioLayout> layout)
    {
        string other = layout.SelectorOptions("ms-timezone-select").First(static id =>
            TimeZoneInfo.TryFindSystemTimeZoneById(id, out TimeZoneInfo? zone) && zone.GetUtcOffset(Created) != TimeSpan.FromHours(14));

        layout.Find("#ms-timezone-select").Change(other);
        return other;
    }

    /// <summary>
    /// F6: the zone change at circuit start reached a page as a scope change, so a page that reads its data when the scope changes read it a second time, for every
    /// visitor whose browser is not in the server's zone. It draws the browser's zone now and has read once.
    /// </summary>
    [Fact]
    public async Task The_browsers_zone_draws_a_page_in_it_and_the_page_reads_its_data_once()
    {
        using StudioComponentContext context = await NewContextAsync();

        IRenderedComponent<StudioLayout> layout = RenderOver<Streams>(context, "Pacific/Kiritimati");

        layout.WaitForAssertion(() => CreatedCell(layout).Should().Be(Spelt("Pacific/Kiritimati")).And.Be("2026-09-01 22:00:00 +14:00"));
        context.EventData.StreamRequests.Should().ContainSingle("the circuit's start is one load of the page, not one for the zone as well");
    }

    [Fact]
    public async Task A_zone_picked_in_Preferences_draws_the_page_again_and_reads_nothing()
    {
        using StudioComponentContext context = await NewContextAsync();
        IRenderedComponent<StudioLayout> layout = RenderOver<Streams>(context, "Pacific/Kiritimati");
        layout.WaitForAssertion(() => CreatedCell(layout).Should().Be("2026-09-01 22:00:00 +14:00"));

        string picked = PickAnotherZone(layout);

        layout.WaitForAssertion(() => CreatedCell(layout).Should().Be(Spelt(picked)));
        CreatedCell(layout).Should().NotBe("2026-09-01 22:00:00 +14:00");
        context.EventData.StreamRequests.Should().ContainSingle("a zone is not a scope: the data on screen is spelt again and nothing is read");
    }

    /// <summary>
    /// A page in the middle of a load when the zone changes keeps that load - it neither loses it nor starts a second one - and draws what it read in the zone
    /// the visitor now has.
    /// </summary>
    [Fact]
    public async Task A_zone_change_in_the_middle_of_a_load_neither_loses_the_load_nor_starts_another()
    {
        using StudioComponentContext context = await NewContextAsync();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.EventData.StreamListGate = gate;

        IRenderedComponent<StudioLayout> layout = RenderOver<Streams>(context, "Pacific/Kiritimati");
        layout.WaitForAssertion(() => context.EventData.StreamRequests.Should().ContainSingle("the load has started"));
        layout.FindAll(".ms-stream-created").Should().BeEmpty("and is still held");

        string picked = PickAnotherZone(layout);
        layout.WaitForAssertion(() => context.Preferences.SelectedTimeZoneId.Should().Be(picked));

        gate.SetResult();

        layout.WaitForAssertion(() => CreatedCell(layout).Should().Be(Spelt(picked)));
        context.EventData.StreamRequests.Should().ContainSingle("the load that was in flight is the one that drew, and no second was started");
    }

    /// <summary>The rail's health badge says when it was measured, in the visitor's zone: a tooltip, spelt again from the glance it was read with.</summary>
    [Fact]
    public async Task The_health_badges_time_follows_the_zone_and_the_rail_reads_nothing()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.NavBadges.Health = new HealthGlance(Created, 1, 0, [new HealthGlanceItem("default|db|D01", "Pinned")]);

        IRenderedComponent<StudioLayout> layout = RenderOver<Streams>(context, "Pacific/Kiritimati");

        string Label() => layout.FindAll("a.ms-nav-link")
            .Single(static x => x.GetAttribute("href")?.Split('?')[0] == "postgres/health")
            .QuerySelector(".ms-nav-badge")!.GetAttribute("aria-label")!;

        layout.WaitForAssertion(() => Label().Should().EndWith(", as of 22:00"));
        int reads = context.NavBadges.Reads;

        string picked = PickAnotherZone(layout);

        string expected = TimeZoneInfo.ConvertTime(Created, TimeZoneInfo.FindSystemTimeZoneById(picked)).ToString("HH:mm", CultureInfo.InvariantCulture);
        layout.WaitForAssertion(() => Label().Should().EndWith(", as of " + expected));
        context.NavBadges.Reads.Should().Be(reads, "the badge is spelt again from the glance it holds");
    }

    /// <summary>The Activity page, whose own subscription to the preferences is gone: it takes the zone as every page does, and its ring is read once.</summary>
    [Fact]
    public async Task The_activity_page_follows_the_zone_through_the_same_value()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.AuthenticationState.SignIn("ops");
        context.ActionLog.Record("DeleteDocument", "customer/42", succeeded: true, "done");

        IRenderedComponent<StudioLayout> layout = RenderOver<DataStudio.Components.Pages.Activity>(context, "Pacific/Kiritimati");

        layout.WaitForAssertion(() => layout.Find("tbody tr td").TextContent.Trim().Should().EndWith("+14:00"));

        PickAnotherZone(layout);

        layout.WaitForAssertion(() => layout.Find("tbody tr td").TextContent.Trim().Should().NotEndWith("+14:00"));
    }
}
