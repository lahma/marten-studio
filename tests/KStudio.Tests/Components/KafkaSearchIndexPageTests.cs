using AngleSharp.Dom;

using Bunit;

using Confluent.Kafka;

using KStudio.Components.Pages;
using KStudio.Tests.Index;
using KStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace KStudio.Tests.Components;

/// <summary>
/// The search page where the host registered a record index (AGENTS.md D48, D-M5 decision 5.3): the checkbox, the caveat, the negative said of the ranges
/// read, "Search the whole window" bound to the run on screen, the audit text, and the Settings line.
/// </summary>
public class KafkaSearchIndexPageTests
{
    private const string Window = "&topic=orders&from=2026-11-08T11%3A00%3A00Z&to=2026-11-08T13%3A00%3A00Z";
    private const string WholeWindow = "Search the whole window";

    private static KafkaSearchRig Rig(FakeRecordIndex? index) =>
        new(engine: index is null ? null : o => o.UseRecordIndex(_ => index, nameof(FakeRecordIndex)));

    /// <summary>Thirty records of one partition; the key <c>needle</c> is at offset 7.</summary>
    private static void Seed(KafkaSearchRig rig)
    {
        rig.Topic("orders");

        for (int i = 0; i < 30; i++)
        {
            rig.Produce("orders", 0, i, i == 7 ? "needle" : "k" + i, "v" + i);
        }
    }

    private static async Task<IRenderedComponent<KafkaSearchPage>> OpenAsync(KafkaSearchRig rig)
    {
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/search?cluster=test" + Window, intercepted: false);
        IRenderedComponent<KafkaSearchPage> page = rig.Studio.Context.Render<KafkaSearchPage>();
        page.WaitForAssertion(() => page.FindAll("textarea.ms-k-predicate").Should().HaveCount(1));
        await page.FindAll("label.ms-k-topic-option").Single(x => x.TextContent.Trim() == "orders").QuerySelector("input")!.ChangeAsync(new ChangeEventArgs { Value = true });
        return page;
    }

    private static async Task SearchForAsync(IRenderedComponent<KafkaSearchPage> page, string predicate)
    {
        await page.Find("textarea.ms-k-predicate").InputAsync(new ChangeEventArgs { Value = predicate });
        await page.Find(".ms-k-search-actions button").ClickAsync(new MouseEventArgs());
    }

    private static string[] Rows(IRenderedComponent<KafkaSearchPage> page) =>
        [.. page.FindAll("table.ms-k-matches > tbody > tr.ms-table-row").Select(static x => x.QuerySelectorAll("td")[2].TextContent.Trim())];

    private static IElement[] Buttons(IRenderedComponent<KafkaSearchPage> page) => [.. page.FindAll(".ms-k-result-banner button")];

    private static Task ClickAsync(IElement button) => button.ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task The_checkbox_is_there_only_when_the_host_registered_an_index()
    {
        await using KafkaSearchRig with = Rig(new FakeRecordIndex());
        Seed(with);
        await using KafkaSearchRig without = Rig(null);
        Seed(without);

        IRenderedComponent<KafkaSearchPage> registered = await OpenAsync(with);
        IRenderedComponent<KafkaSearchPage> none = await OpenAsync(without);

        registered.Find("label.ms-k-ignore-index").TextContent.Trim().Should().Be("Ignore the host's record index");
        none.FindAll("label.ms-k-ignore-index").Should().BeEmpty();
    }

    [Fact]
    public async Task A_narrowed_negative_is_said_of_the_range_read_with_the_caveat_and_the_button()
    {
        FakeRecordIndex index = new();
        await using KafkaSearchRig rig = Rig(index);
        Seed(rig);
        index.Hints(new KafkaIndexHint("orders", 0, 1, 3));
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig);

        await SearchForAsync(page, "key = 'needle'");

        page.WaitForAssertion(() => page.Find(".ms-k-result-banner p").TextContent.Should().Be("No match in the range the host's record index named (2 offsets). The rest of the window was not read."));
        Buttons(page).Select(static x => x.TextContent).Should().Contain(WholeWindow);
        page.Find(".ms-k-caveats").TextContent.Should().Contain("Read only where the host's record index pointed (2 of 30 offsets); other matches in the window were not looked for.");
        page.Find(".ms-k-plan-notes").TextContent.Should().Contain("The host's record index narrowed this search to 1 range (2 of 30 offsets of the window).");
        Rows(page).Should().BeEmpty();
    }

    [Fact]
    public async Task Search_the_whole_window_plans_again_without_the_index_and_finds_what_the_hint_missed()
    {
        FakeRecordIndex index = new();
        await using KafkaSearchRig rig = Rig(index);
        Seed(rig);
        index.Hints(new KafkaIndexHint("orders", 0, 1, 3));
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig);
        await SearchForAsync(page, "key = 'needle'");
        page.WaitForAssertion(() => Buttons(page).Select(static x => x.TextContent).Should().Contain(WholeWindow));

        await ClickAsync(Buttons(page).Single(static x => x.TextContent == WholeWindow));

        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 7"));
        page.Find(".ms-k-result-banner p").TextContent.Should().Be("Read every offset in the window: 30 records, " + page.Find(".ms-k-result-banner p").TextContent.Split(", ")[1] + ", 1 match.");
        index.Queries.Should().ContainSingle("the second plan is the same input with the index ignored");
        Buttons(page).Select(static x => x.TextContent).Should().NotContain(WholeWindow);
        page.FindAll(".ms-k-caveats li").Select(static x => x.TextContent).Should().NotContain(static x => x.Contains("record index", StringComparison.Ordinal), "nothing is left out of the whole window");
        page.FindAll(".ms-k-plan-notes li").Select(static x => x.TextContent).Should().NotContain(static x => x.Contains("record index", StringComparison.Ordinal));

        string?[] searches = [.. rig.Ring.Where(static x => x.Action == "SearchRecords").OrderBy(static x => x.Sequence).Select(static x => x.Message)];
        searches.Should().HaveCount(2, "each is its own scan and its own entry");
        searches[0].Should().Contain("; narrowed by the host's record index to 1 range of orders (2 offsets)");
        searches[1].Should().NotContain("narrowed by");
    }

    [Fact]
    public async Task A_narrowed_positive_carries_the_caveat_and_the_button()
    {
        FakeRecordIndex index = new();
        await using KafkaSearchRig rig = Rig(index);
        Seed(rig);
        index.Hints(new KafkaIndexHint("orders", 0, 7, 8));
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig);

        await SearchForAsync(page, "key = 'needle'");

        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 7"));
        page.Find(".ms-k-result-banner p").TextContent.Should().StartWith("Read every offset in the ranges the host's record index named (1 offset): 1 record,").And.EndWith("1 match. The rest of the window was not read.");
        page.Find(".ms-k-caveats").TextContent.Should().Contain("Read only where the host's record index pointed (1 of 30 offsets); other matches in the window were not looked for.");
        Buttons(page).Select(static x => x.TextContent).Should().Contain(WholeWindow);
    }

    [Fact]
    public async Task The_checkbox_reads_the_whole_window_and_never_asks_the_index()
    {
        FakeRecordIndex index = new();
        await using KafkaSearchRig rig = Rig(index);
        Seed(rig);
        index.Hints(new KafkaIndexHint("orders", 0, 1, 3));
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig);
        await page.Find("label.ms-k-ignore-index input").ChangeAsync(new ChangeEventArgs { Value = true });

        await SearchForAsync(page, "key = 'needle'");

        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 7"));
        index.Queries.Should().BeEmpty();
        Buttons(page).Select(static x => x.TextContent).Should().NotContain(WholeWindow);
        page.FindAll(".ms-k-caveats li").Select(static x => x.TextContent).Should().NotContain(static x => x.Contains("record index", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_index_that_throws_reads_the_whole_window_and_the_plan_says_so_before_it_runs()
    {
        FakeRecordIndex index = new() { Answer = static (_, _) => throw new InvalidOperationException("EXC") };
        await using KafkaSearchRig rig = Rig(index);
        Seed(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig);

        await SearchForAsync(page, "key = 'needle'");

        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 7"));
        page.Find(".ms-k-plan-notes").TextContent.Should().Contain("The host's record index did not answer; the whole window is read.");
        Buttons(page).Select(static x => x.TextContent).Should().NotContain(WholeWindow);
        page.Markup.Should().NotContain("EXC");
    }

    [Fact]
    public async Task The_button_belongs_to_the_run_on_screen_and_not_to_the_latest_plan()
    {
        FakeRecordIndex index = new();
        await using KafkaSearchRig rig = Rig(index);
        Seed(rig);
        index.Hints(new KafkaIndexHint("orders", 0, 1, 3));
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig);
        await SearchForAsync(page, "key = 'needle'");
        page.WaitForAssertion(() => Buttons(page).Select(static x => x.TextContent).Should().Contain(WholeWindow));

        // A new plan replaces `planned` and leaves the rows: the text does not parse.
        await SearchForAsync(page, "key = ");
        page.WaitForAssertion(() => page.FindAll(".ms-k-predicate-errors li").Should().NotBeEmpty());

        Buttons(page).Select(static x => x.TextContent).Should().Contain(WholeWindow, "the narrowed run is still on screen");
        page.Find(".ms-k-caveats").TextContent.Should().Contain("Read only where the host's record index pointed");

        await ClickAsync(Buttons(page).Single(static x => x.TextContent == WholeWindow));

        // The button re-ran the search of the run on screen, not the unparsable text in the form.
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 7"));
        index.Queries.Should().ContainSingle();
    }

    [Fact]
    public async Task The_settings_page_names_the_registered_index_or_none()
    {
        FakeRecordIndex index = new();
        await using KafkaCatalogRig with = new(o =>
        {
            o.AddCluster("settings", new ClientConfig { BootstrapServers = "broker-1:9092" });
            o.UseRecordIndex(_ => index, "MyRecordIndex");
        }, cluster: "settings");
        await using KafkaCatalogRig without = new(o => o.AddCluster("settings", new ClientConfig { BootstrapServers = "broker-1:9092" }), cluster: "settings");

        IRenderedComponent<KafkaSettingsPage> registered = await OpenSettingsAsync(with);
        IRenderedComponent<KafkaSettingsPage> none = await OpenSettingsAsync(without);

        registered.WaitForAssertion(() => registered.Find(".ms-k-record-index").TextContent.Should().Be("Record index: MyRecordIndex"));
        none.WaitForAssertion(() => none.Find(".ms-k-record-index").TextContent.Should().Be("Record index: none"));
    }

    private static async Task<IRenderedComponent<KafkaSettingsPage>> OpenSettingsAsync(KafkaCatalogRig rig)
    {
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/settings?cluster=settings", intercepted: false);
        return rig.Studio.Context.Render<KafkaSettingsPage>();
    }
}
