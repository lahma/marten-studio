using Bunit;

using KStudio.Components.Pages;
using KStudio.Components.Shared;
using KStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

using PostgresStudio.Components.Pages;

namespace KStudio.Tests.Components;

/// <summary>
/// D-PAYLOAD 3.6, the pages and the circuit: a masked value (a searchable JSON-path leaf and a pattern-masked phone) is in no sink. A search matches the
/// record by another field, its detail is expanded, its record page opened, two records compared and every copy item opened; the raw masked strings are
/// in none of the markup, the addresses, the audit, the Activity page, the log at Trace, the toasts or the interop calls.
/// </summary>
public class KafkaPayloadSinkSentinelTests
{
    private const string Email = "SENTINEL-b83e91@example.com";
    private const string Phone = "+358 40 555 0199";
    private const string Window = "&topic=orders&from=2026-11-08T11%3A00%3A00Z&to=2026-11-08T13%3A00%3A00Z";

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static string Json(int number, string status) =>
        "{\"status\":\"" + status + "\",\"customer\":{\"id\":\"customer-" + number + "\",\"email\":\"" + Email + "\"},\"note\":\"call " + Phone + " today\"}";

    /// <summary>Opens the node menu of the first tree row containing <paramref name="text" />, copies each of its items and returns their labels.</summary>
    private static async Task<string[]> OpenNodeMenuAsync(IRenderedComponent<KafkaSearchPage> search, string text)
    {
        search.FindAll("[role=treeitem]").First(x => x.TextContent.Contains(text, StringComparison.Ordinal)).QuerySelector(".ms-json-key")!.Click();
        string[] labels = [.. search.FindAll(".ms-copy-menu")[0].QuerySelectorAll(".ms-menu-item-label").Select(static x => x.TextContent)];

        for (int i = 0; i < labels.Length; i++)
        {
            await search.FindAll(".ms-copy-menu")[0].QuerySelectorAll(".ms-menu-item")[i].ClickAsync(new MouseEventArgs());
        }

        return labels;
    }

    private static string Describe(object? argument)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(argument);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            return argument?.ToString() ?? string.Empty;
        }
    }

    [Fact]
    public async Task No_masked_value_reaches_any_page_address_audit_log_toast_or_interop_call()
    {
        await using KafkaSearchRig rig = new(
            payloads: true,
            traceCircuit: true,
            engine: o => o.Topic("orders", t => t.Redact(r => r.JsonPath("$.customer.email").Searchable().Pattern(@"\+[0-9]{3} [0-9]{2} [0-9]{3} [0-9]{4}", "phone"))));
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", Json(1, "placed"));
        rig.Produce("orders", 0, 2, "k1", Json(2, "placed"));
        rig.Produce("orders", 0, 3, "k2", Json(3, "shipped"));
        await rig.Studio.Context.ReadyAsync();
        List<string> markup = [];

        // 1. A search that matches by another field, expanded, with two records compared.
        rig.Studio.Navigation.Simulate("kafka/search?cluster=test" + Window, intercepted: false);
        IRenderedComponent<KafkaSearchPage> search = rig.Studio.Context.Render<KafkaSearchPage>();
        search.WaitForAssertion(() => search.FindAll("textarea.ms-k-predicate").Should().HaveCount(1));
        await search.FindAll("label.ms-k-topic-option").Single(static x => x.TextContent.Trim() == "orders").QuerySelector("input")!.ChangeAsync(new ChangeEventArgs { Value = true });
        await search.Find("textarea.ms-k-predicate").InputAsync(new ChangeEventArgs { Value = "$.status = 'placed'" });
        await search.Find(".ms-k-search-actions button").ClickAsync(new MouseEventArgs());
        search.WaitForAssertion(() => search.FindAll("table.ms-k-matches > tbody > tr.ms-table-row").Should().HaveCount(2));

        for (int i = 0; i < 2; i++)
        {
            // A fresh query after each render: the tree changes under every click.
            await search.FindAll("table.ms-k-matches > tbody > tr.ms-table-row button").Where(static x => x.TextContent == "Details").ElementAt(i).ClickAsync(new MouseEventArgs());
        }

        search.WaitForAssertion(() => search.FindAll(".ms-k-match-detail").Should().HaveCount(2));
        for (int i = 0; i < search.FindAll(".ms-k-copy-popover button").Count; i++)
        {
            await search.FindAll(".ms-k-copy-popover button")[i].ClickAsync(new MouseEventArgs());
        }

        // The JSON view's node menu, opened on the masked leaf and on an unmasked one, with every item copied.
        string[] maskedItems = await OpenNodeMenuAsync(search, "redacted by $.customer.email");
        maskedItems.Should().Equal("Copy value", "Copy JSONPath");
        string[] idItems = await OpenNodeMenuAsync(search, "customer-1");
        idItems.Should().Equal("Copy value", "Copy JSONPath", "Copy as search");
        rig.Studio.Context.JSInterop.Invocations["martenStudio.clipboard.copyText"]
            .Select(static x => x.Arguments[0]?.ToString())
            .Should().Contain(x => x != null && x.Contains("$.customer.id = 'customer-1'", StringComparison.Ordinal), "Copy as search copied the unmasked leaf's predicate");

        for (int i = 0; i < 2; i++)
        {
            await search.FindAll("table.ms-k-matches input[type=checkbox]")[i].ChangeAsync(new ChangeEventArgs { Value = true });
        }

        await search.Find(".ms-k-compare-bar button").ClickAsync(new MouseEventArgs());
        search.WaitForAssertion(() => search.FindAll(".ms-k-compare").Should().HaveCount(1));
        search.Markup.Should().Contain("••• (redacted by $.customer.email)", "the visitor does see that the part is masked");
        markup.Add(search.Markup);
        markup.AddRange(search.FindComponents<KafkaRecordDetail>().Select(static x => x.Markup));
        markup.AddRange(search.FindComponents<KafkaRecordValue>().Select(static x => x.Markup));
        markup.AddRange(search.FindComponents<KafkaRecordCompare>().Select(static x => x.Markup));

        // 2. The record page, its copy items, and a compare with a neighbour.
        rig.Studio.Navigation.Simulate("kafka/record?cluster=test&topic=orders&partition=0&offset=1", intercepted: false);
        IRenderedComponent<KafkaRecordPage> page = rig.Studio.Context.Render<KafkaRecordPage>();
        page.WaitForAssertion(() => page.FindAll("table.ms-k-neighbours tbody tr").Should().HaveCount(3));
        for (int i = 0; i < page.FindAll(".ms-k-copy-popover button").Count; i++)
        {
            await page.FindAll(".ms-k-copy-popover button")[i].ClickAsync(new MouseEventArgs());
        }

        await page.FindAll("button").First(static x => x.TextContent == "Compare with this record").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => page.FindAll(".ms-k-compare").Should().HaveCount(1));
        markup.Add(page.Markup);
        markup.AddRange(page.FindComponents<KafkaRecordValue>().Select(static x => x.Markup));
        markup.AddRange(page.FindComponents<KafkaRecordCompare>().Select(static x => x.Markup));

        // 3. The Activity page.
        IRenderedComponent<Activity> activity = rig.Studio.Context.Render<Activity>();
        activity.WaitForAssertion(() => activity.Markup.Should().Contain("ReadPayloads"));
        markup.Add(activity.Markup);

        string[] raw = [Email, Phone, "555 0199", "b83e91"];

        foreach (string sentinel in raw)
        {
            markup.Should().NotContain(x => x.Contains(sentinel, StringComparison.Ordinal), "no rendered markup holds " + sentinel);
            rig.Studio.Navigation.Reported.Select(static x => x.Uri).Concat(rig.Studio.Navigation.Requests.Select(static x => x.Uri))
                .Should().NotContain(x => x.Contains(sentinel, StringComparison.Ordinal) || Uri.UnescapeDataString(x).Contains(sentinel, StringComparison.Ordinal), "no address holds " + sentinel);
            rig.Ring.Select(static x => string.Join('|', x.User, x.Action, x.Target, x.Message, x.Capability, x.ScopeLabel)).Should().NotContain(x => x.Contains(sentinel, StringComparison.Ordinal));
            rig.CircuitLogs.Entries.Concat(rig.Logs.Entries).Select(static x => x.Message).Should().NotContain(x => x.Contains(sentinel, StringComparison.Ordinal));
            rig.Toasts.Messages.Select(static x => x.Message).Should().NotContain(x => x.Contains(sentinel, StringComparison.Ordinal));
            rig.Studio.Context.JSInterop.Invocations
                .Select(static x => string.Join('|', x.Arguments.Select(Describe)))
                .Should().NotContain(x => x.Contains(sentinel, StringComparison.Ordinal), "no interop call (copy goes through interop) carries " + sentinel);
        }

        rig.Ring.Should().Contain(static x => x.Capability == "ReadPayloads", "the value views were audited, without the values");
        rig.CircuitLogs.Entries.Should().NotBeEmpty("the capturing provider saw the circuit's services");
    }
}
