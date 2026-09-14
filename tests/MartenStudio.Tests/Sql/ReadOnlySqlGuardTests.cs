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
/// <para>
/// The <see cref="SqlRejectionReason.DisallowedFunction"/> rows are the other half of that argument. A
/// read-only transaction stops writes and nothing else, so <c>pg_terminate_backend</c>,
/// <c>pg_read_file</c>, <c>lo_export</c> and <c>dblink</c> all run happily inside one — verified against
/// Postgres 17, where the first of them killed the backend it was asked on. Those are refused by name,
/// which is policy rather than a boundary, and the message says so.
/// </para>
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
        { "select $_t1$a;b$_t1$", nameof(SqlRejectionReason.None) },
        { "select $1", nameof(SqlRejectionReason.None) },

        // A dollar-quote tag follows the rules of an unquoted identifier and cannot start with a digit:
        // to Postgres `$1$` is a parameter placeholder followed by a dollar sign, so the `;` between the
        // two is a real separator and the scanner must see it rather than skip a body that is not there.
        { "select 'x' = $1$;drop table t;--$1$", nameof(SqlRejectionReason.MultipleStatements) },

        // A backslash escapes the quote only in an E'' literal. With standard_conforming_strings on -
        // Postgres' default since 9.1 - the quote after the backslash CLOSES the string, and what follows
        // is a second statement that Npgsql will happily split off and run.
        { @"select E'a\'; still one'", nameof(SqlRejectionReason.None) },
        { @"select e'a\'; still one'", nameof(SqlRejectionReason.None) },
        { @"select 'a\'", nameof(SqlRejectionReason.None) },
        { @"select 'a\'; drop table t --'", nameof(SqlRejectionReason.MultipleStatements) },
        { @"select 'a\\'; drop table t", nameof(SqlRejectionReason.MultipleStatements) },
        { @"select E'a\'; drop table t", nameof(SqlRejectionReason.UnterminatedString) },

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

        // --- refused: a function the read-only transaction does not stop -------------------------------
        // Every one of these was verified to run inside `begin; set transaction read only` on Postgres 17.
        // The first one killed the backend it was asked on.
        { "select pg_terminate_backend(pg_backend_pid())", nameof(SqlRejectionReason.DisallowedFunction) },
        { "select pg_cancel_backend(1234)", nameof(SqlRejectionReason.DisallowedFunction) },
        { "select PG_RELOAD_CONF()", nameof(SqlRejectionReason.DisallowedFunction) },
        { "select pg_read_file('/etc/passwd')", nameof(SqlRejectionReason.DisallowedFunction) },
        { "select pg_read_binary_file('postgresql.conf')", nameof(SqlRejectionReason.DisallowedFunction) },
        { "select pg_ls_dir('.')", nameof(SqlRejectionReason.DisallowedFunction) },
        { "select lo_export(16384, '/tmp/out')", nameof(SqlRejectionReason.DisallowedFunction) },
        { "select dblink_exec('dbname=other', 'delete from t')", nameof(SqlRejectionReason.DisallowedFunction) },
        { "select pg_advisory_lock(1)", nameof(SqlRejectionReason.DisallowedFunction) },
        { "select pg_sleep_until(now() + interval '1 hour')", nameof(SqlRejectionReason.DisallowedFunction) },
        { "select set_config('statement_timeout', '0', false)", nameof(SqlRejectionReason.DisallowedFunction) },
        { "select pg_switch_wal()", nameof(SqlRejectionReason.DisallowedFunction) },

        // Schema-qualified, which is how somebody gets past a check that only looks at the whole token.
        { "select pg_catalog.pg_terminate_backend(1)", nameof(SqlRejectionReason.DisallowedFunction) },

        // And inside a CTE, which is where the first statement token stops telling you anything.
        {
            "with victims as (select pid from pg_stat_activity) " +
            "select pg_terminate_backend(pid) from victims",
            nameof(SqlRejectionReason.DisallowedFunction)
        },
        { "explain analyze select pg_reload_conf()", nameof(SqlRejectionReason.DisallowedFunction) },

        // The near misses that must still run: pg_sleep is how the timeout tests prove the timeout fires,
        // and txid_current does nothing a read-only transaction has to care about.
        { "select txid_current()", nameof(SqlRejectionReason.None) },
        { "select 'pg_terminate_backend'", nameof(SqlRejectionReason.None) },
        { "select \"pg_terminate_backend\" from t", nameof(SqlRejectionReason.None) },

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

    [Fact]
    public void A_refused_function_is_named_along_with_what_it_would_have_done()
    {
        var result = ReadOnlySqlGuard.Check("select pg_terminate_backend(pg_backend_pid())");

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be(SqlRejectionReason.DisallowedFunction);
        result.Token.Should().Be("pg_terminate_backend");
        result.Position.Should().Be(7);
        result.Message.Should().Contain("terminates other sessions");
        result.Message.Should().Contain("role", "the message has to point at the only real narrowing there is");
    }

    /// <summary>
    /// The denylist is matched case-insensitively over identifier tokens, which is what makes the
    /// schema-qualified spelling free: <c>pg_catalog.pg_terminate_backend</c> reaches the scanner as three
    /// tokens and it is the bare name that is looked up.
    /// </summary>
    [Fact]
    public void The_denylist_is_matched_by_bare_name_whatever_the_case_or_the_schema()
    {
        foreach (var sql in new[]
        {
            "select pg_read_file('x')",
            "select PG_READ_FILE('x')",
            "select Pg_Read_File('x')",
            "select pg_catalog.pg_read_file('x')",
            "select public.pg_catalog.pg_read_file('x')",
        })
        {
            ReadOnlySqlGuard.Check(sql).Reason.Should().Be(SqlRejectionReason.DisallowedFunction, $"of: {sql}");
        }
    }

    /// <summary>
    /// The list itself, asserted as a set. Changing it is a security decision — either a function somebody
    /// can now reach that they could not, or one that used to be refused and is not — so it belongs in a
    /// diff a reviewer reads, not in a theory row that quietly stopped being exercised.
    /// </summary>
    [Fact]
    public void The_denylist_is_exactly_these_functions()
    {
        ReadOnlySqlGuard.DisallowedFunctions.Keys.Should().BeEquivalentTo(
        [
            "pg_terminate_backend", "pg_cancel_backend", "pg_log_backend_memory_contexts", "pg_reload_conf",
            "pg_rotate_logfile", "pg_stat_reset", "pg_switch_wal", "pg_create_restore_point",
            "pg_backup_start", "pg_backup_stop", "pg_promote", "pg_read_file", "pg_read_binary_file",
            "pg_stat_file", "pg_ls_dir", "pg_ls_logdir", "pg_ls_waldir", "lo_export", "lo_import",
            "lo_unlink", "dblink", "dblink_exec", "dblink_connect", "pg_advisory_lock",
            "pg_advisory_lock_shared", "pg_advisory_xact_lock", "pg_advisory_xact_lock_shared",
            "pg_sleep_for", "pg_sleep_until", "set_config", "pg_notify",
        ]);

        ReadOnlySqlGuard.DisallowedFunctions.Should().NotContainKey(
            "pg_sleep", "statement_timeout bounds it, and the timeout tests are written on it");
        ReadOnlySqlGuard.DisallowedFunctions.Should().NotContainKey(
            "txid_current", "it assigns a transaction id and reaches nothing outside this session");

        ReadOnlySqlGuard.DisallowedFunctions.Values.Should().AllSatisfy(
            why => why.Should().NotBeNullOrWhiteSpace("a refusal that does not say why is a refusal nobody can act on"));
    }
}
