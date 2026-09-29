using Marten.Storage;

using MartenStudio.Internal.Sql;

using Microsoft.Extensions.Options;

using Npgsql;

namespace MartenStudio.Services.Database;

/// <summary>Which parts of the catalog a read wants.</summary>
[Flags]
internal enum CatalogParts
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Tables, views, materialized views, foreign tables.</summary>
    Relations = 1,

    /// <summary>Functions and their kin.</summary>
    Routines = 2,

    /// <summary>Triggers.</summary>
    Triggers = 4,

    /// <summary>Sequences.</summary>
    Sequences = 8,

    /// <summary>Types.</summary>
    Types = 16,

    /// <summary>Foreign keys with either end in the set.</summary>
    ForeignKeys = 32,

    /// <summary>What the views in the set read.</summary>
    ViewDependencies = 64,

    /// <summary>Everything the overview counts.</summary>
    Listings = Relations | Routines | Triggers | Sequences | Types,
}

/// <summary>The parts of the catalog one read asked for; a part not asked for is empty.</summary>
/// <param name="Relations">Relations.</param>
/// <param name="Routines">Routines.</param>
/// <param name="Triggers">Triggers.</param>
/// <param name="Sequences">Sequences.</param>
/// <param name="Types">Types.</param>
/// <param name="ForeignKeys">Foreign keys.</param>
/// <param name="ViewDependencies">View dependencies.</param>
internal sealed record CatalogSnapshot(
    CatalogList<CatalogRelation> Relations,
    CatalogList<CatalogRoutine> Routines,
    CatalogList<CatalogTrigger> Triggers,
    CatalogList<CatalogSequence> Sequences,
    CatalogList<CatalogType> Types,
    CatalogList<CatalogForeignKey> ForeignKeys,
    CatalogList<CatalogViewDependency> ViewDependencies)
{
    /// <summary>Nothing at all.</summary>
    public static CatalogSnapshot Empty { get; } = new(
        CatalogList<CatalogRelation>.Empty,
        CatalogList<CatalogRoutine>.Empty,
        CatalogList<CatalogTrigger>.Empty,
        CatalogList<CatalogSequence>.Empty,
        CatalogList<CatalogType>.Empty,
        CatalogList<CatalogForeignKey>.Empty,
        CatalogList<CatalogViewDependency>.Empty);
}

/// <summary>
/// Reads the database browser's catalog, inside the read-only session, and remembers what it read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every read is a read-only transaction</b> through <see cref="ReadOnlySqlSession.InTransactionAsync{T}" />:
/// <c>SET TRANSACTION READ ONLY</c>, <c>statement_timeout</c> from
/// <see cref="MartenStudioOptions.QueryTimeout" />, a three-second <c>lock_timeout</c> - so a catalog read
/// queued behind somebody's migration gives up rather than joining the queue - and
/// <c>SET LOCAL ROLE</c> to <see cref="MartenStudioOptions.SqlConsoleRole" /> when one is set, which is what
/// makes <c>has_*_privilege</c> answer for that role. The browser therefore sees exactly as far as the
/// console does, and a table the role cannot select from is listed as not readable rather than read.
/// </para>
/// <para>
/// <b>Cached for sixty seconds, shared by every circuit.</b> <see cref="CatalogCache{TValue}" />, keyed by
/// the database's identity, the role, the part, the schema set and the name filter - never by the
/// visitor. What differs per visitor is the gate, and the gate is applied <em>after</em> the cache: a
/// cached answer is a fact about the database, filtered afresh for whoever asks. A failed read is not
/// cached.
/// </para>
/// <para>
/// <b>Nothing here migrates.</b> The connection comes from
/// <c>IMartenDatabase.CreateConnection(ConnectionUsage.Read)</c>, and nothing touches <c>AllObjects()</c>,
/// <c>AllSchemaNames()</c> or any Weasel migration (AGENTS.md hard rule 14);
/// <c>DatabaseNoDdlLiveTests</c> holds every service method to that, live.
/// </para>
/// </remarks>
internal sealed class DatabaseCatalog
{
    /// <summary>
    /// How many objects of one kind a read takes before it stops and says there were more. A list shows
    /// fewer; the overview's counts become floors past it.
    /// </summary>
    internal const int ListCap = 5000;

    private const char Separator = '\u001f';

    private readonly IOptions<MartenStudioOptions> options;

    private readonly CatalogCache<IReadOnlyList<CatalogSchema>> schemas = new();
    private readonly CatalogCache<CatalogList<CatalogRelation>> relations = new();
    private readonly CatalogCache<CatalogList<CatalogRoutine>> routines = new();
    private readonly CatalogCache<CatalogList<CatalogTrigger>> triggers = new();
    private readonly CatalogCache<CatalogList<CatalogSequence>> sequences = new();
    private readonly CatalogCache<CatalogList<CatalogType>> types = new();
    private readonly CatalogCache<CatalogList<CatalogForeignKey>> foreignKeys = new();
    private readonly CatalogCache<CatalogList<CatalogViewDependency>> viewDependencies = new();
    private readonly CatalogCache<CatalogRelationDetail?> details = new();

    public DatabaseCatalog(IOptions<MartenStudioOptions> options)
    {
        this.options = options;
    }

    /// <summary>Every schema of the database, as the reading role sees them.</summary>
    public async Task<IReadOnlyList<CatalogSchema>> SchemasAsync(
        IMartenDatabase database,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        string key = Key(database, "schemas");

        if (schemas.TryGet(key, out IReadOnlyList<CatalogSchema> cached))
        {
            return cached;
        }

        IReadOnlyList<CatalogSchema> read = await RunAsync(
                database,
                (connection, transaction, timeout, token) =>
                    DatabaseCatalogQueries.ReadSchemasAsync(connection, transaction, timeout, token),
                cancellationToken)
            .ConfigureAwait(false);

        schemas.Set(key, read);

        return read;
    }

    /// <summary>
    /// The asked-for parts of the catalog within <paramref name="schemaSet" />, from the cache where it
    /// has them and in one transaction for the rest.
    /// </summary>
    /// <param name="database">The resolved database.</param>
    /// <param name="schemaSet">The schemas the gate allows; nothing outside them is read.</param>
    /// <param name="parts">What to read.</param>
    /// <param name="filter">A case-insensitive name substring for the listing parts, or <see langword="null" />.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<CatalogSnapshot> SnapshotAsync(
        IMartenDatabase database,
        IReadOnlyList<string> schemaSet,
        CatalogParts parts,
        string? filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(schemaSet);

        string[] ordered = [.. schemaSet.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string q = filter?.Trim() ?? string.Empty;
        string scope = string.Join('\u001e', ordered);

        if (ordered.Length == 0 || parts == CatalogParts.None)
        {
            return CatalogSnapshot.Empty;
        }

        Slot<CatalogRelation> relationSlot = new(relations, Key(database, "relations", scope, q), parts.HasFlag(CatalogParts.Relations));
        Slot<CatalogRoutine> routineSlot = new(routines, Key(database, "routines", scope, q), parts.HasFlag(CatalogParts.Routines));
        Slot<CatalogTrigger> triggerSlot = new(triggers, Key(database, "triggers", scope, q), parts.HasFlag(CatalogParts.Triggers));
        Slot<CatalogSequence> sequenceSlot = new(sequences, Key(database, "sequences", scope, q), parts.HasFlag(CatalogParts.Sequences));
        Slot<CatalogType> typeSlot = new(types, Key(database, "types", scope, q), parts.HasFlag(CatalogParts.Types));

        // Keys and view dependencies are not name-filtered: they are what a list's rows are annotated
        // with, whatever the list was filtered by.
        Slot<CatalogForeignKey> foreignKeySlot = new(foreignKeys, Key(database, "fks", scope), parts.HasFlag(CatalogParts.ForeignKeys));
        Slot<CatalogViewDependency> dependencySlot = new(viewDependencies, Key(database, "deps", scope), parts.HasFlag(CatalogParts.ViewDependencies));

        bool missing = relationSlot.Missing || routineSlot.Missing || triggerSlot.Missing || sequenceSlot.Missing
            || typeSlot.Missing || foreignKeySlot.Missing || dependencySlot.Missing;

        if (missing)
        {
            await RunAsync(
                    database,
                    async (connection, transaction, timeout, token) =>
                    {
                        if (relationSlot.Missing)
                        {
                            relationSlot.Store(await DatabaseCatalogQueries.ReadRelationsAsync(
                                connection, transaction, ordered, q, null, ListCap, timeout, token).ConfigureAwait(false));
                        }

                        if (routineSlot.Missing)
                        {
                            routineSlot.Store(await DatabaseCatalogQueries.ReadRoutinesAsync(
                                connection, transaction, ordered, q, null, ListCap, timeout, token).ConfigureAwait(false));
                        }

                        if (triggerSlot.Missing)
                        {
                            triggerSlot.Store(await DatabaseCatalogQueries.ReadTriggersAsync(
                                connection, transaction, ordered, q, null, null, ListCap, timeout, token).ConfigureAwait(false));
                        }

                        if (sequenceSlot.Missing)
                        {
                            sequenceSlot.Store(await DatabaseCatalogQueries.ReadSequencesAsync(
                                connection, transaction, ordered, q, null, ListCap, timeout, token).ConfigureAwait(false));
                        }

                        if (typeSlot.Missing)
                        {
                            typeSlot.Store(await DatabaseCatalogQueries.ReadTypesAsync(
                                connection, transaction, ordered, q, null, ListCap, timeout, token).ConfigureAwait(false));
                        }

                        if (foreignKeySlot.Missing)
                        {
                            foreignKeySlot.Store(await DatabaseCatalogQueries.ReadForeignKeysAsync(
                                connection, transaction, ordered, null, null, ListCap, timeout, token).ConfigureAwait(false));
                        }

                        if (dependencySlot.Missing)
                        {
                            dependencySlot.Store(await DatabaseCatalogQueries.ReadViewDependenciesAsync(
                                connection, transaction, ordered, null, ListCap, timeout, token).ConfigureAwait(false));
                        }

                        return true;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new CatalogSnapshot(
            relationSlot.Value,
            routineSlot.Value,
            triggerSlot.Value,
            sequenceSlot.Value,
            typeSlot.Value,
            foreignKeySlot.Value,
            dependencySlot.Value);
    }

    /// <summary>
    /// One relation's detail, or <see langword="null" /> when <paramref name="schema" /> has no such
    /// relation (partitions, extension members and indexes included - none of them is a relation here).
    /// </summary>
    public async Task<CatalogRelationDetail?> RelationAsync(
        IMartenDatabase database,
        string schema,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrEmpty(schema);
        ArgumentException.ThrowIfNullOrEmpty(name);

        string key = Key(database, "relation", schema, name);

        if (details.TryGet(key, out CatalogRelationDetail? cached))
        {
            return cached;
        }

        CatalogRelationDetail? read = await RunAsync(
                database,
                (connection, transaction, timeout, token) =>
                    DatabaseCatalogQueries.ReadRelationDetailAsync(connection, transaction, schema, name, timeout, token),
                cancellationToken)
            .ConfigureAwait(false);

        details.Set(key, read);

        return read;
    }

    /// <summary>A view's query. Not cached: it is asked for one object at a time, on purpose.</summary>
    public Task<CatalogDefinition?> ViewDefinitionAsync(
        IMartenDatabase database,
        string schema,
        string name,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            database,
            (connection, transaction, timeout, token) =>
                DatabaseCatalogQueries.ReadViewDefinitionAsync(connection, transaction, schema, name, timeout, token),
            cancellationToken);

    /// <summary>One routine overload's definition. Not cached.</summary>
    public Task<CatalogDefinition?> RoutineDefinitionAsync(
        IMartenDatabase database,
        string schema,
        string name,
        string identityArguments,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            database,
            (connection, transaction, timeout, token) =>
                DatabaseCatalogQueries.ReadRoutineDefinitionAsync(
                    connection, transaction, schema, name, identityArguments, timeout, token),
            cancellationToken);

    /// <summary>One trigger's definition. Not cached.</summary>
    public Task<CatalogDefinition?> TriggerDefinitionAsync(
        IMartenDatabase database,
        string schema,
        string table,
        string name,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            database,
            (connection, transaction, timeout, token) =>
                DatabaseCatalogQueries.ReadTriggerDefinitionAsync(connection, transaction, schema, table, name, timeout, token),
            cancellationToken);

    /// <summary>Forgets everything, for a test or after the studio itself changed the schema.</summary>
    public void Clear()
    {
        schemas.Clear();
        relations.Clear();
        routines.Clear();
        triggers.Clear();
        sequences.Clear();
        types.Clear();
        foreignKeys.Clear();
        viewDependencies.Clear();
        details.Clear();
    }

    private async Task<T> RunAsync<T>(
        IMartenDatabase database,
        Func<NpgsqlConnection, NpgsqlTransaction, int, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        MartenStudioOptions value = options.Value;

        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions
        {
            StatementTimeout = value.QueryTimeout,
            Role = value.SqlConsoleRole,
        });

        // A little past the server-side statement_timeout, which is what should fire: it produces 57014
        // with a message, where a client-side timeout only breaks the connection.
        int commandTimeout = (int) Math.Ceiling(value.QueryTimeout.TotalSeconds) + 5;

        await using NpgsqlConnection connection = database.CreateConnection(ConnectionUsage.Read);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await session
            .InTransactionAsync(
                connection,
                (transaction, token) => read(connection, transaction, commandTimeout, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A cache key: the database's identity (server and database - never a connection string, which holds
    /// a password), the role the read ran as, and whatever else distinguishes the read.
    /// </summary>
    private string Key(IMartenDatabase database, params string[] parts) =>
        database.Id.Identity + Separator + (options.Value.SqlConsoleRole ?? string.Empty) + Separator
        + string.Join(Separator, parts);

    /// <summary>One part of a snapshot: from the cache, or read and then remembered.</summary>
    private sealed class Slot<T>
    {
        private readonly CatalogCache<CatalogList<T>> cache;
        private readonly string key;

        public Slot(CatalogCache<CatalogList<T>> cache, string key, bool wanted)
        {
            this.cache = cache;
            this.key = key;

            if (!wanted)
            {
                Value = CatalogList<T>.Empty;
            }
            else if (cache.TryGet(key, out CatalogList<T> cached))
            {
                Value = cached;
            }
            else
            {
                Missing = true;
            }
        }

        public bool Missing { get; private set; }

        public CatalogList<T> Value { get; private set; } = CatalogList<T>.Empty;

        public void Store(CatalogList<T> read)
        {
            Value = read;
            Missing = false;
            cache.Set(key, read);
        }
    }
}
