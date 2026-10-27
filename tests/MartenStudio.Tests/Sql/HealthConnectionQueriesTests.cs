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
    public static TheoryData<string> SessionReads => new() { "IdleInTransaction", "LongRunningStatements", "LockTable", "SessionSnapshot" };

    /// <summary>Every statement the builder makes.</summary>
    public static TheoryData<string> Every => new()
    {
        "ServerConnections", "IdleInTransaction", "LongRunningStatements", "LockTable", "LockStatistics",
        "PreparedTransactions", "DatabaseCounters", "SessionSnapshot", "LockSnapshot", "ServerHolders",
    };

    /// <summary>One statement by name - the theories pass names, because the query type is internal.</summary>
    private static HealthQuery Query(string name) => name switch
    {
        "ServerConnections" => HealthConnectionQueries.ServerConnections(),
        "IdleInTransaction" => HealthConnectionQueries.IdleInTransaction(TimeSpan.FromSeconds(1), [12], new Dictionary<int, long> { [12] = 3 }),
        "LongRunningStatements" => HealthConnectionQueries.LongRunningStatements(TimeSpan.FromSeconds(1)),
        "LockTable" => HealthConnectionQueries.LockTable(),
        "LockStatistics" => HealthConnectionQueries.LockStatistics(),
        "PreparedTransactions" => HealthConnectionQueries.PreparedTransactions(),
        "DatabaseCounters" => HealthConnectionQueries.DatabaseCounters(),
        "SessionSnapshot" => HealthConnectionQueries.SessionSnapshot(),
        "LockSnapshot" => HealthConnectionQueries.LockSnapshot(),
        "ServerHolders" => HealthConnectionQueries.ServerHolders(),
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
        HealthConnectionQueries.LockTable().Sql.Should().Contain("a.wait_event_type = 'Lock'");
        Regex.Count(HealthConnectionQueries.LockTable().Sql, @"pg_blocking_pids\(").Should().Be(1, "it is called in the waiting sessions' CTE alone");

        foreach (string sql in new[]
        {
            HealthConnectionQueries.IdleInTransaction(TimeSpan.Zero, [], new Dictionary<int, long>()).Sql,
            HealthConnectionQueries.LongRunningStatements(TimeSpan.Zero).Sql,
            HealthConnectionQueries.ServerConnections().Sql,
            HealthConnectionQueries.LockSnapshot().Sql,
            HealthConnectionQueries.PreparedTransactions().Sql,
        })
        {
            sql.Should().NotContain("pg_blocking_pids", "it takes the lock manager's state exclusively for a moment, so nobody else calls it");
        }
    }

    /// <summary>The select list of the holders read, in the order <c>SessionActivityReader.MapHolder</c> reads its ordinals.</summary>
    private static readonly string[] HolderColumns =
    [
        "can_read_all_stats", "max_connections", "superuser_reserved", "reserved", "server_version", "server_connections",
        "groups", "database", "application", "role", "client", "hidden", "total", "active", "idle",
        "idle_in_transaction", "state_unknown", "oldest_idle_seconds", "oldest_idle_in_transaction_seconds", "this_database",
    ];

    /// <summary>
    /// Issue #20: the holders read is the whole server's - the one session read that is not bounded to this database - in one
    /// scan of <c>pg_stat_activity</c> (the sessions CTE is read twice, materialized once), bounded in SQL by one bound limit, over
    /// the same client sessions C03 counts, with the studio's own backend left out of the groups and counted in the total.
    /// </summary>
    [Fact]
    public void The_holders_read_is_the_whole_servers_in_one_bounded_scan_over_the_sessions_C03_counts()
    {
        HealthQuery query = HealthConnectionQueries.ServerHolders();
        string sql = query.Sql;

        Regex.Count(sql, @"from\s+pg_stat_activity\b").Should().Be(1, "one scan of the process array, however often the CTE is referred to");
        sql.Should().NotContain("a.datname = current_database()", "it is not bounded to this database: it says which database each session is in")
            .And.Contain("r.database = current_database() as this_database", "and names this one only to mark the studio's own");
        Regex.Count(sql, @"current_database\(\)").Should().Be(1);
        sql.Should().Contain("where " + HealthConnectionQueries.ClientSession, "the sessions C03 counts, a hidden one included");
        sql.Should().Contain("a.pid = pg_backend_pid() as own").And.Contain("where not s.own", "the studio's own reading session is not a group");
        sql.Should().Contain("(select count(*) from sessions) as server_connections", "and is counted in the total C03 judges");

        query.Parameters.Should().ContainSingle().Which.Value.Should().Be(HealthConnectionQueries.MaxHolderGroups);
        sql.Should().Contain("left join ranked r on r.rank <= @limit");
        HealthConnectionQueries.MaxHolderGroups.Should().Be(1_000);

        sql.Should().Contain("group by s.database, s.application, s.role, s.client, s.hidden");
        sql.Should().Contain("s.state in ('idle in transaction', 'idle in transaction (aborted)')", "aborted ones count as idle in a transaction");
        sql.Should().Contain("min(s.state_change) filter (where s.state = 'idle')").And.Contain("count(*) filter (where s.state is null) as state_unknown");
        sql.Should().Contain("host(a.client_addr) as client", "an address without its mask");
        sql.Should().Contain("a.backend_type is null as hidden", "a session the role may not see into is a group of its own");
    }

    [Fact]
    public void The_holders_read_calls_no_function_that_takes_a_lock_and_selects_its_columns_in_the_order_the_reader_reads_them()
    {
        string sql = HealthConnectionQueries.ServerHolders().Sql;

        sql.Should().NotContain("pg_blocking_pids").And.NotContain("pg_locks").And.NotContain("pg_partition_")
            .And.NotContain("pg_sequence_last_value").And.NotContain("::regclass").And.NotContain("pg_get_");
        sql.Should().Contain("current_setting('reserved_connections', true)", "reserved_connections is 16+; an older server answers null, not 42703");

        // The outer select list, one column per line item: its names are the ordinals MapHolder reads.
        int start = sql.LastIndexOf("select v.can_read_all_stats", StringComparison.Ordinal);
        int end = sql.IndexOf("  from visibility v", start, StringComparison.Ordinal);
        start.Should().BePositive();
        end.Should().BeGreaterThan(start);

        string[] columns =
        [
            .. sql[(start + "select ".Length)..end]
                .Replace("(select count(*) from sessions)", "count_sessions", StringComparison.Ordinal)
                .Replace("r.database = current_database()", "this", StringComparison.Ordinal)
                .Split(',')
                .Select(static x => x.Trim())
                .Select(static x => x.Contains(" as ", StringComparison.Ordinal) ? x[(x.LastIndexOf(" as ", StringComparison.Ordinal) + 4)..] : x[(x.LastIndexOf('.') + 1)..])
                .Select(static x => x.Trim()),
        ];

        columns.Should().Equal(HolderColumns);
    }

    [Fact]
    public void Columns_a_server_may_not_have_are_read_so_that_their_absence_is_null()
    {
        HealthConnectionQueries.LockTable().Sql.Should().Contain("(to_jsonb(l) ->> 'waitstart')::timestamptz")
            .And.NotContain("l.waitstart", "naming pg_locks.waitstart on PostgreSQL 13 is 42703, a failed check");
        HealthConnectionQueries.LockSnapshot().Sql.Should().NotContain("l.waitstart");
        HealthConnectionQueries.ServerConnections().Sql.Should().Contain("current_setting('reserved_connections', true)");
    }

    [Fact]
    public void Only_the_server_count_reaches_past_this_database_and_it_is_only_a_count()
    {
        string sql = HealthConnectionQueries.ServerConnections().Sql;

        sql.Should().Contain("(select count(*) from pg_stat_activity a where " + HealthConnectionQueries.ClientSession + ") as client_backends");
        sql.Should().NotContain("query").And.NotContain("client_addr").And.NotContain("application_name");

        // W5-AUR: the login role's sessions on the server are counted against its CONNECTION LIMIT - a filter on usename, never
        // a column: the one place the read names a session's role is the count of the login role's own.
        Regex.Matches(sql, @"usename").Should().ContainSingle();
        sql.Should().Contain("(select count(*) from pg_stat_activity a where " + HealthConnectionQueries.ClientSession + " and a.usename = session_user) as role_sessions");
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
    [InlineData("LockTable")]
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
        string sql = HealthConnectionQueries.LockTable().Sql;

        foreach (string column in new[] { "a.state end", "a.usename::text end", "a.application_name end", "host(a.client_addr) end", "a.backend_start end", "a.wait_event end" })
        {
            sql.Should().Contain("case when a.datname = current_database() then " + column);
        }
    }

    [Fact]
    public void Every_call_builds_a_fresh_statement_with_fresh_parameters()
    {
        HealthQuery first = HealthConnectionQueries.IdleInTransaction(TimeSpan.FromSeconds(1), [12], new Dictionary<int, long>());
        HealthQuery second = HealthConnectionQueries.IdleInTransaction(TimeSpan.FromSeconds(1), [12], new Dictionary<int, long>());

        first.Should().NotBeSameAs(second);
        first.Parameters.Should().NotIntersectWith(second.Parameters, "an NpgsqlParameter belongs to one command");
    }

    [Fact]
    public void The_idle_read_binds_the_floor_the_lock_tables_answers_and_the_limit()
    {
        HealthQuery query = HealthConnectionQueries.IdleInTransaction(
            TimeSpan.FromSeconds(1.5), [77, 12], new Dictionary<int, long> { [30] = 2, [12] = 5 });

        query.ParameterNames.Should().Equal("@floor_seconds", "@daemon_lock_holders", "@relation_lock_pids", "@relation_lock_counts", "@limit");
        query.Parameters[0].Value.Should().Be(1.5);
        query.Parameters[1].Value.Should().BeOfType<int[]>().Which.Should().Equal([77, 12], "the sessions the lock table read found holding the daemon's keys");
        query.Parameters[2].Value.Should().BeOfType<int[]>().Which.Should().Equal([12, 30]);
        query.Parameters[3].Value.Should().BeOfType<long[]>().Which.Should().Equal([5L, 2L], "each count beside its session");
        query.Parameters[4].Value.Should().Be(HealthConnectionQueries.MaxOffenders);
        query.Sql.Should().NotContain("pg_locks", "C04 takes the lock table from the sweep's one read of it (X3)");
    }

    /// <summary>
    /// X3, H3's half: the lock table is read by one statement, which reads <c>pg_locks</c> once - a materialized CTE every other
    /// part of the statement reads - and nothing else in the builder reads it, but Database › Sessions' own lock list.
    /// </summary>
    [Fact]
    public void The_lock_table_is_read_once_in_one_statement()
    {
        string sql = HealthConnectionQueries.LockTable().Sql;

        Regex.Count(sql, @"\bfrom\s+pg_locks\b").Should().Be(1, "pg_locks is one Function Scan in the plan");
        sql.Should().StartWith("with recursive locks as materialized (", "every reader of the lock table reads the materialized snapshot");
        sql.Replace("'pg_catalog.pg_class'::regclass", string.Empty, StringComparison.Ordinal).Should().NotContain("::regclass",
            "an output cast of an oid dropped since the snapshot errors; owners are found by oid in the catalog, D27");

        foreach (string reader in new[] { "IdleInTransaction", "LongRunningStatements", "SessionSnapshot", "PreparedTransactions", "ServerConnections" })
        {
            Query(reader).Sql.Should().NotContain("pg_locks", "{0} takes what it needs of the lock table as parameters, or needs none", reader);
        }
    }

    /// <summary>PostgreSQL 19's <c>pg_stat_lock</c> is read whole and plain: per lock type, server-wide, nothing named.</summary>
    [Fact]
    public void The_lock_statistics_read_names_only_the_view()
    {
        HealthQuery query = HealthConnectionQueries.LockStatistics();

        query.Sql.Should().Contain("from pg_catalog.pg_stat_lock s");
        query.Sql.Should().Contain("s.locktype, s.waits, s.wait_time, s.fastpath_exceeded, s.stats_reset");
        query.Parameters.Should().BeEmpty();
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
