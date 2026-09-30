using MartenStudio.Services.Database;

namespace MartenStudio.Tests.Database;

/// <summary>
/// F1: every <c>schema.</c> qualifier naming a withheld schema is masked wherever Postgres printed it -
/// bare or quoted, inside a literal, a comment or a function body - and nothing else is touched.
/// </summary>
/// <remarks>
/// The inputs are what Postgres' deparsers really print once the search path is pinned to
/// <c>pg_catalog</c> (measured on PG 17): <c>nextval('hr.seq'::regclass)</c>, <c>CHECK (hr.is_ok(c))</c>,
/// <c>CREATE INDEX … (hr.norm(id))</c>, <c>EXECUTE FUNCTION hr.audit()</c>, <c>FROM hr.salaries</c>.
/// </remarks>
public class WithheldNamesTests
{
    private const string Mask = WithheldNames.Token;

    private static readonly IReadOnlySet<string> Withheld = new HashSet<string>(StringComparer.Ordinal) { "hr", "Legal Dept" };

    [Theory]
    [InlineData("nextval('hr.seq'::regclass)", "nextval('" + Mask + ".seq'::regclass)")]
    [InlineData("CHECK (hr.is_ok(c))", "CHECK (" + Mask + ".is_ok(c))")]
    [InlineData("CREATE INDEX t_idx ON lk.t USING btree (hr.norm(id))", "CREATE INDEX t_idx ON lk.t USING btree (" + Mask + ".norm(id))")]
    [InlineData("CREATE TRIGGER t BEFORE INSERT ON lk.t FOR EACH ROW WHEN (hr.is_ok(new.id)) EXECUTE FUNCTION lk.trg()",
        "CREATE TRIGGER t BEFORE INSERT ON lk.t FOR EACH ROW WHEN (" + Mask + ".is_ok(new.id)) EXECUTE FUNCTION lk.trg()")]
    [InlineData(" SELECT id\n   FROM hr.salaries;", " SELECT id\n   FROM " + Mask + ".salaries;")]
    [InlineData("SETOF hr.salaries", "SETOF " + Mask + ".salaries")]
    [InlineData("hr.grade[]", Mask + ".grade[]")]
    [InlineData("x OPERATOR(hr.===) y", "x OPERATOR(" + Mask + ".===) y")]
    [InlineData("\"Legal Dept\".contracts", Mask + ".contracts")]
    [InlineData("HR.seq", Mask + ".seq")]
    [InlineData("hr . salaries", Mask + " . salaries")]
    public void A_qualifier_naming_a_withheld_schema_is_masked(string text, string expected) =>
        WithheldNames.Redact(text, Withheld).Should().Be(expected);

    [Theory]
    [InlineData("character varying(120)")]
    [InlineData("legacy.order_status")]
    [InlineData("nextval('legacy.orders_id_seq'::regclass)")]
    [InlineData("hr_archive.x")]
    [InlineData("shr.x")]
    [InlineData("\"hr\"")]
    [InlineData("\"HR\".x")]
    [InlineData("hr")]
    [InlineData("select 'hr department' as label")]
    [InlineData("SET search_path TO 'hr, public'")]
    [InlineData("")]
    public void Nothing_else_is_touched(string text) =>
        WithheldNames.Redact(text, Withheld).Should().Be(text);

    /// <summary>
    /// A literal that is exactly a withheld schema's name is masked whole: it is how
    /// <c>pg_get_functiondef</c> prints each item of a routine's <c>SET search_path</c> (measured on PG 17:
    /// <c>SET search_path TO 'hr', 'Legal Dept', 'public'</c>), and how a <c>regnamespace</c> constant is
    /// printed. <c>'hr, public'</c> is one schema called that, not <c>hr</c>, and is left alone above.
    /// </summary>
    [Theory]
    [InlineData(" SET search_path TO 'hr', 'Legal Dept', 'public'", " SET search_path TO '" + Mask + "', '" + Mask + "', 'public'")]
    [InlineData("where n.oid = 'hr'::regnamespace", "where n.oid = '" + Mask + "'::regnamespace")]
    [InlineData("select 'HR' as department", "select '" + Mask + "' as department")]
    public void A_literal_that_is_exactly_a_withheld_schemas_name_is_masked(string text, string expected) =>
        WithheldNames.Redact(text, Withheld).Should().Be(expected);

    [Fact]
    public void A_null_stays_null_and_an_empty_set_masks_nothing()
    {
        WithheldNames.Redact(null, Withheld).Should().BeNull();
        WithheldNames.Redact("hr.salaries", new HashSet<string>()).Should().Be("hr.salaries");
    }

    /// <summary>A function body is a dollar-quoted literal, and it is read as SQL too - comments and all.</summary>
    [Fact]
    public void A_function_bodys_qualifiers_are_masked_as_well()
    {
        const string definition =
            "CREATE OR REPLACE FUNCTION legacy.peek()\n RETURNS SETOF hr.salaries\n LANGUAGE plpgsql\nAS $function$\n" +
            "begin\n  -- reads hr.salaries, and 'the \"quote\" in this comment' must not swallow anything\n" +
            "  return query select * from hr.salaries where note <> 'it''s hr.x';\nend\n$function$";

        string redacted = WithheldNames.Redact(definition, Withheld)!;

        redacted.Should().NotContain("hr.");
        redacted.Should().Contain("RETURNS SETOF " + Mask + ".salaries")
            .And.Contain("-- reads " + Mask + ".salaries")
            .And.Contain("select * from " + Mask + ".salaries")
            .And.Contain("'it''s " + Mask + ".x'")
            .And.StartWith("CREATE OR REPLACE FUNCTION legacy.peek()");
    }

    /// <summary>
    /// An unmatched quote in a literal or a comment must not make the walk skip a qualifier after it - the
    /// one way a lexer that only knew about identifiers would miss one.
    /// </summary>
    [Theory]
    [InlineData("select '\"' || hr.salaries.x || '\"'")]
    [InlineData("/* a \" quote */ select hr.x")]
    [InlineData("select 'a\" hr.y \"b'")]
    [InlineData("select $$ it's $$ || hr.x")]
    [InlineData("select E'\\' hr.x'")]
    public void An_unmatched_quote_never_hides_a_qualifier(string text) =>
        WithheldNames.Redact(text, Withheld).Should().NotContain("hr.");

    [Fact]
    public void An_unterminated_literal_or_comment_is_still_masked_to_its_end()
    {
        WithheldNames.Redact("select 'hr.x", Withheld).Should().Be("select '" + Mask + ".x");
        WithheldNames.Redact("select 1 /* hr.x", Withheld).Should().Be("select 1 /* " + Mask + ".x");
        WithheldNames.Redact("select \"hr.x", Withheld).Should().NotContain("hr.");
    }

    /// <summary>A <c>search_path</c> names schemas bare, with no dot after them: each item is masked on its own.</summary>
    [Fact]
    public void A_search_path_setting_is_masked_item_by_item()
    {
        IReadOnlyList<string> config = WithheldNames.RedactConfig(
            ["search_path=hr, public", "search_path=\"Legal Dept\", \"$user\"", "work_mem=64MB", "app.note=see hr.salaries"],
            Withheld);

        config.Should().Equal(
            "search_path=" + Mask + ", public",
            "search_path=" + Mask + ", \"$user\"",
            "work_mem=64MB",
            "app.note=see " + Mask + ".salaries");
    }

    /// <summary>
    /// DB-1-fix re-review, item 3: <c>$€$</c> is a dollar tag to Postgres (<c>dolq_start</c> takes any high
    /// character), so a walk that did not think so read the body as SQL, took the quote inside it for an
    /// identifier, and let the qualifier inside that "identifier" through.
    /// </summary>
    [Theory]
    [InlineData("select $€$ \"x $€$ || hr.secret || $€$ \" $€$")]
    [InlineData("select $é1$ hr.secret $é1$")]
    [InlineData("select $_€_$ \" $_€_$ || hr.secret || $_€_$ \" $_€_$")]
    public void A_dollar_tag_with_a_high_character_is_a_tag(string text) =>
        WithheldNames.Redact(text, Withheld).Should().Contain(Mask).And.NotContain("hr.");

    /// <summary>
    /// And a quoted identifier's own text is walked too: what looks like one may be the inside of a body the
    /// walk misread, and a column alias spelled like a withheld qualifier costs a word to mask.
    /// </summary>
    [Fact]
    public void A_quoted_identifiers_content_is_masked_as_free_text()
    {
        WithheldNames.Redact("select 1 as \"hr.secret\"", Withheld).Should().Be("select 1 as \"" + Mask + ".secret\"");
        WithheldNames.Redact("select 1 as \"a \"\"quoted\"\" hr.x\"", Withheld).Should().Be("select 1 as \"a \"\"quoted\"\" " + Mask + ".x\"");
        WithheldNames.Redact("select \"hr\".\"secret\", \"hr\" from t", Withheld).Should().Be("select " + Mask + ".\"secret\", \"hr\" from t");
    }

    [Fact]
    public void Free_text_is_masked_without_assuming_any_SQL()
    {
        WithheldNames.RedactText("A copy of hr.salaries, refreshed nightly.", Withheld)
            .Should().Be("A copy of " + Mask + ".salaries, refreshed nightly.");
        WithheldNames.RedactText("Don't touch \"Legal Dept\".contracts", Withheld)
            .Should().Be("Don't touch " + Mask + ".contracts");
    }
}
