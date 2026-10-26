using System.Text.RegularExpressions;

using NpgsqlTypes;
using PostgresStudio.Internal.Sql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The shape of the wraparound and horizon statements (T12's database half, T13, T14): catalog reads that lock nothing
/// they list, names as values, nothing created, no transaction id assigned, and every version-dependent column read so a
/// supported server without it answers null rather than 42703.
/// </summary>
/// <remarks>
/// No database: these are built statements. The live tests run them with a second connection watching
/// <c>pg_locks</c> (<c>HealthSweepProbe</c>).
/// </remarks>
public partial class HealthHorizonQueriesTests
{
    private static readonly string[] TargetSchemas = ["studio", "studio_events"];

    private static readonly string[] TargetTables = ["mt_doc_order", "mt_events"];

    private static readonly long[] LockIds = [4004L, 123L];

    private static readonly HealthTableTarget[] Targets =
    [
        new("studio", "mt_doc_order"),
        new("studio_events", "mt_events"),
    ];

    /// <summary>Every function that locks what it is handed, or that applies DDL, or that reads a sequence's state.</summary>
    [GeneratedRegex(
        @"\b(pg_partition_tree|pg_partition_ancestors|pg_partition_root|pg_relation_size|pg_total_relation_size|pg_table_size|pg_indexes_size|pg_sequence_last_value|pg_get_expr|pg_blocking_pids)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LockingFunction();

    /// <summary>Anything that writes, or selects by star.</summary>
    [GeneratedRegex(
        @"\b(insert|update|delete|create|alter|drop|truncate|grant|vacuum|analyze|nextval|setval)\b|select\s+\*|from\s+information_schema",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WritesOrHides();

    /// <summary>
    /// Every function that assigns a transaction id: a health read runs in a read-only transaction, and a forecast of the
    /// transaction-id rate that spent one per sample would be measuring itself.
    /// </summary>
    [GeneratedRegex(@"\b(txid_current|pg_current_xact_id|txid_current_if_assigned)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssignsATransactionId();

    public static TheoryData<string> Statements() => [.. HealthHorizonQueries.Statements];

    [Theory]
    [MemberData(nameof(Statements))]
    public void A_horizon_statement_calls_no_function_that_locks_what_it_lists(string sql)
    {
        LockingFunction().IsMatch(sql).Should().BeFalse("a size function opens the relation and pg_partition_tree locks every partition (D27)");
    }

    [Theory]
    [MemberData(nameof(Statements))]
    public void A_horizon_statement_writes_nothing_and_assigns_no_transaction_id(string sql)
    {
        WritesOrHides().IsMatch(sql).Should().BeFalse();
        AssignsATransactionId().IsMatch(sql).Should().BeFalse();
    }

    /// <summary>The anti-vacuity half: the scans see the things they look for.</summary>
    [Fact]
    public void The_scans_catch_what_they_look_for()
    {
        LockingFunction().IsMatch("select pg_catalog.pg_partition_tree('x'::regclass)").Should().BeTrue();
        WritesOrHides().IsMatch("select * from pg_catalog.pg_class").Should().BeTrue();
        WritesOrHides().IsMatch("select 1 from information_schema.tables").Should().BeTrue();
        WritesOrHides().IsMatch("where n.nspname in ('pg_catalog', 'information_schema')").Should().BeFalse("a schema's name as a value is not a read of it");
        AssignsATransactionId().IsMatch("select pg_catalog.pg_current_xact_id()").Should().BeTrue();
        AssignsATransactionId().IsMatch("select txid_current ()").Should().BeTrue();
    }

    [Fact]
    public void The_counter_is_the_snapshots_xmax_which_assigns_nothing()
    {
        HealthHorizonQueries.WraparoundSql.Should().Contain("pg_catalog.pg_snapshot_xmax(pg_catalog.pg_current_snapshot())");
        HealthHorizonQueries.WraparoundSql.Should().Contain("pg_catalog.mxid_age(d.datminmxid)", "the multixact counter is read from shared memory");
    }

    [Fact]
    public void The_stores_tables_travel_as_two_text_arrays_and_no_name_is_in_the_text()
    {
        HealthQuery query = HealthHorizonQueries.Wraparound(Targets);

        query.ParameterNames.Should().Equal("@schemas", "@tables");
        query.Parameters[0].NpgsqlDbType.Should().Be(NpgsqlDbType.Array | NpgsqlDbType.Text);
        query.Parameters[0].Value.Should().BeEquivalentTo(TargetSchemas);
        query.Parameters[1].Value.Should().BeEquivalentTo(TargetTables);
        query.Sql.Should().NotContain("mt_doc_order").And.NotContain("studio_events");
    }

    [Fact]
    public void The_other_tables_are_counted_and_never_selected_by_name()
    {
        string sql = HealthHorizonQueries.WraparoundSql;

        // The only relname the statement selects is a listed table's, through `listed`; the others are aggregates.
        Regex.Count(sql, @"\brelname\b", RegexOptions.CultureInvariant).Should().Be(2,
            "relname is read in `listed` - to match the store's own names - and selected as its table_name, and nowhere else");
        sql.Should().Contain("not exists (select 1 from members m where m.relid = a.oid)", "a store table, its TOAST table and its partitions are not 'others'");
        sql.Should().Contain("pg_catalog.count(*) filter").And.Contain("pg_catalog.max(o.tree_xid_age)");
    }

    [Fact]
    public void Partitions_are_walked_through_pg_inherits_and_a_parents_zero_relfrozenxid_is_never_aged()
    {
        string sql = HealthHorizonQueries.WraparoundSql;

        sql.Should().Contain("pg_catalog.pg_inherits").And.Contain("ch.relispartition");
        sql.Should().Contain("where c.relkind in ('r', 't', 'm')",
            "age() of a partitioned parent's zero relfrozenxid is int.MaxValue, which would be a Critical T12 on every partitioned store");
    }

    [Fact]
    public void A_setting_or_a_column_a_supported_server_may_lack_is_read_as_null_rather_than_named()
    {
        HealthHorizonQueries.WraparoundSql.Should().Contain("pg_catalog.current_setting('vacuum_failsafe_age', true)")
            .And.Contain("pg_catalog.current_setting('vacuum_multixact_failsafe_age', true)");

        string slots = HealthServerQueries.ReplicationSlots().Sql;
        slots.Should().Contain("inactive_since").And.Contain("invalidation_reason").And.Contain("conflicting");
        slots.Should().Contain("pg_catalog.pg_is_in_recovery()",
            "pg_current_wal_lsn() raises on a standby; R01's read uses the replay position there");
    }

    [Fact]
    public void T14_reads_the_slots_through_R01s_shared_read_and_its_key()
    {
        HealthServerQueries.ReplicationSlotsKey.Should().Be("server.replication-slots");
        HealthHorizonQueries.Statements.Should().NotContain(static x => x.Contains("pg_replication_slots", StringComparison.Ordinal),
            "T14 has no slots read of its own: one shape of answer under one key, R01's");
    }

    [Fact]
    public void The_holders_are_this_databases_sessions_by_H3s_rule_and_another_databases_only_as_an_age()
    {
        string sql = HealthHorizonQueries.HoldersSql;

        sql.Should().Contain(HealthConnectionQueries.ClientSession, "a session whose backend_type is withheld is still a session (H3)");
        sql.Should().Contain("a.datname = pg_catalog.current_database()").And.Contain("a.pid <> pg_catalog.pg_backend_pid()");
        sql.Should().Contain("a.datname is distinct from pg_catalog.current_database()");

        // The other databases' branches select no identity at all: seven nulls where this database's names its holder.
        Regex.Count(sql, "null::int, null::text, null::text, null::text, null::timestamptz, null::text, null::text", RegexOptions.CultureInvariant)
            .Should().Be(2, "another database's session and another database's prepared transaction are an age and nothing else");
        sql.Should().Contain("limit 1)", "and only the oldest of each");
    }

    [Fact]
    public void A_session_is_never_aged_by_its_transaction_alone()
    {
        string sql = HealthHorizonQueries.HoldersSql;

        sql.Should().Contain("when a.state = 'active' then greatest(a.query_start, a.xact_start)")
            .And.Contain("when a.state like 'idle in transaction%' then greatest(a.state_change, a.xact_start)",
                "a read-committed transaction takes a fresh snapshot per statement, so its age is only the most it can be - and a "
                + "statement that commits in a loop is older than its transaction, so the bound is the later of the two (B2)");
        sql.Should().Contain("pg_catalog.now() - a.xact_start", "which is still read, as the upper bound");
        sql.Should().NotContain("then a.query_start\n", "a statement's age alone can outlive its transaction");
    }

    /// <summary>
    /// B1: a lazy VACUUM shows a backend_xmin but is PROC_IN_VACUUM, which the horizon skips - so a session in
    /// pg_stat_progress_vacuum is no holder, in this database's branch and another database's alike.
    /// </summary>
    [Fact]
    public void A_lazy_vacuum_is_never_a_holder()
    {
        Regex.Count(HealthHorizonQueries.HoldersSql, @"not exists \(select 1 from pg_catalog\.pg_stat_progress_vacuum v where v\.pid = a\.pid\)",
                RegexOptions.CultureInvariant)
            .Should().Be(2, "both session branches - this database's and another's - leave a lazy VACUUM out");
    }

    /// <summary>
    /// F1: a parallel VACUUM's workers are PROC_IN_VACUUM too and have no row in pg_stat_progress_vacuum. A role that may read
    /// them sees backend_type 'parallel worker', which H3's predicate leaves out; one that may not sees a withheld row, which
    /// is left out when it holds only a snapshot at a running VACUUM's xmin, under that VACUUM's role, in its database - by
    /// the columns Postgres shows every role, never leader_pid, which it withholds.
    /// </summary>
    [Fact]
    public void A_parallel_vacuums_withheld_workers_are_never_holders()
    {
        string sql = HealthHorizonQueries.HoldersSql;

        Regex.Count(sql, @"and not \(a\.backend_type is null and a\.backend_xid is null and exists \(\s*select 1 from pg_catalog\.pg_stat_activity w\s*"
                + @"join pg_catalog\.pg_stat_progress_vacuum v on v\.pid = w\.pid\s*"
                + @"where w\.backend_xmin = a\.backend_xmin and w\.usesysid = a\.usesysid and w\.datid = a\.datid\)\)",
                RegexOptions.CultureInvariant)
            .Should().Be(2, "both session branches leave a withheld parallel worker out");

        sql.Should().NotContain("leader_pid", "Postgres fills it only for a role that may read the row");
        HealthConnectionQueries.ClientSession.Should().Contain("a.backend_type = 'client backend'",
            "a visible parallel worker is not a client backend, and is never read");
    }

    /// <summary>
    /// Blocking 1: the id a holder owns is read apart from how far back it holds - a session's own transaction id, a
    /// prepared transaction's - because only that id is dated by the counter's samples; a standby's feedback owns none.
    /// </summary>
    [Fact]
    public void The_id_a_holder_owns_is_read_beside_how_far_back_it_holds()
    {
        string sql = HealthHorizonQueries.HoldersSql;

        Regex.Count(sql, @"pg_catalog\.age\(a\.backend_xid\)::bigint as own_xid_age,\n|pg_catalog\.age\(a\.backend_xid\)::bigint,\n", RegexOptions.CultureInvariant)
            .Should().Be(2, "this database's sessions and another database's");
        Regex.Count(sql, @"pg_catalog\.count\(\*\) over \(\),\n\s*pg_catalog\.age\(p\.transaction\)::bigint,\n", RegexOptions.CultureInvariant)
            .Should().Be(1, "this database's prepared transactions");
        Regex.Count(sql, @"1::bigint,\n\s*pg_catalog\.age\(p\.transaction\)::bigint,\n", RegexOptions.CultureInvariant)
            .Should().Be(1, "another database's prepared transaction");
        sql.Should().Contain("pg_catalog.count(*) over (),\n            null::bigint,\n", "a standby's feedback is the oldest id running when it took its snapshot");
        sql.Should().Contain("h.holds_daemon_lock, h.total,\n       h.own_xid_age, h.own_xid, h.xmin_raw\n");

        // W5-V1: the same ids raw, so a slot pinned at a writer's own transaction id matches it (and a hold has an identity).
        Regex.Count(sql, @"a\.backend_xid::text", RegexOptions.CultureInvariant).Should().Be(2, "both session branches");
        Regex.Count(sql, @"p\.transaction::text", RegexOptions.CultureInvariant).Should().Be(2, "both prepared branches");
        sql.Should().Contain("null::text, r.backend_xmin::text", "a standby's raw xmin is its identity's");
    }

    /// <summary>
    /// Follow-up 3: an owner ties the snapshots pinned at its id on how far back they hold, so the session branches' limits
    /// keep it first - by own_xid_age, the 17th column - or a limit could cut it and leave the snapshots matched to nothing.
    /// </summary>
    [Fact]
    public void The_session_branches_keep_an_owner_ahead_of_the_snapshots_pinned_at_its_id()
    {
        string sql = HealthHorizonQueries.HoldersSql;

        Regex.Count(sql, @"order by 2 desc nulls last, 17 desc nulls last\n\s*limit (@limit|1)\)", RegexOptions.CultureInvariant)
            .Should().Be(2, "this database's sessions under @limit, and another database's oldest under limit 1");

        // The 17th column of the session branch is the owned id's age.
        string branch = sql[sql.IndexOf("(select 0 as kind", StringComparison.Ordinal)..sql.IndexOf(" from pg_catalog.pg_stat_activity a\n     where a.datname = pg_catalog.current_database()", StringComparison.Ordinal)];
        branch.Should().Contain("pg_catalog.age(a.backend_xid)::bigint as own_xid_age,\n");
        branch.TrimEnd().Should().EndWith("a.backend_xid::text as own_xid, a.backend_xmin::text as xmin_raw", "W5-V1's raw ids come after the 17th");
        Regex.Count(branch, @" as (kind|xid_age|at_least|transaction_open|bound|usename|application_name|client_addr|state|gid|has_xid|hidden|holds_daemon_lock|total|own_xid_age)\b",
                RegexOptions.CultureInvariant)
            .Should().Be(15, "15 aliased columns, and a.pid and a.backend_start unaliased: 17 in all up to own_xid_age");
    }

    /// <summary>Follow-up 4: another database's holders are an age and nothing else - not even a count.</summary>
    [Fact]
    public void Another_databases_holders_are_not_counted()
    {
        Regex.Count(HealthHorizonQueries.HoldersSql, @"pg_catalog\.count\(\*\) over \(\)", RegexOptions.CultureInvariant)
            .Should().Be(3, "this database's sessions, its prepared transactions and the standbys are counted; another database's are not");
    }

    [Fact]
    public void The_servers_oldest_database_leaves_out_one_DROP_DATABASE_left_invalid()
    {
        HealthHorizonQueries.WraparoundSql.Should().Contain("where d.datconnlimit <> -2");
    }

    [Fact]
    public void Every_value_the_holders_read_binds_is_a_parameter()
    {
        HealthQuery query = HealthHorizonQueries.Holders(LockIds);

        query.ParameterNames.Should().Equal("@daemon_lock_ids", "@limit");
        query.Parameters[0].NpgsqlDbType.Should().Be(NpgsqlDbType.Array | NpgsqlDbType.Bigint);
        query.Parameters[0].Value.Should().BeEquivalentTo(LockIds);
        query.Parameters[1].Value.Should().Be(HealthHorizonQueries.MaxHolders);
        query.Sql.Should().NotContain("4004");
    }
}
