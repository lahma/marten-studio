using System.Text.RegularExpressions;

using Npgsql;
using PostgresStudio.Internal.Sql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The shape of every connection and session statement, with no database: this database only, never the
/// studio's own session, client backends only, every value a parameter, no table name, nothing that ends a
/// session, and the version-dependent columns read so that an older server answers null rather than 42703.
/// </summary>
public class HealthConnectionQueriesTests
{
    /// <summary>The reads of one database's sessions.</summary>
    public static TheoryData<string> SessionReads => new() { "IdleInTransaction", "LongRunningStatements", "LockWaits", "SessionSnapshot" };

    /// <summary>Every statement the builder makes.</summary>
    public static TheoryData<string> Every => new()
    {
        "ServerConnections", "IdleInTransaction", "LongRunningStatements", "LockWaits", "Blockers",
        "PreparedTransactions", "DatabaseCounters", "SessionSnapshot", "LockSnapshot",
    };

    /// <summary>One statement by name - the theories pass names, because the query type is internal.</summary>
    private static HealthQuery Query(string name) => name switch
    {
        "ServerConnections" => HealthConnectionQueries.ServerConnections(),
        "IdleInTransaction" => HealthConnectionQueries.IdleInTransaction(TimeSpan.FromSeconds(1), [4444L, 123456L]),
        "LongRunningStatements" => HealthConnectionQueries.LongRunningStatements(TimeSpan.FromSeconds(1)),
        "LockWaits" => HealthConnectionQueries.LockWaits(TimeSpan.FromSeconds(1)),
        "Blockers" => HealthConnectionQueries.Blockers([12, 34]),
        "PreparedTransactions" => HealthConnectionQueries.PreparedTransactions(),
        "DatabaseCounters" => HealthConnectionQueries.DatabaseCounters(),
        "SessionSnapshot" => HealthConnectionQueries.SessionSnapshot(),
        "LockSnapshot" => HealthConnectionQueries.LockSnapshot(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such statement."),
    };

    [Theory]
    [MemberData(nameof(SessionReads))]
    public void A_session_read_is_bounded_to_this_databases_client_sessions_and_never_the_studios_own(string name)
    {
        HealthQuery query = Query(name);

        query.Sql.Should().Contain("a.datname = current_database()", name);
        query.Sql.Should().Contain("a.backend_type = 'client backend'", name);
        query.Sql.Should().Contain("a.pid <> pg_backend_pid()", name);
        query.Sql.Should().Contain("pg_has_role(current_user, 'pg_read_all_stats', 'USAGE')",
            "{0} says how much of pg_stat_activity this role could see", name);
    }

    [Theory]
    [MemberData(nameof(Every))]
    public void No_statement_ends_a_session_writes_or_names_a_table(string name)
    {
        HealthQuery query = Query(name);

        query.Sql.Should().NotContainEquivalentOf("pg_cancel_backend", name);
        query.Sql.Should().NotContainEquivalentOf("pg_terminate_backend", name);
        query.Sql.Should().NotMatchRegex(@"(?i)\b(insert|update|delete|create|alter|drop|truncate|grant|vacuum|analyze|lock)\s", name);
        query.Sql.Should().NotContain("mt_", "{0} names no Marten table: a hidden type's table must never be in Show SQL", name);
    }

    [Theory]
    [MemberData(nameof(Every))]
    public void Every_value_and_every_limit_is_a_parameter(string name)
    {
        HealthQuery query = Query(name);

        // "limit 1" in a scalar subquery is structure, not a value; any other limit is bound.
        query.Sql.Should().NotMatchRegex(@"limit\s+([02-9]|\d{2,})", "{0} binds its limit", name);
        query.Sql.Should().NotMatchRegex(@"secs\s*=>\s*\d", name);

        foreach (NpgsqlParameter parameter in query.Parameters)
        {
            query.Sql.Should().Contain("@" + parameter.ParameterName, "{0} uses every parameter it binds", name);
        }
    }

    [Fact]
    public void pg_blocking_pids_is_asked_only_of_a_session_that_waits_for_a_lock()
    {
        HealthConnectionQueries.SessionSnapshot().Sql.Should().Contain("case when a.wait_event_type = 'Lock' then pg_blocking_pids(a.pid) end");
        HealthConnectionQueries.LockWaits(TimeSpan.Zero).Sql.Should().Contain("a.wait_event_type = 'Lock'");

        foreach (string sql in new[]
        {
            HealthConnectionQueries.IdleInTransaction(TimeSpan.Zero, []).Sql,
            HealthConnectionQueries.LongRunningStatements(TimeSpan.Zero).Sql,
            HealthConnectionQueries.ServerConnections().Sql,
            HealthConnectionQueries.LockSnapshot().Sql,
            HealthConnectionQueries.PreparedTransactions().Sql,
        })
        {
            sql.Should().NotContain("pg_blocking_pids", "it takes the lock manager's state exclusively for a moment, so nobody else calls it");
        }
    }

    [Fact]
    public void Columns_a_server_may_not_have_are_read_so_that_their_absence_is_null()
    {
        HealthConnectionQueries.LockWaits(TimeSpan.Zero).Sql.Should().Contain("(to_jsonb(l) ->> 'waitstart')::timestamptz")
            .And.NotContain("l.waitstart", "naming pg_locks.waitstart on PostgreSQL 13 is 42703, a failed check");
        HealthConnectionQueries.LockSnapshot().Sql.Should().NotContain("l.waitstart");
        HealthConnectionQueries.ServerConnections().Sql.Should().Contain("current_setting('reserved_connections', true)");
    }

    [Fact]
    public void Only_the_server_count_reaches_past_this_database_and_it_is_only_a_count()
    {
        string sql = HealthConnectionQueries.ServerConnections().Sql;

        sql.Should().Contain("(select count(*) from pg_stat_activity a where " + HealthConnectionQueries.ClientSession + ") as client_backends");
        sql.Should().NotContain("usename").And.NotContain("query").And.NotContain("client_addr").And.NotContain("application_name");
    }

    /// <summary>
    /// B3: a role without <c>pg_read_all_stats</c> reads another role's row with <c>backend_type</c> null, so a bare
    /// <c>backend_type = 'client backend'</c> drops it - and "cannot see" becomes "nobody there". Every filter on the
    /// backend type is the whole predicate, which counts such a row when a role is behind it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Every))]
    public void A_session_the_role_cannot_see_into_is_counted_and_never_dropped(string name)
    {
        string sql = Query(name).Sql;

        int bare = Regex.Count(sql, Regex.Escape("a.backend_type = 'client backend'"));
        int whole = Regex.Count(sql, Regex.Escape(HealthConnectionQueries.ClientSession));

        bare.Should().Be(whole, "{0} filters on the backend type only through the predicate that keeps a hidden session", name);
        HealthConnectionQueries.ClientSession.Should().Be(
            "(a.backend_type = 'client backend' or (a.backend_type is null and a.usesysid is not null and a.datid is not null))",
            "a client session has a role and a database; the logical replication launcher has a role and no database");
    }

    [Fact]
    public void A_hidden_session_is_one_whose_backend_type_is_withheld()
    {
        HealthConnectionQueries.SessionSnapshot().Sql.Should().Contain("count(*) filter (where a.backend_type is null) as hidden_sessions");
        HealthConnectionQueries.LockSnapshot().Sql.Should().Contain(HealthConnectionQueries.ClientSession, "a hidden session's locks are its locks");
    }

    /// <summary>
    /// B1: a lock read names the table a locked relation belongs to, never the locked relation - an index's, a
    /// partition's or a TOAST table's name carries a tenant id, a hidden type's alias or an oid.
    /// </summary>
    [Theory]
    [InlineData("LockWaits")]
    [InlineData("LockSnapshot")]
    public void A_lock_read_returns_the_owner_never_the_locked_relations_own_name(string name)
    {
        string sql = Query(name).Sql;

        sql.Should().StartWith("with recursive ");
        sql.Should().Contain("left join pg_catalog.pg_index ix on ix.indexrelid = k.relation", "an index resolves to the table it indexes");
        sql.Should().Contain("toast.reltoastrelid = t.rel", "a TOAST table resolves to its table");
        sql.Should().Contain("d.deptype in ('a', 'i')", "a column-owned sequence resolves to its table");
        sql.Should().Contain("join pg_catalog.pg_inherits i on i.inhrelid = r.rel").And.Contain("child.relispartition",
            "a partition resolves to its root, and a table that merely inherits does not");
        sql.Should().NotContain("pg_partition_", "a list locks nothing it lists (D27)");

        Regex.Count(sql, @"\brelname\b").Should().Be(1, "{0} selects one name, the owner's", name);
        sql.Should().Contain("c.relname::text as owner_name");
    }

    [Fact]
    public void A_blocker_in_another_database_is_named_by_nothing_but_its_process_id()
    {
        string sql = HealthConnectionQueries.Blockers([1]).Sql;

        foreach (string column in new[] { "a.state end as state", "a.usename::text end as usename", "a.application_name end as application_name", "host(a.client_addr) end as client_addr" })
        {
            sql.Should().Contain("case when a.datname = current_database() then " + column);
        }
    }

    [Fact]
    public void Every_call_builds_a_fresh_statement_with_fresh_parameters()
    {
        HealthQuery first = HealthConnectionQueries.IdleInTransaction(TimeSpan.FromSeconds(1), [4444L]);
        HealthQuery second = HealthConnectionQueries.IdleInTransaction(TimeSpan.FromSeconds(1), [4444L]);

        first.Should().NotBeSameAs(second);
        first.Parameters.Should().NotIntersectWith(second.Parameters, "an NpgsqlParameter belongs to one command");
    }

    [Fact]
    public void The_idle_read_binds_the_floor_the_daemon_lock_ids_and_the_limit()
    {
        HealthQuery query = HealthConnectionQueries.IdleInTransaction(TimeSpan.FromSeconds(1.5), [4444L, -7L]);

        query.ParameterNames.Should().Equal("@floor_seconds", "@daemon_lock_ids", "@limit");
        query.Parameters[0].Value.Should().Be(1.5);
        query.Parameters[1].Value.Should().BeEquivalentTo(new[] { 4444L, -7L }, "the 64-bit key is matched whole, sign included");
        query.Parameters[2].Value.Should().Be(HealthConnectionQueries.MaxOffenders);
        query.Sql.Should().Contain("((l.classid::int8 << 32) | l.objid::int8) end = any(@daemon_lock_ids)");
    }

    [Fact]
    public void A_negative_floor_is_bound_as_zero()
    {
        HealthConnectionQueries.LongRunningStatements(TimeSpan.FromSeconds(-5)).Parameters[0].Value.Should().Be(0d);
    }

    [Fact]
    public void The_session_snapshot_cuts_the_statement_on_the_server_and_says_how_long_it_was()
    {
        HealthQuery query = HealthConnectionQueries.SessionSnapshot();

        query.Sql.Should().Contain("left(a.query, @max_query)");
        query.Sql.Should().Contain("octet_length(a.query)").And.Contain("char_length(a.query)");
        query.Sql.Should().Contain("s.name = 'track_activity_query_size'", "the setting's raw value is in bytes; current_setting would say 1kB");
        query.Parameters.Single(static x => x.ParameterName == "max_query").Value.Should().Be(HealthConnectionQueries.MaxQueryCharacters);
    }
}
