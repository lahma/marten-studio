using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What S07 reads.</summary>
/// <param name="Extensions">The extensions installed, with the server's default versions.</param>
/// <param name="Updatable">
/// For each whose version differs from the default, whether the server has an update path from it to the default
/// (<c>pg_extension_update_paths</c>).
/// </param>
internal sealed record OutdatedExtensionFacts(ServerExtensionsRead Extensions, IReadOnlyDictionary<string, bool> Updatable);

/// <summary>
/// S07: an extension installed in this database is at an older version than the one the server's files would install now,
/// and the server can update it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from the catalog and the control files.</b> <c>pg_extension.extversion</c> against
/// <c>pg_available_extensions.default_version</c>: a package upgrade on the server - a minor release, a new extension build -
/// replaces the library at once, and the SQL objects the extension created in each database stay at their old version until
/// somebody updates them there. An extension whose files are gone (no default version) is not judged.
/// </para>
/// <para>
/// <b>Older, not merely different.</b> Version strings do not sort, so "older" is the server's own answer: an update path from
/// the installed version to the default one (<c>pg_extension_update_paths</c>, asked only for the few whose versions differ).
/// One with no such path - installed at a version newer than the default, or one its scripts cannot reach - is said in the
/// status line to differ from the default, with nothing to update.
/// </para>
/// <para>
/// <b>Info, and the remedy is words.</b> <c>ALTER EXTENSION … UPDATE</c> is DDL, which a configuration remedy may not be, and
/// it is the extension's owner's to run after reading its release notes.
/// </para>
/// </remarks>
[HealthCheck("S07", HealthArea.Server, HealthCostTier.Catalog, "Outdated extension")]
internal sealed class OutdatedExtensionCheck : HealthCheck<OutdatedExtensionFacts>
{
    /// <inheritdoc />
    public override async Task<OutdatedExtensionFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ServerExtensionsRead extensions = await ServerExtensionsRead.SharedAsync(context).ConfigureAwait(false);
        List<(string Name, string Installed, string Target)> candidates =
        [
            .. extensions.Rows.Where(static x => x.Outdated).Select(static x => (x.Name, x.InstalledVersion!, x.DefaultVersion!)),
        ];

        if (candidates.Count == 0)
        {
            return new OutdatedExtensionFacts(extensions, new Dictionary<string, bool>(StringComparer.Ordinal));
        }

        var query = ServerPostureQueries.UpdatePaths(candidates);
        IReadOnlyList<(string Name, bool Updatable)> paths = await context.Sql
            .QueryAsync(query, static reader => (reader.GetString(0), reader.GetBoolean(1)), cancellationToken)
            .ConfigureAwait(false);

        return new OutdatedExtensionFacts(extensions, paths.ToDictionary(static x => x.Name, static x => x.Updatable, StringComparer.Ordinal));
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(OutdatedExtensionFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        ServerExtensionsRead read = facts.Extensions;
        List<ServerExtensionRow> installed = [.. read.Rows.Where(static x => x.InstalledVersion is not null)];
        List<ServerExtensionRow> outdated = [.. installed.Where(x => x.Outdated && facts.Updatable.TryGetValue(x.Name, out bool updatable) && updatable)];
        List<ServerExtensionRow> differing = [.. installed.Where(x => x.Outdated && !outdated.Contains(x))];
        List<string> unjudged = [.. installed.Where(static x => x.DefaultVersion is null).Select(static x => x.Name)];

        List<string> notes = [];

        notes.AddRange(differing.Select(static x =>
            x.Name + " is at " + x.InstalledVersion + ", which differs from the server's default " + x.DefaultVersion
            + "; the server has no update path from it, so there is nothing to update it to."));

        if (unjudged.Count > 0)
        {
            notes.Add("The server has no files for " + string.Join(", ", unjudged) + ", so " + (unjudged.Count == 1 ? "it is" : "they are") + " not judged.");
        }

        if (read.Total > read.Rows.Count)
        {
            notes.Add("Only " + SessionText.Number(read.Rows.Count) + " of " + SessionText.Number(read.Total) + " extensions were read.");
        }

        string? note = notes.Count == 0 ? null : string.Join(" ", notes);

        if (outdated.Count == 0)
        {
            string lead = installed.Count == 0
                ? "No extension is installed in this database."
                : differing.Count == 0
                    ? "Every extension installed in this database is at the version the server offers."
                    : "No extension installed in this database is behind a version the server can update it to.";

            return HealthVerdict.Quiet(note is null ? lead : lead + " " + note);
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
                    Caveat = "Read from pg_extension and pg_available_extensions, which reads the extension's control file on the server; "
                        + "the server's update path from the installed version is pg_extension_update_paths'.",
                }),
            ],
            note);
    }
}
