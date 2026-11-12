using Bunit;

using KStudio.Components.Shared;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Support;

namespace KStudio.Tests.Components;

/// <summary>Compare (D-PAYLOAD decision 5): two in-memory matches, a masked part listed and never diffed, and a value that cannot be compared said so.</summary>
public class KafkaRecordCompareTests
{
    private const string EmailA = "SENTINEL-a1b2c3@example.com";
    private const string EmailB = "SENTINEL-d4e5f6@example.com";

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    /// <summary>The records at offsets 1 and 0 of <c>orders</c>, as one read returns them.</summary>
    private static async Task<(KafkaMatch Second, KafkaMatch First)> PairAsync(KafkaSearchRig rig)
    {
        KafkaRecordPageResult.Read read = (KafkaRecordPageResult.Read) await rig.Records().ReadAsync(rig.Entry, "orders", 0, 1, Ct);
        return (read.Value.Record!, read.Value.Before.Single());
    }

    private static IRenderedComponent<KafkaRecordCompare> Render(
        KafkaSearchRig rig,
        KafkaMatch left,
        KafkaMatch right,
        KafkaValueAccess leftAccess = KafkaValueAccess.Granted,
        KafkaValueAccess rightAccess = KafkaValueAccess.Granted) =>
        rig.Studio.Context.Render<KafkaRecordCompare>(p => p
            .Add(x => x.Left, left)
            .Add(x => x.Right, right)
            .Add(x => x.LeftAccess, leftAccess)
            .Add(x => x.RightAccess, rightAccess));

    private static string Section(IRenderedComponent<KafkaRecordCompare> compare, string heading)
    {
        AngleSharp.Dom.IElement title = compare.FindAll("h3").Single(x => x.TextContent.Trim() == heading);
        List<string> parts = [];

        for (AngleSharp.Dom.IElement? next = title.NextElementSibling; next is not null && next.TagName != "H3"; next = next.NextElementSibling)
        {
            parts.Add(next.TextContent);
        }

        return string.Join('\n', parts);
    }

    [Fact]
    public async Task A_masked_header_is_never_same_or_changed()
    {
        await using KafkaSearchRig rig = new(payloads: true, engine: o => o.Topic("orders", t => t.Redact(r => r.Header("customer-email"))));
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "{\"a\":1}", ("customer-email", EmailA), ("correlation-id", "c-1"), ("tenant-id", "acme"));
        rig.Produce("orders", 0, 2, "k1", "{\"a\":1}", ("customer-email", EmailA), ("correlation-id", "c-1"), ("tenant-id", "globex"));
        (KafkaMatch second, KafkaMatch first) = await PairAsync(rig);

        IRenderedComponent<KafkaRecordCompare> compare = Render(rig, first, second);

        string[][] rows = [.. compare.FindAll("table.ms-k-compare-table tbody tr").Select(static x => x.QuerySelectorAll("td").Select(static c => c.TextContent.Trim()).ToArray()).Where(static x => x.Length == 4)];
        rows.Single(static x => x[0] == "customer-email")[1].Should().Be("masked (redacted by header customer-email)");
        rows.Single(static x => x[0] == "correlation-id")[1].Should().Be("same");
        rows.Single(static x => x[0] == "tenant-id")[1].Should().Be("changed");
        compare.Markup.Should().NotContain(EmailA);
    }

    [Fact]
    public async Task Headers_are_matched_by_name_and_occurrence_and_one_sided_ones_say_so()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "{\"a\":1}", ("trace", "x"), ("trace", "y"), ("only-first", "1"));
        rig.Produce("orders", 0, 2, "k1", "{\"a\":1}", ("trace", "x"), ("trace", "z"));
        (KafkaMatch second, KafkaMatch first) = await PairAsync(rig);

        IRenderedComponent<KafkaRecordCompare> compare = Render(rig, first, second);

        string[][] rows = [.. compare.FindAll("table.ms-k-compare-table tbody tr").Select(static x => x.QuerySelectorAll("td").Select(static c => c.TextContent.Trim()).ToArray()).Where(static x => x.Length == 4)];
        rows.Single(static x => x[0] == "trace")[1].Should().Be("same");
        rows.Single(static x => x[0] == "trace #2")[1].Should().Be("changed");
        rows.Single(static x => x[0] == "only-first")[1].Should().Be("only in 0/0");
    }

    [Fact]
    public async Task A_masked_path_is_listed_and_not_diffed()
    {
        await using KafkaSearchRig rig = new(payloads: true, engine: o => o.Topic("orders", t => t.Redact(r => r.JsonPath("$.customer.email"))));
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "{\"customer\":{\"id\":\"c-1\",\"email\":\"" + EmailA + "\"},\"status\":\"placed\"}");
        rig.Produce("orders", 0, 2, "k1", "{\"customer\":{\"id\":\"c-1\",\"email\":\"" + EmailB + "\"},\"status\":\"shipped\"}");
        (KafkaMatch second, KafkaMatch first) = await PairAsync(rig);

        IRenderedComponent<KafkaRecordCompare> compare = Render(rig, first, second);

        string value = Section(compare, "Value");
        value.Should().Contain("Not compared, masked: $.customer.email");
        compare.FindAll(".ms-diff-path").Select(static x => x.TextContent).Should().Equal("$.status");
        compare.Markup.Should().NotContain(EmailA).And.NotContain(EmailB);
    }

    [Fact]
    public async Task Identical_values_read_the_same_and_pattern_masks_say_the_values_may_differ()
    {
        await using KafkaSearchRig rig = new(payloads: true, engine: o => o.Topic("orders", t => t.Redact(r => r.Pattern(@"\+[0-9]{3} [0-9]{2} [0-9]{3} [0-9]{4}", "phone"))));
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "{\"note\":\"call +358 40 123 4567\"}");
        rig.Produce("orders", 0, 2, "k1", "{\"note\":\"call +358 40 999 0000\"}");
        (KafkaMatch second, KafkaMatch first) = await PairAsync(rig);

        IRenderedComponent<KafkaRecordCompare> compare = Render(rig, first, second);

        string value = Section(compare, "Value");
        value.Should().Contain("The values are the same.").And.Contain("Text matching pattern phone is masked on both sides: the values may differ there.");
        compare.Markup.Should().NotContain("123 4567").And.NotContain("999 0000");
    }

    [Fact]
    public async Task Text_values_are_shown_side_by_side()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "plain one");
        rig.Produce("orders", 0, 2, "k1", "plain two");
        (KafkaMatch second, KafkaMatch first) = await PairAsync(rig);

        IRenderedComponent<KafkaRecordCompare> compare = Render(rig, first, second);

        Section(compare, "Value").Should().Contain("The values differ.");
        compare.FindAll(".ms-k-compare-sides pre").Select(static x => x.TextContent).Should().Equal("plain one", "plain two");
    }

    [Fact]
    public async Task Cut_values_are_not_compared()
    {
        await using KafkaSearchRig rig = new(payloads: true, engine: o => o.Limits.MaxMatchBytes = 1_024);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", new string('a', 5_000));
        rig.Produce("orders", 0, 2, "k1", new string('b', 5_000));
        KafkaSearchService search = rig.Search();
        KafkaSearchPlanned planned = ((KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders"]), Ct)).Value;
        await using KafkaSearchRun run = ((KafkaSearchStartResult.Running) await search.StartAsync(planned, confirmed: false, Ct)).Run;
        (_, List<KafkaMatch> matches) = await KafkaSearchRig.DrainAsync(run);

        IRenderedComponent<KafkaRecordCompare> compare = Render(rig, matches[0], matches[1]);

        Section(compare, "Value").Should().Contain("The value of 0/0 was cut at ").And.Contain(", so the values are not compared. Open each record to compare them whole.");
        compare.FindAll(".ms-k-compare-sides").Should().BeEmpty();
    }

    [Fact]
    public async Task A_withheld_value_is_not_compared()
    {
        await using KafkaSearchRig rig = new(payloads: true, engine: o => o.Topic("orders", t => t.Redact(r => r.JsonPath("$.a"))));
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "not json, so the rule cannot be applied");
        rig.Produce("orders", 0, 2, "k1", "{\"a\":1}");
        (KafkaMatch second, KafkaMatch first) = await PairAsync(rig);

        IRenderedComponent<KafkaRecordCompare> compare = Render(rig, first, second);

        Section(compare, "Value").Should().Contain("Not compared: the value of 0/0 is withheld by rule $.a.");
        compare.Markup.Should().NotContain("not json, so");
    }

    [Theory]
    [InlineData("CapabilityOff", "Reading values is not enabled.")]
    [InlineData("PolicyRefused", "The value of this record is not available to you.")]
    [InlineData("SearchWithoutValues", "This search could not read values on every topic it named. Open the record page to read this record's value.")]
    public async Task Without_Payload_the_value_section_says_why(string accessName, string sentence)
    {
        KafkaValueAccess access = Enum.Parse<KafkaValueAccess>(accessName);
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "{\"a\":\"SENTINEL-value-1\"}");
        rig.Produce("orders", 0, 2, "k1", "{\"a\":\"SENTINEL-value-2\"}");
        (KafkaMatch second, KafkaMatch first) = await PairAsync(rig);

        IRenderedComponent<KafkaRecordCompare> compare = Render(rig, first, second, access, KafkaValueAccess.Granted);

        Section(compare, "Value").Should().Contain("0/0: " + sentence);
        compare.Markup.Should().NotContain("SENTINEL-value-", "when one value is not the visitor's, neither is diffed");
    }

    [Fact]
    public async Task Different_kinds_say_so()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "{\"a\":1}");
        rig.Produce("orders", 0, 2, "k1", null);
        (KafkaMatch second, KafkaMatch first) = await PairAsync(rig);

        IRenderedComponent<KafkaRecordCompare> compare = Render(rig, first, second);

        Section(compare, "Value").Should().Contain("The values are of different kinds: JSON and a tombstone.");
    }
}
