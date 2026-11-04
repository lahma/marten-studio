using JasperFx.Events;
using JasperFx.Events.Projections;

using MartenStudio.Internal.Sql;

using Npgsql;

using PostgresStudio.Services;

namespace MartenStudio.Services.Projections;

/// <summary>Removing an orphaned progression row (1.2-PROJ).</summary>
internal sealed partial class ProjectionDataService
{
    /// <summary>The audit entry's action for <see cref="RemoveOrphanedProgressionAsync" />.</summary>
    internal const string RemoveOrphanAction = "RemoveOrphanedProgression";

    private static readonly IReadOnlyDictionary<string, ShardState> NoLiveState =
        new Dictionary<string, ShardState>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The progression rows that nothing this store registers advances: the rows <see cref="OrphansAsync" /> lists, and the
    /// only rows <see cref="RemoveOrphanedProgressionAsync" /> will consider.
    /// </summary>
    private static IEnumerable<ShardProgress> OrphanRowsOf(IReadOnlyList<ShardProgress> merged) =>
        merged.Where(static x => !x.IsRegistered && x.Classification is null or { Kind: ProgressionRowKind.Orphan });

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>The same chain as every correction</b> (hard rule 5): <c>Require(CorrectProgression)</c>, the scope resolved with the
    /// capability (the store policy and the write policy for the scope in the URL), then the question this operation is really
    /// about - <b>(store, database, <see langword="null" />)</b>, whatever tenant the selector is on (D27, <i>reach, not
    /// scope</i>): the row is the database's bookkeeping, listed by name only to a visitor that gate admits
    /// (<see cref="OrphansAsync" />), so it is removed only by one. Then the operation, and the audit entry on success, on
    /// refusal and on failure.
    /// </para>
    /// <para>
    /// <b>The name is never trusted.</b> The page sends the name of the row it drew; the service reads the table again, at the
    /// database as a whole, and removes the row only if it is still an orphan this store's configuration does not claim, the
    /// daemon may not read, and whose name can be shown as what it is - the rule that withholds Marten's call from the page. A
    /// row that stopped being one (it was removed, or a deployment registered a projection that claims it) is a refusal, audited
    /// as one and logged as 9226, not an error.
    /// </para>
    /// <para>
    /// <b>The call</b> is Marten's own, <c>IEventDatabase.DeleteProjectionProgressByShardNameAsync</c> on the resolved database
    /// (<c>MartenDatabase.EventStorage.cs</c>, 9.57): it first ensures the event store's storage under the database's
    /// <c>AutoCreate</c> - the one place on this screen that does, because this is a write (hard rule 14) - opens a session of
    /// its own on that database, and deletes the one row whose <c>name</c> is the identity given; a
    /// <c>{Name}:{ShardKey}:{tenantId}</c> identity deletes only that tenant's row.
    /// </para>
    /// </remarks>
    public async Task<OrphanRemoval> RemoveOrphanedProgressionAsync(
        StudioScope scope,
        string rowName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowName);

        const StudioCapability capability = StudioCapability.CorrectProgression;

        // What the circuit named, until it has been found in the table: the audit dispatchers and the log wrappers cap it.
        string target = rowName;

        try
        {
            capabilities.Require(capability);

            ResolvedScope resolved = await resolver
                .ResolveAsync(scope, capability.ToString(), cancellationToken)
                .ConfigureAwait(false);

            string databaseId = resolved.Database.Id.Identity;

            // (store, database, null), always: not RequireWholeDatabaseAsync, whose exception for a database that is exclusively
            // the selected tenant's would let a tenant's visitor remove a row the view never named to them.
            await reach.RequireDatabaseAsync(resolved, databaseId, tenantId: null, capability, cancellationToken).ConfigureAwait(false);

            string storeKey = resolved.Registration.Key;
            string user = await authorization.UserNameAsync().ConfigureAwait(false);

            ShardProgress? orphan = await FindOrphanAsync(resolved, rowName, cancellationToken).ConfigureAwait(false);

            string? refusal =
                orphan is null ? OrphanWording.RemovalNoLongerOrphan
                : orphan.Classification is { ReadByDaemon: true } || OrphanWording.MartenCall(orphan.ShardName) is null
                    ? OrphanWording.RemovalNotOffered
                : null;

            if (refusal is not null)
            {
                logger.OrphanedProgressionRemovalRefused(user, rowName, storeKey, databaseId, refusal);
                audit.Record(RemoveOrphanAction, target, false, refusal, capability, scope);

                return new OrphanRemoval(rowName, false, refusal);
            }

            // Past the re-check the name is the table's own, not the circuit's.
            string name = orphan!.ShardName;
            target = name;

            IEventDatabase events = resolved.Database as IEventDatabase
                ?? throw new InvalidOperationException("This store's database does not remove a progression row by its name.");

            logger.OrphanedProgressionRemovalRequested(user, name, storeKey, databaseId);

            await events.DeleteProjectionProgressByShardNameAsync(name, cancellationToken).ConfigureAwait(false);

            audit.Record(
                RemoveOrphanAction,
                name,
                true,
                "Removed one row of mt_event_progression (Marten first ensured the event store's storage).",
                capability,
                scope);

            // The database's own bookkeeping changed, for every tenant's view of it.
            cache.InvalidatePrefix(CachePrefix(resolved));

            return new OrphanRemoval(name, true, "Removed the progression row " + name + ".");
        }
        catch (StudioCapabilityDeniedException denial)
        {
            audit.RecordCapabilityDenied(denial, RemoveOrphanAction, target);
            throw;
        }
        catch (StudioNotAuthorizedException denied)
        {
            audit.RecordScopeDenied(denied.Scope, PolicyName(capability), RemoveOrphanAction, target);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            audit.Record(RemoveOrphanAction, target, false, exception.Message, capability, scope);
            throw;
        }
    }

    /// <summary>
    /// The progression row called <paramref name="rowName" /> if it is, now, an orphan - read again, at the database as a whole
    /// and not through the snapshot cache, with the configuration's claims applied exactly as the page applies them.
    /// </summary>
    private async Task<ShardProgress?> FindOrphanAsync(ResolvedScope resolved, string rowName, CancellationToken cancellationToken)
    {
        List<ProjectionInfo> projections = RegisteredProjectionsOf(resolved.Store);

        await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);

        ProgressionRows rows = await ProjectionProgressQueries
            .ReadProgressionRowsAsync(
                connection,
                catalog,
                EventSchemaOf(resolved),
                resolved.Store.Options.Events.EnableExtendedProgressionTracking,
                tenantId: null,
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        List<ShardState> states = [.. rows.Rows.Select(ToShardState)];

        Dictionary<string, ProgressionRow> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProgressionRow row in rows.Rows)
        {
            byName.TryAdd(row.Name, row);
        }

        List<ShardProgress> merged = MergeProgress(
            projections, states, NoLiveState, 0, byName, ProgressionRowClassifier.For(resolved.Store, projections));

        return OrphanRowsOf(merged).FirstOrDefault(x => string.Equals(x.ShardName, rowName, StringComparison.Ordinal));
    }
}
