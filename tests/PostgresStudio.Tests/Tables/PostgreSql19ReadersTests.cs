using System.Text.RegularExpressions;

using static PostgresStudio.Tests.Tables.LintBuilders;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Maintenance;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.Tables;

/// <summary>
/// What PostgreSQL 19 adds that 1.1 reads (<c>pg_stat_autovacuum_scores</c>, <c>pg_get_multixact_stats()</c>, a table's own
/// <c>stats_reset</c>, display only), held over stated facts: the evidence each check adds and nothing else it moves, the
/// Maintenance page's "Next for autovacuum" list - and the statements, which name the 19-only objects only inside a statement
/// the callers run on 19.
/// </summary>
public partial class PostgreSql19ReadersTests
{
    private static StudioTable Orders { get; } = Host("orders");

    // ---- T07: the table's own score as evidence --------------------------------------------------------------------------

    private static TableFacts Behind() => Table(Orders, 64 * 1024 * 1024, static row => row with
    {
        EstimatedRows = 1_000_000,
        LiveRows = 1_000_000,
        DeadRows = 40_000,
        LastVacuum = null,
        LastAutovacuum = Now.AddDays(-3),
        StorageOptions = ["autovacuum_vacuum_scale_factor=0.01"],
        ServerVersion = 190_000,
        Vacuum = new AutovacuumUnit { Counter = 40_000, Trigger = 10_050, Last = Now.AddDays(-3), Threshold = 50, ScaleFactor = 0.01, Tuples = 1_000_000 },
    });

    private static HealthEvaluation Held() => Evaluation(
        history: History(
            (TimeSpan.FromMinutes(12), [(VacuumBehindCheck.SweptSeries, 1), (VacuumBehindCheck.BehindSeries + Orders.Key, 1)]),
            (TimeSpan.Zero, [(VacuumBehindCheck.SweptSeries, 1), (VacuumBehindCheck.BehindSeries + Orders.Key, 1)])));

    private static AutovacuumScoreRow Score(double score, double vacuum, bool doVacuum, bool doAnalyze = false, bool wraparound = false) =>
        new("app", "orders", score, 0.0000001, 0, vacuum, 0.004, 0.2, doVacuum, doAnalyze, wraparound);

    [Fact]
    public void T07_adds_the_tables_own_score_and_what_it_is_made_of_and_moves_nothing_else()
    {
        VacuumFacts bare = new(Now, [Behind()]);
        VacuumFacts scored = bare with { Scores = new Dictionary<string, AutovacuumScoreRow> { [Orders.Key] = Score(3.98, 3.98, doVacuum: true) } };

        HealthFinding without = new VacuumBehindCheck().Evaluate(bare, Held()).Findings.Single();
        HealthFinding with = new VacuumBehindCheck().Evaluate(scored, Held()).Findings.Single();

        with.Key.Should().Be(without.Key);
        with.Severity.Should().Be(without.Severity);
        with.Title.Should().Be(without.Title);
        with.Remedy.Should().BeEquivalentTo(without.Remedy);
        without.Evidence.Select(static x => x.Label).Should().NotContain("Autovacuum's own score (PostgreSQL 19)");

        with.Evidence.Single(static x => x.Label == "Autovacuum's own score (PostgreSQL 19)").Value
            .Should().Be("3.98, mostly dead rows (dead rows 3.98, inserts under 0.01, changes since analyze 0.2, transaction-id age under 0.01, "
                + "multixact age 0); autovacuum would vacuum it at its next look");
    }

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(0.00000005, "under 0.01")]
    [InlineData(0.0099, "under 0.01")]
    [InlineData(0.01, "0.01")]
    [InlineData(1.0, "1")]
    [InlineData(20, "20")]
    [InlineData(3.14159, "3.14")]
    public void A_score_part_is_worded_to_two_places_and_a_tiny_one_says_so(double value, string text)
    {
        VacuumBehindCheck.Score(value).Should().Be(text);
    }

    [Fact]
    public void A_score_names_the_part_that_is_the_largest_and_what_autovacuum_would_do()
    {
        Score(20, 0, doVacuum: false, doAnalyze: true).Dominant.Should().Be("changes since analyze", "the analyze part, 0.2, is the largest of 0, 0.004 and 0.2");
        Score(20, 5, doVacuum: true).Dominant.Should().Be("dead rows");
        VacuumBehindCheck.ScoreEvidence(Score(1.2, 1.2, doVacuum: true, doAnalyze: true, wraparound: true)).Value
            .Should().EndWith("; autovacuum would vacuum and analyse it at its next look, to prevent wraparound");
        VacuumBehindCheck.ScoreEvidence(Score(0.1, 0.1, doVacuum: false)).Value.Should().EndWith("; autovacuum would leave it at its next look");
    }

    // ---- T13: multixact members as evidence ------------------------------------------------------------------------------

    private static MultixactAgeFacts Aged(MultixactUsage? usage) =>
        new(
            new HealthWraparoundDatabase
            {
                MultixactFreezeMaxAge = 400_000_000,
                MultixactFailsafeAge = 1_600_000_000,
                ServerVersion = 190_000,
                DatabaseMultixactAge = 1_300_000_000,
                ServerMultixactAge = 1_300_000_000,
                MultixactCounter = 5_000_000,
            },
            [])
        {
            Usage = usage,
        };

    [Fact]
    public void T13_adds_the_member_usage_as_evidence_and_says_in_its_caveat_that_no_line_is_drawn_on_it()
    {
        HealthFinding bare = new MultixactAgeCheck().Evaluate(Aged(null), Evaluation()).Findings.Single();
        HealthFinding with = new MultixactAgeCheck()
            .Evaluate(Aged(new MultixactUsage(12_000, 48_000, 245_760, 77)), Evaluation())
            .Findings.Single();

        with.Severity.Should().Be(bare.Severity, "evidence moves no line");
        with.Title.Should().Be(bare.Title);
        with.Variant.Should().Be(bare.Variant);

        bare.Evidence.Select(static x => x.Label).Should().NotContain(static x => x.StartsWith("Multixact members", StringComparison.Ordinal));
        bare.Caveat.Should().Contain("PostgreSQL refused pg_get_multixact_stats() to the role the studio reads as");

        with.Evidence.Single(static x => x.Label == "Multixact members, whole server (PostgreSQL 19)").Value
            .Should().Be("48,000 members in 12,000 multixacts, 240 KiB of member storage; the oldest still needed is multixact 77");
        with.Caveat.Should().Contain("pg_get_multixact_stats() shows it here as evidence only: no line is drawn on it")
            .And.NotContain("Postgres has no view of it");
    }

    [Fact]
    public void A_role_outside_pg_read_all_stats_is_told_it_is_not_shown_and_a_server_with_no_multixact_reads_zeros()
    {
        // Measured on 19rc1: such a role gets a row of nulls, and a privileged role on a server with no multixact gets zeros.
        MultixactUsage withheld = new(null, null, null, null);
        withheld.Withheld.Should().BeTrue();
        MultixactAgeCheck.UsageEvidence(withheld).Value
            .Should().Be("not shown: PostgreSQL answers this role nothing - pg_get_multixact_stats() needs pg_read_all_stats (pg_monitor includes it)");

        MultixactUsage none = new(0, 0, 0, 1);
        none.Withheld.Should().BeFalse();
        MultixactAgeCheck.UsageEvidence(none).Value
            .Should().Be("0 members in 0 multixacts, 0 B of member storage; the oldest still needed is multixact 1");

        HealthFinding finding = new MultixactAgeCheck().Evaluate(Aged(withheld), Evaluation()).Findings.Single();
        finding.Caveat.Should().Contain("pg_get_multixact_stats() answers a role outside pg_read_all_stats nothing, so it is not shown to this role");
    }

    [Fact]
    public void T13s_caveat_tells_apart_below_19_shown_withheld_and_refused_on_19()
    {
        // Below 19: today's text, unchanged.
        MultixactAgeCheck.MemberStorageCaveat(Aged(null) with { Database = Aged(null).Database with { ServerVersion = 180_006 } })
            .Should().Be("Multixact member storage, which can run out before the ids do, is not read: Postgres has no view of it before 19.");

        // On 19 the members area no longer wraps; past about 2 billion members autovacuum vacuums aggressively.
        string shown = MultixactAgeCheck.MemberStorageCaveat(Aged(new MultixactUsage(1, 2, 10, 1)));
        shown.Should().StartWith("On PostgreSQL 19 multixact member storage no longer wraps around; past about 2 billion members (about 10 GB)")
            .And.EndWith("shows it here as evidence only: no line is drawn on it.");

        MultixactAgeCheck.MemberStorageCaveat(Aged(new MultixactUsage(null, null, null, null))).Should().Contain("so it is not shown to this role");

        string refused = MultixactAgeCheck.MemberStorageCaveat(Aged(null));
        refused.Should().Contain("PostgreSQL refused pg_get_multixact_stats() to the role the studio reads as").And.NotContain("before 19");
    }

    [Theory]
    [InlineData("42P01", true)]
    [InlineData("42703", true)]
    [InlineData("42883", true)]
    [InlineData("42501", true)]
    [InlineData("57014", true)]
    [InlineData("40001", false)]
    [InlineData("53300", false)]
    public void Evidence_reads_drop_what_postgres_turns_down_or_times_out_and_fail_on_nothing_else(string sqlState, bool dropped)
    {
        HealthTables.IsEvidenceRefused(new Npgsql.PostgresException("msg", "ERROR", "ERROR", sqlState)).Should().Be(dropped);
    }

    // ---- Maintenance: "Next for autovacuum" and "Stats since" -------------------------------------------------------------

    private static MaintenanceTableView View(string table, double? score, DateTimeOffset? reset = null) => new()
    {
        Key = "app." + table,
        Schema = "app",
        Table = table,
        Description = table,
        CountersReset = reset,
        Score = score is { } s ? new AutovacuumScoreRow("app", table, s, 0, 0, s, 0, 0, true, false, false) : null,
    };

    [Fact]
    public void Next_for_autovacuum_is_the_shown_tables_with_a_score_highest_first_and_at_most_ten()
    {
        List<MaintenanceTableView> shown =
        [
            View("quiet", null),
            .. Enumerable.Range(1, 12).Select(static i => View("t" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), i / 10d)),
            View("tied_b", 0.5),
            View("tied_a", 0.5),
        ];

        IReadOnlyList<MaintenanceTableView> next = MaintenanceService.NextOf(shown);

        next.Should().HaveCount(MaintenanceService.NextCount);
        next.Select(static x => x.Table).Take(4).Should().Equal("t12", "t11", "t10", "t09");
        next.Select(static x => x.Score!.Score).Should().BeInDescendingOrder();
        next.Select(static x => x.Table).Should().NotContain("quiet", "a table the view has no score for is not listed");

        MaintenanceService.NextOf([View("tied_b", 0.5), View("tied_a", 0.5)]).Select(static x => x.Table).Should().Equal("tied_a", "tied_b");
        MaintenanceService.NextOf([View("quiet", null)]).Should().BeEmpty();
    }

    [Fact]
    public void The_view_of_a_table_carries_its_score_and_its_own_reset_and_nothing_below_19()
    {
        DateTimeOffset reset = Now.AddDays(-3);
        TableFacts table = Table(Orders, shape: row => row with { CountersReset = reset, ServerVersion = 190_000 });
        AutovacuumScoreRow score = Score(2, 2, doVacuum: true);

        MaintenanceTableView view = MaintenanceService.ViewOf(table, Now, score);
        view.CountersReset.Should().Be(reset);
        view.Score.Should().BeSameAs(score);

        MaintenanceTableView old = MaintenanceService.ViewOf(Table(Orders, shape: static row => row with { ServerVersion = 180_006 }), Now);
        old.CountersReset.Should().BeNull();
        old.Score.Should().BeNull();
    }

    // ---- The statements ----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_score_statement_names_only_the_listed_tables_and_reads_the_view_through_their_own_oids()
    {
        string sql = HealthTableQueries.AutovacuumScoresSql;

        sql.Should().Contain("pg_catalog.pg_stat_autovacuum_scores").And.Contain("join scored s on s.root = l.oid and s.place = 1");
        sql.Should().Contain("@schemas").And.Contain("@tables", "the tables travel as values, so Show SQL names none");
        HealthTableQueries.ListStatements.Should().Contain(sql, "the list statements are held to D27's no-lock rules by HealthTableQueriesTests");
        HealthTableQueries.AutovacuumScores([new HealthTableTarget("app", "orders")]).ParameterNames.Should().BeEquivalentTo("@schemas", "@tables");
    }

    [Fact]
    public void The_multixact_statement_calls_the_function_by_its_catalog_name_and_casts_the_xid_through_text()
    {
        HealthHorizonQueries.MultixactUsageSql.Should().Contain("pg_catalog.pg_get_multixact_stats()").And.Contain("oldest_multixact::text::bigint");
        HealthHorizonQueries.Statements.Should().Contain(HealthHorizonQueries.MultixactUsageSql);
    }

    [Fact]
    public void The_table_and_storage_statements_read_a_tables_own_reset_by_name_so_one_statement_runs_on_every_version()
    {
        Regex byName = ByName();

        byName.Count(HealthTableQueries.TablesSql).Should().BeGreaterThanOrEqualTo(2, "the leaf rollup and the table's own row");
        byName.IsMatch(StorageQueries.TablesSql).Should().BeTrue();
        HealthTableQueries.TablesSql.Should().NotContain("s.stats_reset", "the column does not exist below 19, so it is only ever read through to_jsonb");
        StorageQueries.TablesSql.Should().NotContain("s.stats_reset");
    }

    [GeneratedRegex(@"to_jsonb\(s\) ->> 'stats_reset'", RegexOptions.CultureInvariant)]
    private static partial Regex ByName();
}
