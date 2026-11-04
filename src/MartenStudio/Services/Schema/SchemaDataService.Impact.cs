using System.Globalization;

using Marten.Storage;

using MartenStudio.Internal.Sql;

using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Services;

namespace MartenStudio.Services.Schema;

/// <summary>
/// 1.2-APPLY: what the previewed migration will lock and rewrite, read when the preview is rendered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Notes, never a refusal.</b> Nothing here authorizes, refuses or changes the apply (hard rule 14: <c>CreateOrUpdate</c>, the typed
/// confirmation and the <c>(db, null, ApplySchemaChanges)</c> authorization are untouched). A read that fails leaves the lock
/// statements on screen without their figures, and says so.
/// </para>
/// <para>
/// <b>The preview's gate already covers the names.</b> The script is shown only to a visitor who passes <see cref="SchemaScriptGate" />, so
/// the tables it names, and their <c>pg_class</c> statistics, are the visitor's to read; the figures are estimates read without
/// opening any of the tables (<see cref="MigrationImpactQueries" />, D27).
/// </para>
/// <para>
/// <b>Sessions are Activity (D29).</b> Other people's sessions on a table are shown, as counts and states and never as text, only
/// when <c>ViewDatabaseActivity</c> is enabled (and <c>ReadOnly</c> is off) and the write policy allows it for the database as a
/// whole - the store policy having passed already, for the preview itself. A visitor short of it gets one sentence saying so and why,
/// and the read is not made. A read that is made is one audit entry, with a count and no name, under <c>ViewDatabaseActivity</c>, and a
/// failed one is an entry too; a refusal at the gate is not, as for the high-water Explain, because the visitor asked for a preview and
/// nothing about anybody's sessions was read.
/// </para>
/// </remarks>
internal sealed partial class SchemaDataService
{
    /// <summary>What the audit entry for the sessions read is called.</summary>
    internal const string ImpactSessionsAction = "Read sessions locking a migration's tables";

    /// <summary>
    /// The tables <paramref name="sql" /> locks and rewrites, their estimated rows and size, and - behind the Activity gate - the
    /// sessions on them now. Never throws but for cancellation.
    /// </summary>
    private async Task<MigrationImpact> ReadImpactAsync(ResolvedScope resolved, string sql, CancellationToken cancellationToken)
    {
        IReadOnlyList<TableLockStatement> statements = MigrationRisk.LockStatements(sql);

        if (statements.Count == 0)
        {
            return MigrationImpact.None;
        }

        string identity = resolved.Database.Id.Identity;
        StudioScope whole = new(resolved.Registration.Key, identity, null);
        IReadOnlyList<(string Schema, string Name)> relations = MigrationImpact.RelationsOf(statements);

        IReadOnlyList<RelationFootprintRow> footprints = [];
        string? notice = null;

        if (relations.Count > 0)
        {
            try
            {
                footprints = await InSessionAsync(
                        resolved,
                        (connection, transaction, timeout, token) =>
                            MigrationImpactQueries.ReadFootprintsAsync(connection, transaction, relations, timeout, token),
                        cancellationToken,
                        ConnectionUsage.ReadWrite)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogImpactFailure(exception, identity, "table statistics");
                notice = "Estimated rows and sizes could not be read (" + FailureKind(exception) + ").";
            }
        }

        (bool shown, string? sessionsNotice) = await ActivityGateAsync(whole, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TableSessionRow>? sessions = null;

        if (shown && footprints.Count > 0)
        {
            uint[] tables = [.. footprints.Select(static x => x.TableOid).Distinct()];

            try
            {
                sessions = await InSessionAsync(
                        resolved,
                        (connection, transaction, timeout, token) =>
                            MigrationImpactQueries.ReadSessionsAsync(connection, transaction, tables, timeout, token),
                        cancellationToken,
                        ConnectionUsage.ReadWrite)
                    .ConfigureAwait(false);

                int counted = sessions.Sum(static x => x.Sessions);
                audit.Record(
                    ImpactSessionsAction,
                    Target(whole),
                    succeeded: true,
                    string.Create(CultureInfo.InvariantCulture, $"Counted {counted} session lock group(s) on {tables.Length} table(s)."),
                    StudioCapability.ViewDatabaseActivity,
                    whole);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogImpactFailure(exception, identity, "sessions");
                sessionsNotice = "Sessions on these tables could not be read (" + FailureKind(exception) + ").";
                audit.Record(ImpactSessionsAction, Target(whole), succeeded: false, sessionsNotice, StudioCapability.ViewDatabaseActivity, whole);
            }
        }
        else if (shown)
        {
            sessionsNotice = "Sessions on these tables were not read: the tables could not be resolved.";
        }

        // A function dropped with CASCADE names the tables whose declared indexes use it, from the configuration alone (no connection).
        IReadOnlyList<PostgresStudio.Services.Schema.DeclaredIndex>? declared = null;

        if (statements.Any(static x => x.IsFunction))
        {
            PostgresStudio.Services.Schema.SchemaDeclarationRead own = SchemaDeclarationReader.ReadForClassification(resolved.Store.Options, options.Value.IsDocumentTypeVisible);
            declared = own.Succeeded ? own.Declarations.Indexes : null;
        }

        return MigrationImpact.Assemble(statements, footprints, sessions, declared) with { Notice = notice, SessionsNotice = sessionsNotice };
    }

    /// <summary>
    /// <c>ViewDatabaseActivity</c>, in the advisor's order: the capability (and <c>ReadOnly</c>), then the write policy with the
    /// capability named, against the database as a whole. The store policy has passed already, for the preview itself.
    /// </summary>
    private async Task<(bool Passes, string? Reason)> ActivityGateAsync(StudioScope whole, CancellationToken cancellationToken)
    {
        if (!capabilities.IsEnabled(StudioCapability.ViewDatabaseActivity))
        {
            return (false, capabilities.ReadOnly
                ? "Sessions on these tables are not shown: they are other people's activity and need ViewDatabaseActivity, and MartenStudioOptions.ReadOnly is true, which turns every capability off."
                : "Sessions on these tables are not shown: they are other people's activity and need " + StudioCapabilityGuard.OptionName(StudioCapability.ViewDatabaseActivity) + ".");
        }

        bool allowed;

        try
        {
            allowed = await authorization.IsAuthorizedAsync(whole, StudioCapability.ViewDatabaseActivity, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Fails closed: a policy that cannot be asked has not said yes.
            LogImpactFailure(exception, whole.DatabaseId, "the activity policy");
            allowed = false;
        }

        return allowed
            ? (true, null)
            : (false, "Sessions on these tables are not shown: they are other people's activity and need your account's write policy to allow ViewDatabaseActivity for this database.");
    }

    /// <summary>A failure as the page may say it: Postgres' SQLSTATE, or the exception's type - never its message, which can name any object.</summary>
    internal static string FailureKind(Exception exception) => exception is PostgresException postgres
        ? string.Create(CultureInfo.InvariantCulture, $"SQLSTATE {postgres.SqlState}")
        : exception.GetType().Name;

    private void LogImpactFailure(Exception exception, string databaseId, string what)
    {
        // A lock timeout is an expected state - somebody is migrating - and the read says so on screen, so Debug at most.
        if (exception is PostgresException { SqlState: LockNotAvailable })
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "Marten Studio's migration notes ({What}) for database {DatabaseId} gave up on a lock", what, databaseId);
            }

            return;
        }

        logger.LogWarning(exception, "Marten Studio could not read {What} for a migration preview of database {DatabaseId}", what, databaseId);
    }
}
