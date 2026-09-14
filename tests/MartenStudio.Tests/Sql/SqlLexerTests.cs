using MartenStudio.Internal.Sql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The one lexical walk, asserted on its own rather than only through the two guards that drive it.
/// </summary>
/// <remarks>
/// <para>
/// Every row here is a place where Postgres does something a hand-written scanner gets wrong, and every
/// one of them was a live-proven escape before it was a rule. They used to be asserted twice, once through
/// <see cref="ReadOnlySqlGuard" /> and once through <see cref="QuerySqlComposer" />, because there were two
/// scanners - and the carriage-return defect still had to be found twice and fixed twice. There is one
/// scanner now, so this is where the lexical facts live; the guard suites keep asserting their own
/// verdicts, which is what they are about.
/// </para>
/// <para>
/// <b>Every carriage return in this file is written as <c>\r</c> and never as a literal</b>: the repository
/// normalises line endings to LF on checkout, so a literal CR in a source file would silently become
/// something else and the test would pass for the wrong reason.
/// </para>
/// </remarks>
public class SqlLexerTests
{
    /// <summary>Every word the lexer found, in order.</summary>
    private static List<string> Words(string text)
    {
        List<string> words = [];
        var lexer = new SqlLexer(text);

        while (lexer.TryRead(out SqlToken token))
        {
            if (token.Kind == SqlTokenKind.Word)
            {
                words.Add(token.Text);
            }
        }

        return words;
    }

    /// <summary>The first thing the lexer could not read, or <see langword="null" />.</summary>
    private static SqlToken? FaultOf(string text)
    {
        var lexer = new SqlLexer(text);

        while (lexer.TryRead(out SqlToken token))
        {
            if (token.Kind == SqlTokenKind.Unreadable)
            {
                return token;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------------------------------------
    // The five things Postgres does that a scanner gets wrong
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Postgres' own lexer spells <c>non_newline</c> as <c>[^\n\r]</c>, so a lone carriage return ends a
    /// <c>--</c> comment and everything after it is live SQL. A scanner that read on to the LF hid the rest
    /// of that line from every rule while Postgres ran it - which is the defect that re-defeated the clause
    /// guard after all the others had been fixed.
    /// </summary>
    [Theory]
    [InlineData("alpha --\rbeta", new[] { "alpha", "beta" })]
    [InlineData("alpha --\nbeta", new[] { "alpha", "beta" })]
    [InlineData("alpha --\r\nbeta", new[] { "alpha", "beta" })]
    [InlineData("alpha -- beta", new[] { "alpha" })]
    [InlineData("alpha --\r-- beta\rgamma", new[] { "alpha", "gamma" })]
    public void A_line_comment_ends_at_a_carriage_return_as_well_as_a_line_feed(string text, string[] expected) =>
        Words(text).Should().Equal(expected);

    [Fact]
    public void Block_comments_nest_the_way_postgres_nests_them()
    {
        Words("a /* outer /* inner */ still */ b").Should().Equal("a", "b");
        Words("a /* one */ b /* two */ c").Should().Equal("a", "b", "c");

        SqlToken? unclosed = FaultOf("a /* never closed b");

        unclosed.Should().NotBeNull();
        unclosed!.Value.Fault.Should().Be(SqlLexFault.UnterminatedComment);
    }

    [Fact]
    public void A_doubled_quote_is_an_escaped_quote_and_does_not_end_the_literal()
    {
        Words("a = 'it''s x' and b").Should().Equal("a", "and", "b");
        Words("\"col\"\"umn\" and b").Should().Equal("and", "b");
    }

    /// <summary>
    /// With the default <c>standard_conforming_strings = on</c> a backslash is an ordinary character, so
    /// the quote after <c>'a\</c> <em>closes</em> the string. Treating it as an escape made
    /// <c>'a\'; drop table t --'</c> look like one statement while Npgsql split it into two and ran both.
    /// </summary>
    [Fact]
    public void A_backslash_escapes_inside_an_E_literal_and_nowhere_else()
    {
        // Plain literal: the quote after the backslash closes it, so `visible` is a bare word.
        Words(@"a = 'x\' visible").Should().Equal("a", "visible");

        // E'' literal: the backslash escapes the quote, so the same text is all inside the string. The
        // `E` itself is a bare word - it is read before the quote is - which is harmless and is why the
        // interesting half is that `hidden` is not one.
        Words(@"a = E'x\' hidden'").Should().Equal("a", "E");
        Words(@"a = e'x\' hidden'").Should().Equal("a", "e");

        // The e has to be a token of its own - `date'…'` also ends in an e and is a typed literal, so the
        // quote after the backslash closes it and what follows is live text again.
        Words(@"a = date'x\' visible'").Should().Equal("a", "date", "visible");
    }

    /// <summary>
    /// A dollar-quote tag follows the rules of an unquoted identifier, so <c>$1</c> is a parameter
    /// placeholder. Reading <c>$1$…$1$</c> as a body let a <c>;</c> hide between two placeholders.
    /// </summary>
    [Fact]
    public void A_dollar_tag_may_not_begin_with_a_digit()
    {
        Words("x = $1$ hidden $1$").Should().Equal("x", "hidden");
        Words("x = $$ hidden $$").Should().Equal("x");
        Words("x = $tag$ hidden $tag$").Should().Equal("x");
        Words("x = $_t1$ hidden $_t1$").Should().Equal("x");
    }

    // ------------------------------------------------------------------------------------------------
    // What the callers read off it
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_word_carries_where_it_was_and_how_deep_in_brackets()
    {
        List<SqlToken> tokens = [];
        var lexer = new SqlLexer("a and (b or (c)) and d");

        while (lexer.TryRead(out SqlToken token))
        {
            tokens.Add(token);
        }

        SqlToken b = tokens.Single(x => x.Text == "b");
        SqlToken c = tokens.Single(x => x.Text == "c");
        SqlToken d = tokens.Single(x => x.Text == "d");

        b.Depth.Should().Be(1);
        c.Depth.Should().Be(2);
        d.Depth.Should().Be(0);
        d.Start.Should().Be("a and (b or (c)) and ".Length);
        d.End.Should().Be(d.Start + 1);

        lexer.Depth.Should().Be(0, "the clause balances");
    }

    /// <summary>
    /// A <c>)</c> that closes nothing leaves the depth at zero rather than going negative, and comes back
    /// as a token at depth zero - which is exactly the fact the clause guard's balance rule reads.
    /// </summary>
    [Fact]
    public void A_close_bracket_that_closes_nothing_does_not_take_the_depth_negative()
    {
        List<SqlToken> tokens = [];
        var lexer = new SqlLexer("a) or (b");

        while (lexer.TryRead(out SqlToken token))
        {
            tokens.Add(token);
        }

        tokens.Should().ContainSingle(x => x.Is(')')).Which.Depth.Should().Be(0);
        tokens.Single(x => x.Text == "b").Depth.Should().Be(1);
        lexer.Depth.Should().Be(1, "the '(' was never closed");
    }

    /// <remarks>
    /// The fault travels as a name rather than as the enum itself: xunit requires a public test method and
    /// every type in this area of the studio is internal (D12), which is the same reason
    /// <see cref="ReadOnlySqlGuardTests" /> spells its verdicts out as strings.
    /// </remarks>
    [Theory]
    [InlineData("a = 'never closed", nameof(SqlLexFault.UnterminatedString), "'", 4)]
    [InlineData("a = \"never closed", nameof(SqlLexFault.UnterminatedString), "\"", 4)]
    [InlineData("a /* never closed", nameof(SqlLexFault.UnterminatedComment), "/*", 2)]
    [InlineData("a = $tag$ never closed", nameof(SqlLexFault.UnterminatedDollarQuote), "$tag$", 4)]
    public void Text_that_does_not_close_is_a_fault_that_names_it_and_points_at_it(
        string text,
        string fault,
        string token,
        int position)
    {
        SqlToken? found = FaultOf(text);

        found.Should().NotBeNull();
        found!.Value.Kind.Should().Be(SqlTokenKind.Unreadable);
        found.Value.Fault.ToString().Should().Be(fault);
        found.Value.Text.Should().Be(token);
        found.Value.Start.Should().Be(position);
    }

    /// <summary>
    /// A caller that keeps reading past a fault gets "no more tokens" rather than the same fault for ever.
    /// An unterminated dollar body is the one that used to leave the position where it was, which would be
    /// an infinite loop in any caller that did not refuse on the spot.
    /// </summary>
    [Fact]
    public void A_walk_that_faulted_is_finished()
    {
        var lexer = new SqlLexer("a = $tag$ never closed");
        SqlToken fault = default;

        while (lexer.TryRead(out SqlToken token))
        {
            fault = token;
        }

        fault.Kind.Should().Be(SqlTokenKind.Unreadable, "the last thing the walk found is the fault");
        fault.Fault.Should().Be(SqlLexFault.UnterminatedDollarQuote);

        lexer.TryRead(out _).Should().BeFalse("and there is nothing after it, however often a caller asks");
    }

    // ------------------------------------------------------------------------------------------------
    // One scanner, two guards
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The point of extracting the lexer: the console guard and the clause guard reach the same lexical
    /// answer about the same text, because it is the same walk. Each row is a text where a scanner that
    /// disagreed with Postgres would let one of the two through - and the two used to be separate copies,
    /// so "the fix landed in one of them" was a real and unreviewable failure mode.
    /// </summary>
    /// <param name="tail">The text both guards are given, after a prefix each one needs.</param>
    /// <param name="hidden">
    /// Whether the <c>;</c> in the text is really inside a literal. When it is not, both guards have to see
    /// it; when it is, neither may.
    /// </param>
    [Theory]
    [InlineData("--\r; select 2", false)]
    [InlineData("--\n; select 2", false)]
    [InlineData("'x' = $1$;drop table t;--$1$", false)]
    [InlineData(@"'a\'; drop table t --'", false)]
    [InlineData("$$a;b$$ = 1", true)]
    [InlineData("$tag$a;b$tag$ = 1", true)]
    [InlineData("'it''s fine; really' = 1", true)]
    [InlineData("\"a;column\" = 1", true)]
    [InlineData("-- a;b\r\n1 = 1", true)]
    [InlineData("/* a;b */ 1 = 1", true)]
    public void The_two_guards_agree_about_where_the_literals_are(string tail, bool hidden)
    {
        // The console sees a whole statement; the clause guard sees a fragment. Same text in both, so the
        // only thing that can make the two answers differ is the lexing.
        SqlGuardResult console = ReadOnlySqlGuard.Check("select 1 where " + tail);
        int clauseSeparator = QuerySqlComposer.IndexOfStatementSeparator("1 = 1 and " + tail);

        if (hidden)
        {
            console.Reason.Should().NotBe(SqlRejectionReason.MultipleStatements, tail);
            clauseSeparator.Should().Be(-1, tail);
        }
        else
        {
            console.Reason.Should().Be(SqlRejectionReason.MultipleStatements, tail);
            clauseSeparator.Should().BeGreaterThanOrEqualTo(0, tail);
        }
    }

    /// <summary>
    /// And the same for the denylist, which is the other thing a hidden token would defeat: the console
    /// refuses <c>pg_advisory_lock</c> by name and so does a <c>where</c> clause, and both only see it
    /// because the lexer told them the <c>--</c> comment had already ended.
    /// </summary>
    [Fact]
    public void Both_guards_see_a_function_a_carriage_return_tried_to_hide()
    {
        ReadOnlySqlGuard.Check("select 1 where 'x' = 'y' --\r or pg_advisory_lock(1) is not null")
            .Reason.Should().Be(SqlRejectionReason.DisallowedFunction);

        QuerySqlComposer.CheckClause("1 = 1 --\r and pg_advisory_lock(1) is not null", allowNestedReads: true)
            .Reason.Should().Be(SqlRejectionReason.DisallowedFunction);
    }
}
