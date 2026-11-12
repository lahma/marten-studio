using System.Reflection;

using AngleSharp.Dom;

using Bunit;

using KStudio.Components.Shared;
using KStudio.Services;
using KStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components.Layout;
using PostgresStudio.Services;

namespace KStudio.Tests.Components;

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
    public async Task The_kafka_section_is_drawn_before_System()
    {
        using KafkaStudioHarness harness = new();
        await harness.Context.ReadyAsync();

        IRenderedComponent<NavMenu> rail = harness.Context.Render<NavMenu>();

        string[] sections = [.. rail.FindAll(".ms-sidebar-section").Select(static x => x.TextContent.Trim())];
        sections.Should().Equal("Data", "Database", "Operations", "Kafka", "System");
        rail.FindAll(".ms-nav-link-text").Select(static x => x.TextContent.Trim()).Should().Contain(["Topics", "Cluster settings"]).And.NotContain("Search", "the capability is off");
    }

    [Fact]
    public async Task Search_is_on_the_rail_only_while_the_capability_is_effectively_on()
    {
        using KafkaStudioHarness off = new();
        using KafkaStudioHarness on = new(static o => o.Capabilities = KafkaStudioCapabilities.All());
        using KafkaStudioHarness readOnly = new(static o => o.Capabilities = KafkaStudioCapabilities.All());
        readOnly.Context.Options.ReadOnly = true;
        await off.Context.ReadyAsync();
        await on.Context.ReadyAsync();
        await readOnly.Context.ReadyAsync();

        static string[] Links(KafkaStudioHarness harness) =>
            [.. harness.Context.Render<NavMenu>().FindAll(".ms-nav-link-text").Select(static x => x.TextContent.Trim())];

        Links(off).Should().NotContain("Search");
        Links(on).Should().Contain("Search");
        Links(readOnly).Should().NotContain("Search", "the host studio's ReadOnly turns every capability off");
    }

    [Fact]
    public async Task A_kafka_link_carries_no_host_scope_and_a_host_link_still_does()
    {
        using KafkaStudioHarness harness = new();
        await harness.Context.ReadyAsync();

        IRenderedComponent<NavMenu> rail = harness.Context.Render<NavMenu>();

        string kafka = rail.FindAll("a.ms-nav-link").Single(static x => x.TextContent.Trim() == "Topics").GetAttribute("href")!;
        kafka.Should().EndWith("kafka").And.NotContain("store=").And.NotContain("db=").And.NotContain("tenant=");

        string host = rail.FindAll("a.ms-nav-link").First(static x => x.TextContent.Trim() == "Activity").GetAttribute("href")!;
        host.Should().Contain("store=default");
    }

    [Fact]
    public async Task The_rail_credit_names_the_module()
    {
        using KafkaStudioHarness harness = new();
        await harness.Context.ReadyAsync();

        IRenderedComponent<NavMenu> rail = harness.Context.Render<NavMenu>();

        string[] lines = [.. rail.Find(".ms-rail-credit").QuerySelectorAll(".ms-rail-credit-line").Select(static x => x.TextContent.Trim())];
        lines.Should().HaveCount(4);
        lines[0].Should().StartWith("Postgres Studio ");
        lines[1].Should().Be("KStudio for Apache Kafka®");
        lines[2].Should().Be("MIT licensed");
        lines[3].Should().Be("by Marko Lahma");
    }

    // ---- the chip and the banner

    [Fact]
    public async Task The_chip_counts_the_kafka_capabilities()
    {
        using KafkaStudioHarness off = new();
        using KafkaStudioHarness on = new(static o => o.Capabilities = KafkaStudioCapabilities.All());
        await off.Context.ReadyAsync();
        await on.Context.ReadyAsync();
        StudioCapabilityGuard host = off.Context.Services.GetRequiredService<StudioCapabilityGuard>();

        string chipOff = off.Context.Render<CapabilityBanner>().Find(".ms-chip-button").TextContent.Trim();
        string chipOn = on.Context.Render<CapabilityBanner>().Find(".ms-chip-button").TextContent.Trim();

        // KStudio offers two capabilities: SearchRecords and ReadPayloads.
        chipOff.Should().StartWith($"{host.EnabledCount} / {host.Offered.Count + 2}");
        chipOn.Should().StartWith($"{host.EnabledCount + 2} / {host.Offered.Count + 2}");
    }

    [Fact]
    public async Task The_banner_lists_the_kafka_group_with_its_options()
    {
        using KafkaStudioHarness harness = new(static o => o.Capabilities = KafkaStudioCapabilities.All());
        await harness.Context.ReadyAsync();

        IRenderedComponent<CapabilityBanner> banner = harness.Context.Render<CapabilityBanner>();

        IElement group = banner.Find("ul[aria-label='KStudio for Apache Kafka® reads']");
        IElement[] rows = [.. group.QuerySelectorAll("li")];
        rows.Should().HaveCount(2);
        rows.Select(static x => x.QuerySelector(".ms-capability-name")!.TextContent).Should().Equal("SearchRecords", "ReadPayloads");
        rows.Should().OnlyContain(static x => x.ClassList.Contains("ms-capability-on"));
        rows[0].QuerySelector("code")!.TextContent.Should().Be("KafkaStudioOptions.Capabilities.SearchRecords");
        rows[1].QuerySelector("code")!.TextContent.Should().Be("KafkaStudioOptions.Capabilities.ReadPayloads");
    }

    [Fact]
    public async Task A_kafka_capability_that_is_off_is_listed_off()
    {
        using KafkaStudioHarness harness = new();
        await harness.Context.ReadyAsync();

        IRenderedComponent<CapabilityBanner> banner = harness.Context.Render<CapabilityBanner>();

        banner.FindAll("ul[aria-label='KStudio for Apache Kafka® reads'] li").Should().OnlyContain(static x => x.ClassList.Contains("ms-capability-off"));
    }

    [Fact]
    public async Task ReadOnly_counts_the_kafka_capabilities_off()
    {
        using KafkaStudioHarness harness = new(static o => o.Capabilities = KafkaStudioCapabilities.All());
        harness.Context.Options.ReadOnly = true;
        await harness.Context.ReadyAsync();

        IRenderedComponent<CapabilityBanner> banner = harness.Context.Render<CapabilityBanner>();

        banner.Find(".ms-chip").TextContent.Trim().Should().Be("Read-only");
        banner.FindAll(".ms-chip-button").Should().BeEmpty();
    }

    // ---- the page frame

    [Fact]
    public void A_granted_cluster_runs_the_body()
    {
        using KafkaStudioHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");
        Counter counter = new();

        IRenderedComponent<KafkaPageFrame> frame = harness.Context.Render<KafkaPageFrame>(p => p.Add(x => x.ChildContent, ProbeBody(counter)));

        frame.WaitForAssertion(() => counter.Initialized.Should().Be(1));
        frame.Find(".ms-k-trademark").TextContent.Should().Be(KafkaStudioText.Trademark);
    }

    [Fact]
    public void A_body_is_created_only_once_the_frame_has_released_reads_so_neither_way_of_reading_runs_during_a_prerender()
    {
        using KafkaStudioHarness harness = new();
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
        using KafkaStudioHarness harness = new();
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
        using KafkaStudioHarness harness = new(null, KafkaStudioHarness.Primary, KafkaStudioHarness.Secondary);
        await harness.Context.ReadyAsync();
        await harness.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);
        harness.State.ActiveCluster.Should().Be(KafkaStudioHarness.Primary, "the premise: the state first loaded from an address that named none");

        // Arriving from a host page, or by Back: the state is the circuit's, so it still holds the cluster it last had.
        harness.Context.Navigate("/postgres/kafka?cluster=secondary");
        harness.Context.Render<KafkaPageFrame>(p => p.Add(x => x.ChildContent, ProbeBody(new Counter())));

        harness.State.ActiveCluster.Should().Be(KafkaStudioHarness.Secondary);
    }

    [Fact]
    public async Task The_frame_ignores_an_address_cluster_the_listing_does_not_carry()
    {
        using KafkaStudioHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics", KafkaStudioHarness.Primary, KafkaStudioHarness.Secondary);
        harness.Authorization.AllowKafka(static r => r.Cluster != "secondary");
        await harness.Context.ReadyAsync();
        await harness.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);

        harness.Context.Navigate("/postgres/kafka?cluster=secondary");
        harness.Context.Render<KafkaPageFrame>(p => p.Add(x => x.ChildContent, ProbeBody(new Counter())));

        harness.State.ActiveCluster.Should().Be(KafkaStudioHarness.Primary, "a refused cluster reads as an unknown one");
    }

    [Fact]
    public void A_refused_cluster_never_initializes_the_body()
    {
        using KafkaStudioHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");

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
        using KafkaStudioHarness harness = new();
        harness.Entries.Entries = [];
        Counter counter = new();

        IRenderedComponent<KafkaPageFrame> frame = harness.Context.Render<KafkaPageFrame>(p => p.Add(x => x.ChildContent, ProbeBody(counter)));

        frame.WaitForAssertion(() => frame.Find(".ms-empty-title").TextContent.Should().Be("No cluster"));
        counter.Initialized.Should().Be(0);
    }

    [Fact]
    public void A_policy_that_throws_closes_the_page()
    {
        using KafkaStudioHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");
        Counter counter = new();

        // The state loads the entries through the same policy, so make the first question fail by emptying the entries under a policy that throws.
        harness.Authorization.AllowKafka(static _ => throw new InvalidOperationException("the host's handler failed"));

        IRenderedComponent<KafkaPageFrame> frame = harness.Context.Render<KafkaPageFrame>(p => p.Add(x => x.ChildContent, ProbeBody(counter)));

        frame.WaitForAssertion(() => frame.Find(".ms-empty-title").TextContent.Should().Be("Not authorized"));
        counter.Initialized.Should().Be(0);
    }

    // ---- the disabled-capability notice

    [Fact]
    public async Task The_disabled_notice_names_the_option_or_the_hosts_ReadOnly()
    {
        using KafkaStudioHarness harness = new();
        await harness.Context.ReadyAsync();

        IRenderedComponent<KafkaCapabilityDisabled> off = harness.Context.Render<KafkaCapabilityDisabled>(
            p => p.Add(x => x.Capability, nameof(KafkaStudioCapabilities.SearchRecords)).Add(x => x.Action, "Searching records"));

        off.Markup.Should().Contain("Searching records is not enabled.").And.Contain("KafkaStudioOptions.Capabilities.SearchRecords");

        harness.Context.Options.ReadOnly = true;
        IRenderedComponent<KafkaCapabilityDisabled> readOnly = harness.Context.Render<KafkaCapabilityDisabled>(
            p => p.Add(x => x.Capability, nameof(KafkaStudioCapabilities.SearchRecords)));

        readOnly.Markup.Should().Contain("PostgresStudioOptions.ReadOnly");
    }

    // ---- every page ends with the trademark line

    [Fact]
    public async Task Every_kafka_page_renders_the_trademark_line()
    {
        Type[] pages =
        [
            .. typeof(KafkaStudioOptions).Assembly.GetTypes()
                .Where(static x => typeof(IComponent).IsAssignableFrom(x) && x.GetCustomAttributes<RouteAttribute>().Any()),
        ];
        pages.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        foreach (Type page in pages)
        {
            using KafkaStudioHarness harness = new();
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
        using KafkaStudioHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics", KafkaStudioHarness.Primary, KafkaStudioHarness.Secondary);
        harness.Authorization.AllowKafka(static r => r.Cluster != "primary");
        await harness.Context.ReadyAsync();

        IRenderedComponent<KStudio.Components.Pages.KafkaHome> home = harness.Context.Render<KStudio.Components.Pages.KafkaHome>();

        home.WaitForAssertion(() => home.Find(".ms-k-cluster-strip .ms-k-cluster-name").TextContent.Trim().Should().Be("Secondary"));
        home.Markup.Should().NotContain("Primary", "a cluster the policy refuses is not on the page");
    }

    /// <summary>Anti-vacuity for the layout rule: a routed component of this assembly is a page, and none declares a layout of its own.</summary>
    [Fact]
    public void No_kafka_component_declares_a_layout()
    {
        Type[] components = [.. typeof(KafkaStudioOptions).Assembly.GetTypes().Where(static x => typeof(IComponent).IsAssignableFrom(x))];

        components.Should().HaveCountGreaterThan(4);
        components.Where(static x => x.GetCustomAttribute<LayoutAttribute>() is not null).Should().BeEmpty(
            "StudioRoutes renders every page in the studio's layout; a layout of its own would leave the shell, its banner and its preferences behind");

        string[] razor = [.. Directory.EnumerateFiles(MartenStudio.Tests.Conventions.RepositoryRoot.Combine("src", "KStudio"), "*.razor", SearchOption.AllDirectories)];
        razor.Should().NotBeEmpty();
        razor.Where(static x => File.ReadAllText(x).Contains("@layout", StringComparison.Ordinal)).Should().BeEmpty();
    }
}
