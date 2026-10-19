using System.Text.RegularExpressions;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Tests.Conventions;

using Npgsql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The shape of every replication and server statement, with no database: catalog and statistics reads only,
/// nothing that writes, ends a session or names a table, every value and limit a parameter, the columns later
/// releases added read so an older server answers null, and no other database ever named.
/// </summary>
public class HealthServerQueriesTests
{
    /// <summary>Every statement the builder makes.</summary>
    public static TheoryData<string> Every => new() { "Platform", "ReplicationSlots", "Settings", "Checksums", "BackendProcess" };

    private static HealthQuery Query(string name) => name switch
    {
        "Platform" => HealthServerQueries.Platform(),
        "ReplicationSlots" => HealthServerQueries.ReplicationSlots(),
        "Settings" => HealthServerQueries.Settings(DangerousSettingsCheck.Watched),
        "Checksums" => HealthServerQueries.Checksums(),
        "BackendProcess" => HealthServerQueries.BackendProcess(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such statement."),
    };

    [Theory]
    [MemberData(nameof(Every))]
    public void No_statement_writes_ends_a_session_takes_a_lock_or_names_a_table(string name)
    {
        HealthQuery query = Query(name);

        query.Sql.Should().NotMatchRegex(@"(?i)\b(insert|update|delete|create|alter|drop|truncate|grant|vacuum|analyze|lock|checkpoint|reset)\s", name);
        query.Sql.Should().NotContainEquivalentOf("pg_terminate_backend", name);
        query.Sql.Should().NotContainEquivalentOf("pg_cancel_backend", name);
        query.Sql.Should().NotContainEquivalentOf("pg_drop_replication_slot", name);
        query.Sql.Should().NotContainEquivalentOf("advisory", name);
        query.Sql.Should().NotContain("mt_", "{0} names no Marten table: a hidden type's table must never be in Show SQL", name);
        query.Sql.Should().NotMatchRegex(@"(?i)\bpg_(relation|table|total_relation|indexes)_size\b", "{0} calls no size function over a list (D27)", name);
    }

    [Theory]
    [MemberData(nameof(Every))]
    public void No_statement_reads_an_activity_view(string name)
    {
        ActivityViewsOffThePollPathTests.ReadsAnActivityView.IsMatch(Query(name).Sql)
            .Should().BeFalse("{0} is not one of the reviewed readers of the process array and the lock table", name);
    }

    [Theory]
    [MemberData(nameof(Every))]
    public void Every_value_and_every_limit_is_a_parameter(string name)
    {
        HealthQuery query = Query(name);

        query.Sql.Should().NotMatchRegex(@"limit\s+\d", "{0} binds its limit", name);

        foreach (NpgsqlParameter parameter in query.Parameters)
        {
            query.Sql.Should().Contain("@" + parameter.ParameterName, "{0} uses every parameter it binds", name);
        }
    }

    [Fact]
    public void The_slot_read_measures_retention_against_the_primarys_position_or_a_standbys_replay()
    {
        string sql = HealthServerQueries.ReplicationSlots().Sql;

        sql.Should().Contain("from pg_catalog.pg_replication_slots s");
        sql.Should().Contain("pg_catalog.pg_wal_lsn_diff(v.current_lsn, s.restart_lsn)");
        sql.Should().Contain("case when pg_catalog.pg_is_in_recovery() then pg_catalog.pg_last_wal_replay_lsn()");
        sql.Should().Contain("else pg_catalog.pg_current_wal_lsn() end");
        sql.Should().Contain("s.name = 'max_slot_wal_keep_size'").And.Contain("s.name = 'wal_keep_size'");
        sql.Should().Contain("from server v left join slots r on true", "the settings are read even on a server with no slot");
        sql.Should().Contain("order by retained_bytes desc nulls last");
    }

    /// <summary>
    /// <c>wal_status</c>/<c>safe_wal_size</c> (13), <c>conflicting</c> (16) and <c>inactive_since</c>/<c>invalidation_reason</c>
    /// (17) are read through <c>to_jsonb</c>, never by name, so an older server answers null rather than 42703.
    /// </summary>
    [Theory]
    [InlineData("wal_status")]
    [InlineData("safe_wal_size")]
    [InlineData("conflicting")]
    [InlineData("inactive_since")]
    [InlineData("invalidation_reason")]
    public void A_column_a_later_release_added_is_read_through_to_jsonb(string column)
    {
        string sql = HealthServerQueries.ReplicationSlots().Sql;

        sql.Should().Contain("x.j ->> '" + column + "'");
        sql.Should().NotContain("s." + column, "naming the column would fail on a server that does not have it");
    }

    /// <summary>
    /// B1: every slot says whether this database may name it - a physical slot (no database) or a logical slot of the
    /// connected database - read as the last column, so that the check withholds another database's.
    /// </summary>
    [Fact]
    public void The_slot_read_says_which_slots_this_database_may_name()
    {
        string sql = HealthServerQueries.ReplicationSlots().Sql;

        sql.Should().Contain("(s.database is null or s.database = pg_catalog.current_database()) as this_database");
        sql.Should().Contain("r.total, r.this_database\n  from server v left join slots r on true",
            "the reader maps this_database at ordinal 19, after every column it already read");
    }

    [Fact]
    public void The_slot_read_carries_the_horizon_T14_needs()
    {
        string sql = HealthServerQueries.ReplicationSlots().Sql;

        sql.Should().Contain("pg_catalog.age(s.xmin) as xmin_age").And.Contain("pg_catalog.age(s.catalog_xmin) as catalog_xmin_age");
        HealthServerQueries.ReplicationSlotsKey.Should().Be("server.replication-slots", "the key H9's T14 shares, by agreement");
    }

    [Fact]
    public void The_settings_read_asks_for_its_six_settings_and_every_pending_restart_and_never_a_sizing_one()
    {
        HealthQuery query = HealthServerQueries.Settings(DangerousSettingsCheck.Watched);

        query.Sql.Should().Contain("where s.name = any(@names) or s.pending_restart");
        ((string[]) query.Parameters.Single(static x => x.ParameterName == "names").Value!)
            .Should().BeEquivalentTo(["track_counts", "fsync", "full_page_writes", "zero_damaged_pages", "ignore_checksum_failure", "synchronous_commit"]);

        DangerousSettingsCheck.Watched.Should().NotContain(["shared_buffers", "work_mem", "random_page_cost", "effective_cache_size", "maintenance_work_mem"],
            "advice computed from the server's memory cries wolf (R1 §D)");
        query.Sql.Should().NotContain("sourcefile").And.NotContain("sourceline");
    }

    /// <summary>Another database is counted, never named: every mention of <c>datname</c> is a comparison.</summary>
    [Fact]
    public void The_checksum_read_counts_other_databases_and_names_none()
    {
        string sql = HealthServerQueries.Checksums().Sql;

        sql.Should().Contain("from pg_catalog.pg_stat_database d");
        sql.Should().Contain("checksum_failures").And.Contain("checksum_last_failure");
        sql.Should().Contain("filter (where d.datid = 0)", "the shared catalogs' row");
        sql.Should().Contain("pg_catalog.current_setting('data_checksums') = 'on'");

        MatchCollection mentions = Regex.Matches(sql, @"d\.datname\s+(\S+)");
        mentions.Should().NotBeEmpty();
        mentions.Select(static x => x.Groups[1].Value).Should().OnlyContain(static x => x == "=" || x == "is",
            "a database's name is compared, never selected");
    }

    [Fact]
    public void The_platform_read_tests_for_each_marker_and_calls_none_of_them()
    {
        string sql = HealthServerQueries.Platform().Sql;

        sql.Should().Contain("s.name in ('rds.extensions', 'rds.allowed_extensions')");
        sql.Should().Contain("p.proname = 'aurora_version'");
        sql.Should().NotContain("aurora_version()", "Aurora's function is looked up in pg_proc, never called - it does not exist anywhere else");
        sql.Should().Contain("s.name = 'azure.extensions'");
        sql.Should().Contain("s.name like 'cloudsql.%'");
        sql.Should().Contain("r.rolname = 'neon_superuser'");
        sql.Should().Contain("current_setting('server_version_num')");
    }

    [Fact]
    public void The_pooler_probe_asks_for_the_backend_and_nothing_else()
    {
        HealthServerQueries.BackendProcess().Sql.Should().Be("select pg_catalog.pg_backend_pid()");
        HealthServerQueries.BackendProcess().Parameters.Should().BeEmpty();
    }

    [Fact]
    public void The_statements_with_nothing_to_bind_are_cached_and_the_runner_clones_what_binds()
    {
        HealthServerQueries.Platform().Should().BeSameAs(HealthServerQueries.Platform());
        HealthServerQueries.Checksums().Should().BeSameAs(HealthServerQueries.Checksums());
        HealthServerQueries.BackendProcess().Should().BeSameAs(HealthServerQueries.BackendProcess());
    }
}
