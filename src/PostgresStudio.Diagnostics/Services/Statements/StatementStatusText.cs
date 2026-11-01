using PostgresStudio.Services.Health;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Statements;

/// <summary>What <c>pg_stat_statements</c>'s state says, said alike by the Statements page and the diagnostics API.</summary>
internal static class StatementStatusText
{
    /// <summary>
    /// The extension's status when the read did not read, in words that name who can fix it on this platform (A7): the grant,
    /// the setting and where it changes, through <see cref="PostgresGrants" /> and the server facts.
    /// </summary>
    public static string? Of(StatementFacts read, StudioText text)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(text);

        PostgresPlatform platform = read.Platform ?? PostgresPlatform.SelfHosted;

        return read.State switch
        {
            StatementsState.NotInstalled when platform.Kind == PostgresPlatformKind.AmazonAurora =>
                "pg_stat_statements is not installed in this database, so there is nothing to show. On " + platform.Name + " its library "
                + "is \"typically\" loaded already, in AWS's word (shared_preload_libraries, in the DB cluster parameter group), so it "
                + "usually needs only CREATE EXTENSION pg_stat_statements in this database - and where the parameter group leaves it "
                + "out, it is added there and the cluster restarted. "
                + ServerSettingsFacts.WhoCreates("pg_stat_statements", trusted: false, platform),
            StatementsState.NotInstalled =>
                "pg_stat_statements is not installed in this database, so there is nothing to show. Its library must be in "
                + "shared_preload_libraries (set in " + platform.WhereSettingsChange + "), which takes effect only when the server "
                + "restarts, and CREATE EXTENSION pg_stat_statements must be run in this database. "
                + ServerSettingsFacts.WhoCreates("pg_stat_statements", trusted: false, platform),
            StatementsState.NotPreloaded =>
                "pg_stat_statements is installed in this database, but its library is not loaded: shared_preload_libraries must name "
                + "it - set in " + platform.WhereSettingsChange + " - and the server must restart. Until then there is nothing to show.",
            StatementsState.TooOld =>
                "pg_stat_statements " + read.ExtensionVersion + " is installed in this database, and this page reads 1.8 or later "
                + "(PostgreSQL 13+); ALTER EXTENSION pg_stat_statements UPDATE in this database brings it to the server's own version.",
            StatementsState.NothingRecorded =>
                "pg_stat_statements has recorded no statement of " + text.SourcePossessive + " own role in this database yet - since its "
                + "last reset, or since the extension was installed - so there is nothing to show until the application has run.",
            StatementsState.Failed => "The statements could not be read: " + (read.Failure ?? "the statement checks did not run."),
            _ => null,
        };
    }
}
