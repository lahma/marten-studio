using System.Globalization;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;
using MartenStudio.Services.Live;
using MartenStudio.Services.Settings;
using MartenStudio.Services.Sources;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Services.Relationships;

/// <summary>
/// The reads every Relationships question shares, whoever asks it: the visitor's database-browser gate and the
/// relations it admits, and the one cached foreign-key read per database.
/// </summary>
/// <remarks>
/// <para>
/// <b>One shared read, filtered per visitor.</b> The foreign keys come from one <c>pg_constraint</c> read per
/// database, cached through <see cref="StudioSnapshotCache" /> under a key built only from the source, the database,
/// <c>SqlConsoleRole</c> and a schema set that is the same for every visitor: the source's own schemas and whatever
/// <see cref="IStudioSettings.BrowsableSchemas" /> admits. Who is asking is never part of it. Each visitor's
/// database-browser gate (<see cref="DatabaseAccess.GateAsync" />, D27) is then applied by
/// <see cref="RelationshipGraphBuilder" /> <em>after</em> the cache, so nothing another visitor may not see is ever
/// handed to them from it.
/// </para>
/// <para>
/// <b>Nothing here migrates.</b> The schema set comes from the source's own declarations (a node source's
/// <see cref="RelationshipNodeSet.OwnSchemas" />, read from configuration) and the catalog - never
/// <c>AllSchemaNames()</c>, which is <c>AllObjects()</c> and applies Marten's HiLo migration on the way through
/// (AGENTS.md hard rule 14). The key read runs in the read-only session, as every read of objects Marten does not own
/// does; <c>RelationshipsNoDdlLiveTests</c> is what holds this file to it.
/// </para>
/// <para>
/// Shared by <see cref="RelationshipDataService" /> and a studio's <see cref="IRelationshipNodeSource" />, so the graph
/// the screen draws and the graph a "referenced by" panel counts from are one read. Its log lines are written under
/// <see cref="RelationshipDataService" />'s category, where they have always been.
/// </para>
/// </remarks>
internal sealed class RelationshipReads
{
    private readonly IStudioSettings settings;
    private readonly StudioSnapshotCache cache;
    private readonly ILogger<RelationshipDataService> logger;
    private readonly DatabaseAccess access;
    private readonly DatabaseCatalog catalog;
    private readonly StudioCapabilityGuard capabilities;

    public RelationshipReads(
        IStudioSettings settings,
        StudioSnapshotCache cache,
        ILogger<RelationshipDataService> logger,
        DatabaseAccess access,
        DatabaseCatalog catalog,
        StudioCapabilityGuard capabilities)
    {
        this.settings = settings;
        this.cache = cache;
        this.logger = logger;
        this.access = access;
        this.catalog = catalog;
        this.capabilities = capabilities;
    }

    /// <summary>The command timeout a catalog estimate runs under: the studio's query timeout, at least a second.</summary>
    internal int CommandTimeoutSeconds => Math.Max(1, (int) settings.QueryTimeout.TotalSeconds);

    /// <summary>
    /// A little past the server-side <c>statement_timeout</c>, which is what should fire: it produces 57014 with a
    /// message, where a client-side timeout only breaks the connection.
    /// </summary>
    internal int SessionCommandTimeoutSeconds => (int) Math.Ceiling(settings.QueryTimeout.TotalSeconds) + 5;

    /// <summary>
    /// This visitor's database-browser gate, with the relations it admits when the picture needs them - or, when the
    /// gate cannot be read, nothing and the reason, so the caller falls back to the source's own nodes rather than
    /// guess whose each table is (D27's classification fails closed).
    /// </summary>
    /// <param name="scope">The scope as the visitor asked for it.</param>
    /// <param name="resolved">The same scope, resolved for this visitor.</param>
    /// <param name="withRelations">Whether to read the relations the gate admits, for the picture's table nodes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal async Task<(RelationshipDatabaseView? View, string? Unavailable)> ViewAsync(
        StudioScope scope,
        ResolvedSource resolved,
        bool withRelations,
        CancellationToken cancellationToken)
    {
        DatabaseGate? known = null;

        try
        {
            DatabaseGateRead read = await access.GateAsync(scope, resolved, cancellationToken).ConfigureAwait(false);

            if (!read.Succeeded)
            {
                // Its sentence names another store only to a visitor that store's policy passes (D27).
                return (null, read.Reason ?? "The database browser's gate could not be read.");
            }

            known = read.Gate;

            if (!withRelations)
            {
                return (new RelationshipDatabaseView(read.Gate, []), null);
            }

            // The same cached, read-only catalog read the database browser lists its tables from, over exactly the
            // schemas this visitor's gate admits.
            CatalogSnapshot snapshot = await catalog
                .SnapshotAsync(resolved.Target, read.Gate.VisibleSchemas, CatalogParts.Relations, null, cancellationToken)
                .ConfigureAwait(false);

            return (new RelationshipDatabaseView(read.Gate, snapshot.Relations.Items, snapshot.Relations.Truncated), null);
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            // The screen says so beside a picture of the source's own nodes; the database's own trouble is what the
            // catalog read reports, and it is not a studio fault worth a Warning of its own.
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "Marten Studio drew the relationships of {StoreKey} without tables", scope.StoreKey);
            }

            return (null, DatabaseAccess.CatalogFailure(exception, known));
        }
    }

    /// <summary>
    /// Every foreign key touching the schemas any visitor might see, from the single-flight cache.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>pg_constraint</c> read is what every document-detail load and every Relationships load paid for, and it
    /// depends on nothing about the visitor: the schema set is the source's own plus what <c>BrowsableSchemas</c>
    /// admits, which is a fact about the options and the database. So it belongs behind the cache the live pages
    /// already share (D10), keyed per database - and deliberately not per tenant, because a catalog read has nothing
    /// tenant-shaped in it.
    /// </para>
    /// <para>
    /// The scope has already been resolved for this visitor when this runs, and the result is filtered for them
    /// afterwards; the cache key names no visitor because nothing in the value is theirs.
    /// </para>
    /// </remarks>
    /// <param name="resolved">The scope, resolved for this visitor.</param>
    /// <param name="ownSchemas">The source's own schemas, always part of the read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal async Task<PhysicalForeignKeyRead> ForeignKeysAsync(
        ResolvedSource resolved,
        IReadOnlyList<string> ownSchemas,
        CancellationToken cancellationToken)
    {
        string[] schemas = await SchemaSetAsync(resolved, ownSchemas, cancellationToken).ConfigureAwait(false);

        return await cache
            .GetAsync(CacheKey(resolved, schemas), token => ReadForeignKeysAsync(resolved.Target, schemas, token), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The schemas the shared read spans: the source's own, and - when <c>BrowseDatabase</c> is on for the process at
    /// all - every schema <c>BrowsableSchemas</c> admits. The same for every visitor.
    /// </summary>
    private async Task<string[]> SchemaSetAsync(
        ResolvedSource resolved,
        IReadOnlyList<string> ownSchemas,
        CancellationToken cancellationToken)
    {
        HashSet<string> set = new(ownSchemas, StringComparer.Ordinal);

        IReadOnlyList<string> browsable = settings.BrowsableSchemas;

        if (capabilities.IsEnabled(StudioCapability.BrowseDatabase) && browsable.Count > 0)
        {
            try
            {
                IReadOnlyList<CatalogSchema> live = await catalog.SchemasAsync(resolved.Target, cancellationToken)
                    .ConfigureAwait(false);

                set.UnionWith(BrowsableSchemaMatcher.Match(browsable, live));
            }
            catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
            {
                // The source's own schemas still draw; the gate read beside this one fails the same way and says so
                // on the screen.
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(exception, "Marten Studio read the relationships of the store's own schemas only");
                }
            }
        }

        return [.. set.Order(StringComparer.Ordinal)];
    }

    /// <summary>The key read itself, in the read-only session.</summary>
    private async Task<PhysicalForeignKeyRead> ReadForeignKeysAsync(
        IStudioDatabase database,
        IReadOnlyList<string> schemas,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = database.CreateConnection(StudioConnectionUsage.Read);
        await PostgresFailure.OpenAsync(connection, cancellationToken).ConfigureAwait(false);

        return await Session()
            .InTransactionAsync(
                connection,
                (transaction, token) => RelationshipQueries.ReadForeignKeysTouchingAsync(
                    connection,
                    transaction,
                    schemas,
                    RelationshipQueries.DefaultForeignKeyCap,
                    SessionCommandTimeoutSeconds,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One key per source, database and schema set, in the <c>source|database|…</c> shape the other services use, so
    /// a prefix invalidation of that source reaches it.
    /// </summary>
    /// <remarks>
    /// Built from what the resolver settled on - the registration's key and the database's own identity - never from
    /// the scope as the URL spelled it: an empty <c>?db=</c> and the default database named outright are one database,
    /// and keyed on the request they were two cache entries and two key reads.
    /// </remarks>
    private string CacheKey(ResolvedSource resolved, IReadOnlyList<string> schemas) =>
        resolved.Source.Key + "|" + resolved.Target.Identity + "|relationship-keys|"
        + (settings.SqlConsoleRole ?? string.Empty) + "|" + string.Join('\u001e', schemas);

    /// <summary>
    /// The read-only session every read of an object Marten does not own runs in: read-only transaction,
    /// <c>statement_timeout</c> from the studio's query timeout, a short <c>lock_timeout</c>, and <c>SqlConsoleRole</c>
    /// when one is set.
    /// </summary>
    internal ReadOnlySqlSession Session() =>
        new(new ReadOnlySqlOptions
        {
            StatementTimeout = settings.QueryTimeout,
            Role = settings.SqlConsoleRole,
        });

    /// <summary>What a count that ran out of time says on its row.</summary>
    internal string NotCountedInTime() =>
        "Counting these took longer than " + settings.OptionName("QueryTimeout") + " (" +
        settings.QueryTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) +
        " s), so they were not counted.";

    /// <summary>
    /// A failure as the page shows it, Postgres' own words masked through the visitor's gate when it was read
    /// (<see cref="PostgresErrorText" />); before it was, what failed was the scope or the gate itself.
    /// </summary>
    internal static string Describe(Exception exception, DatabaseGate? gate)
    {
        string message = exception switch
        {
            PostgresException postgres => $"{postgres.SqlState}: {postgres.MessageText}",
            NpgsqlException => "The database could not be reached: " + exception.Message,
            _ => exception.Message,
        };

        return gate is null ? message : PostgresErrorText.Redact(message, gate.WithheldSchemas) ?? message;
    }
}
