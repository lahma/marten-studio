using System.Text;

using Bunit;

using KStudio.Components.Shared;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Support;

namespace KStudio.Tests.Components;

/// <summary>A record's value on the page (D-PAYLOAD decisions 1.3, 4.3 and 4.5): each case reads as the design words it, and nothing is an empty string.</summary>
public class KafkaRecordValueTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static async Task<KafkaMatch> ReadAsync(KafkaSearchRig rig, string topic, long offset)
    {
        KafkaRecordPageResult result = await rig.Records().ReadAsync(rig.Entry, topic, 0, offset, Ct);
        return result.Should().BeOfType<KafkaRecordPageResult.Read>().Subject.Value.Record!;
    }

    private static IRenderedComponent<KafkaRecordValue> Render(KafkaSearchRig rig, KafkaMatch match, KafkaValueAccess access = KafkaValueAccess.Granted, bool recordPage = false) =>
        rig.Studio.Context.Render<KafkaRecordValue>(p => p
            .Add(x => x.Model, match)
            .Add(x => x.Access, access)
            .Add(x => x.OnRecordPage, recordPage)
            .Add(x => x.RecordHref, "/postgres/kafka/record?topic=orders&partition=0&offset=0"));

    [Fact]
    public async Task A_tombstone_an_empty_value_and_a_withheld_value_read_as_sentences()
    {
        await using KafkaSearchRig rig = new(payloads: true, engine: o => o.Topic("texts", t => t.Redact(r => r.JsonPath("$.a"))));
        rig.Topic("orders");
        rig.Topic("texts");
        rig.Produce("orders", 0, 1, "k0", null);
        rig.Produce("orders", 0, 2, "k1", string.Empty);
        rig.Produce("texts", 0, 3, "k2", "plain words, not JSON");

        Render(rig, await ReadAsync(rig, "orders", 0)).Find(".ms-k-value").TextContent.Trim().Should().Be("Tombstone: a null value, which deletes this key on a compacted topic.");
        Render(rig, await ReadAsync(rig, "orders", 1)).Find(".ms-k-value").TextContent.Trim().Should().Be("Empty: the value has 0 bytes.");

        // A text value under a JSON-path rule cannot have the rule applied: it is withheld whole.
        IRenderedComponent<KafkaRecordValue> withheld = Render(rig, await ReadAsync(rig, "texts", 0));
        withheld.Find(".ms-k-value").TextContent.Trim().Should().Be("Value withheld: rule $.a could not be applied.");
        withheld.Markup.Should().NotContain("plain words");
    }

    [Fact]
    public async Task A_value_that_cannot_be_decoded_says_why()
    {
        await using KafkaSearchRig rig = new(payloads: true, engine: o => o.Topic("events", t => t.Value(KafkaDecoders.CloudEvents)));
        rig.Topic("events");
        rig.Produce("events", 0, 1, "k0", "{\"not\":\"a cloud event\"}");

        IRenderedComponent<KafkaRecordValue> value = Render(rig, await ReadAsync(rig, "events", 0));

        value.Find(".ms-k-value").TextContent.Trim().Should().Be("The value could not be decoded: the topic declares CloudEvents and this is not one.");
    }

    [Fact]
    public async Task JSON_is_drawn_by_the_JSON_view_without_a_download_and_without_Postgres_items()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "{\"customer\":{\"id\":\"customer-042\"},\"total\":12.5}");

        IRenderedComponent<KafkaRecordValue> value = Render(rig, await ReadAsync(rig, "orders", 0));

        value.FindAll("[role=tree]").Should().ContainSingle().Which.GetAttribute("aria-label").Should().Be("Record value");
        value.Markup.Should().NotContain("Download", "downloading a value is exporting it, which ExportRecords is for");
        value.Markup.Should().Contain("Copy JSON");
        value.Markup.Should().NotContain("Copy Postgres").And.NotContain("Marten LINQ").And.NotContain("containment filter");
        value.Markup.Should().Contain("Copy value").And.Contain("Copy JSONPath");
    }

    [Fact]
    public async Task A_masked_value_draws_the_mask_and_the_note_above_it()
    {
        await using KafkaSearchRig rig = new(
            payloads: true,
            engine: o => o.Topic("orders", t => t.Redact(r => r.JsonPath("$.customer.email").Searchable().Pattern(@"\+[0-9]{3} [0-9]{2} [0-9]{3} [0-9]{4}", "phone"))));
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "{\"customer\":{\"id\":\"customer-042\",\"email\":\"customer42@example.com\"},\"note\":\"call +358 40 123 4567\"}");

        IRenderedComponent<KafkaRecordValue> value = Render(rig, await ReadAsync(rig, "orders", 0));

        string[] notes = [.. value.FindAll(".ms-k-masked-note").Select(static x => x.TextContent.Trim())];
        notes.Should().Equal("1 part of this value is masked: $.customer.email.", "Text matching pattern phone is masked.");
        value.Markup.Should().Contain("••• (redacted by $.customer.email)").And.NotContain("customer42@example.com").And.NotContain("123 4567");
    }

    [Fact]
    public async Task Many_masked_paths_list_five_and_count_the_rest()
    {
        await using KafkaSearchRig rig = new(payloads: true, engine: o => o.Topic("orders", t => t.Redact(r => r.JsonPath("$.items[*].sku"))));
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "{\"items\":[" + string.Join(',', Enumerable.Range(0, 8).Select(static i => "{\"sku\":\"S" + i + "\"}")) + "]}");

        IRenderedComponent<KafkaRecordValue> value = Render(rig, await ReadAsync(rig, "orders", 0));

        value.Find(".ms-k-masked-note").TextContent.Trim().Should().Be(
            "8 parts of this value are masked: $.items[0].sku, $.items[1].sku, $.items[2].sku, $.items[3].sku, $.items[4].sku and 3 more.");
    }

    [Fact]
    public async Task A_cloud_events_value_says_it_shows_the_data()
    {
        await using KafkaSearchRig rig = new(payloads: true, engine: o => o.Topic("events", t => t.Value(KafkaDecoders.CloudEvents)));
        rig.Topic("events");
        rig.Produce("events", 0, 1, "k0", "{\"specversion\":\"1.0\",\"id\":\"1\",\"type\":\"T\",\"source\":\"/s\",\"datacontenttype\":\"application/json\",\"data\":{\"orderId\":\"order-1\"}}");

        IRenderedComponent<KafkaRecordValue> value = Render(rig, await ReadAsync(rig, "events", 0));

        value.Find(".ms-k-value-lead").TextContent.Trim().Should().Be("The event's data:");
        value.Markup.Should().Contain("orderId").And.NotContain("specversion");
    }

    [Fact]
    public async Task A_cut_value_says_how_much_is_shown_and_where_the_rest_is()
    {
        await using KafkaSearchRig rig = new(payloads: true, engine: o => o.Limits.MaxMatchBytes = 1_024);
        rig.Topic("orders");
        string text = new('a', 6_000);
        rig.Produce("orders", 0, 1, "k0", text);
        KafkaSearchService search = rig.Search();
        KafkaSearchPlanned planned = ((KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders"], "key = 'k0'"), Ct)).Value;
        await using KafkaSearchRun run = ((KafkaSearchStartResult.Running) await search.StartAsync(planned, confirmed: false, Ct)).Run;
        (_, List<KafkaMatch> matches) = await KafkaSearchRig.DrainAsync(run);
        KafkaMatch cut = matches.Single();
        cut.ValueTruncated.Should().BeTrue();

        IRenderedComponent<KafkaRecordValue> inDetail = Render(rig, cut);
        IRenderedComponent<KafkaRecordValue> onPage = Render(rig, cut, recordPage: true);

        inDetail.Find(".ms-k-value-cut").TextContent.Should().StartWith("Shown ").And.Contain(" of 5.9 KiB.").And.Contain("Open the record page to read it whole.");
        inDetail.Find(".ms-k-value-cut a").GetAttribute("href").Should().Contain("kafka/record");
        onPage.Find(".ms-k-value-cut").TextContent.Should().Contain("Values above KafkaStudioOptions.Limits.MaxDecodeBytes are not shown whole.");
        onPage.FindAll(".ms-k-value-cut a").Should().BeEmpty();
    }

    [Fact]
    public async Task Binary_is_a_hex_dump_and_the_wire_format_names_its_schema()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Topic("orders");
        rig.Cluster.Produce("orders", 0, Encoding.UTF8.GetBytes("k0"), [0x00, 0x00, 0x00, 0x00, 0x2a, 0x68, 0x69, 0x21], null, KafkaSearchRig.T0.ToUnixTimeMilliseconds());
        rig.Cluster.Produce("orders", 0, Encoding.UTF8.GetBytes("k1"), [0xff, 0xfe, 0x00, 0x41, 0x42], null, KafkaSearchRig.T0.ToUnixTimeMilliseconds() + 1000);

        IRenderedComponent<KafkaRecordValue> wire = Render(rig, await ReadAsync(rig, "orders", 0));
        IRenderedComponent<KafkaRecordValue> binary = Render(rig, await ReadAsync(rig, "orders", 1));

        wire.Find(".ms-k-value").TextContent.Should().Contain("Confluent wire format, schema id 42");
        wire.Find("pre.ms-k-value-hex").TextContent.Should().StartWith("0000  68 69 21");
        binary.Find("pre.ms-k-value-hex").TextContent.Should().StartWith("0000  ff fe 00 41 42").And.Contain(" ...AB");
    }

    [Theory]
    [InlineData("CapabilityOff", "Reading values is not enabled")]
    [InlineData("PolicyRefused", "The value of this record is not available to you.")]
    [InlineData("SearchWithoutValues", "This search could not read values on every topic it named. Open the record page to read this record's value.")]
    public async Task A_visitor_without_the_value_reads_why_and_the_value_is_not_in_the_markup(string accessName, string sentence)
    {
        KafkaValueAccess access = Enum.Parse<KafkaValueAccess>(accessName);
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "{\"visible\":\"SENTINEL-in-the-value\"}");
        KafkaMatch withValue = await ReadAsync(rig, "orders", 0);

        IRenderedComponent<KafkaRecordValue> value = Render(rig, withValue, access);

        value.Markup.Should().Contain(sentence).And.NotContain("SENTINEL-in-the-value", "a value the visitor may not read is never drawn, even if the match holds it");
    }

    [Fact]
    public async Task A_one_MiB_value_renders_within_bounds()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Topic("orders");
        string json = "{\"items\":[" + string.Join(',', Enumerable.Range(0, 30_000).Select(static i => "{\"sku\":\"SKU-" + i.ToString("000000", System.Globalization.CultureInfo.InvariantCulture) + "\",\"q\":" + i + "}")) + "]}";
        json.Length.Should().BeGreaterThan(900_000);
        rig.Produce("orders", 0, 1, "k0", json);
        rig.Produce("orders", 0, 2, "k1", new string('x', 1_000_000));

        IRenderedComponent<KafkaRecordValue> tree = Render(rig, await ReadAsync(rig, "orders", 0), recordPage: true);
        IRenderedComponent<KafkaRecordValue> text = Render(rig, await ReadAsync(rig, "orders", 1), recordPage: true);

        tree.Markup.Length.Should().BeLessThan(1_500_000);
        text.Markup.Length.Should().BeLessThan(300_000);
        text.Find(".ms-k-value-cut").TextContent.Should().Contain("Shown 256 KiB of 977 KiB.");
    }
}
