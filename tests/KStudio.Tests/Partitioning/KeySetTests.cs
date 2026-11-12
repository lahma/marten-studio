using KStudio.Core.Predicates;
using KStudio.Core.Scanning;

namespace KStudio.Tests.Partitioning;

/// <summary>
/// What a search fixes its key to (D-PAYLOAD decision 7.3, rule 3), and whether the key format's byte forms are complete (rule 4): the four
/// rules of the key set, nested, the JSON-string literal rule and a host's own format.
/// </summary>
public class KeySetTests
{
    private static IReadOnlyList<string>? Set(string text)
    {
        PredicateParseResult parsed = PredicateParser.Parse(text);

        parsed.IsValid.Should().BeTrue(text);
        return KeyPruning.KeySet(parsed.Tree);
    }

    [Theory]
    [InlineData("key = 'a'", "a")]
    [InlineData("key in ('a', 'b')", "a,b")]
    [InlineData("key in ('a', 'a', 'b')", "a,b")]
    [InlineData("key = 'a' and header.x = 'y'", "a")]
    [InlineData("header.x = 'y' and key in ('a', 'b')", "a,b")]
    public void A_key_equals_or_in_gives_its_literals_and_an_and_keeps_the_one_that_fixes_it(string text, string expected) =>
        string.Join(',', Set(text)!).Should().Be(expected);

    [Theory]
    [InlineData("key = 'a' and key in ('a', 'b', 'c')", "a", "an and gives the smallest set")]
    [InlineData("key in ('a', 'b', 'c') and key in ('b', 'c')", "b,c", "an and gives the smallest set")]
    [InlineData("key in ('a', 'b') and key in ('c', 'd')", "a,b", "a tie keeps the first")]
    [InlineData("key = 'a' and not key = 'b'", "a", "a not beside it gives none, the key clause still fixes it")]
    public void An_and_gives_the_smallest_set_among_its_children_that_have_one(string text, string expected, string why) =>
        string.Join(',', Set(text)!).Should().Be(expected, why);

    [Theory]
    [InlineData("key = 'a' or key = 'b'", "a,b")]
    [InlineData("key = 'a' or key in ('b', 'a')", "a,b")]
    [InlineData("(key = 'a' or key = 'b') and (key = 'b' or key = 'c' or key = 'd')", "a,b")]
    [InlineData("key = 'a' or (key = 'b' and header.x = 'y')", "a,b")]
    public void An_or_gives_the_union_when_every_child_has_one(string text, string expected) =>
        string.Join(',', Set(text)!).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("header.x = 'y'")]
    [InlineData("key = 'a' or header.x = 'y'")]
    [InlineData("key = 'a' or not key = 'b'")]
    [InlineData("not key = 'a'")]
    [InlineData("not (key = 'a' or key = 'b')")]
    [InlineData("key ^= 'a'")]
    [InlineData("key ~ 'a'")]
    [InlineData("key is null")]
    [InlineData("key is not null")]
    [InlineData("any = 'a'")]
    [InlineData("header.key = 'a'")]
    [InlineData("ce.type = 'a'")]
    [InlineData("key ~ 'a' and header.x = 'y'")]
    public void Every_other_search_fixes_no_key(string text) => Set(text).Should().BeNull();

    [Fact]
    public void A_null_tree_fixes_no_key() => KeyPruning.KeySet(null).Should().BeNull();

    [Theory]
    [InlineData("order-1:x@y.z _", true)]
    [InlineData("ABC123", true)]
    [InlineData("é", false)]
    [InlineData("a\"b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a/b", false)]
    [InlineData("a<b", false)]
    [InlineData("a+b", false)]
    [InlineData("tab\there", false)]
    public void A_json_string_key_is_pruned_only_for_characters_every_serializer_writes_alike(string literal, bool complete) =>
        KeyPruning.FormsAreComplete(KafkaKeyFormat.JsonString, [literal]).Should().Be(complete);

    [Fact]
    public void One_unsafe_literal_among_safe_ones_is_enough()
    {
        KeyPruning.FormsAreComplete(KafkaKeyFormat.JsonString, ["safe-1", "café"]).Should().BeFalse();
    }

    [Fact]
    public void Every_other_built_in_format_and_a_hosts_own_has_complete_forms()
    {
        foreach (KafkaKeyFormat format in new[]
                 {
                     KafkaKeyFormat.Utf8String, KafkaKeyFormat.Int64BigEndian, KafkaKeyFormat.Int32BigEndian, KafkaKeyFormat.GuidRfc4122,
                     KafkaKeyFormat.GuidDotNet, KafkaKeyFormat.Bytes, new TwoFormsFormat(),
                 })
        {
            KeyPruning.FormsAreComplete(format, ["é\"\\ anything"]).Should().BeTrue(format.Name);
        }
    }

    [Fact]
    public void The_forms_are_every_form_of_every_literal_and_a_host_format_may_have_several()
    {
        IReadOnlyList<byte[]> forms = KeyPruning.Forms(new TwoFormsFormat(), ["a", "b"])!;

        forms.Select(static x => System.Text.Encoding.UTF8.GetString(x)).Should().Equal("a", "A", "b", "B");
        KeyPruning.Forms(KafkaKeyFormat.Utf8String, ["a", "a"])!.Should().HaveCount(1, "a form is listed once");
        KeyPruning.Forms(KafkaKeyFormat.Int64BigEndian, ["12"])!.Single().Should().Equal(0, 0, 0, 0, 0, 0, 0, 12);
        KeyPruning.Forms(KafkaKeyFormat.Int64BigEndian, ["twelve"]).Should().BeNull("a literal the format cannot write has no forms");
    }

    private sealed class TwoFormsFormat() : KafkaKeyFormat("TwoForms")
    {
        public override bool TryEncode(string literal, out IReadOnlyList<byte[]> forms, out string? problem)
        {
            forms = [System.Text.Encoding.UTF8.GetBytes(literal), System.Text.Encoding.UTF8.GetBytes(literal.ToUpperInvariant())];
            problem = null;
            return true;
        }

        public override bool TryFormat(ReadOnlySpan<byte> key, out string? text)
        {
            text = System.Text.Encoding.UTF8.GetString(key);
            return true;
        }
    }
}
