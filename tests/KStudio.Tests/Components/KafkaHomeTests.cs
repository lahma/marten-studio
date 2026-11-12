using AngleSharp.Dom;

using Bunit;

using KStudio.Components.Pages;
using KStudio.Components.Shared;
using KStudio.Core.Wire;
using KStudio.Tests.Support;

namespace KStudio.Tests.Components;

/// <summary>Topics, the home (D-UI decision 7), rendered against the real engine over the fake broker.</summary>
public class KafkaHomeTests
{
    private static async Task<IRenderedComponent<KafkaHome>> OpenAsync(KafkaCatalogRig rig, string address = "kafka?cluster=test")
    {
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate(address, intercepted: false);
        return rig.Studio.Context.Render<KafkaHome>();
    }

    private static string[] Names(IRenderedComponent<KafkaHome> page, string group) =>
        [.. page.FindAll(group + " tr.ms-table-row td:first-child").Select(static x => (x.QuerySelector("a")?.TextContent ?? x.FirstChild?.TextContent ?? string.Empty).Trim())];

    [Fact]
    public async Task Declared_topics_come_first_then_the_others()
    {
        await using KafkaCatalogRig rig = new(engine: o => o.Topic("orders.v1", t => t.Description("Orders as CloudEvents.").Key(KafkaKeyFormat.JsonString)));
        rig.Topic("zeta", 1, 1);
        rig.Topic("alpha", 2, 1, 1);
        rig.Topic("orders.v1", 1, 3);

        IRenderedComponent<KafkaHome> page = await OpenAsync(rig);

        page.WaitForAssertion(() => page.FindAll(".ms-k-topic-group-declared tr.ms-table-row").Should().HaveCount(1));
        Names(page, ".ms-k-topic-group-declared").Should().Equal("orders.v1");
        Names(page, ".ms-k-topic-group-other").Should().Equal("alpha", "zeta");

        IElement declared = page.Find(".ms-k-topic-group-declared tr.ms-table-row");
        declared.TextContent.Should().Contain("Orders as CloudEvents.").And.Contain("JsonString");
        page.Find(".ms-k-topic-group-other tr.ms-table-row").TextContent.Should().Contain("UTF-8 text (assumed)");
        page.Find("th[scope=rowgroup]").TextContent.Trim().Should().Be("Declared");
    }

    [Fact]
    public async Task A_declared_topic_metadata_does_not_list_says_not_listed()
    {
        await using KafkaCatalogRig rig = new(engine: o => o.Topic("ghost.v1", t => t.Description("Never created.")));
        rig.Topic("alpha", 1, 1);

        IRenderedComponent<KafkaHome> page = await OpenAsync(rig);

        page.WaitForAssertion(() => page.FindAll(".ms-k-topic-group-declared tr.ms-table-row").Should().HaveCount(1));
        IElement ghost = page.Find(".ms-k-topic-group-declared tr.ms-table-row");
        ghost.QuerySelector("a").Should().BeNull("a topic the cluster does not list has nowhere to link to");
        ghost.QuerySelector(".ms-k-not-listed")!.TextContent.Should().Be(
            "Not listed: it does not exist, or this application's credentials cannot describe it");
    }

    [Fact]
    public async Task A_refused_topic_is_absent()
    {
        await using KafkaCatalogRig rig = new(
            engine: o => o.Topic("secret.declared", t => t.Description("Declared, but refused.")),
            studio: o => o.ClusterAuthorizationPolicy = "kafka-topics");
        rig.Studio.Authorization.AllowKafka(static resource => resource.Topic is null || !resource.Topic.StartsWith("secret", StringComparison.Ordinal));
        rig.Topic("secret.declared", 1, 1);
        rig.Topic("secret.other", 1, 1);
        rig.Topic("public", 1, 1);

        IRenderedComponent<KafkaHome> page = await OpenAsync(rig);

        page.WaitForAssertion(() => page.Markup.Should().Contain("public"));
        page.Markup.Should().NotContain("secret", "a topic the policy refuses is absent whether it is declared or not (D35)");
    }

    [Fact]
    public async Task Spans_are_read_for_the_current_page_only()
    {
        await using KafkaCatalogRig rig = new();

        for (int i = 0; i < 150; i++)
        {
            rig.Topic("t" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), 1, 1);
        }

        IRenderedComponent<KafkaHome> page = await OpenAsync(rig);

        page.WaitForAssertion(() => page.FindAll(".ms-k-topic-group-other tr.ms-table-row").Should().HaveCount(100));
        page.WaitForAssertion(() => page.Find(".ms-k-span").TextContent.Should().Contain("~1"));
        rig.ListOffsetCalls.Should().Be(2, "one page of 100 topics costs an earliest and a latest read, however many topics the cluster holds");
        page.Markup.Should().NotContain("t100");

        rig.Studio.Navigation.Simulate("kafka?cluster=test&page=2", intercepted: false);

        page.WaitForAssertion(() => Names(page, ".ms-k-topic-group-other").Should().HaveCount(50).And.Contain("t149"));
        rig.ListOffsetCalls.Should().BeLessThanOrEqualTo(4, "the second page is one more pair of reads");
    }

    [Fact]
    public async Task Spans_render_with_a_tilde()
    {
        await using KafkaCatalogRig rig = new();
        rig.Topic("orders", 2, 5, 4);

        IRenderedComponent<KafkaHome> page = await OpenAsync(rig);

        page.WaitForAssertion(() => page.Find(".ms-k-span").TextContent.Trim().Should().Be("~9"));
        page.Find("th[title^='An offset span']").TextContent.Should().Be("Offset span");
    }

    [Fact]
    public async Task Internal_topics_are_in_a_closed_details()
    {
        await using KafkaCatalogRig rig = new();
        rig.Topic("__consumer_offsets", 1, 1);
        rig.Topic("orders", 1, 1);

        IRenderedComponent<KafkaHome> page = await OpenAsync(rig);

        page.WaitForAssertion(() => page.FindAll("details.ms-k-internal").Should().HaveCount(1));
        IElement details = page.Find("details.ms-k-internal");
        details.HasAttribute("open").Should().BeFalse();
        details.TextContent.Should().Contain("__consumer_offsets");
        page.Find(".ms-k-topic-group-other").TextContent.Should().NotContain("__consumer_offsets");
        details.QuerySelector("[role=region]")!.GetAttribute("aria-label").Should().Be("Internal topics");
    }

    [Fact]
    public async Task The_filter_and_page_survive_a_reload_of_the_address()
    {
        await using KafkaCatalogRig rig = new();
        rig.Topic("orders", 1, 1);
        rig.Topic("order-lines", 1, 1);
        rig.Topic("payments", 1, 1);

        IRenderedComponent<KafkaHome> page = await OpenAsync(rig, "kafka?cluster=test&filter=ORDER");

        page.WaitForAssertion(() => Names(page, ".ms-k-topic-group-other").Should().Equal("order-lines", "orders"));
        page.Find("input.ms-search-filter").GetAttribute("value").Should().Be("ORDER", "the box shows the text the address carried");

        // Typing a new filter writes it back with replace, keeping the cluster.
        page.Find("input.ms-search-filter").Input("pay");

        page.WaitForAssertion(() => Names(page, ".ms-k-topic-group-other").Should().Equal("payments"));
        page.WaitForAssertion(() => rig.Studio.Navigation.Requests.Should().NotBeEmpty());
        (string uri, Microsoft.AspNetCore.Components.NavigationOptions options) = rig.Studio.Navigation.Requests.Last();
        uri.Should().EndWith("kafka?cluster=test&filter=pay");
        options.ReplaceHistoryEntry.Should().BeTrue();
    }

    [Fact]
    public async Task A_withheld_list_shows_the_ACL_sentence()
    {
        await using KafkaCatalogRig rig = new();
        rig.Topic("orders", 1, 1);
        rig.Cluster.FailNext(WireCall.Metadata, new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Cluster, "kafka-cluster"));

        IRenderedComponent<KafkaHome> page = await OpenAsync(rig);

        page.WaitForAssertion(() => page.Find(".ms-error-alert").TextContent.Should().Contain("Not available: needs `DESCRIBE` on the cluster"));
        page.FindAll("table.ms-k-topics-table").Should().BeEmpty("the table is replaced, not left empty");
    }

    [Fact]
    public async Task No_broker_call_before_Ready()
    {
        await using KafkaCatalogRig rig = new();
        rig.Topic("orders", 1, 1);
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka?cluster=test", intercepted: false);
        await rig.Studio.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        IRenderedComponent<KafkaTopicsBody> body = rig.Studio.Context.Render<KafkaTopicsBody>(
            parameters => parameters.AddCascadingValue(KafkaPageFrame.ReadyName, ready.Task));

        rig.BrokerCalls.Should().Be(0, "a body created while the page is prerendered reads nothing: it waits for the frame's Ready");
        body.FindAll("table").Should().BeEmpty();

        ready.SetResult();

        body.WaitForAssertion(() => body.Markup.Should().Contain("orders"));
        rig.BrokerCalls.Should().BeGreaterThan(0);
    }
}
