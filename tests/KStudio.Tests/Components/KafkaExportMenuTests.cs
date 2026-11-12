using System.Text.RegularExpressions;

using AngleSharp.Dom;

using Bunit;

using KStudio.Components.Layout;
using KStudio.Components.Pages;
using KStudio.Services;
using KStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Web;

namespace KStudio.Tests.Components;

/// <summary>
/// The Export control on the search results and the record page, the profile on the Settings page and in the cluster selector (AGENTS.md D45 and D47,
/// D-M5 decisions 2.8, 2.9 and 4.6): the menu offers what the page holds, the click is what the service gates, the disabled states name the setting a
/// host would change, and there is no <c>data:</c> link anywhere in what KStudio draws.
/// </summary>
public class KafkaExportMenuTests
{
    private const string Window = "&topic=orders&from=2026-11-08T11%3A00%3A00Z&to=2026-11-08T13%3A00%3A00Z";

    private static KafkaSearchRig Rig(KafkaProfile? profile = null, bool exportOn = true, bool payloads = false) =>
        new(studio: o => o.Capabilities.ExportRecords = exportOn, payloads: payloads, profile: profile);

    private static void Seed(KafkaSearchRig rig, int count = 3)
    {
        rig.Topic("orders");

        for (int i = 0; i < count; i++)
        {
            rig.Produce("orders", 0, i, "k" + i, "{\"n\":" + i + "}", ("correlation-id", "c-" + i), ("authorization", "Bearer secret"));
        }
    }

    private static async Task<IRenderedComponent<KafkaSearchPage>> SearchedAsync(KafkaSearchRig rig)
    {
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/search?cluster=test" + Window, intercepted: false);
        IRenderedComponent<KafkaSearchPage> page = rig.Studio.Context.Render<KafkaSearchPage>();
        page.WaitForAssertion(() => page.FindAll("textarea.ms-k-predicate").Should().HaveCount(1));
        await page.Find(".ms-k-search-actions button").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => page.FindAll(".ms-k-result-banner").Should().HaveCount(1));
        return page;
    }

    private static IElement[] Items(IRenderedComponent<KafkaSearchPage> page) => [.. page.FindAll(".ms-k-export-popover .ms-menu-item")];

    // ---- the search results

    [Fact]
    public async Task A_search_result_offers_both_formats_with_the_as_shown_line()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);

        IRenderedComponent<KafkaSearchPage> page = await SearchedAsync(rig);

        IElement[] items = Items(page);
        items.Select(static x => x.QuerySelector(".ms-menu-item-label")!.TextContent).Should().Equal("JSON Lines (.jsonl)", "CSV (.csv)");
        items.Should().OnlyContain(static x => x.QuerySelector(".ms-menu-item-detail")!.TextContent == "3 records, as shown: masked parts stay masked");
        page.Find(".ms-k-export-menu button[popovertarget]").TextContent.Trim().Should().Be("Export");
    }

    [Fact]
    public async Task Picking_a_format_saves_a_file_through_the_circuit_and_records_it_once()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        rig.Studio.Context.JSInterop.Setup<bool>("martenStudio.download.stream", static _ => true).SetResult(true);
        IRenderedComponent<KafkaSearchPage> page = await SearchedAsync(rig);

        await Items(page)[0].ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.Find(".ms-k-export").TextContent.Should().Contain("Saved kstudio-test-orders-"));
        IReadOnlyList<object?> arguments = rig.Studio.Context.JSInterop.Invocations["martenStudio.download.stream"].Single().Arguments;
        arguments[0].Should().BeOfType<string>().Which.Should().StartWith("kstudio-test-orders-").And.EndWith("Z.jsonl");
        arguments[1].Should().Be("application/x-ndjson");
        arguments[2].Should().BeOfType<Microsoft.JSInterop.DotNetStreamReference>();
        rig.Ring.Where(static x => x.Action == "ExportRecords").Should().ContainSingle().Which.Succeeded.Should().BeTrue();

        // A re-render records nothing more.
        await page.InvokeAsync(() => { });
        rig.Ring.Count(static x => x.Action == "ExportRecords").Should().Be(1);
    }

    [Fact]
    public async Task A_file_the_browser_did_not_take_is_said_so_and_recorded_as_a_failure()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        rig.Studio.Context.JSInterop.Setup<bool>("martenStudio.download.stream", static _ => true).SetResult(false);
        IRenderedComponent<KafkaSearchPage> page = await SearchedAsync(rig);

        await Items(page)[1].ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.Find(".ms-k-export [role=alert]").TextContent.Should().Be(KafkaStudioText.ExportNotDelivered));
        rig.Ring.Where(static x => x.Action == "ExportRecords").Should().ContainSingle().Which.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task With_the_capability_off_the_menu_is_a_notice_naming_the_option()
    {
        await using KafkaSearchRig rig = Rig(exportOn: false);
        Seed(rig);

        IRenderedComponent<KafkaSearchPage> page = await SearchedAsync(rig);

        page.FindAll(".ms-k-export-menu").Should().BeEmpty();
        page.Find(".ms-k-export-off").TextContent.Should().Contain("KafkaStudioOptions.Capabilities.ExportRecords").And.Contain("Exporting records is not enabled");
    }

    [Fact]
    public async Task On_a_production_cluster_the_button_is_off_and_says_why_in_words_that_name_the_setting()
    {
        await using KafkaSearchRig rig = Rig(profile: KafkaProfile.Production);
        Seed(rig);

        IRenderedComponent<KafkaSearchPage> page = await SearchedAsync(rig);

        string expected = KafkaStudioText.ProfileForbids(KafkaStudioText.ExportingRecords, rig.Entry);
        IElement button = page.Find(".ms-k-export-forbidden button");
        button.HasAttribute("disabled").Should().BeTrue();
        button.GetAttribute("title").Should().Be(expected);
        page.Find(".ms-k-export-forbidden").TextContent.Should().Contain(expected);
        expected.Should().Contain("KafkaClusterBuilder.Profile").And.Contain("Production").And.Contain("even when they are granted");
        page.FindAll(".ms-k-export-popover").Should().BeEmpty();
        rig.Studio.Context.JSInterop.Invocations.Should().NotContain(static x => x.Identifier == "martenStudio.download.stream");
    }

    // ---- the record page

    [Fact]
    public async Task The_record_page_offers_this_record_and_its_value_and_JsonView_offers_no_download()
    {
        await using KafkaSearchRig rig = Rig(payloads: true);
        Seed(rig);
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/record?cluster=test&topic=orders&partition=0&offset=1", intercepted: false);

        IRenderedComponent<KafkaRecordPage> page = rig.Studio.Context.Render<KafkaRecordPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-k-export-popover .ms-menu-item").Should().HaveCount(2));
        page.FindAll(".ms-k-export-popover .ms-menu-item-label").Select(static x => x.TextContent).Should().Equal("This record (.jsonl)", "Value (.json)");
        page.FindAll("a[download]").Should().BeEmpty("JsonView's Download stays off: a data: link can be neither gated nor audited");
        Regex.IsMatch(page.Markup, @"(href|src)\s*=\s*""data:", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)).Should().BeFalse();
    }

    [Fact]
    public async Task The_record_page_saves_the_value_alone_with_the_record_audited_by_its_place()
    {
        await using KafkaSearchRig rig = Rig(payloads: true);
        Seed(rig);
        rig.Studio.Context.JSInterop.Setup<bool>("martenStudio.download.stream", static _ => true).SetResult(true);
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/record?cluster=test&topic=orders&partition=0&offset=1", intercepted: false);
        IRenderedComponent<KafkaRecordPage> page = rig.Studio.Context.Render<KafkaRecordPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-k-export-popover .ms-menu-item").Should().HaveCount(2));

        await page.FindAll(".ms-k-export-popover .ms-menu-item")[1].ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => rig.Studio.Context.JSInterop.Invocations["martenStudio.download.stream"].Should().ContainSingle());
        IReadOnlyList<object?> arguments = rig.Studio.Context.JSInterop.Invocations["martenStudio.download.stream"].Single().Arguments;
        arguments[0].Should().BeOfType<string>().Which.Should().Contain("-orders-0-1-").And.EndWith("Z.json");
        arguments[1].Should().Be("application/json");
        rig.Ring.Single(static x => x.Action == "ExportRecords").Target.Should().Be("export of orders/0/1");
    }

    [Fact]
    public async Task A_record_read_without_values_offers_no_value_item()
    {
        await using KafkaSearchRig rig = Rig(payloads: false);
        Seed(rig);
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/record?cluster=test&topic=orders&partition=0&offset=1", intercepted: false);

        IRenderedComponent<KafkaRecordPage> page = rig.Studio.Context.Render<KafkaRecordPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-k-export-popover .ms-menu-item").Should().HaveCount(1));
        page.Find(".ms-k-export-popover .ms-menu-item-label").TextContent.Should().Be("This record (.jsonl)");
    }

    [Fact]
    public async Task No_search_or_record_markup_carries_a_data_link()
    {
        await using KafkaSearchRig rig = Rig(payloads: true);
        Seed(rig);
        IRenderedComponent<KafkaSearchPage> search = await SearchedAsync(rig);

        Regex.IsMatch(search.Markup, @"(href|src)\s*=\s*[""']?data:", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)).Should().BeFalse();
        search.FindAll("a[download]").Should().BeEmpty();
    }

    // ---- Settings and the selector

    [Theory]
    [InlineData(null, "None: the host's limits.", true)]
    [InlineData(KafkaProfile.Development, "Development: the host's limits.", false)]
    [InlineData(KafkaProfile.Test, "Test: scans capped at 256 MiB, 5,000,000 records and 2 minutes, 10 MiB/s and 2 GiB an hour per visitor on this cluster.", false)]
    [InlineData(KafkaProfile.Production, "Production: scans capped at 64 MiB, 1,000,000 records and 1 minute, 5 MiB/s and 512 MiB an hour per visitor on this cluster; export and live tail are off.", false)]
    public async Task The_settings_page_shows_the_profile_and_the_limits_it_comes_to(KafkaProfile? profile, string expected, bool warns)
    {
        await using KafkaSearchRig rig = Rig(profile);
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/settings?cluster=test", intercepted: false);

        IRenderedComponent<KafkaSettingsPage> page = rig.Studio.Context.Render<KafkaSettingsPage>();

        page.WaitForAssertion(() => page.Find("p.ms-k-profile").TextContent.Should().Be(expected));
        page.Markup.Contains(KafkaStudioText.NoProfileWarning, StringComparison.Ordinal).Should().Be(warns);
    }

    [Fact]
    public async Task The_selector_draws_the_profile_chip_beside_the_cluster()
    {
        await using KafkaSearchRig rig = Rig(KafkaProfile.Production);
        await rig.Studio.Context.ReadyAsync();
        await rig.Studio.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);

        IRenderedComponent<ClusterSelector> selector = rig.Studio.Context.Render<ClusterSelector>();

        selector.WaitForAssertion(() => selector.Find(".ms-k-profile-chip").TextContent.Trim().Should().Be("Production profile"));
        selector.Find(".ms-k-profile-chip").ClassList.Should().Contain("ms-k-profile-production");
    }

    [Fact]
    public async Task A_cluster_without_a_profile_draws_no_chip()
    {
        await using KafkaSearchRig rig = Rig();
        await rig.Studio.Context.ReadyAsync();
        await rig.Studio.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);

        IRenderedComponent<ClusterSelector> selector = rig.Studio.Context.Render<ClusterSelector>();

        selector.WaitForAssertion(() => selector.Find(".ms-k-cluster-name").TextContent.Should().Be("Test cluster"));
        selector.FindAll(".ms-k-profile-chip").Should().BeEmpty();
    }
}
