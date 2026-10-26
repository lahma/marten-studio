using MartenStudio.Services.Health.Checks;

using Npgsql;

using NpgsqlTypes;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The shape of the statement checks' statements, with no database (acceptance 1 and 4): the counters never read text,
/// the text is read for the statements the counters kept and no more than twenty, both are bounded to the store's own
/// role and this database and say they are read as that role, the columns follow the installed extension's version,
/// and a plan is a generic one of a plain select - never ANALYZE, never the statement itself, never its text in "Show SQL".
/// </summary>
public class HealthStatementQueriesTests
{
    public static TheoryData<string> Versions => new() { "1.8", "1.9", "1.10", "1.11", "1.12" };

    private static StatementExtension At(string version, int server = 170006) => new(server, "monitoring", version);

    private static HealthQuery Counters(string version = "1.11") => HealthStatementQueries.Counters(At(version), StatementReads.Selection);

    [Theory]
    [MemberData(nameof(Versions))]
    public void The_counters_never_read_a_statements_text(string version)
    {
        HealthQuery query = Counters(version);

        query.Sql.Should().Contain("\"monitoring\".\"pg_stat_statements\"(showtext := false) s", "the extension's schema is found, and the function is qualified");
        query.Sql.Should().NotContain("showtext := true");
        query.Sql.Should().NotMatchRegex(@"\bs\.query\b", "no text, and no column that carries it");
        query.Sql.Should().Contain(" limit @max");
        query.Parameters.Single(static x => x.ParameterName == "max").Value.Should().Be(HealthStatementQueries.MaxTexts);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Every_read_is_the_store_roles_own_statements_in_this_database_and_says_so(string version)
    {
        HealthQuery[] queries = [Counters(version), HealthStatementQueries.Texts(At(version), [1L, 2L])];

        foreach (HealthQuery query in queries)
        {
            query.Sql.Should().StartWith(HealthStatementQueries.ApplicationRoleComment);
            query.Sql.Should().Contain("s.userid = (select r.oid from pg_catalog.pg_roles r where r.rolname = current_user)");
            query.Sql.Should().Contain("s.dbid = (select d.oid from pg_catalog.pg_database d where d.datname = pg_catalog.current_database())");
            query.Sql.Should().NotContainEquivalentOf("set role").And.NotContainEquivalentOf("set_config");

            // "Show SQL" is drawn for everybody the finding is shown to, and the extension's schema may be withheld.
            query.Shown.Should().NotBeNull().And.Contain("‹withheld›.\"pg_stat_statements\"").And.NotContain("monitoring");
            query.Shown.Should().Be(query.Sql.Replace("\"monitoring\"", "‹withheld›", StringComparison.Ordinal),
                "only the schema differs between what runs and what is shown");
        }
    }

    /// <summary>
    /// The checks' twenty and the Statements page's two hundreds (W5-Q1: by total time, and on Aurora by storage reads) are
    /// the read's shares, and they fit its limit exactly, so the limit drops nobody's pick.
    /// </summary>
    [Fact]
    public void Every_checks_share_fits_so_the_limit_never_drops_one_checks_picks_for_another()
    {
        StatementSelection selection = StatementReads.Selection;

        (selection.TopByLoad + selection.TopByMean + selection.TopGuarded + selection.TopJit).Should().Be(20, "the checks' own picks are unchanged");
        (selection.TopByTotal, selection.TopByStorage).Should().Be((StatementReads.PageTop, StatementReads.PageTop));
        (selection.TopByLoad + selection.TopByMean + selection.TopGuarded + selection.TopJit + selection.TopByTotal + selection.TopByStorage)
            .Should().Be(HealthStatementQueries.MaxTexts);
        selection.SharesFit.Should().BeTrue();

        FluentActions.Invoking(() => HealthStatementQueries.Counters(At("1.11"), selection with { TopJit = selection.TopJit + 1 }))
            .Should().Throw<ArgumentException>().WithMessage("*shares must fit*");
        FluentActions.Invoking(() => HealthStatementQueries.Counters(At("1.11"), selection with { TopByTotal = selection.TopByTotal + 1 }))
            .Should().Throw<ArgumentException>().WithMessage("*shares must fit*");
    }

    [Fact]
    public void The_text_is_read_for_the_kept_statements_only_and_never_for_more_than_the_read_keeps()
    {
        HealthQuery query = HealthStatementQueries.Texts(At("1.11"), [7L, 8L, 7L]);

        query.Sql.Should().Contain("(showtext := true) s").And.Contain("s.queryid = any(@ids)");
        query.Sql.Should().Contain("pg_catalog.left(s.query, @characters)");
        NpgsqlParameter ids = query.Parameters.Single(static x => x.ParameterName == "ids");
        ids.NpgsqlDbType.Should().Be(NpgsqlDbType.Array | NpgsqlDbType.Bigint);
        ids.Value.Should().BeOfType<long[]>().Which.Should().Equal(7L, 8L);

        FluentActions.Invoking(() => HealthStatementQueries.Texts(At("1.11"), [.. Enumerable.Range(1, HealthStatementQueries.MaxTexts + 1).Select(static x => (long) x)]))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => HealthStatementQueries.Texts(At("1.11"), [])).Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// W5-Q1: the page's own ranking - total time since the counters began - and the blocks and read time it shows are in the
    /// one counters read, by name for the installed extension's version: 17's <c>shared_blk_read_time</c> rename (1.11) and
    /// 18's parallel workers and <c>wal_buffers_full</c> (1.12).
    /// </summary>
    [Theory]
    [InlineData("1.8", "s.blk_read_time::float8 as io_read_ms", false)]
    [InlineData("1.10", "s.blk_read_time::float8 as io_read_ms", false)]
    [InlineData("1.11", "s.shared_blk_read_time::float8 as io_read_ms", false)]
    [InlineData("1.12", "s.shared_blk_read_time::float8 as io_read_ms", true)]
    public void The_17_and_18_column_names_are_read_by_the_installed_extensions_version(string version, string readTime, bool parallel)
    {
        string sql = Counters(version).Sql;

        sql.Should().Contain(readTime).And.Contain("s.shared_blks_hit, s.shared_blks_read");
        sql.Contains("s.parallel_workers_to_launch::int8 as parallel_to_launch", StringComparison.Ordinal).Should().Be(parallel);
        sql.Contains("s.wal_buffers_full::int8 as wal_buffers_full", StringComparison.Ordinal).Should().Be(parallel);
        sql.Should().Contain(parallel ? "s.parallel_workers_launched::int8 as parallel_launched" : "null::int8 as parallel_launched");
        sql.Should().Contain("row_number() over (order by s.total_exec_time desc, s.queryid) as by_total").And.Contain("or r.by_total <= @top_total");
        Counters(version).Parameters.Single(static x => x.ParameterName == "top_total").Value.Should().Be(StatementReads.PageTop);
    }

    /// <summary>
    /// A5: on Aurora the counters join <c>pg_catalog.aurora_stat_statements(false)</c> on its four keys and take its storage
    /// columns by name - never its column list, which on 17 collides with the extension's own - with its rows fenced to the
    /// store's role and this database as the extension's are; elsewhere the columns are null and nothing ranks by them.
    /// </summary>
    [Theory]
    [InlineData("1.10", true)]
    [InlineData("1.11", true)]
    [InlineData("1.8", false)]
    public void Auroras_storage_reads_are_joined_on_the_four_keys_by_name_and_fenced(string version, bool toplevel)
    {
        HealthQuery query = HealthStatementQueries.Counters(At(version), StatementReads.Selection, storageReads: true);
        string sql = query.Sql;

        sql.Should().Contain("from (select pg_catalog.to_jsonb(x) as j from pg_catalog.\"aurora_stat_statements\"(false) x) a");
        sql.Should().Contain("(a.j ->> 'storage_blks_read')::int8 as storage_blks_read")
            .And.Contain("(a.j ->> 'orcache_blks_hit')::int8 as orcache_blks_hit")
            .And.Contain("(a.j ->> 'storage_blk_read_time')::float8 as storage_blk_read_time");
        sql.Should().NotContain("x.*").And.NotContain("a.*");
        sql.Should().Contain("left join aurora a on a.queryid = s.queryid and a.userid = s.userid and a.dbid = s.dbid"
            + (toplevel ? " and a.toplevel = s.toplevel" : string.Empty));
        sql.Contains("(a.j ->> 'toplevel')::bool as toplevel", StringComparison.Ordinal).Should().Be(toplevel);

        // The fence on Aurora's rows, before the join, as well as the extension's own.
        sql.Should().Contain("(a.j ->> 'userid')::oid = (select r.oid from pg_catalog.pg_roles r where r.rolname = current_user)");
        sql.Should().Contain("(a.j ->> 'dbid')::oid = (select d.oid from pg_catalog.pg_database d where d.datname = pg_catalog.current_database())");
        sql.Should().Contain("s.userid = (select r.oid from pg_catalog.pg_roles r where r.rolname = current_user)");
        sql.Should().Contain("or (r.by_storage <= @top_storage and r.storage_blks_read is not null)");
        query.Parameters.Single(static x => x.ParameterName == "top_storage").Value.Should().Be(StatementReads.PageTop);

        HealthQuery elsewhere = HealthStatementQueries.Counters(At(version), StatementReads.Selection);
        elsewhere.Sql.Should().NotContain("aurora").And.Contain("null::int8 as storage_blks_read");
        elsewhere.Parameters.Single(static x => x.ParameterName == "top_storage").Value.Should().Be(0, "nothing ranks by storage reads that are not read");
    }

    [Fact]
    public void Auroras_function_is_looked_for_in_pg_catalog_alone()
    {
        string sql = HealthStatementQueries.Extension().Sql;

        sql.Should().Contain("p.proname = 'aurora_stat_statements'")
            .And.Contain("p.pronamespace = 'pg_catalog'::pg_catalog.regnamespace")
            .And.Contain("p.proargtypes[0] = 'pg_catalog.bool'::pg_catalog.regtype");
    }

    [Theory]
    [InlineData("1.8", false, false, false)]
    [InlineData("1.9", true, false, false)]
    [InlineData("1.10", true, true, false)]
    [InlineData("1.11", true, true, true)]
    public void The_columns_follow_the_installed_extensions_version(string version, bool toplevelAndInfo, bool jit, bool since)
    {
        string sql = Counters(version).Sql;

        sql.Contains("and s.toplevel", StringComparison.Ordinal).Should().Be(toplevelAndInfo);
        sql.Contains("\"monitoring\".\"pg_stat_statements_info\" i", StringComparison.Ordinal).Should().Be(toplevelAndInfo);
        sql.Contains("s.jit_generation_time + s.jit_inlining_time + s.jit_optimization_time + s.jit_emission_time", StringComparison.Ordinal).Should().Be(jit);
        sql.Contains("s.jit_deform_time", StringComparison.Ordinal).Should().Be(since);
        sql.Contains("s.stats_since as stats_since", StringComparison.Ordinal).Should().Be(since);
    }

    [Fact]
    public void An_extension_too_old_to_read_is_refused_rather_than_guessed_at()
    {
        FluentActions.Invoking(() => HealthStatementQueries.Counters(At("1.7"), StatementReads.Selection)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => HealthStatementQueries.Counters(new StatementExtension(170006, null, null), StatementReads.Selection))
            .Should().Throw<ArgumentException>();

        new StatementExtension(170006, null, null).Installed.Should().BeFalse();
        At("1.7").Supported.Should().BeFalse();
    }

    [Fact]
    public void Where_the_extension_lives_is_a_catalog_read_that_is_not_the_store_roles()
    {
        HealthQuery query = HealthStatementQueries.Extension();

        query.Sql.Should().Contain("pg_catalog.pg_extension e on e.extname = 'pg_stat_statements'").And.Contain("e.extnamespace");
        query.Sql.Should().NotStartWith(HealthStatementQueries.ApplicationRoleComment);
        query.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void A_plan_is_the_generic_plan_of_the_prepared_statement_with_nothing_bound_and_nothing_left_behind()
    {
        const string Text = "select d.id, d.data from studio_sql.mt_doc_order as d where d.data ->> $3 = $1 limit $2";

        HealthQuery query = HealthStatementQueries.Explain(4242, Text, truncated: false)!;

        query.Sql.Should().Be(
            "prepare \"marten_studio_plan\" as " + Text + "\n;\n"
            + "set local plan_cache_mode = force_generic_plan;\n"
            + "explain (format json) execute \"marten_studio_plan\"(null, null, null);\n"
            + "deallocate \"marten_studio_plan\"");
        query.Sql.Should().NotContainEquivalentOf("analyze");
        query.Parameters.Should().BeEmpty("a bound value is folded into the plan as a constant, and a generic plan has none");

        HealthStatementQueries.Explain(1, "select 1", truncated: false)!.Sql.Should().Contain("explain (format json) execute \"marten_studio_plan\";");
        HealthStatementQueries.Deallocate().Sql.Should().Be("deallocate \"marten_studio_plan\"");
    }

    /// <summary>
    /// Re-review F7: the statement's own text may end in a <c>--</c> comment, which runs to the end of its line - so the
    /// <c>;</c> that ends the prepare is on a line of its own, and the batch is still four statements to Postgres.
    /// </summary>
    [Fact]
    public void A_text_that_ends_in_a_line_comment_cannot_swallow_the_end_of_the_prepare()
    {
        const string Text = "select d.data from studio_sql.mt_doc_order as d where d.id = $1 -- a trailing note";

        HealthQuery query = HealthStatementQueries.Explain(1, Text, truncated: false)!;

        query.Sql.Should().StartWith("prepare \"marten_studio_plan\" as " + Text + "\n;\n");

        List<SqlToken> semicolons = [];
        SqlLexer lexer = new(query.Sql);

        while (lexer.TryRead(out SqlToken token))
        {
            if (token.Is(';'))
            {
                semicolons.Add(token);
            }
        }

        semicolons.Should().HaveCount(3, "prepare; set local; explain; deallocate - the comment ends at its line break, before the first");
    }

    /// <summary>
    /// Security review item 2: a name of its own per plan added a new, permanent entry to the host's pg_stat_statements on
    /// every plan. One name, for every statement and every sweep.
    /// </summary>
    [Fact]
    public void Every_plan_prepares_under_the_one_name()
    {
        HealthQuery first = HealthStatementQueries.Explain(1, "select 1 from t where id = $1", truncated: false)!;
        HealthQuery second = HealthStatementQueries.Explain(2, "select 2 from u where id = $1", truncated: false)!;

        first.Sql.Split('\n').Skip(1).Should().Equal(second.Sql.Split('\n').Skip(1),
            "for statements with as many placeholders, everything but the prepared text is the same, so what pg_stat_statements records of it is too");
        HealthStatementQueries.PlanStatementName.Should().Be("marten_studio_plan");
    }

    [Fact]
    public void Show_SQL_prints_where_the_text_came_from_and_never_the_text()
    {
        const string Text = "select s.salary from hr.payroll s where s.id = $1";

        HealthQuery query = HealthStatementQueries.Explain(4242, Text, truncated: false)!;

        query.Shown.Should().StartWith(HealthStatementQueries.PlanPrefix).And.Contain(HealthStatementQueries.StatementShown + "4242")
            .And.Contain("force_generic_plan").And.NotContain("payroll");
    }

    [Theory]
    [InlineData("select 1 from t where id = $1", false, nameof(StatementPlanRefusal.None))]
    [InlineData("  SELECT 1", false, nameof(StatementPlanRefusal.None))]
    [InlineData("select 1 from t", true, nameof(StatementPlanRefusal.Truncated))]
    [InlineData("insert into t values ($1)", false, nameof(StatementPlanRefusal.NotASelect))]
    [InlineData("update t set x = $1", false, nameof(StatementPlanRefusal.NotASelect))]
    [InlineData("with x as (delete from t returning *) select * from x", false, nameof(StatementPlanRefusal.NotASelect))]
    [InlineData("select * from t where id = $1 for update", false, nameof(StatementPlanRefusal.LockingClause))]
    [InlineData("select * from t for no key update skip locked", false, nameof(StatementPlanRefusal.LockingClause))]
    [InlineData("select * from t for share", false, nameof(StatementPlanRefusal.LockingClause))]
    [InlineData("select * from t for key share", false, nameof(StatementPlanRefusal.LockingClause))]
    // Security review item 7: a comment between the words is no word at all to Postgres - and so not to the lexer.
    [InlineData("select * from t for/**/update", false, nameof(StatementPlanRefusal.LockingClause))]
    [InlineData("select * from t for -- a comment\nupdate", false, nameof(StatementPlanRefusal.LockingClause))]
    [InlineData("select * from t for no /* x */ key /* y */ update", false, nameof(StatementPlanRefusal.LockingClause))]
    [InlineData("/* lead */ select * from t", false, nameof(StatementPlanRefusal.None))]
    [InlineData("select substring(x from 1 for 2) from t", false, nameof(StatementPlanRefusal.None))]
    [InlineData("select * from t -- for update", false, nameof(StatementPlanRefusal.None))]
    [InlineData("select \"for update\" from t", false, nameof(StatementPlanRefusal.None))]
    [InlineData("select 1; drop table t", false, nameof(StatementPlanRefusal.Shape))]
    [InlineData("select $$x$$", false, nameof(StatementPlanRefusal.Shape))]
    [InlineData("select 'never closed", false, nameof(StatementPlanRefusal.Shape))]
    [InlineData("select pg_terminate_backend($1)", false, nameof(StatementPlanRefusal.Shape))]
    public void Only_a_plain_single_select_read_whole_is_planned(string text, bool truncated, string refusal)
    {
        HealthStatementQueries.Refusal(text, truncated).ToString().Should().Be(refusal);
        (HealthStatementQueries.Explain(1, text, truncated) is null).Should().Be(refusal != nameof(StatementPlanRefusal.None));
    }

    [Theory]
    [InlineData("select 1", 0)]
    [InlineData("select $1, $12, $3", 12)]
    public void The_highest_placeholder_is_how_many_are_bound(string text, int highest)
    {
        HealthStatementQueries.HighestParameter(text).Should().Be(highest);
    }

    [Fact]
    public void The_selection_keeps_each_checks_top_few_ranked_on_the_server()
    {
        string sql = Counters().Sql;

        sql.Should().Contain("by_load <= @top_load").And.Contain("by_mean <= @top_mean").And.Contain("by_guarded <= @top_guarded").And.Contain("by_jit <= @top_jit");
        sql.Should().Contain("s.wal_records >= s.rows", "a select that sometimes finds nothing writes no WAL and does not crowd the guarded writes out");
        StatementReads.Selection.Max.Should().Be(HealthStatementQueries.MaxTexts);
        StatementReads.Selection.GuardedCalls.Should().Be(ConcurrencyConflictCheck.MinCalls);
        StatementReads.Selection.JitCalls.Should().Be(JitShareCheck.MinCalls);
    }
}
