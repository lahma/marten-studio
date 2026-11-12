using KStudio.Core.Predicates;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Support;

namespace KStudio.Tests.Components;

/// <summary>Copy as, with payloads (D-PAYLOAD decision 6): what is offered parses back to a clause that finds the thing it was copied from, and a masked or cut part offers nothing.</summary>
public class KafkaCopyAsValueTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static async Task<KafkaMatch> ReadAsync(KafkaSearchRig rig, long offset = 0)
    {
        KafkaRecordPageResult result = await rig.Records().ReadAsync(rig.Entry, "orders", 0, offset, Ct);
        return result.Should().BeOfType<KafkaRecordPageResult.Read>().Subject.Value.Record!;
    }

    private static (string? Key, int Index)[] Path(params object[] steps) =>
        [.. steps.Select(static x => x is int index ? ((string?) null, index) : ((string?) x, -1))];

    private static void ParsesBackTo(string predicate, string field)
    {
        PredicateParseResult parsed = PredicateParser.Parse(predicate);

        parsed.IsValid.Should().BeTrue(string.Join("; ", parsed.Errors.Select(static x => x.Message)));
        ClauseNode clause = parsed.Tree.Should().BeOfType<ClauseNode>().Subject;
        clause.Field.Should().BeAssignableTo<FieldNode>();
        PredicateFormatter.Format(parsed.Tree).Should().Be(predicate, "what is copied is the canonical text");
        predicate.Should().StartWith(field);
    }

    [Fact]
    public async Task A_text_value_offers_its_predicate_and_it_parses_back()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "refund requested, it's \"urgent\"");

        string? predicate = KafkaMatchView.ValuePredicate(await ReadAsync(rig));

        predicate.Should().Be("value = 'refund requested, it''s \"urgent\"'");
        ParsesBackTo(predicate!, "value = ");
    }

    [Fact]
    public async Task A_JSON_leaf_offers_its_path_predicate_and_it_parses_back()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "{\"customer\":{\"id\":\"customer-042\"},\"total\":1.50,\"paid\":true,\"coupon\":null,\"items\":[{\"sku\":\"A-1\"}]}");
        KafkaMatch match = await ReadAsync(rig);

        KafkaMatchView.LeafPredicate(match, Path("customer", "id"), "\"customer-042\"").Should().Be("$.customer.id = 'customer-042'");
        KafkaMatchView.LeafPredicate(match, Path("total"), "1.50").Should().Be("$.total = 1.50", "the raw text is copied");
        KafkaMatchView.LeafPredicate(match, Path("paid"), "true").Should().Be("$.paid = true");
        KafkaMatchView.LeafPredicate(match, Path("coupon"), "null").Should().Be("$.coupon = null");
        KafkaMatchView.LeafPredicate(match, Path("items", 0, "sku"), "\"A-1\"").Should().Be("$.items[0].sku = 'A-1'");

        foreach ((string predicate, string field) in new[]
                 {
                     ("$.customer.id = 'customer-042'", "$.customer.id"),
                     ("$.total = 1.50", "$.total"),
                     ("$.paid = true", "$.paid"),
                     ("$.coupon = null", "$.coupon"),
                     ("$.items[0].sku = 'A-1'", "$.items[0].sku"),
                 })
        {
            ParsesBackTo(predicate, field);
        }
    }

    [Fact]
    public async Task A_member_the_plain_grammar_cannot_write_is_written_in_brackets()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "{\"customer-email\":\"x\"}");

        string? predicate = KafkaMatchView.LeafPredicate(await ReadAsync(rig), Path("customer-email"), "\"x\"");

        predicate.Should().Be("$['customer-email'] = 'x'");
        PredicateParser.Parse(predicate!).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_masked_leaf_offers_nothing()
    {
        await using KafkaSearchRig rig = new(payloads: true, engine: o => o.Topic("orders", t => t.Redact(r => r.JsonPath("$.customer.email").Searchable().Pattern(@"\+[0-9]{3} [0-9]{2} [0-9]{3} [0-9]{4}", "phone"))));
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "{\"customer\":{\"id\":\"c-1\",\"email\":\"a@example.com\"},\"note\":\"call +358 40 123 4567\"}");
        KafkaMatch match = await ReadAsync(rig);

        KafkaMatchView.LeafPredicate(match, Path("customer", "email"), "\"••• (redacted by $.customer.email)\"").Should().BeNull();
        KafkaMatchView.LeafPredicate(match, Path("customer", "email", "x"), "1").Should().BeNull("under a masked node too");
        KafkaMatchView.LeafPredicate(match, Path("note"), "\"call ••• (redacted by pattern phone)\"").Should().BeNull("a leaf containing a mask is never a faithful copy");
        KafkaMatchView.LeafPredicate(match, Path("customer", "id"), "\"c-1\"").Should().Be("$.customer.id = 'c-1'", "an unmasked sibling still offers its own");
    }

    [Fact]
    public async Task A_cut_or_withheld_value_offers_nothing()
    {
        await using KafkaSearchRig rig = new(payloads: true, engine: o =>
        {
            o.Limits.MaxMatchBytes = 1_024;
            o.Topic("secret", t => t.Redact(r => r.JsonPath("$.a")));
        });
        rig.Topic("orders");
        rig.Topic("secret");
        rig.Produce("orders", 0, 1, "k0", new string('a', 5_000));
        rig.Produce("secret", 0, 2, "k0", "plain text under a path rule");
        KafkaSearchService search = rig.Search();
        KafkaSearchPlanned planned = ((KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders", "secret"], "key = 'k0'"), Ct)).Value;
        await using KafkaSearchRun run = ((KafkaSearchStartResult.Running) await search.StartAsync(planned, confirmed: false, Ct)).Run;
        (_, List<KafkaMatch> matches) = await KafkaSearchRig.DrainAsync(run);

        KafkaMatch cut = matches.Single(static x => x.Topic == "orders");
        KafkaMatch withheld = matches.Single(static x => x.Topic == "secret");

        cut.ValueTruncated.Should().BeTrue();
        withheld.ValueWithheldBy.Should().NotBeNull();
        KafkaMatchView.ValuePredicate(cut).Should().BeNull();
        KafkaMatchView.ValuePredicate(withheld).Should().BeNull();
        KafkaMatchView.LeafPredicate(cut, Path("a"), "\"x\"").Should().BeNull();
        KafkaMatchView.LeafPredicate(withheld, Path("a"), "\"x\"").Should().BeNull();
    }

    [Fact]
    public async Task A_JSON_value_offers_no_value_predicate_and_a_long_leaf_offers_nothing()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k0", "{\"a\":1}");
        KafkaMatch match = await ReadAsync(rig);

        KafkaMatchView.ValuePredicate(match).Should().BeNull("an exact match on a whole JSON text depends on its spacing");
        KafkaMatchView.LeafPredicate(match, Path("a"), "\"" + new string('x', 1_001) + "\"").Should().BeNull();
        KafkaMatchView.LeafPredicate(match, Path("a"), "{\"b\":1}").Should().BeNull("only scalars");
    }

    [Fact]
    public void The_kcat_line_still_has_no_credential()
    {
        string kcat = KafkaMatchView.Kcat("orders", 0, 7, null);

        kcat.Should().StartWith("kcat -C -b \"$BOOTSTRAP\" -t 'orders' -p 0 -o 7 -c 1 -J");
        kcat.Should().Contain("# add your own credentials").And.NotContain("sasl.password=hunter").And.NotContain("value");
    }
}
