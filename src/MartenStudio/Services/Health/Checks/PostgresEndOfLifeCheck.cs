using System.Globalization;

using MartenStudio.Services.Database;

namespace MartenStudio.Services.Health.Checks;

/// <summary>One PostgreSQL major version and the dates the community supports it between.</summary>
/// <param name="Major">The major, as <see cref="PostgresServerFacts.Major" /> numbers it: 17, or 906 for 9.6.</param>
/// <param name="Name">The major, as Postgres names it: "17", "9.6".</param>
/// <param name="FirstRelease">Its first release.</param>
/// <param name="FinalRelease">Its final release: the community's end of life.</param>
internal sealed record PostgresMajorSupport(int Major, string Name, DateOnly FirstRelease, DateOnly FinalRelease);

/// <summary>
/// The PostgreSQL community's support dates, shipped in the package: "The PostgreSQL Global Development Group supports a
/// major version for 5 years after its initial release" (<see cref="Source" />, read 2026-10-17).
/// </summary>
/// <remarks>
/// <b>Dated, and held to its date.</b> The final-release dates are the ones the versioning page publishes, which are the
/// November minor releases five years on; a release that ships after this table was written is not in it, so the
/// fast suite fails a year before <see cref="Last" />'s end of life, and whenever the Postgres image the live suite
/// runs is a major the table does not have (<c>PostgresEndOfLifeTableTests</c>). Extending it is a line per major.
/// </remarks>
internal static class PostgresCommunitySupport
{
    /// <summary>Where the dates come from.</summary>
    public const string Source = "https://www.postgresql.org/support/versioning/";

    /// <summary>Every major from 9.6 on, oldest first.</summary>
    public static IReadOnlyList<PostgresMajorSupport> Majors { get; } =
    [
        new(906, "9.6", new DateOnly(2016, 9, 29), new DateOnly(2021, 11, 11)),
        new(10, "10", new DateOnly(2017, 10, 5), new DateOnly(2022, 11, 10)),
        new(11, "11", new DateOnly(2018, 10, 18), new DateOnly(2023, 11, 9)),
        new(12, "12", new DateOnly(2019, 10, 3), new DateOnly(2024, 11, 21)),
        new(13, "13", new DateOnly(2020, 9, 24), new DateOnly(2025, 11, 13)),
        new(14, "14", new DateOnly(2021, 9, 30), new DateOnly(2026, 11, 12)),
        new(15, "15", new DateOnly(2022, 10, 13), new DateOnly(2027, 11, 11)),
        new(16, "16", new DateOnly(2023, 9, 14), new DateOnly(2028, 11, 9)),
        new(17, "17", new DateOnly(2024, 9, 26), new DateOnly(2029, 11, 8)),
        new(18, "18", new DateOnly(2025, 9, 25), new DateOnly(2030, 11, 14)),
    ];

    /// <summary>The newest major in the table.</summary>
    public static PostgresMajorSupport Last => Majors[^1];

    /// <summary>The row for <paramref name="major" />, or <see langword="null" />.</summary>
    public static PostgresMajorSupport? Find(int major) => Majors.FirstOrDefault(x => x.Major == major);

    /// <summary>
    /// The order majors come in: 9.6 (906) is older than 10, although its number is larger.
    /// </summary>
    public static int Order(int major) => major >= 100 ? major / 100 : major;
}

/// <summary>
/// S01: the PostgreSQL major version the store runs on has reached, or is near, the community's end of life.
/// </summary>
/// <remarks>
/// <para>
/// After its final release a major gets no more fixes from the community - no security fix, no fix for data
/// corruption - and the drivers and extensions that support it start to drop it. <b>PostgreSQL 14's final release is
/// 2026-11-12.</b>
/// </para>
/// <para>
/// <b>Severity</b>: Critical on and after the final release; a Warning within <see cref="WarningWindow" /> of it; Info
/// within <see cref="InfoWindow" />. A major older than the table is past its end of life; a major newer than the table
/// is newer than this release of the studio knows, and says so rather than guessing a date.
/// </para>
/// <para>
/// <b>On a managed platform the date is the community's, and is said to be.</b> A provider decides when it stops
/// offering and patching a major, with an extended-support offer or a forced upgrade of its own, so the finding there
/// says "community support ends; your provider's schedule may differ" (<see cref="PostgresPlatform" />).
/// </para>
/// <para>
/// <see cref="HealthGate.None" />: the version is on the Overview and the Configuration page already. The platform is
/// evidence at the database-wide gate.
/// </para>
/// </remarks>
[HealthCheck("S01", HealthArea.Server, HealthCostTier.Catalog, "PostgreSQL version end of life", Gate = HealthGate.None)]
internal sealed class PostgresEndOfLifeCheck : HealthCheck<PostgresServerFacts>
{
    /// <summary>Within this long of the final release, a Warning.</summary>
    public static readonly TimeSpan WarningWindow = TimeSpan.FromDays(90);

    /// <summary>Within this long of the final release, Info.</summary>
    public static readonly TimeSpan InfoWindow = TimeSpan.FromDays(365);

    /// <inheritdoc />
    public override Task<PostgresServerFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        PostgresServerFacts.SharedAsync(context);

    /// <inheritdoc />
    public override HealthVerdict Evaluate(PostgresServerFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        DateOnly today = DateOnly.FromDateTime(evaluation.Now.UtcDateTime);
        PostgresMajorSupport? row = PostgresCommunitySupport.Find(facts.Major);
        int order = PostgresCommunitySupport.Order(facts.Major);

        if (row is null && order > PostgresCommunitySupport.Order(PostgresCommunitySupport.Last.Major))
        {
            return HealthVerdict.Quiet(
                "PostgreSQL " + facts.MajorName + " is newer than this release of the studio's support table, which ends at "
                + PostgresCommunitySupport.Last.Name + "; its end of life is on " + PostgresCommunitySupport.Source + ".");
        }

        DateOnly finalRelease = row?.FinalRelease ?? PostgresCommunitySupport.Majors[0].FinalRelease;
        TimeSpan until = finalRelease.ToDateTime(TimeOnly.MinValue) - today.ToDateTime(TimeOnly.MinValue);
        string date = finalRelease.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (row is not null && until > InfoWindow)
        {
            return HealthVerdict.Quiet("PostgreSQL " + facts.MajorName + " has community support until " + date + ".");
        }

        bool ended = row is null || until <= TimeSpan.Zero;
        HealthSeverity severity = ended ? HealthSeverity.Critical : until <= WarningWindow ? HealthSeverity.Warning : HealthSeverity.Info;
        int days = (int) Math.Round(until.TotalDays);
        bool managed = facts.Platform.IsManaged;

        string title = row is null
            ? "PostgreSQL " + facts.MajorName + " is past the community's end of life, which came by " + date + " at the latest"
            : ended
                ? "PostgreSQL " + facts.MajorName + " reached the community's end of life on " + date
                : "PostgreSQL " + facts.MajorName + "'s community support ends on " + date + ", in " + SessionText.Count(days, "day");

        List<HealthEvidence> evidence =
        [
            new("Server version", facts.Version),
            new("Major version", facts.MajorName),
            new("Community final release", row is null ? "before " + date : date),
            new("Source", PostgresCommunitySupport.Source),
        ];

        if (managed)
        {
            evidence.Add(new HealthEvidence("Platform", facts.Platform.Name, HealthGate.Database));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "S01",
            Severity = severity,
            Title = managed ? title + "; your provider's schedule may differ" : title,
            WhyItMatters =
                (ended
                    ? "The community no longer ships fixes for this major - no security fix and no fix for a bug that corrupts data - "
                    : "After that date the community ships no more fixes for this major - no security fix and no fix for a bug that corrupts data - ")
                + "and the drivers, extensions and tools around Postgres drop it in turn. A major-version upgrade (pg_upgrade, a "
                + "dump and restore, or logical replication into a new server) is a planned piece of work, not a patch, so it is worth "
                + "starting well before the date."
                + (managed
                    ? " Community support ends on this date; your provider runs this server and its schedule may differ - an extended-support "
                      + "period, or an upgrade it starts itself - so its own lifecycle page is the one that decides."
                    : string.Empty),
            Evidence = evidence,
            Caveat =
                "Dates from the PostgreSQL community's versioning policy (" + PostgresCommunitySupport.Source + ", support for five years "
                + "from a major's first release), as shipped in this release of the studio. The minor release a server runs does not "
                + "change them.",
        });
    }
}
