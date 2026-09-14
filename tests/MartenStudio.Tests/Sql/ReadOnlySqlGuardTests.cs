using MartenStudio.Internal.Sql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The guard theory of plan §5.4. Every row is a statement somebody could paste into the console and the
/// verdict the <em>parser</em> is supposed to reach — never the verdict the database reaches, which is a
/// separate thing the live tests assert.
/// </summary>
/// <remarks>
/// The interesting rows are the ones that pass. <c>WITH x AS (DELETE …) SELECT</c> and
/// <c>SELECT … FOR UPDATE</c> both start with an allowed keyword and both write; the parser lets them
/// through on purpose, and <c>SET TRANSACTION READ ONLY</c> is what refuses them, with SQLSTATE 25006.
/// A guard that tried to catch those would be claiming to be a security boundary it cannot be (D13).
/// </remarks>
public class ReadOnlySqlGuardTests
{
    public static TheoryData<string, string> Statements => new()
    {
        // --- allowed: the five shapes, in the ways they are actually typed -----------------------------
        { "select 1", nameof(SqlRejectionReason.None) },
        { "SELECT * FROM mt_doc_customer", nameof(SqlRejectionReason.None) },
        { "sElEcT 1", nameof(SqlRejectionReason.None) },
        { "   \n\t select 1", nameof(SqlRejectionReason.None) },
        { "-- what this does\nselect 1", nameof(SqlRejectionReason.None) },
        { "/* a block comment */ select 1", nameof(SqlRejectionReason.None) },
        { "/* nested /* deeper */ still a comment */ select 1", nameof(SqlRejectionReason.None) },
        { "with x as (select 1) select * from x", nameof(SqlRejectionReason.None) },
        { "explain select 1", nameof(SqlRejectionReason.None) },
        { "explain analyze select 1", nameof(SqlRejectionReason.None) },
        { "explain (analyze, buffers, format json) select 1", nameof(SqlRejectionReason.None) },
        { "table mt_doc_customer", nameof(SqlRejectionReason.None) },
        { "values (1), (2)", nameof(SqlRejectionReason.None) },
        { "select 1;", nameof(SqlRejectionReason.None) },
        { "select 1;;", nameof(SqlRejectionReason.None) },
        { "select 1; -- and nothing else", nameof(SqlRejectionReason.None) },
        { "select 1;\n/* done */", nameof(SqlRejectionReason.None) },
        { "select pg_sleep(10)", nameof(SqlRejectionReason.None) },

        // Semicolons that are not statement separators.
        { "select ';'", nameof(SqlRejectionReason.None) },
        { "select 'it''s fine; really'", nameof(SqlRejectionReason.None) },
        { "select \"a;column\" from t", nameof(SqlRejectionReason.None) },
        { "select $$a;b$$", nameof(SqlRejectionReason.None) },
        { "select $tag$a;b$tag$", nameof(SqlRejectionReason.None) },
        { "select $1", nameof(SqlRejectionReason.None) },

        // Allowed by the parser, refused by the transaction. These are the point of D13.
        { "WITH x AS (DELETE FROM t RETURNING *) SELECT * FROM x", nameof(SqlRejectionReason.None) },
        { "with x as (update t set a = 1 returning *) select * from x", nameof(SqlRejectionReason.None) },
        { "with x as (insert into t values (1) returning *) select * from x", nameof(SqlRejectionReason.None) },
        { "select * from t for update", nameof(SqlRejectionReason.None) },
        { "select nextval('some_sequence')", nameof(SqlRejectionReason.None) },

        // --- refused: not one of the five shapes -------------------------------------------------------
        { "delete from mt_doc_customer", nameof(SqlRejectionReason.DisallowedStatement) },
        { "insert into mt_doc_customer (id) values ('x')", nameof(SqlRejectionReason.DisallowedStatement) },
        { "update mt_doc_customer set data = '{}'", nameof(SqlRejectionReason.DisallowedStatement) },
        { "truncate mt_doc_customer", nameof(SqlRejectionReason.DisallowedStatement) },
        { "drop table mt_doc_customer", nameof(SqlRejectionReason.DisallowedStatement) },
        { "create table t (i int)", nameof(SqlRejectionReason.DisallowedStatement) },
        { "alter table t add column c int", nameof(SqlRejectionReason.DisallowedStatement) },
        { "copy t from '/etc/passwd'", nameof(SqlRejectionReason.DisallowedStatement) },
        { "call some_procedure()", nameof(SqlRejectionReason.DisallowedStatement) },
        { "do $$ begin delete from t; end $$", nameof(SqlRejectionReason.DisallowedStatement) },
        { "set role postgres", nameof(SqlRejectionReason.DisallowedStatement) },
        { "begin", nameof(SqlRejectionReason.DisallowedStatement) },
        { "commit", nameof(SqlRejectionReason.DisallowedStatement) },
        { "vacuum full", nameof(SqlRejectionReason.DisallowedStatement) },
        { "grant select on t to public", nameof(SqlRejectionReason.DisallowedStatement) },
        { "refresh materialized view mv", nameof(SqlRejectionReason.DisallowedStatement) },
        { "/* select */ delete from t", nameof(SqlRejectionReason.DisallowedStatement) },
        { "-- select 1\ndelete from t", nameof(SqlRejectionReason.DisallowedStatement) },

        // EXPLAIN ANALYZE runs what it is given, so the target is held to the same list.
        { "explain analyze delete from t", nameof(SqlRejectionReason.DisallowedStatement) },
        { "explain (analyze) update t set a = 1", nameof(SqlRejectionReason.DisallowedStatement) },

        // --- refused: more than one statement ----------------------------------------------------------
        { "select 1; drop table t", nameof(SqlRejectionReason.MultipleStatements) },
        { "select 1; select 2", nameof(SqlRejectionReason.MultipleStatements) },
        { "select 'a'; delete from t", nameof(SqlRejectionReason.MultipleStatements) },
        { "select 1;; select 2", nameof(SqlRejectionReason.MultipleStatements) },

        // --- refused: nothing to run -------------------------------------------------------------------
        { "", nameof(SqlRejectionReason.Empty) },
        { "    \n  ", nameof(SqlRejectionReason.Empty) },
        { "-- just a comment", nameof(SqlRejectionReason.Empty) },
        { "/* just a comment */", nameof(SqlRejectionReason.Empty) },

        // --- refused: the text does not close ----------------------------------------------------------
        { "select 'abc", nameof(SqlRejectionReason.UnterminatedString) },
        { "select \"abc", nameof(SqlRejectionReason.UnterminatedString) },
        { "/* never closed select 1", nameof(SqlRejectionReason.UnterminatedComment) },
        { "select $$abc", nameof(SqlRejectionReason.UnterminatedDollarQuote) },
    };

    [Theory]
    [MemberData(nameof(Statements))]
    public void The_guard_reaches_the_stated_verdict(string sql, string expected)
    {
        // The verdict travels as a name rather than as the enum itself: xunit requires a public test
        // class, and every type in this area of the studio is internal (D12).
        var result = ReadOnlySqlGuard.Check(sql);

        result.Reason.ToString().Should().Be(expected, $"of: {sql}");
        result.Allowed.Should().Be(expected == nameof(SqlRejectionReason.None));
    }

    [Fact]
    public void A_refusal_names_the_token_and_where_it_is()
    {
        var result = ReadOnlySqlGuard.Check("  \n delete from t");

        result.Allowed.Should().BeFalse();
        result.Token.Should().Be("delete");
        result.Position.Should().Be(4);
        result.Message.Should().Contain("select, with, explain, table, values");
    }

    [Fact]
    public void A_second_statement_is_named_so_the_user_can_split_it()
    {
        var result = ReadOnlySqlGuard.Check("select 1; drop table mt_doc_customer");

        result.Reason.Should().Be(SqlRejectionReason.MultipleStatements);
        result.Token.Should().Be("drop");
        result.Message.Should().Contain("one statement at a time");
    }

    [Fact]
    public void The_allow_list_is_exactly_the_five_shapes()
    {
        ReadOnlySqlGuard.AllowedStatements.Should().BeEquivalentTo(
            ["select", "with", "explain", "table", "values"],
            "adding a sixth shape is a security decision, not a convenience");
    }
}
