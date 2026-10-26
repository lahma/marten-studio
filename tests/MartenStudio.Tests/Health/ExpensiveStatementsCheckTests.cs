using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Query;

using static MartenStudio.Tests.Health.StatementBuilders;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Query;

namespace MartenStudio.Tests.Health;

/// <summary>
/// T02's judgement, with no Postgres: what is expensive, over which window, what its plan can soundly say, and that
/// a statement's text is never put where a page draws it unmasked.
/// </summary>
public class ExpensiveStatementsCheckTests
{
    private static readonly ExpensiveStatementsCheck Check = new();

    private static HealthVerdict Judge(StatementRead read, HealthHistoryView? history = null, IReadOnlyList<StatementPlan>? plans = null, IReadOnlyList<IndexFacts>? indexes = null) =>
        Check.Evaluate(new ExpensiveStatementFacts(read, plans ?? [], indexes ?? []), TableCheckBuilders.Evaluation(history));

    /// <summary>A store statement that ran <paramref name="share" /> of the last hour.</summary>
    private static ObservedStatement Busy(long id, double share, long calls = 100_000) =>
        Statement(id, SelectOnDoc, [Doc], calls, share * 3_600_000);

    [Fact]
    public void A_store_whose_statements_are_cheap_is_quiet_and_says_how_many_it_read()
    {
        HealthVerdict verdict = Judge(Read(Busy(1, 0.001), Statement(2, "select 1", [], 10, 5)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("1 of the 2 most expensive statements name one of its tables");
    }

    [Theory]
    [InlineData(0.05, nameof(HealthSeverity.Info))]
    [InlineData(0.25, nameof(HealthSeverity.Warning))]
    public void A_statement_that_keeps_the_database_busy_is_info_from_2_percent_and_a_warning_from_10(double share, string severity)
    {
        HealthFinding finding = Judge(Read(Busy(42, share))).Findings.Should().ContainSingle().Subject;

        finding.Severity.ToString().Should().Be(severity);
        finding.Subject.Should().Be("42","the queryid names the statement, and nothing above the finding's gate");
        finding.Title.Should().Be("A statement on the StatementDoc collection spent " + StatementWindows.Milliseconds(share * 3_600_000) + " executing in 60 min");
        finding.DocumentTypes.Should().Equal(typeof(StatementDoc));
        finding.Evidence.Single(static x => x.Label == "Execution time").Value.Should().EndWith(TableText.Percent(share) + " of the window's time");
        finding.Evidence.Single(static x => x.Label == "Window").Value.Should().StartWith("since its counters began");
        finding.Caveat.Should().Contain("has not run the statement checks twice yet").And.Contain("as the store's own role");
    }

    [Fact]
    public void A_burst_on_an_entry_a_few_seconds_old_is_not_a_share_of_anything()
    {
        // Half a second in a five-second-old entry is 10% of its life, and half a second is no cost.
        Judge(Read(Statement(3, SelectOnDoc, [Doc], 500, 500, age: TimeSpan.FromSeconds(5)))).Findings.Should().BeEmpty();
        Judge(Read(Statement(3, SelectOnDoc, [Doc], 5000, 12_000, age: TimeSpan.FromSeconds(60)))).Findings.Should().ContainSingle()
            .Which.Severity.Should().Be(HealthSeverity.Warning, "twelve seconds in a minute is a cost");
    }

    [Fact]
    public void Several_connections_running_a_statement_at_once_are_said_as_times_the_window_never_as_over_a_whole()
    {
        // Two hours of execution in one: four connections ran it half the time each.
        HealthFinding finding = Judge(Read(Busy(9, 2.0))).Findings.Single();

        finding.Title.Should().Be("A statement on the StatementDoc collection spent 2.0 h executing in 60 min");
        finding.Evidence.Single(static x => x.Label == "Execution time").Value.Should()
            .EndWith("2.0 times the window's length - several connections ran it at once").And.NotContain("%");
    }

    [Fact]
    public void A_slow_statement_is_a_warning_whatever_its_share()
    {
        HealthFinding finding = Judge(Read(Statement(7, SelectOnDoc, [Doc], 20, 20 * 800))).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Be("A statement on the StatementDoc collection averages 800 ms a call");
    }

    [Fact]
    public void A_slow_statement_called_too_few_times_is_not_judged_by_its_mean()
    {
        Judge(Read(Statement(7, SelectOnDoc, [Doc], 3, 3 * 800))).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_statement_that_names_none_of_the_stores_tables_is_never_a_finding()
    {
        Judge(Read(Statement(1, "select * from other_app.orders", [], 100_000, 0.5 * 3_600_000))).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_migration_or_a_plan_that_names_the_stores_table_is_not_its_workload()
    {
        string table = Doc.Schema + "." + Doc.Table;

        HealthVerdict verdict = Judge(Read(
            Statement(1, "CREATE TABLE IF NOT EXISTS " + table + " (id uuid primary key, data jsonb)", [Doc], 1, 0.5 * 3_600_000),
            Statement(2, "-- Marten\nDO $$ BEGIN ALTER TABLE " + table + " ADD COLUMN x int; END $$", [Doc], 1, 0.5 * 3_600_000),
            Statement(3, "explain (generic_plan, format json) " + SelectOnDoc, [Doc], 10, 0.5 * 3_600_000)));

        verdict.Findings.Should().BeEmpty("a deploy's DDL and the studio's own EXPLAIN are not where the application spends its time");
    }

    [Theory]
    [InlineData("select 1", true)]
    [InlineData("  /* marten */ insert into t values ($1)", true)]
    [InlineData("-- Read as the store's own role\nselect 1", true)]
    [InlineData("copy t from stdin binary", true)]
    [InlineData("with x as (select 1) select * from x", true)]
    [InlineData("create index i on t (x)", false)]
    [InlineData("do $$ begin end $$", false)]
    [InlineData("BEGIN", false)]
    [InlineData("SET LOCAL statement_timeout = $1", false)]
    [InlineData("explain select 1", false)]
    public void Workload_is_told_from_utility_statements_by_the_first_keyword_past_any_comment(string text, bool workload)
    {
        StatementReads.IsWorkload(text).Should().Be(workload);
    }

    [Fact]
    public void Once_two_statement_checks_have_run_the_window_between_them_is_what_is_judged()
    {
        // Cheap over its whole day of counters, busy over the last ten minutes: 120 s of execution in 600 s.
        ObservedStatement statement = Statement(5, SelectOnDoc, [Doc], 2000, 121_000, age: TimeSpan.FromDays(1));
        HealthHistoryView history = History(
            (TimeSpan.FromMinutes(10), [(ExpensiveStatementsCheck.CallsSeries + "5", 1000), (ExpensiveStatementsCheck.ExecSeries + "5", 1000), (ExpensiveStatementsCheck.RowsSeries + "5", 1000)]),
            (TimeSpan.Zero, [(ExpensiveStatementsCheck.CallsSeries + "5", 2000), (ExpensiveStatementsCheck.ExecSeries + "5", 121_000), (ExpensiveStatementsCheck.RowsSeries + "5", 2000)]));

        Judge(Read(statement)).Findings.Should().BeEmpty("120 s in a day is 0.1% of it");

        HealthFinding finding = Judge(Read(statement), history).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Single(static x => x.Label == "Calls").Value.Should().Be("1,000");
        finding.Evidence.Single(static x => x.Label == "Window").Value.Should().StartWith("over the last 10 min the advisor watched");
        finding.Caveat.Should().Contain("between two statement checks");
    }

    [Fact]
    public void The_statement_is_carried_for_the_report_to_mask_and_never_as_a_value()
    {
        HealthFinding finding = Judge(Read(Busy(1, 0.5))).Findings.Single();

        HealthEvidence statement = finding.Evidence.Single(static x => x.Label == "Statement");
        statement.Gate.Should().Be(HealthGate.Activity);
        statement.Value.Should().Be(HealthEvidence.StatementPending);
        statement.Statement!.Text.Should().Be(SelectOnDoc);

        finding.Evidence.Should().NotContain(static x => x.Value.Contains("select d.id", StringComparison.Ordinal),
            "a value is drawn as it is; only the report puts a masked statement there");
    }

    [Fact]
    public void At_most_five_findings_worst_first()
    {
        HealthVerdict verdict = Judge(Read([.. Enumerable.Range(1, 7).Select(i => Busy(i, 0.03 * i))]));

        verdict.Findings.Should().HaveCount(ExpensiveStatementsCheck.MaxFindings);
        verdict.Findings[0].Subject.Should().Be("7");
        verdict.Findings.Select(static x => x.Subject).Should().NotContain(["1", "2"]);
    }

    [Fact]
    public void A_whole_table_read_filtering_on_a_duplicated_column_with_no_index_gets_the_advisors_line()
    {
        StatementPlan plan = new(1, [new ExplainPlanScan("Seq Scan", Doc.Table, null, "((color)::text = $1)", null)], null);

        HealthFinding finding = Judge(Read(Busy(1, 0.5)), plans: [plan]).Findings.Single();

        finding.Remedy!.Code.Should().Contain("Schema.For<StatementDoc>().Index(x => x.Color);");
        finding.Evidence.Single(static x => x.Label == "Plan").Value.Should()
            .Contain("Seq Scan on " + Doc.Table + ", reading every row to filter on Color (duplicated column color)");
        finding.Evidence.Single(static x => x.Label == "Index advice").Value.Should().Contain("no index covers it");
        finding.Caveat.Should().Contain("generic plan");
    }

    [Fact]
    public void An_index_that_serves_the_filter_is_no_remedy_even_when_the_plan_reads_the_table()
    {
        StatementPlan plan = new(1, [new ExplainPlanScan("Seq Scan", Doc.Table, null, "((color)::text = $1)", null)], null);
        IndexFacts index = TableCheckBuilders.Index(Doc, TableCheckBuilders.IndexRow(Doc, "mt_doc_statementdoc_idx_color", "color"));

        HealthFinding finding = Judge(Read(Busy(1, 0.5)), plans: [plan], indexes: [index]).Findings.Single();

        finding.Remedy.Should().BeNull("the index exists; a small table or a broad filter is the planner's call");
        finding.Evidence.Should().NotContain(static x => x.Label == "Index advice");
    }

    [Fact]
    public void A_json_member_the_recorded_text_does_not_name_is_never_taken_for_a_missing_index()
    {
        StatementPlan plan = new(1, [new ExplainPlanScan("Seq Scan", Doc.Table, null, "((data ->> $2) = $1)", null)], null);

        HealthFinding finding = Judge(Read(Busy(1, 0.5)), plans: [plan]).Findings.Single();

        finding.Evidence.Single(static x => x.Label == "Plan").Value.Should().Contain("a JSON member the recorded statement does not name");
        finding.Evidence.Should().NotContain(static x => x.Label == "Index advice");
        finding.Remedy!.Code.Should().Contain("x => x.FilteredMember", "the member cannot be named, so the line says so");
        finding.Remedy.Explanation.Should().Contain("records this statement's constants as placeholders")
            .And.Contain("add the line only if it finds no index on that member");
        finding.Remedy.Code.Should().Contain("if no index on that member exists already");
        finding.Link!.Route.Should().Be("query");
        finding.Link.Query.Should().Contain(new KeyValuePair<string, string?>("type", Doc.Alias));
    }

    [Fact]
    public void A_json_path_with_literal_keys_and_a_containment_test_go_to_the_advisor()
    {
        StatementPlan plan = new(1,
        [
            new ExplainPlanScan("Seq Scan", Doc.Table, null, "((((data -> 'Inner'::text) ->> 'Name'::text) = $1) AND (data @> $2))", null),
        ], null);

        HealthFinding finding = Judge(Read(Busy(1, 0.5)), plans: [plan]).Findings.Single();

        finding.Remedy!.Code.Should().Contain("Duplicate(x => x.Inner.Name)").And.Contain("GinIndexJsonData()");
    }

    [Fact]
    public void A_partition_or_another_stores_table_is_counted_and_never_named()
    {
        StatementPlan plan = new(1,
        [
            new ExplainPlanScan("Index Scan", Doc.Table, Doc.Table + "_pkey", null, "(id = $1)"),
            new ExplainPlanScan("Seq Scan", Doc.Table + "_acme", null, null, null),
            new ExplainPlanScan("Seq Scan", "mt_doc_payroll", null, null, null),
        ], null);

        string summary = Judge(Read(Busy(1, 0.5)), plans: [plan]).Findings.Single().Evidence.Single(static x => x.Label == "Plan").Value;

        summary.Should().Contain("Index Scan using " + Doc.Table + "_pkey on " + Doc.Table).And.Contain("2 scans of a partition");
        summary.Should().NotContain("acme").And.NotContain("payroll");
    }

    [Fact]
    public void A_whole_table_read_with_no_filter_says_so()
    {
        StatementPlan plan = new(1, [new ExplainPlanScan("Seq Scan", Doc.Table, null, null, null)], null);

        Judge(Read(Busy(1, 0.5)), plans: [plan]).Findings.Single().Evidence.Single(static x => x.Label == "Plan").Value
            .Should().Contain("reading every row with no filter on it");
    }

    [Fact]
    public void A_statement_that_was_not_planned_says_why_and_carries_no_plan_caveat()
    {
        StatementPlan plan = new(1, [], "Not planned: Postgres refused the plan (SQLSTATE 42501).");

        HealthFinding finding = Judge(Read(Busy(1, 0.5)), plans: [plan]).Findings.Single();

        finding.Evidence.Single(static x => x.Label == "Plan").Value.Should().Be(plan.NotPlanned);
        finding.Caveat.Should().NotContain("generic plan");
        finding.Remedy.Should().BeNull();
    }

    [Fact]
    public void Rows_per_call_temp_spills_and_wal_are_evidence()
    {
        ObservedStatement statement = Statement(1, SelectOnDoc, [Doc], 100, 0.5 * 3_600_000, rows: 250_000, tempBlocks: 128, walBytes: 1024);

        HealthFinding finding = Judge(Read(statement)).Findings.Single();

        finding.Evidence.Single(static x => x.Label == "Rows per call").Value.Should().Contain("2,500").And.Contain("ToListAsync()");
        finding.Evidence.Single(static x => x.Label == "Spilled to disk").Value.Should().StartWith("128 temporary blocks").And.Contain("C08");
        finding.Evidence.Single(static x => x.Label == "Share of the role's WAL").Value.Should().StartWith("100%");
    }

    [Fact]
    public void Entries_evicted_in_the_window_are_in_the_caveat()
    {
        HealthHistoryView history = History(
            (TimeSpan.FromMinutes(10), [(StatementWindows.DeallocSeries, 3)]),
            (TimeSpan.Zero, [(StatementWindows.DeallocSeries, 40)]));

        Judge(Read(Busy(1, 0.5)), history).Findings.Single().Caveat.Should().Contain("evicted 37 entries in the window");
    }

    /// <summary>
    /// W5-Q1: every kept statement is sampled with the database's totals - not only the store's, which T02 judges - because the
    /// Statements page shows a window for each, and T02, which every statement sweep runs, is the series' one writer.
    /// </summary>
    [Fact]
    public void Every_kept_statement_is_sampled_with_the_databases_totals()
    {
        StatementRead read = Read(Busy(1, 0.5), Statement(2, "select 1", [], 10, 5));

        Dictionary<string, double> parts = Check.Sample(new ExpensiveStatementFacts(read, [], []))
            .ToDictionary(static x => x.Series, static x => x.Value, StringComparer.Ordinal);

        string[] perStatement =
        [
            ExpensiveStatementsCheck.CallsSeries, ExpensiveStatementsCheck.ExecSeries, ExpensiveStatementsCheck.RowsSeries,
            StatementReads.SharedHitSeries, StatementReads.SharedReadSeries, StatementReads.TempSeries, StatementReads.IoReadSeries,
        ];

        parts.Keys.Should().BeEquivalentTo(
        [
            StatementWindows.AllExecSeries, StatementWindows.AllWalSeries, StatementWindows.DeallocSeries,
            .. perStatement.Select(static x => x + "1"),
            .. perStatement.Select(static x => x + "2"),
        ], "no storage-read series where Aurora's are not read");

        parts[ExpensiveStatementsCheck.CallsSeries + "2"].Should().Be(10);
        parts[ExpensiveStatementsCheck.ExecSeries + "2"].Should().Be(5);
    }

    /// <summary>W5-Q1: a statement's series name is the same string instance every sweep, so a full ring holds numbers, not names.</summary>
    [Fact]
    public void A_series_name_is_shared_between_samples()
    {
        string first = StatementReads.SeriesName(ExpensiveStatementsCheck.CallsSeries, 4242);
        string again = StatementReads.SeriesName(ExpensiveStatementsCheck.CallsSeries, 4242);

        first.Should().Be(ExpensiveStatementsCheck.CallsSeries + "4242");
        ReferenceEquals(first, again).Should().BeTrue();
    }

    [Fact]
    public void A_plan_is_read_for_the_stores_statements_that_cross_the_line_since_their_counters_began_and_no_more_than_five()
    {
        StatementRead read = Read(
        [
            .. Enumerable.Range(1, 7).Select(i => Busy(i, 0.02 * i)),
            Busy(20, 0.001),
            Statement(30, "select 1", [], 100_000, 0.5 * 3_600_000),
        ]);

        ExpensiveStatementsCheck.PlanCandidates(read).Select(static x => x.QueryId).Should().Equal(7, 6, 5, 4, 3);
    }

    [Theory]
    [InlineData("((data ->> 'Color'::text) = $1)", "Color", false)]
    [InlineData("(((data -> 'Inner'::text) ->> 'Name'::text) = $3)", "Inner.Name", false)]
    [InlineData("(((data ->> 'Number'::text))::integer > $2)", "Number", false)]
    [InlineData("(d.data #>> '{Inner,Name}'::text[]) = $1", "Inner.Name", false)]
    [InlineData("((data ->> 'Color'::text) ~~* $1)", "Color", false)]
    [InlineData("((data ->> $2) = $1)", null, true)]
    [InlineData("(((data ->> $8))::integer > $2)", null, true)]
    [InlineData("(id = $1)", null, false)]
    public void The_filter_reader_names_a_member_only_when_the_plan_does(string filter, string? member, bool unnamed)
    {
        MartenStatementPlanAdvisor.Filter read = MartenStatementPlanAdvisor.FilterOf(filter, DocumentTableInfo.FromDocumentType(Doc.DocumentType!));

        read.UnnamedMember.Should().Be(unnamed);

        if (member is null)
        {
            read.Members.Should().BeEmpty();
        }
        else
        {
            read.Members.Should().ContainSingle(x => x.Subject == member);
        }
    }

    [Fact]
    public void A_like_on_a_json_member_is_a_like_and_a_json_key_spelling_a_duplicated_column_is_not_that_column()
    {
        DocumentTableInfo info = DocumentTableInfo.FromDocumentType(Doc.DocumentType!);

        MartenStatementPlanAdvisor.FilterOf("((data ->> 'Number'::text) ~~ $1)", info).Members.Single().Predicate
            .Should().BeOfType<DocumentPredicate.FieldLike>();
        MartenStatementPlanAdvisor.FilterOf("((data ->> 'color'::text) = $1)", info).Members
            .Should().ContainSingle().Which.Subject.Should().Be("color", "a key in a literal is a JSON member, not the column");
    }
}
