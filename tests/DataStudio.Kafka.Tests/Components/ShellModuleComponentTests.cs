using System.Reflection;

using AngleSharp.Dom;

using Bunit;

using DataStudio.Components.Layout;
using DataStudio.Kafka.Components.Shared;
using DataStudio.Kafka.Services;
using DataStudio.Kafka.Tests.Support;
using DataStudio.Services.Settings;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Kafka.Tests.Components;

/// <summary>D34 and D35 in the shell's own components: the rail, the capability chip and banner, and the page frame's gate.</summary>
public class ShellModuleComponentTests
{
    /// <summary>A body that counts how often it was created, so a test can say whether a refused cluster ever started one.</summary>
    private sealed class Counter
    {
        public int Initialized { get; set; }
    }

    private sealed class Probe : ComponentBase
    {
        [Parameter]
        public Counter? Count { get; set; }

        protected override void OnInitialized() => Count!.Initialized++;
    }

    /// <summary>
    /// A page body that reads, as every page's first read does, and says whether the frame had not yet released reads at that moment. The naive one
    /// reads in <c>OnInitializedAsync</c>; the other awaits the frame's cascaded <c>Ready</c> first (D-SHELL 5.5).
    /// </summary>
    private sealed class ReadingBody : ComponentBase
    {
        [CascadingParameter(Name = KafkaPageFrame.ReadyName)]
        public Task? Ready { get; set; }

        [Parameter]
        public bool WaitForReady { get; set; }

        [Parameter]
        public List<bool>? Reads { get; set; }

        protected override async Task OnInitializedAsync()
        {
            if (WaitForReady)
            {
                await Ready!;
            }

            // True: the read started while the frame had not released reads, which is what a prerender is.
            Reads!.Add(!Ready!.IsCompleted);
        }
    }

    private static RenderFragment ReadingBodyFragment(bool waitForReady, List<bool> reads) => builder =>
    {
        builder.OpenComponent<ReadingBody>(0);
        builder.AddAttribute(1, nameof(ReadingBody.WaitForReady), waitForReady);
        builder.AddAttribute(2, nameof(ReadingBody.Reads), reads);
        builder.CloseComponent();
    };

    private static RenderFragment ProbeBody(Counter counter) => builder =>
    {
        builder.OpenComponent<Probe>(0);
        builder.AddAttribute(1, nameof(Probe.Count), counter);
        builder.CloseComponent();
    };

    // ---- the rail

    [Fact]
    public async Task Modules_are_drawn_in_registration_order_and_the_shells_own_section_closes_the_rail()
    {
        using KafkaHarness harness = new(null, null, [], withHostModule: true);
        await harness.Context.ReadyAsync();

        IRenderedComponent<NavMenu> rail = harness.Context.Render<NavMenu>();

        string[] sections = [.. rail.FindAll(".ms-sidebar-section").Select(static x => x.TextContent.Trim())];
        sections.Should().Equal("Host", "Kafka", "Studio");
        rail.FindAll(".ms-nav-link-text").Select(static x => x.TextContent.Trim()).Should().Contain(["Topics", "Cluster settings"]).And.NotContain("Search", "the capability is off");
    }

    [Fact]
    public async Task Search_is_on_the_rail_only_while_the_capability_is_effectively_on()
    {
        using KafkaHarness off = new();
        using KafkaHarness on = new(static o => o.Capabilities = KafkaCapabilities.All());
        using KafkaHarness readOnly = new(static o => o.Capabilities = KafkaCapabilities.All());
        readOnly.Context.ShellOptions.ReadOnly = true;
        await off.Context.ReadyAsync();
        await on.Context.ReadyAsync();
        await readOnly.Context.ReadyAsync();

        static string[] Links(KafkaHarness harness) =>
            [.. harness.Context.Render<NavMenu>().FindAll(".ms-nav-link-text").Select(static x => x.TextContent.Trim())];

        Links(off).Should().NotContain("Search");
        Links(on).Should().Contain("Search");
        Links(readOnly).Should().NotContain("Search", "the shell's ReadOnly turns every capability off");
    }

    [Fact]
    public async Task Live_tail_is_on_the_rail_only_while_it_and_Search_are_effectively_on()
    {
        using KafkaHarness off = new();
        using KafkaHarness searchOnly = new(static o => o.Capabilities.SearchRecords = true);
        using KafkaHarness on = new(static o => o.Capabilities = KafkaCapabilities.All());
        using KafkaHarness readOnly = new(static o => o.Capabilities = KafkaCapabilities.All());
        readOnly.Context.ShellOptions.ReadOnly = true;
        await off.Context.ReadyAsync();
        await searchOnly.Context.ReadyAsync();
        await on.Context.ReadyAsync();
        await readOnly.Context.ReadyAsync();

        static string[] Links(KafkaHarness harness) =>
            [.. harness.Context.Render<NavMenu>().FindAll(".ms-nav-link-text").Select(static x => x.TextContent.Trim())];

        Links(off).Should().NotContain("Live tail");
        Links(searchOnly).Should().Contain("Search").And.NotContain("Live tail", "a capability that is off leaves its entry out (#74)");
        Links(on).Should().Contain("Live tail");
        Links(readOnly).Should().NotContain("Live tail");
    }

    [Fact]
    public async Task A_kafka_link_carries_no_scope_of_another_module()
    {
        using KafkaHarness harness = new(null, null, [], withHostModule: true);
        await harness.Context.ReadyAsync();

        IRenderedComponent<NavMenu> rail = harness.Context.Render<NavMenu>();

        string kafka = rail.FindAll("a.ms-nav-link").Single(static x => x.TextContent.Trim() == "Topics").GetAttribute("href")!;
        kafka.Should().EndWith("kafka").And.NotContain("store=").And.NotContain("db=").And.NotContain("tenant=");

        string host = rail.FindAll("a.ms-nav-link").Single(static x => x.TextContent.Trim() == "Host page").GetAttribute("href")!;
        host.Should().Be(FakeHostModule.PageAddress, "a module with no rail of its own has its links drawn undecorated; what the database module's rail adds is the Postgres suite's");

        // The shell's own Activity entry belongs to no module, so it carries no scope of either.
        rail.FindAll("a.ms-nav-link").Single(static x => x.TextContent.Trim() == "Activity").GetAttribute("href").Should().Be("activity");
    }

    [Fact]
    public async Task The_rail_brings_its_active_item_into_view_inside_its_own_scroll_box_after_each_navigation_and_only_then()
    {
        using KafkaHarness harness = new(static o => o.Capabilities = KafkaCapabilities.All());
        await harness.Context.ReadyAsync();
        harness.Navigation.Simulate("studio/kafka", intercepted: false);

        IRenderedComponent<NavMenu> rail = harness.Context.Render<NavMenu>();

        IReadOnlyList<Bunit.JSRuntimeInvocation> Reveals() => harness.Context.JSInterop.Invocations["martenStudio.scroll.revealWithin"];

        rail.WaitForAssertion(() => Reveals().Should().ContainSingle("the first load reveals what the page marks"));
        Reveals()[0].Arguments[0].Should().BeOfType<ElementReference>("the rail's own scroll box, not the page, is what is scrolled");
        Reveals()[0].Arguments[1].Should().Be(NavMenu.ActiveItemSelector);
        rail.Find(NavMenu.ActiveItemSelector).TextContent.Trim().Should().Be("Topics", "the selector finds the entry the page marks");

        // A render that is not a navigation (a badge poll, a scope change) must not move a rail the visitor has scrolled.
        rail.Render();
        Reveals().Should().ContainSingle();

        harness.Navigation.Simulate("studio/kafka/search", intercepted: true);

        rail.WaitForAssertion(() => Reveals().Should().HaveCount(2, "a navigation brings the new active entry into view"));
        rail.Find(NavMenu.ActiveItemSelector).TextContent.Trim().Should().Be("Search");
    }

    /// <summary>A module's rail, as the database's is one: the entries it hides (they come later, once its scope is known) and the badges it wears.</summary>
    private sealed class FakeHostRail : IStudioModuleRail
    {
        public string ModuleKey => FakeHostModule.ModuleKey;

        public HashSet<string> Hidden { get; } = [];

        public Dictionary<string, StudioNavBadge> Badges { get; } = [];

        /// <summary>What the module's links carry after their address, as the database's carry the scope.</summary>
        public string Scope { get; set; } = string.Empty;

        /// <summary>An entry that is hidden the first time the rail asks and shown every time after: the state moving between a render and what is read after it.</summary>
        public string? HiddenOnlyFirstTime { get; set; }

        private bool askedAbout;

        public string Decorate(string href) => href + Scope;

        public bool IsShown(StudioNavItem item)
        {
            if (item.Label == HiddenOnlyFirstTime)
            {
                bool first = !askedAbout;
                askedAbout = true;
                return !first;
            }

            return !Hidden.Contains(item.Label);
        }

        public event Action? LinksChanged
        {
            add { }
            remove { }
        }

        public event Func<Task>? Changed;

        public Task ChangedAsync() => Changed is { } handler ? handler() : Task.CompletedTask;

        public ValueTask StartAsync(Microsoft.JSInterop.IJSRuntime js, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyDictionary<string, StudioNavBadge>?> ReadBadgesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<string, StudioNavBadge>?>(new Dictionary<string, StudioNavBadge>(Badges));
    }

    [Fact]
    public async Task An_entry_that_appears_after_the_first_reveal_brings_the_active_item_into_view_again()
    {
        FakeHostRail module = new();
        module.Hidden.Add("Host page");
        using KafkaHarness harness = new(null, services => services.AddSingleton<IStudioModuleRail>(module), [], withHostModule: true);
        await harness.Context.ReadyAsync();
        harness.Navigation.Simulate("studio/kafka", intercepted: false);

        IRenderedComponent<NavMenu> rail = harness.Context.Render<NavMenu>();

        IReadOnlyList<Bunit.JSRuntimeInvocation> Reveals() => harness.Context.JSInterop.Invocations["martenStudio.scroll.revealWithin"];

        rail.WaitForAssertion(() => Reveals().Should().ContainSingle());
        rail.FindAll("a.ms-nav-link").Should().NotContain(static x => x.TextContent.Trim() == "Host page");

        // The module's scope becomes known and its entry is drawn above the active one: every row below it moves down.
        module.Hidden.Clear();
        await module.ChangedAsync();

        rail.WaitForAssertion(() => rail.FindAll("a.ms-nav-link").Should().Contain(static x => x.TextContent.Trim() == "Host page"));
        rail.WaitForAssertion(() => Reveals().Should().HaveCount(2, "the entries drawn changed, so the active one is brought into view again"));

        // Nothing else changed: another render reveals nothing.
        rail.Render();
        Reveals().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_scope_that_changes_only_the_links_does_not_bring_the_active_item_into_view_again()
    {
        FakeHostRail module = new();
        using KafkaHarness harness = new(null, services => services.AddSingleton<IStudioModuleRail>(module), [], withHostModule: true);
        await harness.Context.ReadyAsync();
        harness.Navigation.Simulate("studio/kafka", intercepted: false);

        IRenderedComponent<NavMenu> rail = harness.Context.Render<NavMenu>();

        IReadOnlyList<Bunit.JSRuntimeInvocation> Reveals() => harness.Context.JSInterop.Invocations["martenStudio.scroll.revealWithin"];

        rail.WaitForAssertion(() => Reveals().Should().ContainSingle());

        // A scope switch changes every address of the module's entries and moves no row: a visitor who has scrolled the rail away from the active
        // entry to read another group is not scrolled back by it.
        module.Scope = "?db=other";
        await module.ChangedAsync();

        rail.WaitForAssertion(() => rail.FindAll("a.ms-nav-link").Where(static x => x.GetAttribute("href")!.EndsWith("?db=other", StringComparison.Ordinal)).Should().NotBeEmpty());
        rail.Render();
        Reveals().Should().ContainSingle("an address moves no row");
    }

    [Fact]
    public async Task An_entry_drawn_by_a_render_is_judged_by_what_that_render_drew_and_not_by_what_the_state_says_after_it()
    {
        // The rail's answer for the entry moves between the render and what is read after it (the scope's facts are written on the thread pool): the first
        // render draws no Host page entry, so the second, which does, is a change in what is drawn and reveals again.
        FakeHostRail module = new() { HiddenOnlyFirstTime = "Host page" };
        using KafkaHarness harness = new(null, services => services.AddSingleton<IStudioModuleRail>(module), [], withHostModule: true);
        await harness.Context.ReadyAsync();
        harness.Navigation.Simulate("studio/kafka", intercepted: false);

        IRenderedComponent<NavMenu> rail = harness.Context.Render<NavMenu>();

        IReadOnlyList<Bunit.JSRuntimeInvocation> Reveals() => harness.Context.JSInterop.Invocations["martenStudio.scroll.revealWithin"];

        rail.WaitForAssertion(() => Reveals().Should().ContainSingle());
        rail.FindAll("a.ms-nav-link").Should().NotContain(static x => x.TextContent.Trim() == "Host page", "the first render drew it hidden");

        await module.ChangedAsync();

        rail.WaitForAssertion(() => rail.FindAll("a.ms-nav-link").Should().Contain(static x => x.TextContent.Trim() == "Host page"));
        rail.WaitForAssertion(() => Reveals().Should().HaveCount(2, "the second render drew an entry the first did not"));
    }

    [Fact]
    public async Task A_badge_that_appears_does_not_bring_the_active_item_into_view_again()
    {
        FakeHostRail module = new();
        using KafkaHarness harness = new(null, services => services.AddSingleton<IStudioModuleRail>(module), [], withHostModule: true);
        await harness.Context.ReadyAsync();
        harness.Navigation.Simulate("studio/kafka", intercepted: false);

        IRenderedComponent<NavMenu> rail = harness.Context.Render<NavMenu>();

        IReadOnlyList<Bunit.JSRuntimeInvocation> Reveals() => harness.Context.JSInterop.Invocations["martenStudio.scroll.revealWithin"];

        rail.WaitForAssertion(() => Reveals().Should().ContainSingle());

        module.Badges[FakeHostModule.BadgeKey] = new StudioNavBadge("3", "ms-nav-badge-danger", "3 findings");
        await module.ChangedAsync();

        rail.WaitForAssertion(() => rail.Find(".ms-nav-badge").TextContent.Trim().Should().Be("3"));

        // A render on the renderer's own thread settles whatever the badge's render left to do before the count is read.
        rail.Render();
        Reveals().Should().ContainSingle("a badge changes no row of the rail");
    }

    [Fact]
    public async Task A_rail_whose_browser_went_away_while_revealing_is_still_drawn()
    {
        using KafkaHarness harness = new();
        await harness.Context.ReadyAsync();
        harness.Navigation.Simulate("studio/kafka", intercepted: false);
        harness.Context.JSInterop.Setup<bool>("martenStudio.scroll.revealWithin", _ => true)
            .SetException(new Microsoft.JSInterop.JSDisconnectedException("the tab closed"));

        IRenderedComponent<NavMenu> rail = harness.Context.Render<NavMenu>();

        rail.WaitForAssertion(() => harness.Context.JSInterop.Invocations["martenStudio.scroll.revealWithin"].Should().ContainSingle());
        rail.Find(NavMenu.ActiveItemSelector).TextContent.Trim().Should().Be("Topics");
    }

    [Fact]
    public async Task The_preferences_time_zone_select_lists_zones_by_IANA_id_with_their_offset()
    {
        using KafkaHarness harness = new();
        await harness.Context.ReadyAsync();

        // A zone the menu itself lists, taken from what it draws: which ids a host lists depends on its operating system, its region and (on
        // Linux, where a zone such as Etc/UTC is listed only if something looked it up before the list was first built) on what ran earlier in
        // the process, so the test never names the machine's own zone.
        IRenderedComponent<PreferencesMenu> first = harness.Context.Render<PreferencesMenu>(p => p.Add(x => x.SelectedTimeZoneId, "UTC"));
        string own = first.FindAll("#ms-timezone-select option").Select(static x => x.GetAttribute("value")!).First(static x => x.Contains('/', StringComparison.Ordinal));

        IRenderedComponent<PreferencesMenu> menu = harness.Context.Render<PreferencesMenu>(p => p.Add(x => x.SelectedTimeZoneId, own));

        IElement listed = menu.FindAll("#ms-timezone-select option").Single(x => x.GetAttribute("value") == own);
        listed.TextContent.Trim().Should().MatchRegex("^" + System.Text.RegularExpressions.Regex.Escape(own) + @"( \(UTC([+-]\d\d:\d\d)?\))?$", "the id, and the offset beside it unless the id is UTC itself");
        listed.HasAttribute("selected").Should().BeTrue();

        string[] values = [.. menu.FindAll("#ms-timezone-select option").Select(static x => x.GetAttribute("value")!)];
        values.Should().OnlyHaveUniqueItems().And.Contain("UTC");
        values.Where(static x => x.EndsWith("Standard Time", StringComparison.Ordinal) && TimeZoneInfo.TryConvertWindowsIdToIanaId(x, out string? _)).Should().BeEmpty(
            "a zone whose Windows id converts is listed by the IANA id it converts to");
        menu.FindAll("#ms-timezone-select option").Should().NotContain(static x => x.TextContent.Contains("(local)", StringComparison.Ordinal));

        // A Windows id the circuit holds (the host's own zone, or one stored before) selects the IANA id it is listed under, on a host that
        // lists that id (a Linux host's zone list may hold the zone under another of its names).
        string converted = DataStudio.Services.StudioTimeZones.IanaId("FLE Standard Time", DataStudio.Services.StudioTimeZones.CurrentRegion);

        if (values.Contains(converted))
        {
            IRenderedComponent<PreferencesMenu> stored = harness.Context.Render<PreferencesMenu>(p => p.Add(x => x.SelectedTimeZoneId, "FLE Standard Time"));
            stored.FindAll("#ms-timezone-select option").Where(static x => x.HasAttribute("selected")).Select(static x => x.GetAttribute("value"))
                .Should().ContainSingle().Which.Should().Be(converted);
        }
    }

    [Fact]
    public async Task The_rail_credit_names_the_module()
    {
        using KafkaHarness harness = new();
        await harness.Context.ReadyAsync();

        IRenderedComponent<NavMenu> rail = harness.Context.Render<NavMenu>();

        string[] lines = [.. rail.Find(".ms-rail-credit").QuerySelectorAll(".ms-rail-credit-line").Select(static x => x.TextContent.Trim())];
        lines.Should().HaveCount(4, "a Kafka-only host credits the shell and the one module it has");
        lines[0].Should().StartWith("Data Studio ");
        lines[1].Should().Be("Kafka");
        lines[2].Should().Be("MIT licensed");
        lines[3].Should().Be("by Marko Lahma");
    }

    // ---- the chip and the banner

    [Fact]
    public async Task The_chip_counts_the_kafka_capabilities()
    {
        using KafkaHarness off = new();
        using KafkaHarness on = new(static o => o.Capabilities = KafkaCapabilities.All());
        await off.Context.ReadyAsync();
        await on.Context.ReadyAsync();

        string chipOff = off.Context.Render<CapabilityBanner>().Find(".ms-chip-button").TextContent.Trim();
        string chipOn = on.Context.Render<CapabilityBanner>().Find(".ms-chip-button").TextContent.Trim();

        // The Kafka module offers four capabilities: SearchRecords, ReadPayloads, ExportRecords and LiveTail; a Kafka-only host offers no others.
        chipOff.Should().StartWith("0 / 4");
        chipOn.Should().StartWith("4 / 4");
    }

    [Fact]
    public async Task The_banner_lists_the_kafka_group_with_its_options()
    {
        using KafkaHarness harness = new(static o => o.Capabilities = KafkaCapabilities.All());
        await harness.Context.ReadyAsync();

        IRenderedComponent<CapabilityBanner> banner = harness.Context.Render<CapabilityBanner>();

        IElement group = banner.Find("ul[aria-label='Kafka: reads']");
        IElement[] rows = [.. group.QuerySelectorAll("li")];
        rows.Should().HaveCount(4);
        rows.Select(static x => x.QuerySelector(".ms-capability-name")!.TextContent).Should().Equal("SearchRecords", "ReadPayloads", "ExportRecords", "LiveTail");
        rows.Should().OnlyContain(static x => x.ClassList.Contains("ms-capability-on"));
        rows[0].QuerySelector("code")!.TextContent.Should().Be("KafkaOptions.Capabilities.SearchRecords");
        rows[1].QuerySelector("code")!.TextContent.Should().Be("KafkaOptions.Capabilities.ReadPayloads");
        rows[2].QuerySelector("code")!.TextContent.Should().Be("KafkaOptions.Capabilities.ExportRecords");
        rows[3].QuerySelector("code")!.TextContent.Should().Be("KafkaOptions.Capabilities.LiveTail");
    }

    [Fact]
    public async Task A_kafka_capability_that_is_off_is_listed_off()
    {
        using KafkaHarness harness = new();
        await harness.Context.ReadyAsync();

        IRenderedComponent<CapabilityBanner> banner = harness.Context.Render<CapabilityBanner>();

        banner.FindAll("ul[aria-label='Kafka: reads'] li").Should().OnlyContain(static x => x.ClassList.Contains("ms-capability-off"));
    }

    [Fact]
    public async Task ReadOnly_counts_the_kafka_capabilities_off()
    {
        using KafkaHarness harness = new(static o => o.Capabilities = KafkaCapabilities.All());
        harness.Context.ShellOptions.ReadOnly = true;
        await harness.Context.ReadyAsync();

        IRenderedComponent<CapabilityBanner> banner = harness.Context.Render<CapabilityBanner>();

        banner.Find(".ms-chip").TextContent.Trim().Should().Be("Read-only");
        banner.FindAll(".ms-chip-button").Should().BeEmpty();
    }

    // ---- the page frame

    [Fact]
    public void A_granted_cluster_runs_the_body()
    {
        using KafkaHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");
        Counter counter = new();

        IRenderedComponent<KafkaPageFrame> frame = harness.Context.Render<KafkaPageFrame>(p => p.Add(x => x.ChildContent, ProbeBody(counter)));

        frame.WaitForAssertion(() => counter.Initialized.Should().Be(1));
        frame.Find(".ms-k-trademark").TextContent.Should().Be(KafkaText.Trademark);
    }

    [Fact]
    public void A_body_is_created_only_once_the_frame_has_released_reads_so_neither_way_of_reading_runs_during_a_prerender()
    {
        using KafkaHarness harness = new();
        List<bool> naive = [];
        List<bool> awaiting = [];

        // The frame releases reads from its first after-render, which a prerender never has, and creates no body before it. So a body that reads
        // in OnInitializedAsync does not read during a prerender either, and one that awaits the cascaded Ready is released at the same moment: the
        // list holds "the read began before Ready was complete", which is what a read during a prerender would be.
        IRenderedComponent<KafkaPageFrame> frame = harness.Context.Render<KafkaPageFrame>(p => p.Add(x => x.ChildContent, ReadingBodyFragment(waitForReady: false, naive)));
        frame.WaitForAssertion(() => naive.Should().ContainSingle());
        naive.Should().Equal(false);

        IRenderedComponent<KafkaPageFrame> other = harness.Context.Render<KafkaPageFrame>(p => p.Add(x => x.ChildContent, ReadingBodyFragment(waitForReady: true, awaiting)));
        other.WaitForAssertion(() => awaiting.Should().ContainSingle());
        awaiting.Should().Equal(false);
    }

    [Fact]
    public void A_frame_with_no_cluster_never_releases_Ready_and_creates_no_body()
    {
        using KafkaHarness harness = new();
        harness.Entries.Entries = [];
        List<bool> reads = [];

        // No cluster: the policy never passes, so Ready is never released, and the body is never created. Nothing reads, however long it waits.
        IRenderedComponent<KafkaPageFrame> frame = harness.Context.Render<KafkaPageFrame>(p => p.Add(x => x.ChildContent, ReadingBodyFragment(waitForReady: true, reads)));
        frame.WaitForAssertion(() => frame.Find(".ms-empty-title").TextContent.Should().Be("No cluster"));

        reads.Should().BeEmpty();
    }

    [Fact]
    public async Task The_frame_applies_the_cluster_its_address_names_on_its_first_render()
    {
        using KafkaHarness harness = new(null, KafkaHarness.Primary, KafkaHarness.Secondary);
        await harness.Context.ReadyAsync();
        await harness.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);
        harness.State.ActiveCluster.Should().Be(KafkaHarness.Primary, "the premise: the state first loaded from an address that named none");

        // Arriving from a host page, or by Back: the state is the circuit's, so it still holds the cluster it last had.
        harness.Context.Navigate("/studio/kafka?cluster=secondary");
        harness.Context.Render<KafkaPageFrame>(p => p.Add(x => x.ChildContent, ProbeBody(new Counter())));

        harness.State.ActiveCluster.Should().Be(KafkaHarness.Secondary);
    }

    [Fact]
    public async Task The_frame_ignores_an_address_cluster_the_listing_does_not_carry()
    {
        using KafkaHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics", KafkaHarness.Primary, KafkaHarness.Secondary);
        harness.Authorization.AllowKafka(static r => r.Cluster != "secondary");
        await harness.Context.ReadyAsync();
        await harness.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);

        harness.Context.Navigate("/studio/kafka?cluster=secondary");
        harness.Context.Render<KafkaPageFrame>(p => p.Add(x => x.ChildContent, ProbeBody(new Counter())));

        harness.State.ActiveCluster.Should().Be(KafkaHarness.Primary, "a refused cluster reads as an unknown one");
    }

    [Fact]
    public void A_refused_cluster_never_initializes_the_body()
    {
        using KafkaHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");

        // The cluster passes the listing (the state's one question) and is refused by the frame's: a policy that changed its mind, or one that
        // answers differently when asked again. The body of a refused cluster must not start either way.
        int asked = 0;
        harness.Authorization.AllowKafka(_ => Interlocked.Increment(ref asked) == 1);
        Counter counter = new();

        IRenderedComponent<KafkaPageFrame> frame = harness.Context.Render<KafkaPageFrame>(p => p.Add(x => x.ChildContent, ProbeBody(counter)));

        frame.WaitForAssertion(() => frame.Find(".ms-empty-title").TextContent.Should().Be("Not authorized"));
        counter.Initialized.Should().Be(0);
        frame.Markup.Should().NotContain("Primary", "a refused cluster is not named");
        frame.Find(".ms-k-trademark").Should().NotBeNull();
    }

    [Fact]
    public void A_visitor_who_may_see_no_cluster_is_told_there_is_none()
    {
        using KafkaHarness harness = new();
        harness.Entries.Entries = [];
        Counter counter = new();

        IRenderedComponent<KafkaPageFrame> frame = harness.Context.Render<KafkaPageFrame>(p => p.Add(x => x.ChildContent, ProbeBody(counter)));

        frame.WaitForAssertion(() => frame.Find(".ms-empty-title").TextContent.Should().Be("No cluster"));
        counter.Initialized.Should().Be(0);
    }

    [Fact]
    public void A_policy_that_throws_closes_the_page()
    {
        using KafkaHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");
        Counter counter = new();

        // The state loads the entries through the same policy, so make the first question fail by emptying the entries under a policy that throws.
        harness.Authorization.AllowKafka(static _ => throw new InvalidOperationException("the host's handler failed"));

        IRenderedComponent<KafkaPageFrame> frame = harness.Context.Render<KafkaPageFrame>(p => p.Add(x => x.ChildContent, ProbeBody(counter)));

        frame.WaitForAssertion(() => frame.Find(".ms-empty-title").TextContent.Should().Be("Not authorized"));
        counter.Initialized.Should().Be(0);
    }

    // ---- the disabled-capability notice

    [Fact]
    public async Task The_disabled_notice_is_one_neutral_sentence_whatever_turned_the_capability_off()
    {
        using KafkaHarness harness = new();
        await harness.Context.ReadyAsync();

        IRenderedComponent<KafkaCapabilityDisabled> off = harness.Context.Render<KafkaCapabilityDisabled>(
            p => p.Add(x => x.Capability, nameof(KafkaCapabilities.SearchRecords)).Add(x => x.Action, "Searching records"));

        off.Markup.Should().Contain("Searching records is not available here.").And.NotContain("KafkaOptions");

        harness.Context.ShellOptions.ReadOnly = true;
        IRenderedComponent<KafkaCapabilityDisabled> readOnly = harness.Context.Render<KafkaCapabilityDisabled>(
            p => p.Add(x => x.Capability, nameof(KafkaCapabilities.SearchRecords)));

        readOnly.Markup.Should().Contain("is not available here.").And.NotContain("ReadOnly").And.NotContain("Options");
    }

    // ---- every page ends with the trademark line

    [Fact]
    public async Task Every_kafka_page_renders_the_trademark_line()
    {
        Type[] pages =
        [
            .. typeof(KafkaOptions).Assembly.GetTypes()
                .Where(static x => typeof(IComponent).IsAssignableFrom(x) && x.GetCustomAttributes<RouteAttribute>().Any()),
        ];
        pages.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        foreach (Type page in pages)
        {
            using KafkaHarness harness = new();
            await harness.Context.ReadyAsync();

            IRenderedComponent<IComponent> rendered = harness.Context.Render<DynamicComponent>(p => p.Add(x => x.Type, page));

            rendered.WaitForAssertion(
                () => rendered.Find(".ms-k-trademark").TextContent.Should().Contain("Apache Kafka is a trademark of the Apache Software Foundation"),
                TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task The_home_opens_the_first_cluster_the_visitor_may_see()
    {
        using KafkaHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics", KafkaHarness.Primary, KafkaHarness.Secondary);
        harness.Authorization.AllowKafka(static r => r.Cluster != "primary");
        await harness.Context.ReadyAsync();

        IRenderedComponent<DataStudio.Kafka.Components.Pages.KafkaHome> home = harness.Context.Render<DataStudio.Kafka.Components.Pages.KafkaHome>();

        home.WaitForAssertion(() => home.Find(".ms-k-cluster-strip .ms-k-cluster-name").TextContent.Trim().Should().Be("Secondary"));
        home.Markup.Should().NotContain("Primary", "a cluster the policy refuses is not on the page");
    }

    /// <summary>Anti-vacuity for the layout rule: a routed component of this assembly is a page, and none declares a layout of its own.</summary>
    [Fact]
    public void No_kafka_component_declares_a_layout()
    {
        Type[] components = [.. typeof(KafkaOptions).Assembly.GetTypes().Where(static x => typeof(IComponent).IsAssignableFrom(x))];

        components.Should().HaveCountGreaterThan(4);
        components.Where(static x => x.GetCustomAttribute<LayoutAttribute>() is not null).Should().BeEmpty(
            "StudioRoutes renders every page in the studio's layout; a layout of its own would leave the shell, its banner and its preferences behind");

        string[] razor = [.. Directory.EnumerateFiles(DataStudio.Marten.Tests.Conventions.RepositoryRoot.Combine("src", "DataStudio.Kafka"), "*.razor", SearchOption.AllDirectories)];
        razor.Should().NotBeEmpty();
        razor.Where(static x => File.ReadAllText(x).Contains("@layout", StringComparison.Ordinal)).Should().BeEmpty();
    }
}
