using PostgresStudio.Services.Database;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// S07: an extension installed in this database is at another version than the one the server's files would install now.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from the catalog and the control files.</b> <c>pg_extension.extversion</c> against
/// <c>pg_available_extensions.default_version</c>: a package upgrade on the server - a minor release, a new extension build -
/// replaces the library at once, and the SQL objects the extension created in each database stay at their old version until
/// somebody updates them there. An extension whose files are gone (no default version) is not judged.
/// </para>
/// <para>
/// <b>Info, and the remedy is words.</b> <c>ALTER EXTENSION … UPDATE</c> is DDL, which a configuration remedy may not be, and
/// it is the extension's owner's to run after reading its release notes.
/// </para>
/// </remarks>
[HealthCheck("S07", HealthArea.Server, HealthCostTier.Catalog, "Outdated extension")]
internal sealed class OutdatedExtensionCheck : HealthCheck<ServerExtensionsRead>
{
    /// <inheritdoc />
    public override Task<ServerExtensionsRead> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ServerExtensionsRead.SharedAsync(context);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ServerExtensionsRead facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<ServerExtensionRow> installed = [.. facts.Rows.Where(static x => x.InstalledVersion is not null)];
        List<ServerExtensionRow> outdated = [.. installed.Where(static x => x.Outdated)];
        List<string> unjudged = [.. installed.Where(static x => x.DefaultVersion is null).Select(static x => x.Name)];

        string? cut = facts.Total > facts.Rows.Count
            ? " Only " + SessionCount(facts.Rows.Count) + " of " + SessionCount(facts.Total) + " extensions were read."
            : null;

        string? notJudged = unjudged.Count > 0
            ? " The server has no files for " + string.Join(", ", unjudged) + ", so " + (unjudged.Count == 1 ? "it is" : "they are") + " not judged."
            : null;

        if (outdated.Count == 0)
        {
            return HealthVerdict.Quiet(
                (installed.Count == 0
                    ? "No extension is installed in this database."
                    : "Every extension installed in this database is at the version the server offers.")
                + notJudged + cut);
        }

        return new HealthVerdict(
            [
                .. outdated.Select(static x => new HealthFinding
                {
                    Code = "S07",
                    Severity = HealthSeverity.Info,
                    Subject = x.Name,
                    Title = "Extension " + x.Name + " is at " + x.InstalledVersion + "; the server offers " + x.DefaultVersion,
                    WhyItMatters =
                        "The server's files for " + x.Name + " are version " + x.DefaultVersion + ", and this database still has the SQL "
                        + "objects of " + x.InstalledVersion + ": its functions, views and types are the old version's while the library "
                        + "behind them is the new one, which extensions' release notes often warn about, and the fixes and features of "
                        + "the new version's SQL are not here. The extension's owner - or a superuser, or the platform's admin role - runs "
                        + "ALTER EXTENSION " + x.Name + " UPDATE in this database after reading its release notes; it is DDL, so the studio "
                        + "says it here rather than as a line to paste.",
                    Evidence =
                    [
                        new("Extension", x.Name),
                        new("Installed version", x.InstalledVersion!),
                        new("Server's default version", x.DefaultVersion!),
                    ],
                    Caveat = "Read from pg_extension and pg_available_extensions, which reads the extension's control file on the server.",
                }),
            ],
            notJudged is null && cut is null ? null : (notJudged + cut)!.Trim());
    }

    private static string SessionCount(long value) => SessionText.Number(value);
}
