using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health.Checks;

using Npgsql;

using NpgsqlTypes;

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

    [Fact]
    public void Every_checks_share_fits_so_the_limit_never_drops_one_checks_picks_for_another()
    {
        StatementSelection selection = StatementReads.Selection;

        (selection.TopByLoad + selection.TopByMean + selection.TopGuarded + selection.TopJit).Should().Be(HealthStatementQueries.MaxTexts);
        selection.SharesFit.Should().BeTrue();

        FluentActions.Invoking(() => HealthStatementQueries.Counters(At("1.11"), selection with { TopJit = selection.TopJit + 1 }))
            .Should().Throw<ArgumentException>().WithMessage("*shares must fit*");
    }

    [Fact]
    public void The_text_is_read_for_the_kept_statements_only_and_never_for_more_than_twenty()
    {
        HealthQuery query = HealthStatementQueries.Texts(At("1.11"), [7L, 8L, 7L]);

        query.Sql.Should().Contain("(showtext := true) s").And.Contain("s.queryid = any(@ids)");
        query.Sql.Should().Contain("pg_catalog.left(s.query, @characters)");
        NpgsqlParameter ids = query.Parameters.Single(static x => x.ParameterName == "ids");
        ids.NpgsqlDbType.Should().Be(NpgsqlDbType.Array | NpgsqlDbType.Bigint);
        ids.Value.Should().BeOfType<long[]>().Which.Should().Equal(7L, 8L);

        FluentActions.Invoking(() => HealthStatementQueries.Texts(At("1.11"), [.. Enumerable.Range(1, 21).Select(static x => (long) x)]))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => HealthStatementQueries.Texts(At("1.11"), [])).Should().Throw<ArgumentException>();
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
