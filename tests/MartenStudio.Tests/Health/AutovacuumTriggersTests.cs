using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>
/// <c>Autovacuum.Triggers</c> against the numbers B0 measured autovacuum itself acting on (B0 §7b, <c>out-g2.txt</c> and
/// <c>out-g2b.txt</c>, 2026-10-20): <c>store.ev</c> (append-only, insert threshold 1,000, insert scale 0.5) and
/// <c>store.upd</c> (10,000 rows; on 18 a per-table <c>autovacuum_vacuum_max_threshold</c> of 100).
/// </summary>
public class AutovacuumTriggersTests
{
    /// <summary>B0's <c>store.ev</c> storage parameters.</summary>
    private static AutovacuumInputs Ev(double reltuples, long relpages, long? relallfrozen) => new()
    {
        Reltuples = reltuples,
        RelPages = relpages,
        RelAllFrozen = relallfrozen,
        InsertThreshold = 1_000,
        InsertScaleFactor = 0.5,
        AnalyzeThreshold = 1_000_000_000,
        VacuumMaxThreshold = relallfrozen is null ? null : 100_000_000,
    };

    [Fact]
    public void PostgreSQL_17_inserts_trigger_at_6000_then_7494_after_autovacuum_counts_the_appends()
    {
        // After the load and VACUUM (FREEZE): reltuples 10,000; 17 has no relallfrozen.
        Autovacuum.Triggers(Ev(10_000, 213, null)).Insert.Should().Be(6_000);

        // After the first autovacuum: reltuples 12,989 - 1,000 + 0.5 x 12,989 = 7,494.5, which Postgres' round() makes 7,494.
        AutovacuumTriggers after = Autovacuum.Triggers(Ev(12_989, 280, null));
        after.Insert.Should().BeApproximately(7_494.5, 0.001);
        Autovacuum.Rounded(after.Insert!.Value).Should().Be(7_494);

        // 3,500 appends are not past it: no autovacuum within 15 s on 17.
        new AutovacuumUnit { Counter = 3_500, Trigger = after.Insert.Value }.Due.Should().BeFalse();
    }

    [Fact]
    public void PostgreSQL_18_scales_the_insert_trigger_by_the_unfrozen_share_1000_then_2577()
    {
        // All 213 pages frozen: the scale part vanishes and the trigger is the threshold alone.
        AutovacuumTriggers frozen = Autovacuum.Triggers(Ev(10_000, 213, 213));
        frozen.Insert.Should().Be(1_000);
        frozen.UnfrozenShare.Should().Be(0);

        // 212 of 280 pages frozen: 1,000 + 0.5 x 12,989 x (1 - 212/280) = 2,577.2.
        AutovacuumTriggers after = Autovacuum.Triggers(Ev(12_989, 280, 212));
        Autovacuum.Rounded(after.Insert!.Value).Should().Be(2_577);

        // The same 3,500 appends that 17 sits on are past it: autovacuum ran within a second on 18.
        new AutovacuumUnit { Counter = 3_500, Trigger = after.Insert.Value }.Due.Should().BeTrue();
    }

    [Fact]
    public void PostgreSQL_18_caps_the_dead_row_trigger_at_autovacuum_vacuum_max_threshold()
    {
        // 17: 50 + 0.2 x 10,000 = 2,050, then 50 + 0.2 x 9,700 = 1,990 - 300 deletes are not past it.
        Autovacuum.Triggers(new AutovacuumInputs { Reltuples = 10_000 }).Vacuum.Should().Be(2_050);
        Autovacuum.Triggers(new AutovacuumInputs { Reltuples = 9_700 }).Vacuum.Should().Be(1_990);

        // 18, the table's own cap of 100: the trigger is 100, and the same 300 deletes are past it.
        double capped = Autovacuum.Triggers(new AutovacuumInputs { Reltuples = 10_000, VacuumMaxThreshold = 100 }).Vacuum;
        capped.Should().Be(100);
        new AutovacuumUnit { Counter = 300, Trigger = capped }.Due.Should().BeTrue();

        // A negative cap is no cap, as Postgres reads -1.
        Autovacuum.Triggers(new AutovacuumInputs { Reltuples = 10_000, VacuumMaxThreshold = -1 }).Vacuum.Should().Be(2_050);
    }

    [Fact]
    public void A_never_analysed_table_counts_as_empty_and_the_comparison_is_strict()
    {
        AutovacuumTriggers triggers = Autovacuum.Triggers(new AutovacuumInputs { Reltuples = -1, RelPages = 0 });

        triggers.Vacuum.Should().Be(50, "reltuples -1 counts as 0");
        triggers.Insert.Should().Be(1_000);
        triggers.Analyze.Should().Be(50);

        new AutovacuumUnit { Counter = 50, Trigger = 50 }.Due.Should().BeFalse("autovacuum acts only past the trigger (strict >)");
        new AutovacuumUnit { Counter = 51, Trigger = 50 }.Due.Should().BeTrue();
    }

    /// <summary>
    /// The locks autovacuum gives way to (W5-V1 review, N1): a prepared transaction's, which have no process, and a request
    /// still waiting - autovacuum takes its lock only if it can have it at once, so a queued ALTER keeps it away too. Since X3
    /// they are the sweep's one read of the lock table, and the table read takes them as <c>@strong_locks</c>.
    /// </summary>
    [Fact]
    public void The_lock_read_counts_a_prepared_transactions_locks_and_a_queued_request()
    {
        string sql = HealthConnectionQueries.LockTableSql;
        int start = sql.IndexOf("relations as (", StringComparison.Ordinal);
        string locks = sql[start..sql.IndexOf("counted as (", start, StringComparison.Ordinal)];

        locks.Should().Contain("k.pid is null", "a prepared transaction's locks have a null pid");
        locks.Should().NotContain("granted", "a queued strong request keeps autovacuum's conditional lock away as a granted one does");
        locks.Should().Contain("'AccessExclusiveLock'").And.Contain("'ShareUpdateExclusiveLock'").And.NotContain("'AccessShareLock'");
        locks.Should().Contain("k.pid not in (select m.pid from maintenance m)",
            "a running VACUUM or ANALYZE is autovacuum's own kind, and does not keep it off a table");
        sql.Should().Contain("select v.pid from pg_catalog.pg_stat_progress_vacuum v").And.Contain("select z.pid from pg_catalog.pg_stat_progress_analyze z");

        string tables = HealthTableQueries.TablesSql;
        tables.Should().Contain("from pg_catalog.unnest(@strong_locks::oid[]) as l(relid)").And.NotContain("pg_locks");
    }

    [Fact]
    public void A_decision_the_read_marks_locked_is_locked()
    {
        HealthTableQueries.ParseUnit("""{"counter": 1, "trigger": 2, "threshold": 1, "scale": 0.2, "tuples": 5, "last": null, "running": false, "locked": true}""")
            .Locked.Should().BeTrue();
        HealthTableQueries.ParseUnit("""{"counter": 1, "trigger": 2, "threshold": 1, "scale": 0.2, "tuples": 5, "last": null, "running": false}""")
            .Locked.Should().BeFalse("a read without the field is no lock");
    }

    [Fact]
    public void A_negative_insert_threshold_switches_the_insert_trigger_off()
    {
        Autovacuum.Triggers(new AutovacuumInputs { Reltuples = 10_000, InsertThreshold = -1 }).Insert.Should().BeNull();
    }

    [Fact]
    public void Relallfrozen_past_relpages_is_held_to_it_as_Postgres_clamps_it()
    {
        Autovacuum.Triggers(Ev(10_000, 100, 400)).UnfrozenShare.Should().Be(0);
    }

    [Theory]
    [InlineData(0.2, 0.1)]
    [InlineData(0.1, 0.05)]
    [InlineData(0.05, 0.02)]
    [InlineData(0.01, 0.005)]
    [InlineData(0.03, 0.02)]
    [InlineData(0.001, null)]
    [InlineData(0.0005, null)]
    public void The_remedy_offers_the_next_rung_of_the_ladder_below_the_scale_factor_in_force(double current, double? suggested)
    {
        Autovacuum.LowerScaleFactor(current).Should().Be(suggested);
    }

    [Fact]
    public void A_trigger_explains_where_each_of_its_parts_comes_from()
    {
        AutovacuumUnit unit = new() { Counter = 40_000, Trigger = 100, Threshold = 50, ScaleFactor = 0.2, Tuples = 10_000 };

        Autovacuum.Explain(unit, "autovacuum_vacuum", ["autovacuum_vacuum_scale_factor=0.2"], partitioned: false, cap: 100)
            .Should().Be("100 (autovacuum_vacuum_threshold 50, the server's setting, + autovacuum_vacuum_scale_factor 0.2, the table's own "
                + "storage parameter, × ~10,000 rows (reltuples), capped at autovacuum_vacuum_max_threshold 100)");
    }
}
