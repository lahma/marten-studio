using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// The first wall: the text screen, before any parser sees the text. No Marten version matters here, so every case
/// runs at the floor too.
/// </summary>
public class LinqTextScreenTests
{
    private static readonly LinqDocumentGraph OrderGraph = LinqTestStore.Graph<LinqOrder>();

    private static LinqRefusal? Where(string text) => LinqTextScreen.Check(LinqClause.Where, text, OrderGraph);

    private static LinqRefusal? OrderBy(string text) => LinqTextScreen.Check(LinqClause.OrderBy, text, OrderGraph);

    [Theory]
    [InlineData("Status == \"Open\" && Total > 100 && Customer.Name.StartsWith(\"A\")")]
    [InlineData("Status = \"Open\" and Total > 100.5m")]
    [InlineData("Name.ToLower().Contains(\"x\")")]
    [InlineData("Name.Equals(\"a\", StringComparison.OrdinalIgnoreCase)")]
    [InlineData("Tags.Any(it == \"vip\")")]
    [InlineData("Lines.Any(Quantity > 5) or Lines.Count() > 2")]
    [InlineData("Notes != null and Notes != \"fragile\"")]
    [InlineData("Total > -5.5m")]
    [InlineData("Priority == null")]
    [InlineData("not Rush")]
    [InlineData("!(Rush)")]
    [InlineData("Status in (\"Open\", \"Shipped\")")]
    [InlineData("PlacedAt > \"2024-01-01\"")]
    [InlineData("Total % 2 == 0")]
    [InlineData("Name.Length > 3")]
    [InlineData("Name == 'A'")]
    [InlineData("  ")]
    [InlineData("")]
    public void A_predicate_in_the_dialect_passes_the_screen(string text)
    {
        Where(text).Should().BeNull();
    }

    [Theory]
    [InlineData("Total desc, Name")]
    [InlineData("Customer.Name asc")]
    [InlineData("PlacedAt descending, Total ascending, Name")]
    [InlineData("")]
    public void An_ordering_of_member_paths_passes_the_screen(string text)
    {
        OrderBy(text).Should().BeNull();
    }

    /// <summary>Each refused shape, and the character the caret goes under.</summary>
    public static TheoryData<string, int, string> RefusedPredicates() => new()
    {
        { "Name == System.IO.File.ReadAllText(\"c:/x\")", 8, "System" },
        { "Name == \"x\".PadLeft(2000000000)", 11, "'.'" },
        { "typeof(LinqOrder) != null", 0, "typeof" },
        { "new(Name as N) != null", 0, "new" },
        { "iif(Rush, Name, Notes) == \"x\"", 0, "iif" },
        { "np(Customer.Name) == \"x\"", 0, "np" },
        { "cast(Total, \"int\") > 0", 0, "cast" },
        { "it is LinqOrder", 0, "'it'" },
        { "Name is LinqOrder", 5, "is" },
        { "Total mod 2 == 0", 6, "mod" },
        { "Name == @0", 8, "@" },
        { "$.Name == \"x\"", 0, "'$'" },
        { "Name == root.Name", 8, "root" },
        { "Tags[0] == \"x\"", 4, "indexer" },
        { "Rush ? true : false", 5, "'?'" },
        { "Notes ?? \"x\" == \"x\"", 6, "'?'" },
        { "Lines.Any(l => l.Quantity > 5)", 12, "lambda" },
        { "Total + 1 > 5", 6, "arithmetic" },
        { "Name & \"x\" == \"x\"", 5, "'&'" },
        { "Rush | Rush", 5, "'|'" },
        { "Name == \"a\\\"b\"", 10, "backslash" },
        { "Name == 'ab'", 8, "Single quotes" },
        { "\"System.DateTime\"(PlacedAt) > null", 0, "cast" },
        { "Name.GetType() != null", 5, "GetType" },
        { "Name.Length.ToString() == \"3\"", 12, "ToString" },
        { "Total == 1e5", 9, "1e5" },
        { "Total == 0x10", 9, "0x10" },
        { "Name == Math.Max(1, 2)", 8, "Math" },
        { "PlacedAt < DateTime.Now", 11, "DateTime" },
        { "Guid(\"00000000-0000-0000-0000-000000000000\") == Id", 0, "no receiver" },
        { "Any(Rush)", 0, "no receiver" },
        { "(Rush", 1, "open" },
        { "Rush)", 4, "never opened" },
        { "!!!!Rush", 3, "negates" },
        { "Name == \"x\";", 11, "';'" },
        { "Nmae == \"x\"", 0, "Nmae" },
        { "Kind != null", 0, "Kind" },
        { "Payload != null", 0, "Payload" },
        { "Attributes.Any()", 0, "Attributes" },
        { "Converted == \"x\"", 0, "Converted" },
        { "Raw != null", 0, "Raw" },
    };

    [Theory]
    [MemberData(nameof(RefusedPredicates))]
    public void A_refused_predicate_says_where(string text, int position, string mentions)
    {
        LinqRefusal? refusal = Where(text);

        refusal.Should().NotBeNull("'{0}' must not reach a parser", text);
        refusal!.Stage.Should().Be(LinqStage.Screen);
        refusal.Clause.Should().Be(LinqClause.Where);
        refusal.Position.Should().Be(position, "the caret goes under the offending character of '{0}'", text);
        refusal.Message.Should().Contain(mentions);
    }

    [Theory]
    [InlineData("Total desc desc")]
    [InlineData("Total + 1")]
    [InlineData("Total,")]
    [InlineData("new(Total)")]
    [InlineData("iif(Rush, Total, Total)")]
    [InlineData("Total desc, Name, PlacedAt, Status, Notes")]
    [InlineData("Name.Length")]
    [InlineData("it")]
    [InlineData("Total.")]
    [InlineData("\"Total\"")]
    [InlineData("Kind")]
    public void A_refused_ordering_never_reaches_a_parser(string text)
    {
        LinqRefusal? refusal = OrderBy(text);

        refusal.Should().NotBeNull("'{0}' must not reach a parser", text);
        refusal!.Clause.Should().Be(LinqClause.OrderBy);
        refusal.Message.Should().StartWith("The ordering ");
    }

    [Fact]
    public void Length_tokens_and_nesting_are_capped_before_any_parser()
    {
        Where(new string('x', LinqQueryLimits.MaxWhereLength + 1))!.Message.Should().Contain("characters long");
        OrderBy(new string('x', LinqQueryLimits.MaxOrderByLength + 1))!.Message.Should().Contain("characters long");

        string tokens = string.Join(" and ", Enumerable.Repeat("Rush", (LinqQueryLimits.MaxTokens / 2) + 1));
        tokens.Length.Should().BeLessThan(LinqQueryLimits.MaxWhereLength);
        Where(tokens)!.Message.Should().Contain("tokens");

        string nested = new string('(', LinqQueryLimits.MaxDepth + 1) + "Rush" + new string(')', LinqQueryLimits.MaxDepth + 1);
        Where(nested)!.Message.Should().Contain("nests");

        string deepest = new string('(', LinqQueryLimits.MaxDepth) + "Rush" + new string(')', LinqQueryLimits.MaxDepth);
        Where(deepest).Should().BeNull("the cap is a limit, not an off-by-one");
    }

    [Fact]
    public void A_member_named_like_a_framework_type_is_let_through_and_what_follows_it_is_bound_on_its_own_type()
    {
        // The decoy document has members called System, IO, File, Math, Convert, Environment and DateTime, so the
        // screen admits those words as the members they are - and binds the next name on the member's own type, a
        // string, which has no IO. A flat list of the document's names would have let System.IO.File through.
        LinqDocumentGraph decoy = LinqTestStore.Graph<LinqDecoy>();

        LinqTextScreen.Check(LinqClause.Where, "System == \"x\" and File.StartsWith(\"a\")", decoy).Should().BeNull();

        LinqRefusal? refusal = LinqTextScreen.Check(LinqClause.Where, "System.IO.File.ReadAllText(\"c:/x\") == Name", decoy);

        refusal.Should().NotBeNull();
        refusal!.Message.Should().Contain("'IO'");
        refusal.Position.Should().Be(7);
    }

    [Fact]
    public void Every_word_Dynamic_LINQ_reads_as_a_keyword_is_refused_before_it_is_bound()
    {
        // A document member named like a Dynamic LINQ function would be parsed as the function, not the member, so
        // the word is refused before the screen looks for a member of that name.
        foreach (string word in LinqTextScreen.Reserved)
        {
            Where(word + " == \"x\"").Should().NotBeNull("'{0}' is reserved", word);
            Where("Customer." + word + " == \"x\"").Should().NotBeNull("'{0}' is reserved after a '.' too", word);
        }
    }

    /// <summary>
    /// Every name is bound on the type of what it is read from - the F7 security review's B1, from the screen's side.
    /// The full-pipeline proof, with a counting converter, is in <see cref="LinqHostileInputTests" />.
    /// </summary>
    [Theory]
    [InlineData("Quantity > 5", 0, "'Quantity'")]                       // a line's member, read off the order
    [InlineData("Lines.Any(Total > 5)", 10, "'Total'")]                 // the order's member, read off a line
    [InlineData("Customer.Quantity > 5", 9, "'Quantity'")]              // another type's member, after a dot
    [InlineData("Customer.Count > 1", 9, "'Count'")]                    // a framework name on an object
    [InlineData("Customer.Value == null", 9, "'Value'")]                // and another
    [InlineData("Total.Value > 1", 6, "'Value'")]                       // on a value that is not nullable
    [InlineData("Name.HasValue", 5, "'HasValue'")]                      // on a string
    [InlineData("Customer.StartsWith(\"A\")", 9, "'StartsWith'")]       // a string method on an object
    [InlineData("Name.Any(it == 'a')", 5, "'Any'")]                     // a collection method on a string
    [InlineData("Lines.Any(it == \"x\")", 10, "'it'")]                  // an object used as a value
    [InlineData("it == \"x\"", 0, "'it'")]                              // the document used as a value
    [InlineData("Lines.Any(Uri \"http://example.invalid/\" != null)", 10, "'Uri'")]
    [InlineData("Tags.Contains(Sku)", 14, "'Sku'")]                     // Contains reads its argument where the call is
    public void A_name_is_bound_on_the_type_it_is_read_from(string text, int position, string mentions)
    {
        LinqRefusal? refusal = Where(text);

        refusal.Should().NotBeNull("'{0}' names something the receiver it binds on does not have", text);
        refusal!.Position.Should().Be(position, "the caret goes under the name in '{0}'", text);
        refusal.Message.Should().Contain(mentions);
    }

    [Theory]
    [InlineData("Name \"x\" == \"x\"", 5)]
    [InlineData("Name Notes == \"x\"", 5)]
    [InlineData("Total 5 > 1", 6)]
    [InlineData("\"a\" \"b\" == Name", 4)]
    [InlineData("(Rush) Rush", 7)]
    [InlineData("(Rush)(Rush)", 6)]
    [InlineData("Lines.Any(Sku \"http://example.invalid/\" != null)", 14)]
    public void Two_values_in_a_row_are_refused_so_no_type_name_can_convert_the_string_after_it(string text, int position)
    {
        LinqRefusal? refusal = Where(text);

        refusal.Should().NotBeNull("'{0}' puts two values side by side", text);
        refusal!.Position.Should().Be(position);
    }

    [Theory]
    [InlineData("Lines.Any(Sku == \"A\" and Quantity > 1)")]
    [InlineData("Customer.Name.ToLower().Length > 2")]
    [InlineData("Lines.Count > 1 and Tags.Count() > 0")]
    [InlineData("Tags.Any(it.Length > 2)")]
    [InlineData("Priority.HasValue and Priority.Value > 1")]
    [InlineData("it.Name == \"a\"")]
    [InlineData("Name not in (\"a\", \"b\")")]
    [InlineData("not (Rush or Total > 5)")]
    public void Names_bound_where_they_are_read_pass(string text)
    {
        Where(text).Should().BeNull();
    }

    [Theory]
    [InlineData("Customer.Quantity")]
    [InlineData("Lines")]
    [InlineData("Customer.Name.Length")]
    public void An_ordering_binds_each_step_on_the_step_before_it(string text)
    {
        OrderBy(text).Should().NotBeNull("'{0}' is not a member path the screen can bind to a scalar member", text);
    }
}
