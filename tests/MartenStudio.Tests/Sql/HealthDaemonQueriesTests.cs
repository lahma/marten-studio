using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health.Checks;
using PostgresStudio.Internal.Sql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The daemon checks' statements: what they read, that every value is a parameter, and that nothing in them locks
/// what it lists. No database - <c>DaemonHealthLiveTests</c> runs them against a real one.
/// </summary>
public class HealthDaemonQueriesTests
{
    private static readonly string[] Migrated =
    [
        "name", "last_seq_id", "last_updated", "mode", "rebuild_threshold", "assigned_node", "heartbeat", "agent_status",
        "pause_reason", "running_on_node", "warning_behind_threshold", "critical_behind_threshold", "failure_category",
        "failure_event_sequence", "failure_event_type", "failure_event_tenant_id",
    ];

    /// <summary>Everything the builders make, for the properties every one of them must have.</summary>
    public static TheoryData<string> Every()
    {
        TheoryData<string> data = new();

        foreach (string name in (string[]) ["high-water", "shards", "problems", "locks", "dead-letters", "explain-sessions", "explain-prepared", "explain-hidden"])
        {
            data.Add(name);
        }

        return data;
    }

    private static HealthQuery Build(string name) => name switch
    {
        "high-water" => HealthDaemonQueries.HighWater("studio_events"),
        "shards" => HealthDaemonQueries.ShardRows("studio_events", Migrated, true, ["DailySales:All"]),
        "problems" => HealthDaemonQueries.ProblemShardRows("studio_events", Migrated),
        "locks" => HealthDaemonQueries.DaemonLocks([4444, 12345]),
        "dead-letters" => HealthDaemonQueries.DeadLettersByProjection("studio_events", "mt_doc_deadletterevent", "projectionName"),
        "explain-sessions" => HealthDaemonQueries.ExplainSessions("studio_events", [4444], DateTimeOffset.UnixEpoch, null),
        "explain-prepared" => HealthDaemonQueries.ExplainPreparedTransactions(),
        "explain-hidden" => HealthDaemonQueries.ExplainHiddenWriters(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    /// <summary>D27: a list locks nothing it lists. None of these functions takes a lock per object, and none is here.</summary>
    [Theory]
    [MemberData(nameof(Every))]
    public void No_statement_calls_a_function_that_locks_what_it_lists_or_selects_star(string name)
    {
        string sql = Build(name).Sql;

        sql.Should().NotContain("pg_partition_").And.NotContain("pg_sequence_last_value").And.NotContain("pg_relation_size")
            .And.NotContain("pg_total_relation_size").And.NotContain("select *");
    }

    /// <summary>
    /// A fresh statement every call: an NpgsqlParameter belongs to one command. By reference - a statement with no
    /// parameters shares the one empty list, which is no parameter shared.
    /// </summary>
    [Theory]
    [MemberData(nameof(Every))]
    public void Every_call_builds_fresh_parameters(string name)
    {
        HealthQuery first = Build(name);
        HealthQuery second = Build(name);

        first.Parameters.Where(x => second.Parameters.Any(y => ReferenceEquals(x, y))).Should().BeEmpty();
    }

    /// <summary>
    /// CL1 item 6: the daemon checks have no shape statement of their own any more - they read the one the event checks
    /// share (<see cref="HealthEventQueries.EventStoreShape" />, tested in <c>HealthEventQueriesTests</c>), so a sweep that
    /// runs both areas asks the catalog once.
    /// </summary>
    [Fact]
    public void The_daemon_checks_build_no_shape_statement_of_their_own()
    {
        typeof(HealthDaemonQueries).GetMethods().Select(static x => x.Name).Should().NotContain("EventStoreShape");
    }

    [Fact]
    public void The_high_water_reading_is_one_snapshot_of_the_three_rows_the_sequence_and_two_primary_key_probes()
    {
        HealthQuery query = HealthDaemonQueries.HighWater("studio_events");

        query.Sql.Should().Contain("from \"studio_events\".\"mt_event_progression\"");
        query.Sql.Should().Contain("case when s.is_called then s.last_value else 0 end from \"studio_events\".\"mt_events_sequence\"");
        query.Sql.Should().Contain("pg_catalog.min(e.seq_id) from \"studio_events\".\"mt_events\" e where e.seq_id > h.last_seq_id");

        // D09's two: Marten's own reading of what the sequence has handed out (#5091), and the other end of the same key.
        query.Sql.Should().Contain(
            "(select case when s.is_called then s.last_value else s.last_value - 1 end from \"studio_events\".\"mt_events_sequence\" s) as allocated_high");
        query.Sql.Should().Contain("(select pg_catalog.max(e.seq_id) from \"studio_events\".\"mt_events\" e) as highest_committed");
        query.Sql.Should().Contain("pg_catalog.transaction_timestamp() as server_now", "every age is server time");
        query.Sql.Should().NotContain("HighWaterStuckGap", "the bookkeeping row names are values");

        query.Parameters.Select(static x => x.Value).Should().BeEquivalentTo(new object[] { "HighWaterMark", "HighWaterStuckGap", "HighWaterAllocationFence" });
    }

    [Fact]
    public void Shard_rows_read_the_extended_columns_only_under_the_flag_and_the_thresholds_whenever_present()
    {
        HealthQuery on = HealthDaemonQueries.ShardRows("studio_events", Migrated, true, ["DailySales:All"]);
        HealthQuery off = HealthDaemonQueries.ShardRows("studio_events", Migrated, false, ["DailySales:All"]);

        on.Sql.Should().StartWith("select p.name, p.last_seq_id, p.last_updated, p.\"heartbeat\", p.\"agent_status\"");
        on.Sql.Should().Contain("p.\"warning_behind_threshold\", p.\"critical_behind_threshold\"");
        off.Sql.Should().NotContain("heartbeat").And.NotContain("agent_status");
        off.Sql.Should().Contain("p.\"warning_behind_threshold\"", "Marten writes no threshold, a host may, and the flag is not about them");

        on.Sql.Should().Contain("where p.name = any(@shards)").And.EndWith("limit 1000");
        on.Sql.Should().NotContain("running_on_node").And.NotContain("failure_event_tenant_id", "a tenant's name is not read where nothing renders it");

        HealthDaemonQueries.SelectedShardColumns(Migrated, false).Should().Equal("warning_behind_threshold", "critical_behind_threshold");
    }

    [Fact]
    public void A_table_without_last_updated_reads_it_as_null_rather_than_failing()
    {
        HealthDaemonQueries.ShardRows("studio_events", ["name", "last_seq_id"], false, ["x:All"]).Sql
            .Should().StartWith("select p.name, p.last_seq_id, null::timestamptz as last_updated");
    }

    [Fact]
    public void Problem_rows_are_the_not_running_and_the_failed()
    {
        HealthQuery query = HealthDaemonQueries.ProblemShardRows("studio_events", Migrated);

        query.Sql.Should().Contain("where (p.agent_status is not null and p.agent_status <> @running) or p.failure_category is not null");
        query.Parameters.Single().Value.Should().Be("Running");
    }

    /// <summary>
    /// Weasel takes each lock as a single 64-bit key - objsubid 1, the key over classid and objid - so the key is put
    /// back together, which also matches a lock id the hash made negative.
    /// </summary>
    [Fact]
    public void The_lock_read_matches_a_64_bit_advisory_key_in_this_database_only()
    {
        HealthQuery query = HealthDaemonQueries.DaemonLocks([4444]);

        query.Sql.Should().Contain("l.locktype = 'advisory'").And.Contain("l.granted").And.Contain("l.objsubid = 1");
        query.Sql.Should().Contain("((l.classid::bigint << 32) | l.objid::bigint) = any(@lock_keys)");
        query.Sql.Should().Contain("l.database = (select d.oid from pg_catalog.pg_database d where d.datname = pg_catalog.current_database())");
        query.Parameters.Single().Value.Should().BeEquivalentTo(new long[] { 4444 });
    }

    [Fact]
    public void The_dead_letter_count_takes_the_json_key_as_a_value()
    {
        HealthQuery query = HealthDaemonQueries.DeadLettersByProjection("studio_events", "mt_doc_deadletterevent", "projection_name");

        query.Sql.Should().Contain("d.data ->> @projection_key").And.Contain("from \"studio_events\".\"mt_doc_deadletterevent\" d");
        query.Sql.Should().NotContain("projection_name");
    }

    /// <summary>
    /// CL1 item 3: D06 counts the table the dead-letter list reads - <c>DeadLetterQueries.TableFor</c>, its schema and its
    /// name - so a host that re-aliased <c>DeadLetterEvent</c> is counted in <c>mt_doc_failures</c>, quoted, and the default
    /// name is not asked for. (Marten re-pins the type's schema to the event store's - <c>ApplyConfiguration</c> registers
    /// it after the host's own configuration, read at <c>V9.39.0</c> - so the alias is what a host can really move.)
    /// </summary>
    [Fact]
    public void The_dead_letter_count_reads_the_table_the_store_maps_quoted()
    {
        using DocumentStore store = DocumentStore.For(static options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = "studio_dl";
            options.Events.DatabaseSchemaName = "studio_dl_events";
            options.Events.AddEventType<DeadLetterQueriesTests.SomethingHappened>();
            options.Schema.For<DeadLetterEvent>().DocumentAlias("failures");
        });

        DocumentTableInfo table = DeadLetterQueries.TableFor(store.Options);
        table.Schema.Should().Be("studio_dl_events", "the premise: the event store's schema");
        table.Table.Should().Be("mt_doc_failures", "the premise: the host's own alias is honoured");

        HealthQuery query = HealthDaemonQueries.DeadLettersByProjection(table.Schema, table.Table, "projectionName");

        query.Sql.Should().Contain("from \"studio_dl_events\".\"mt_doc_failures\" d");
        query.Sql.Should().NotContain("mt_doc_deadletterevent");
    }

    /// <summary>Marten's GapLivenessProbe questions (V9.39.0, V9.50.2), with its two exclusions.</summary>
    [Fact]
    public void The_explain_asks_martens_questions_and_rules_out_what_marten_rules_out()
    {
        HealthQuery query = HealthDaemonQueries.ExplainSessions("studio_events", [4444], DateTimeOffset.UnixEpoch, null);

        query.Sql.Should().Contain("l.mode = 'RowExclusiveLock'").And.Contain("c.relname like 'mt_events%'");
        query.Sql.Should().Contain("a.state in ('idle in transaction', 'idle in transaction (aborted)')").And.Contain("a.state_change < @fence");
        query.Sql.Should().Contain("a.backend_xid is null", "a daemon lock session that has written is never excluded (#5125)");
        query.Sql.Should().Contain("a.datname = pg_catalog.current_database()").And.Contain("a.xact_start <= @pinned_since");
        query.Sql.Should().Contain("pg_catalog.left(a.query, @query_length) as query")
            .And.Contain("coalesce(pg_catalog.length(a.query) > @query_length, false) as query_truncated",
                "a cut statement must say it was cut, so its half-written last name is dropped before it is masked");

        query.Parameters.Single(static x => x.ParameterName == "fence").Value
            .Should().Be(DateTimeOffset.MinValue.ToUniversalTime(), "no fence means nothing is quiescent - Marten's unfenced behaviour");
    }

    [Fact]
    public void Prepared_transactions_and_hidden_writers_are_this_databases()
    {
        HealthDaemonQueries.ExplainPreparedTransactions().Sql.Should().Contain("p.database = pg_catalog.current_database()");
        HealthDaemonQueries.ExplainHiddenWriters().Sql.Should()
            .Contain("l.locktype = 'transactionid'").And.Contain("a.datname = pg_catalog.current_database()").And.Contain("a.xact_start is null");
    }

    [Fact]
    public void A_schema_that_needs_quoting_is_quoted_and_one_that_cannot_be_one_is_refused()
    {
        HealthDaemonQueries.HighWater("Odd Schema").Sql.Should().Contain("\"Odd Schema\".\"mt_event_progression\"");

        Action refused = () => HealthDaemonQueries.HighWater("has\"quote");
        refused.Should().Throw<ArgumentException>();
    }

    // ---- The lock ids ------------------------------------------------------------------------------------------

    /// <summary>JasperFx's own formula, at V2.74.0 and V2.87.1: the base, and one per shard over the event schema.</summary>
    [Fact]
    public void The_lock_keys_are_the_base_and_jasperfxs_own_per_shard_ids()
    {
        ShardName shard = new("DailySales", "All", 1);

        IReadOnlyList<long> keys = DaemonCheckConfiguration.LockKeysFor("studio_events", [shard], 4444);

        keys.Should().BeEquivalentTo(new long[] { 4444, ProjectionLockIds.Compute("studio_events", shard, 4444) });
    }
}
