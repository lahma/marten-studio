using PostgresStudio.Services.Health;

namespace PostgresStudio.Services.Replication;

/// <summary>A section of the Replication page, in the order the page draws them.</summary>
internal enum ReplicationSection
{
    /// <summary>Replication slots - R01's, with their statistics.</summary>
    Slots,

    /// <summary>Standbys streaming from this server, and their lag; or, on a standby, its own replay.</summary>
    Standbys,

    /// <summary>The WAL archiver.</summary>
    Archiver,

    /// <summary>WAL volume and the full-page-image share.</summary>
    Wal,

    /// <summary>Checkpoints and the background writer.</summary>
    Checkpoints,

    /// <summary><c>pg_stat_io</c>, by backend type.</summary>
    Io,

    /// <summary>Tablespaces.</summary>
    Tablespaces,
}

/// <summary>
/// Issue #8 A2: what does not apply where a provider's storage does the work - said once, here, for the page's sections and
/// for R02-R04, so that a section and a check never disagree about a platform.
/// </summary>
/// <remarks>
/// <para>
/// <b>Aurora.</b> An Aurora cluster's data is in a shared cluster volume, replicated across three Availability Zones by the
/// storage layer, and its readers read that volume rather than stream WAL: "The cluster volume is shared among all instances
/// in your Aurora PostgreSQL DB cluster. Thus, no extra work is needed to replicate a copy of the data for each Aurora
/// Replica" (<i>Replication with Amazon Aurora PostgreSQL</i>, Aurora User Guide,
/// https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/AuroraPostgreSQL.Replication.html; and <i>Amazon Aurora
/// storage</i>, https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/Aurora.Overview.StorageReliability.html, both
/// read 2026-10-26). So checkpoints, the background writer, WAL volume, the archiver, tablespaces and
/// <c>pg_stat_replication</c> describe nothing Aurora's operator controls, and judging them raises false alarms. Slots stay:
/// Aurora has logical slots, and R01 judges them. <c>pg_stat_io</c> stays: what backends read and hit is still theirs.
/// </para>
/// <para>
/// <b>The providers that archive WAL themselves</b> - for their own backups and point-in-time restore - own the archiver,
/// and a host cannot change its command:
/// </para>
/// <list type="bullet">
/// <item>Amazon RDS: "RDS uploads transaction logs for DB instances to Amazon S3 every five minutes" (<i>Restoring a DB
/// instance to a specified time for Amazon RDS</i>, https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/USER_PIT.html);</item>
/// <item>Azure Database for PostgreSQL: "The server also backs up transaction logs when the write-ahead log (WAL) file is
/// ready to be archived" (<i>Backup and Restore in Azure Database for PostgreSQL Flexible Server</i>,
/// https://learn.microsoft.com/en-us/azure/postgresql/flexible-server/concepts-backup-restore);</item>
/// <item>Google Cloud SQL: point-in-time recovery reads "the instance logs ... stored in Cloud Storage" (<i>Perform
/// point-in-time recovery (PITR)</i>, Cloud SQL for PostgreSQL,
/// https://docs.cloud.google.com/sql/docs/postgres/backup-recovery/pitr).</item>
/// </list>
/// <para>
/// Each read 2026-10-26. Neon and Supabase are not listed: neither page the studio cites says so, and R02 only ever fires
/// where <c>archive_mode</c> is on.
/// </para>
/// </remarks>
internal static class ReplicationPlatform
{
    /// <summary>What each of Aurora's not-applicable sections, and R02-R04 there, say (W5-R2 step 2).</summary>
    public const string Aurora = "Not applicable on Aurora: Aurora's storage layer does this, and its readers do not stream.";

    /// <summary>
    /// Why <paramref name="section" /> does not apply on <paramref name="platform" />, or <see langword="null" /> when it does.
    /// </summary>
    public static string? NotApplicable(PostgresPlatform platform, ReplicationSection section)
    {
        ArgumentNullException.ThrowIfNull(platform);

        if (platform.Kind == PostgresPlatformKind.AmazonAurora)
        {
            return section is ReplicationSection.Slots or ReplicationSection.Io ? null : Aurora;
        }

        return section == ReplicationSection.Archiver ? ProviderArchives(platform) : null;
    }

    /// <summary>Why R02 does not apply: Aurora, or a provider that archives WAL itself.</summary>
    public static string? ArchiverNotApplicable(PostgresPlatform platform) => NotApplicable(platform, ReplicationSection.Archiver);

    /// <summary>
    /// Why <c>pg_stat_replication</c>'s standbys do not apply: Aurora's readers do not stream. R03 asks Aurora's own
    /// <c>aurora_replica_status()</c> there instead (<c>aurora-reader</c>, W5-AUR), and the page shows "Aurora replicas".
    /// </summary>
    public static string? StandbyLagNotApplicable(PostgresPlatform platform) => NotApplicable(platform, ReplicationSection.Standbys);

    /// <summary>Why R04 does not apply: Aurora's storage layer checkpoints.</summary>
    public static string? CheckpointsNotApplicable(PostgresPlatform platform) => NotApplicable(platform, ReplicationSection.Checkpoints);

    /// <summary>The sentence for a provider that archives WAL itself, citing its own page; <see langword="null" /> for any other.</summary>
    private static string? ProviderArchives(PostgresPlatform platform) => platform.Kind switch
    {
        PostgresPlatformKind.AmazonRds =>
            "Not applicable on " + platform.Name + ": RDS archives WAL itself for its automated backups and point-in-time restore "
            + "(\"RDS uploads transaction logs for DB instances to Amazon S3 every five minutes\" - Restoring a DB instance to a "
            + "specified time for Amazon RDS), and its archive command is not the host's to change.",
        PostgresPlatformKind.Azure =>
            "Not applicable on " + platform.Name + ": the service archives WAL itself for its backups and point-in-time restore "
            + "(\"The server also backs up transaction logs when the write-ahead log (WAL) file is ready to be archived\" - Backup "
            + "and Restore in Azure Database for PostgreSQL Flexible Server), and its archive command is not the host's to change.",
        PostgresPlatformKind.CloudSql =>
            "Not applicable on " + platform.Name + ": Cloud SQL keeps the instance's logs in Cloud Storage itself for point-in-time "
            + "recovery (Perform point-in-time recovery (PITR), Cloud SQL for PostgreSQL), and its archive command is not the "
            + "host's to change.",
        _ => null,
    };
}
