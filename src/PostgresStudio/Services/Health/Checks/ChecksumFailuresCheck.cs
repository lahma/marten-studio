using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;

using Npgsql;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What S03 reads.</summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Enabled">Whether the server checks page checksums (<c>data_checksums</c>).</param>
/// <param name="ThisDatabase">Failures counted in this database, or <see langword="null" /> when checksums are off.</param>
/// <param name="ThisLast">The latest of them.</param>
/// <param name="OtherDatabases">Failures counted in every other database on the server together.</param>
/// <param name="OtherDatabasesFailing">How many other databases have counted any. A count, never their names.</param>
/// <param name="OtherLast">The latest of those.</param>
/// <param name="SharedObjects">Failures in the shared catalogs (<c>pg_stat_database</c>'s row for database 0).</param>
/// <param name="SharedLast">The latest of those.</param>
/// <param name="StatsReset">When this database's statistics were last reset - the counters run from then.</param>
internal sealed record ChecksumFacts(
    DateTimeOffset ReadAt,
    bool Enabled,
    long? ThisDatabase,
    DateTimeOffset? ThisLast,
    long? OtherDatabases,
    long OtherDatabasesFailing,
    DateTimeOffset? OtherLast,
    long? SharedObjects,
    DateTimeOffset? SharedLast,
    DateTimeOffset? StatsReset)
{
    /// <summary>Every failure counted on the server.</summary>
    public long Total => (ThisDatabase ?? 0) + (OtherDatabases ?? 0) + (SharedObjects ?? 0);
}

/// <summary>S03: Postgres has read a page whose checksum did not match - storage has changed data behind its back.</summary>
/// <remarks>
/// <para>
/// With data checksums on, every page carries a checksum Postgres writes and checks on every read from disk; a page
/// that fails was changed after Postgres wrote it - by the disk, a controller, memory, a filesystem or a backup
/// tool - and <c>pg_stat_database.checksum_failures</c> counts it (PostgreSQL 12+). <b>Any failure is Critical</b>:
/// there is no rate of corruption that is acceptable, and the count does not go down until somebody resets the
/// statistics.
/// </para>
/// <para>
/// <b>The whole server, counted.</b> Corruption in another database is the same disks' corruption, so failures there are
/// counted into the finding - as a number and a count of databases, never their names - beside this database's and the
/// shared catalogs'.
/// </para>
/// <para>
/// <b>Checksums off is a status line, not a finding.</b> Most servers made before PostgreSQL 18 have them off, because
/// <c>initdb</c> did not turn them on by default; turning them on takes a stopped server and <c>pg_checksums</c>, and that
/// is a DBA's decision the studio cannot weigh.
/// </para>
/// <para>
/// <b>Not applicable on Aurora</b> (issue #8 A2, W5-AUR): its data lives in the cluster volume, "copies of the data across three
/// Availability Zones" (<i>Amazon Aurora storage</i>, Aurora User Guide), in a storage system that is "distributed,
/// fault-tolerant, and self-healing" (<i>Amazon Aurora FAQ</i>) - both read 2026-10-26. Whether Postgres' own page checksums are
/// on is not the host's to choose there, and the quiet row's advice (<c>initdb --data-checksums</c>, <c>pg_checksums</c>) has no
/// meaning on a cluster nobody runs <c>initdb</c> for; so S03 is listed, with that reason, and never run.
/// </para>
/// </remarks>
[HealthCheck("S03", HealthArea.Server, HealthCostTier.Catalog, "Checksum failures")]
internal sealed class ChecksumFailuresCheck : HealthCheck<ChecksumFacts>
{
    /// <summary>What S03's row says on Aurora.</summary>
    public const string AuroraReason =
        "Not applicable on Aurora: the cluster volume is Aurora's storage layer - six copies across three Availability Zones, "
        + "\"distributed, fault-tolerant, and self-healing\" - so Postgres' own page checksums are not the host's to choose or repair.";

    /// <inheritdoc />
    public override async Task<ChecksumFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        if (server.Platform.Kind == PostgresPlatformKind.AmazonAurora)
        {
            throw new HealthCheckNotApplicableException(AuroraReason);
        }

        var query = HealthServerQueries.Checksums();
        IReadOnlyList<ChecksumFacts> rows = await context.Sql
            .QueryAsync(query, reader => Map(reader, context.Now), cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException("The checksum counters came back as " + rows.Count.ToString(CultureInfo.InvariantCulture) + " rows.");
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ChecksumFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (!facts.Enabled)
        {
            return HealthVerdict.Quiet(
                "Data checksums are off on this server, so Postgres cannot notice a page that changed on disk when it reads one. "
                + "They are turned on when a cluster is made (initdb --data-checksums, the default from PostgreSQL 18) or, with the "
                + "server stopped, with pg_checksums --enable.");
        }

        if (facts.Total == 0)
        {
            return HealthVerdict.Quiet("Data checksums are on, and no page has failed one" + Since(facts) + ".");
        }

        DateTimeOffset? last = new[] { facts.ThisLast, facts.OtherLast, facts.SharedLast }.Max();

        List<HealthEvidence> evidence =
        [
            new("In this database", Count(facts.ThisDatabase, facts.ThisLast)),
            new("In other databases on this server", facts.OtherDatabasesFailing == 0
                ? "none"
                : Count(facts.OtherDatabases, facts.OtherLast) + " across " + SessionText.Count(facts.OtherDatabasesFailing, "database")),
            new("In the shared catalogs", Count(facts.SharedObjects, facts.SharedLast)),
            new("Counted since", facts.StatsReset is { } reset ? reset.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) : "the statistics were first collected"),
        ];

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "S03",
            Severity = HealthSeverity.Critical,
            Title = (facts.Total == 1
                    ? "Postgres has read a page whose checksum did not match"
                    : "Postgres has read " + SessionText.Number(facts.Total) + " pages whose checksums did not match")
                + (last is { } latest ? ", the last at " + latest.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) : string.Empty),
            WhyItMatters =
                "A checksum failure is a page that changed on disk after Postgres wrote it: the storage, a controller, memory, a "
                + "filesystem or a copy taken the wrong way corrupted it. The statement that read it failed with \"invalid page\", and "
                + "the rows on that page - documents, events, index entries - cannot be read until it is repaired. Corruption seldom "
                + "stops at one page: check the storage and the server's memory, find the relation from the server log (\"page "
                + "verification failed\" names its file), and restore it from a backup or a replica that does not have the damage. "
                + "zero_damaged_pages and ignore_checksum_failure are not repairs - they throw the page away or use it as it is (S02).",
            Evidence = evidence,
            Caveat =
                "Read in pg_stat_database at " + facts.ReadAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
                + ". The counters only grow, until somebody resets the statistics, so a failure stays here after it is repaired; "
                + "a page that is damaged and never read is not counted at all.",
        });
    }

    private static string Count(long? count, DateTimeOffset? last) =>
        SessionText.Number(count ?? 0) + (last is { } at ? ", the last at " + at.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) : string.Empty);

    private static string Since(ChecksumFacts facts) =>
        facts.StatsReset is { } reset ? " since the statistics were reset at " + reset.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) : string.Empty;

    private static ChecksumFacts Map(NpgsqlDataReader reader, DateTimeOffset readAt) => new(
        readAt,
        reader.GetBoolean(0),
        reader.IsDBNull(1) ? null : reader.GetInt64(1),
        reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
        reader.IsDBNull(3) ? null : reader.GetInt64(3),
        reader.GetInt64(4),
        reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));
}
