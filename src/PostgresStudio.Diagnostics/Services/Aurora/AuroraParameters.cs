namespace PostgresStudio.Services.Aurora;

/// <summary>Where an Aurora PostgreSQL parameter is set, as the Aurora User Guide's parameter reference lists it.</summary>
internal enum AuroraParameterScope
{
    /// <summary>Not in either list: the studio does not say where Aurora lets it be changed, or whether it does.</summary>
    Unlisted = 0,

    /// <summary>A cluster-level parameter only: the DB cluster parameter group.</summary>
    Cluster = 1,

    /// <summary>In both lists: the DB cluster parameter group, overridable for one instance in its DB parameter group.</summary>
    ClusterAndInstance = 2,
}

/// <summary>
/// Where each server setting the studio names is changed on Aurora (issue #8 A6): the DB cluster parameter group, an instance's
/// DB parameter group, or neither list - from the Aurora User Guide's <i>Amazon Aurora PostgreSQL parameters</i>
/// (https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/AuroraPostgreSQL.Reference.ParameterGroups.html, read
/// 2026-10-26), whose two tables list the parameters of <c>default.aurora-postgresql14</c>, cluster-level and instance-level.
/// Never guessed: a setting neither table lists is <see cref="AuroraParameterScope.Unlisted" />, and says so.
/// </summary>
/// <remarks>
/// <para>
/// "The DB cluster parameter group also contains default settings for the DB parameter group for the DB instances that make up
/// the cluster", and "You manage cluster-level parameters in DB cluster parameter groups. You manage instance-level parameters
/// in DB parameter groups" (the same page). A parameter in both tables is therefore set for the cluster, and may be overridden
/// for one instance.
/// </para>
/// <para>
/// <b>Serverless v2 derives some of them.</b> "For the following parameters, Aurora PostgreSQL uses default values that are
/// derived from the memory size based on the maximum ACU setting, the same as with <c>max_connections</c>":
/// <c>autovacuum_max_workers</c>, <c>autovacuum_vacuum_cost_limit</c>, <c>autovacuum_work_mem</c>,
/// <c>effective_cache_size</c>, <c>maintenance_work_mem</c>; and "Aurora serverless doesn't use any custom parameter values
/// that you specify" for <c>shared_buffers</c> (<i>Performance and scaling for Aurora serverless</i>, Aurora User Guide, read
/// 2026-10-26).
/// </para>
/// <para>
/// The tables are Aurora PostgreSQL 14's; a setting a later release added (<c>reserved_connections</c> from 16,
/// <c>transaction_timeout</c> from 17, <c>idle_replication_slot_timeout</c> from 18) is in neither, and reads as unlisted.
/// </para>
/// </remarks>
internal static class AuroraParameters
{
    /// <summary>The settings the studio names that the page lists at cluster level only.</summary>
    private static readonly HashSet<string> ClusterOnly = new(StringComparer.Ordinal)
    {
        "compute_query_id", "max_prepared_transactions", "max_replication_slots", "max_wal_senders", "password_encryption",
        "rds.force_ssl", "rds.logical_replication", "ssl", "synchronous_commit", "track_wal_io_timing", "vacuum_failsafe_age",
        "vacuum_multixact_failsafe_age", "vacuum_freeze_min_age", "vacuum_freeze_table_age", "vacuum_cost_delay", "vacuum_cost_limit",
        "huge_pages", "wal_buffers",
    };

    /// <summary>The settings the studio names that the page lists at both levels.</summary>
    private static readonly HashSet<string> ClusterAndInstance = new(StringComparer.Ordinal)
    {
        "default_statistics_target", "default_transaction_isolation", "default_transaction_read_only", "effective_cache_size",
        "effective_io_concurrency", "hot_standby_feedback", "idle_in_transaction_session_timeout", "idle_session_timeout",
        "log_lock_waits", "log_min_duration_statement", "log_temp_files", "maintenance_io_concurrency", "maintenance_work_mem",
        "max_connections", "max_locks_per_transaction", "max_parallel_workers", "max_parallel_workers_per_gather",
        "max_slot_wal_keep_size", "max_standby_streaming_delay", "max_worker_processes", "random_page_cost",
        "rds.rds_superuser_reserved_connections", "seq_page_cost", "shared_buffers", "shared_preload_libraries",
        "statement_timeout", "superuser_reserved_connections", "temp_buffers", "temp_file_limit", "track_activities",
        "track_activity_query_size", "track_counts", "track_functions", "track_io_timing", "work_mem", "deadlock_timeout",
        "pg_stat_statements.track",
    };

    /// <summary>The settings whose Serverless v2 default Aurora derives from the cluster's maximum capacity.</summary>
    private static readonly HashSet<string> DerivedOnServerless = new(StringComparer.Ordinal)
    {
        "autovacuum_max_workers", "autovacuum_vacuum_cost_limit", "autovacuum_work_mem", "effective_cache_size", "maintenance_work_mem",
    };

    /// <summary>
    /// Where the page lists <paramref name="setting" />: every <c>autovacuum*</c> setting at cluster level, every
    /// <c>enable_*</c> planner switch at both, and the rest by name.
    /// </summary>
    public static AuroraParameterScope ScopeOf(string setting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);

        if (setting.StartsWith("autovacuum", StringComparison.Ordinal) && !string.Equals(setting, "autovacuum_vacuum_max_threshold", StringComparison.Ordinal))
        {
            return AuroraParameterScope.Cluster;
        }

        if (setting.StartsWith("enable_", StringComparison.Ordinal))
        {
            return AuroraParameterScope.ClusterAndInstance;
        }

        return ClusterAndInstance.Contains(setting) ? AuroraParameterScope.ClusterAndInstance
            : ClusterOnly.Contains(setting) ? AuroraParameterScope.Cluster
            : AuroraParameterScope.Unlisted;
    }

    /// <summary>
    /// Where <paramref name="setting" /> changes on Aurora, as the end of "set it in ...": the DB cluster parameter group, with
    /// an instance's DB parameter group where the page lists it there too, and what Serverless v2 does with it.
    /// </summary>
    public static string WhereToSet(string setting)
    {
        string where = ScopeOf(setting) switch
        {
            AuroraParameterScope.Cluster => "the cluster's DB cluster parameter group (Aurora)",
            AuroraParameterScope.ClusterAndInstance =>
                "the cluster's DB cluster parameter group, or one instance's DB parameter group to set it for that instance alone (Aurora)",
            _ => "the cluster's DB cluster parameter group (Aurora), where Aurora allows it - the Aurora User Guide's parameter "
                + "reference does not list " + setting,
        };

        return setting switch
        {
            "max_connections" => where + "; on Aurora Serverless v2 it follows the cluster's maximum capacity (ACU) instead, held while the instance scales",
            "shared_buffers" => where + "; on Aurora Serverless v2 Aurora sizes it as the instance scales and does not use a value set there",
            _ when DerivedOnServerless.Contains(setting) => where + "; on Aurora Serverless v2 its default follows the cluster's maximum capacity (ACU)",
            _ => where,
        };
    }
}
