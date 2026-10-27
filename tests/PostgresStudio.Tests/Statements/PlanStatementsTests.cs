using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// The Plan page's statements, as text and with no database: which placeholders a statement has, the commands each mode sends and
/// the options each is asked with - gated on the server's release - the requests nothing is sent for, and the address.
/// </summary>
public class PlanStatementsTests
{
    private const int Seventeen = 170000;
    private const int Sixteen = 160005;

    private static PlanStatementSet Build(string statement, PlanMode mode, PlanOptions? options = null, int version = Seventeen) =>
        PlanStatements.Build(statement, mode, options ?? new PlanOptions(), PlanStatements.Placeholders(statement), version);

    // ---- Placeholders --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("select 1", 0)]
    [InlineData("select * from t where id = $1", 1)]
    [InlineData("select * from t where a = $2 and b = $1", 2)]
    [InlineData("select * from t where a = $1 or b = $1", 1)]
    [InlineData("select * from t where a = $12", 12)]
    [InlineData("select '$3', $1", 1)]
    [InlineData("select \"a$4\", $1", 1)]
    [InlineData("select 1 -- $9", 0)]
    [InlineData("select /* $9 */ $1", 1)]
    [InlineData("select $$ $5 $$, $2", 2)]
    [InlineData("select a$1 from t", 0)]
    [InlineData("select $ 1", 0)]
    [InlineData("select $a", 0)]
    [InlineData("select 'unterminated $7", 0)]
    public void A_statements_placeholders_are_the_highest_dollar_number_outside_every_string_name_and_comment(string statement, int highest) =>
        PlanStatements.Placeholders(statement).Should().Be(highest);

    // ---- The commands --------------------------------------------------------------------------------------------------

    [Fact]
    public void Explain_asks_for_the_plan_in_JSON_and_runs_nothing()
    {
        PlanStatementSet set = Build("select 1", PlanMode.Plan);

        set.Explain.Should().Be("explain (format json) select 1", "BUFFERS and TIMING are on by default, and a plain EXPLAIN asks for neither");
        set.Prepare.Should().BeNull();
        set.ExplainOffset.Should().Be("explain (format json) ".Length, "a Postgres error position counts the prefix");
        set.Described.Should().Be(set.Explain);
    }

    [Fact]
    public void Explain_analyze_asks_for_buffers_and_timing_by_default()
    {
        Build("select 1", PlanMode.Analyze).Explain.Should().Be("explain (analyze, buffers, format json) select 1");
    }

    [Fact]
    public void The_options_that_need_ANALYZE_are_asked_only_with_it()
    {
        PlanOptions all = new() { Buffers = true, Timing = false, Verbose = true, Settings = true, Wal = true, Memory = true, Serialize = true };

        // Plain: BUFFERS (an error before 18 without ANALYZE), TIMING, WAL and SERIALIZE are not sent.
        Build("select 1", PlanMode.Plan, all).Explain.Should().Be("explain (verbose, settings, memory, format json) select 1");

        // Analyzed: all of them, TIMING off asked as "timing off".
        Build("select 1", PlanMode.Analyze, all).Explain.Should().Be("explain (analyze, verbose, buffers, settings, wal, timing off, memory, serialize, format json) select 1");
    }

    [Fact]
    public void Turning_BUFFERS_off_under_ANALYZE_says_so_because_18_counts_them_by_default()
    {
        Build("select 1", PlanMode.Analyze, new PlanOptions { Buffers = false }).Explain.Should().Be("explain (analyze, buffers off, format json) select 1");
    }

    [Fact]
    public void MEMORY_and_SERIALIZE_are_left_out_on_a_server_older_than_17_and_named()
    {
        PlanOptions both = new() { Memory = true, Serialize = true };

        PlanStatementSet old = Build("select 1", PlanMode.Analyze, both, Sixteen);
        old.Explain.Should().Be("explain (analyze, buffers, format json) select 1");
        old.LeftOut.Should().Equal("MEMORY (needs PostgreSQL 17)", "SERIALIZE (needs PostgreSQL 17)");

        PlanStatementSet current = Build("select 1", PlanMode.Analyze, both, Seventeen);
        current.Explain.Should().Contain("memory").And.Contain("serialize");
        current.LeftOut.Should().BeEmpty();

        // SERIALIZE needs ANALYZE too, so a plain plan neither sends nor names it.
        Build("select 1", PlanMode.Plan, both, Sixteen).LeftOut.Should().Equal("MEMORY (needs PostgreSQL 17)");
    }

    [Fact]
    public void A_custom_plan_is_the_statement_explained_with_its_values_bound_and_the_prepare_only_reads_the_types()
    {
        PlanStatementSet set = Build("select * from t where a = $1 and b = $2", PlanMode.Plan);

        // A protocol parameter cannot reach an EXECUTE (measured: "could not determine data type of parameter $1"), so the values
        // are bound to the statement the EXPLAIN explains - which the planner then plans for those values.
        set.Explain.Should().Be("explain (format json) select * from t where a = $1 and b = $2");
        set.BoundParameters.Should().Be(2);
        set.ExplainOffset.Should().Be("explain (format json) ".Length);

        set.Prepare.Should().Be("prepare \"marten_studio_plan\" as select * from t where a = $1 and b = $2");
        set.InferTypesOnly.Should().BeTrue("deallocated as soon as its types are read");
        set.PlanCacheMode.Should().BeNull();
        set.PrepareOffset.Should().Be("prepare \"marten_studio_plan\" as ".Length);
        set.Described.Should().Be(set.Explain + ";\n" + set.Prepare, "the explain first: the audit's target is its first 200 characters");
    }

    [Fact]
    public void An_analyzed_statement_with_placeholders_runs_with_its_values_bound()
    {
        PlanStatementSet set = Build("select * from t where a = $1", PlanMode.Analyze);

        set.Explain.Should().Be("explain (analyze, buffers, format json) select * from t where a = $1");
        set.BoundParameters.Should().Be(1);
        set.InferTypesOnly.Should().BeTrue();
    }

    [Fact]
    public void The_generic_plan_is_prepared_and_explained_under_force_generic_plan_with_nulls_for_values_never_GENERIC_PLAN()
    {
        PlanStatementSet set = Build("select * from t where a = $1 and b = $2", PlanMode.Generic);

        set.Prepare.Should().StartWith("prepare \"marten_studio_plan\" as select");
        set.InferTypesOnly.Should().BeFalse("the generic plan explains the prepared statement");
        set.PlanCacheMode.Should().Be("force_generic_plan");
        set.Explain.Should().Be("explain (format json) execute \"marten_studio_plan\"(null, null)");
        set.BoundParameters.Should().Be(0, "a generic plan is planned with no values, whatever execute passes");
        (set.Described + set.Explain).ToLowerInvariant().Should().NotContain("(generic_plan").And.NotContain(", generic_plan", "EXPLAIN's own GENERIC_PLAN option is never asked for");

        // Analyze is not asked of a generic plan whatever the options say.
        Build("select $1", PlanMode.Generic, new PlanOptions { Buffers = true, Wal = true }).Explain.Should().NotContain("analyze");
    }

    [Fact]
    public void A_value_is_never_written_into_any_statement()
    {
        PlanStatementSet set = Build("select * from t where a = $1", PlanMode.Plan);

        // Nothing but the placeholders: the page binds the values to the command, and the statements carry no value to read.
        string all = set.Prepare + set.Explain + set.Described;
        all.Should().NotContain("'");
    }

    [Fact]
    public void The_plan_cache_mode_is_the_one_constant_and_nothing_else()
    {
        PlanStatements.SetPlanCacheMode("force_generic_plan").Should().Be("set local plan_cache_mode = force_generic_plan");

        Action injected = () => PlanStatements.SetPlanCacheMode("auto; drop table t");
        injected.Should().Throw<ArgumentOutOfRangeException>();
        ((Action) (() => PlanStatements.SetPlanCacheMode("force_custom_plan"))).Should().Throw<ArgumentOutOfRangeException>("a custom plan sets none");
    }

    [Fact]
    public void The_parameter_types_are_read_by_name_from_the_catalog_with_the_name_as_a_parameter()
    {
        PlanRunQueries.ParameterTypesSql.Should().Be("select parameter_types::text[] from pg_catalog.pg_prepared_statements where name = @name");
    }

    // ---- Requests nothing is sent for ----------------------------------------------------------------------------------

    [Fact]
    public void A_generic_plan_of_a_statement_with_no_placeholder_is_refused_with_a_sentence()
    {
        SqlRejection? refusal = PlanRequests.Refusal(new PlanRunRequest("select 1", PlanMode.Generic), 0);

        refusal.Should().NotBeNull();
        refusal!.Reason.Should().Be(PlanRequests.NoPlaceholdersReason);
        refusal.Message.Should().Contain("Explain plans it as it is");
    }

    [Theory]
    [InlineData("Plan")]
    [InlineData("Analyze")]
    public void A_statement_with_placeholders_needs_a_value_for_each_unless_it_asks_for_the_generic_plan(string name)
    {
        PlanMode mode = Enum.Parse<PlanMode>(name);

        PlanRequests.Refusal(new PlanRunRequest("select $1, $2", mode) { Values = ["1"] }, 2)!.Reason.Should().Be(PlanRequests.ValuesMissingReason);
        PlanRequests.Refusal(new PlanRunRequest("select $1, $2", mode), 2)!.Message.Should().Contain("2 placeholders ($1 to $2)").And.Contain("Generic plan");
        PlanRequests.Refusal(new PlanRunRequest("select $1, $2", mode) { Values = ["1", null] }, 2).Should().BeNull("NULL is a value");
        PlanRequests.Refusal(new PlanRunRequest("select 1", mode), 0).Should().BeNull();
        PlanRequests.Refusal(new PlanRunRequest("select $1, $2", PlanMode.Generic), 2).Should().BeNull();
    }

    [Fact]
    public void Too_many_placeholders_and_too_long_a_value_are_refused()
    {
        PlanRequests.Refusal(new PlanRunRequest("select 1", PlanMode.Generic), PlanStatements.MaxParameters + 1)!.Reason.Should().Be(PlanRequests.TooManyPlaceholdersReason);

        PlanRequests.Refusal(
            new PlanRunRequest("select $1", PlanMode.Plan) { Values = [new string('x', PlanStatements.MaxValueCharacters + 1)] }, 1)!.Reason.Should().Be(PlanRequests.ValueTooLongReason);

        PlanRequests.Refusal(
            new PlanRunRequest("select $1", PlanMode.Plan) { Values = [new string('x', PlanStatements.MaxValueCharacters)] }, 1).Should().BeNull();
    }

    // ---- The address (D9) ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("plan", "Plan")]
    [InlineData("analyze", "Analyze")]
    [InlineData("ANALYZE", "Analyze")]
    [InlineData("generic", "Generic")]
    [InlineData("rubbish", "Plan")]
    [InlineData(null, "Plan")]
    public void A_mode_in_the_address_is_read_and_anything_else_is_the_default(string? value, string mode) =>
        PlanLinks.ParseMode(value).Should().Be(Enum.Parse<PlanMode>(mode));

    [Fact]
    public void Options_in_the_address_are_exactly_the_ones_it_lists_and_none_at_all_means_the_defaults()
    {
        PlanLinks.ParseOptions(null).Should().Be(new PlanOptions(), "BUFFERS and TIMING are on by default");
        PlanLinks.ParseOptions("verbose,wal").Should().Be(new PlanOptions { Buffers = false, Timing = false, Verbose = true, Wal = true });
        PlanLinks.ParseOptions(string.Empty).Should().Be(new PlanOptions { Buffers = false, Timing = false }, "options= is none");
        PlanLinks.ParseOptions(" Buffers , SETTINGS ,nonsense").Should().Be(new PlanOptions { Timing = false, Settings = true });
    }

    [Fact]
    public void Options_round_trip_through_the_address()
    {
        PlanOptions options = new() { Buffers = true, Timing = false, Verbose = true, Settings = true, Wal = true, Memory = true, Serialize = true };

        PlanLinks.OptionsKey(options).Should().Be("buffers,verbose,settings,wal,memory,serialize");
        PlanLinks.ParseOptions(PlanLinks.OptionsKey(options)).Should().Be(options);
        PlanLinks.OptionsKey(new PlanOptions { Buffers = false, Timing = false }).Should().BeEmpty();
    }
}
