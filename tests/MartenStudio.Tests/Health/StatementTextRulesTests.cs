using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Query;

using static MartenStudio.Tests.Health.StatementBuilders;

namespace MartenStudio.Tests.Health;

/// <summary>
/// Security review item 1: a text read as the store's own role is shown only when it carries no constant as written and
/// is a plain query or write - on the shared lexer's tokens, so a comment hides nothing and a literal fakes nothing.
/// </summary>
public class StatementTextRulesTests
{
    [Theory]
    // What PostgreSQL 17 keeps of a COPY: its constants, as written.
    [InlineData("copy (select d.data from studio_sql.mt_doc_statementdoc d where d.data->>'ssn' = '123-45-6789') to stdout", StatementReads.WithheldUtility)]
    // What 13-15 keep of a CALL and a DECLARE.
    [InlineData("call studio_sql.give_raise('acme', 1000)", StatementReads.WithheldUtility)]
    [InlineData("declare c cursor for select * from t where secret = 'x'", StatementReads.WithheldUtility)]
    [InlineData("fetch 10 from c", StatementReads.WithheldUtility)]
    [InlineData("COPY studio_sql.mt_doc_statementdoc(\"id\", \"data\") FROM STDIN BINARY", StatementReads.WithheldUtility)]
    // A plain statement whose constant the extension did not replace, in every spelling of a string.
    [InlineData("select 1 from t where name = 'alice'", StatementReads.WithheldLiteral)]
    [InlineData("select 1 from t where name = E'al\\'ice'", StatementReads.WithheldLiteral)]
    [InlineData("select 1 from t where name = U&'\\0061lice'", StatementReads.WithheldLiteral)]
    [InlineData("select $tag$secret$tag$", StatementReads.WithheldLiteral)]
    [InlineData("select 1 /* it's a comment */ from t where a = $1", null)]
    [InlineData("-- Marten's own query\nselect d.data from t as d where CAST(d.data ->> $2 as integer) = $1", null)]
    [InlineData("select \"it's a column\" from t where a = $1", null)]
    [InlineData("insert into t (id, data) values ($1, $2) on conflict (id) do update set data = excluded.data returning mt_version", null)]
    [InlineData("with x as (select $1) select * from x", null)]
    [InlineData("select 'never closed", StatementReads.WithheldUnreadable)]
    [InlineData("   ", StatementReads.WithheldUnreadable)]
    public void A_text_is_withheld_when_it_keeps_a_constant_or_is_not_a_plain_statement(string text, string? withheld)
    {
        StatementReads.Withheld(text).Should().Be(withheld);
    }

    [Fact]
    public void Only_a_lexer_asked_to_reports_literals_and_every_other_still_walks_over_them()
    {
        const string Text = "select 'a', $b$x$b$ from t";

        List<SqlToken> reported = Tokens(new SqlLexer(Text, reportLiterals: true));
        reported.Where(static x => x.Kind == SqlTokenKind.Literal).Select(static x => x.Text).Should().Equal("'a'", "$b$x$b$");

        List<SqlToken> plain = Tokens(new SqlLexer(Text));
        plain.Should().NotContain(static x => x.Kind == SqlTokenKind.Literal, "the guards that walk over literals keep doing so");
        plain.Select(static x => x.Text).Should().Equal("select", ",", "from", "t");
    }

    [Fact]
    public void A_finding_on_a_statement_whose_text_is_withheld_keeps_its_numbers_and_says_why()
    {
        string copy = "copy (select d.data from " + Doc.Schema + "." + Doc.Table + " d where d.data->>'ssn' = '123-45-6789') to stdout";

        HealthFinding finding = new ExpensiveStatementsCheck()
            .Evaluate(new ExpensiveStatementFacts(Read(Statement(1, copy, [Doc], 100, 0.5 * 3_600_000)), [], []), TableCheckBuilders.Evaluation())
            .Findings.Should().ContainSingle().Subject;

        HealthEvidence statement = finding.Evidence.Single(static x => x.Label == "Statement");
        statement.Value.Should().Be(StatementReads.WithheldUtility);
        statement.Statement.Should().BeNull("nothing of the text is carried for the report to show");
        statement.Gate.Should().Be(HealthGate.Activity);
        finding.Evidence.Should().NotContain(static x => x.Value.Contains("123-45-6789", StringComparison.Ordinal));
        finding.Evidence.Single(static x => x.Label == "Calls").Value.Should().Be("100");
    }

    [Fact]
    public void A_plain_text_with_placeholders_is_still_carried_for_its_visitor()
    {
        ObservedStatement statement = Statement(1, SelectOnDoc, [Doc], 100, 1);

        statement.TextWithheld.Should().BeNull();
        HealthEvidence evidence = statement.Evidence("Statement");
        evidence.Statement!.Text.Should().Be(SelectOnDoc);
        evidence.Value.Should().Be(HealthEvidence.StatementPending);
    }

    [Fact]
    public void T23_withholds_the_same_way()
    {
        string call = "select studio_sql.secret_lookup('acme') from " + Doc.Schema + "." + Doc.Table;

        HealthFinding finding = new JitShareCheck()
            .Evaluate(new JitFacts(Read(Statement(9, call, [Doc], 150, 1500, jitMs: 600))), TableCheckBuilders.Evaluation())
            .Findings.Single();

        finding.Evidence.Single(static x => x.Label == "Statement 1").Value.Should().Be(StatementReads.WithheldLiteral);
        finding.Evidence.Should().NotContain(static x => x.Value.Contains("acme", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------
    // Re-review F2: a select ... into is not a plain query, and the numeric residual is said
    // ------------------------------------------------------------------------------------------------

    /// <summary>PostgreSQL 13-15 record a <c>select … into</c> as a utility statement, with its constants as written.</summary>
    [Fact]
    public void A_select_into_with_a_numeric_constant_as_written_is_withheld()
    {
        StatementReads.Withheld("select * into t2 from s.mt_doc_x where id = 4111111111111111").Should().NotBeNull()
            .And.Be(StatementReads.WithheldUtility);
    }

    [Theory]
    [InlineData("select a, b into temp t from s.x", StatementReads.WithheldUtility)]
    [InlineData("SELECT a INTO UNLOGGED t FROM s.x", StatementReads.WithheldUtility)]
    [InlineData("with x as (select $1) select * into t from x", StatementReads.WithheldUtility)]
    // A write's target is not a select's into, and an into inside parentheses is not top-level.
    [InlineData("insert into t (id) select id from s where id = $1", null)]
    [InlineData("with x as (select $1) insert into t select * from x", null)]
    [InlineData("merge into t using s on t.id = s.id when matched then do nothing", null)]
    [InlineData("select \"into\" from t where a = $1", null)]
    // The residual: a digit run is not withheld, because a normalised text has them too.
    [InlineData("select d.data from t as d order by 1 limit $1", null)]
    [InlineData("select cast(d.data ->> $1 as varchar(10)) from t as d", null)]
    public void Only_a_top_level_into_that_is_not_a_writes_target_is_withheld(string text, string? withheld)
    {
        StatementReads.Withheld(text).Should().Be(withheld);
    }

    // ------------------------------------------------------------------------------------------------
    // Re-review F3: comments are another application's free text, and are never shown
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("select 1 /* user='alice@example.com',route='/orders' */ from t", "select 1 /* ‹withheld› */ from t")]
    [InlineData("select 1 -- user=alice@example.com\nfrom t", "select 1 /* ‹withheld› */\nfrom t")]
    [InlineData("select 1 -- ends at a carriage return too\rfrom t", "select 1 /* ‹withheld› */\rfrom t")]
    [InlineData("select 1 -- the last line", "select 1 /* ‹withheld› */")]
    [InlineData("select 1 /* outer /* inner */ still outer */ from t", "select 1 /* ‹withheld› */ from t")]
    [InlineData("/* a */ select 1 /* b */", "/* ‹withheld› */ select 1 /* ‹withheld› */")]
    // A comment marker inside a string or a quoted identifier is not a comment.
    [InlineData("select '/* not a comment */', \"-- nor this\" from t", "select '/* not a comment */', \"-- nor this\" from t")]
    [InlineData("select d.data from t as d where d.id = $1", "select d.data from t as d where d.id = $1")]
    public void Every_comment_of_a_shown_text_is_masked_and_nothing_else(string text, string shown)
    {
        StatementReads.MaskComments(text).Should().Be(shown);
    }

    [Fact]
    public void A_statements_evidence_carries_its_text_with_the_comments_masked()
    {
        string tagged = "select d.id, d.data from " + Doc.Schema + "." + Doc.Table + " as d /* user='alice@example.com' */ where d.data ->> $2 = $1";

        HealthEvidence evidence = Statement(1, tagged, [Doc], 100, 1).Evidence("Statement");

        evidence.Statement!.Text.Should().NotContain("alice").And.Contain(StatementReads.CommentMask).And.Contain(Doc.Table);
    }

    [Fact]
    public void A_lexer_asked_to_report_comments_adds_them_and_changes_nothing_else()
    {
        const string Text = "select a /* x */, b -- y\nfrom t where c = 'z' /* outer /* inner */ */";

        List<SqlToken> plain = Tokens(new SqlLexer(Text));
        Tokens(new SqlLexer(Text, reportLiterals: false, reportComments: false)).Should().Equal(plain, "off is the old walk");

        List<SqlToken> reported = Tokens(new SqlLexer(Text, reportLiterals: false, reportComments: true));
        reported.Where(static x => x.Kind == SqlTokenKind.Comment).Select(static x => x.Text)
            .Should().Equal("/* x */", "-- y", "/* outer /* inner */ */");
        reported.Where(static x => x.Kind != SqlTokenKind.Comment).Should().Equal(plain, "every other token is where it was");
    }

    // ------------------------------------------------------------------------------------------------
    // Re-review F4: a withheld text is never planned
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_statement_whose_text_is_withheld_is_never_planned_and_its_plan_evidence_says_why()
    {
        string literal = "select d.data from " + Doc.Schema + "." + Doc.Table + " as d where d.data ->> 'Ssn' = $1";
        StatementRead read = Read(Statement(1, literal, [Doc], 100, 0.5 * 3_600_000), Statement(2, SelectOnDoc, [Doc], 100, 0.4 * 3_600_000));

        ExpensiveStatementsCheck.PlanCandidates(read).Select(static x => x.QueryId).Should().Equal(2L);

        // Even handed a plan of it, the finding reads none: its literal key would reach the Plan, the advice and the remedy.
        StatementPlan plan = new(1, [new ExplainPlanScan("Seq Scan", Doc.Table, null, "((data ->> 'Ssn'::text) = $1)", null)], null);
        HealthFinding finding = new ExpensiveStatementsCheck()
            .Evaluate(new ExpensiveStatementFacts(read, [plan], []), TableCheckBuilders.Evaluation())
            .Findings.Single(static x => x.Subject == "1");

        finding.Evidence.Single(static x => x.Label == "Plan").Value.Should().Be(ExpensiveStatementsCheck.NotPlannedWithheld);
        finding.Evidence.Should().NotContain(static x => x.Label == "Index advice");
        finding.Evidence.Should().NotContain(static x => x.Value.Contains("Ssn", StringComparison.Ordinal));
        finding.Remedy.Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------------
    // Re-review F7: the workload's first word is read by the lexer
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/* Marten */ create table t (id int)", false)]
    [InlineData("-- a migration\ncreate table t (id int)", false)]
    // Postgres ends a -- comment at a carriage return; the old walker read on to the line feed and missed the DDL.
    [InlineData("-- a migration\rcreate table t (id int)", false)]
    [InlineData("/* outer /* inner */ */ explain select 1", false)]
    [InlineData("BEGIN", false)]
    [InlineData("/* a request */ select d.data from t as d where d.id = $1", true)]
    [InlineData("(select 1) union (select 2)", true)]
    [InlineData("settings_table_read", true)]
    public void The_workload_is_told_from_a_utility_statement_by_the_first_word_postgres_reads(string text, bool workload)
    {
        StatementReads.IsWorkload(text).Should().Be(workload);
    }

    private static List<SqlToken> Tokens(SqlLexer lexer)
    {
        List<SqlToken> tokens = [];

        while (lexer.TryRead(out SqlToken token))
        {
            tokens.Add(token);
        }

        return tokens;
    }
}
