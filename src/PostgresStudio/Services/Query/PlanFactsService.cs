using System.Globalization;
using System.Net.Sockets;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Query;

/// <summary>
/// The Plan page's facts (0.10-A, issue #28): what the database knows about the relations a plan names and the columns it uses,
/// read after the plan.
/// </summary>
/// <remarks>
/// An interface so a component test can hand the page facts without Postgres (D12).
/// </remarks>
internal interface IPlanFactsService
{
    /// <summary>
    /// Reads the facts of a plan this page ran - the console's gate, whole, then each relation's schema gate - in the console's
    /// read-only transaction, as the console's role. A failure of the read is a value, <see cref="PlanFacts.Failure" />.
    /// </summary>
    /// <param name="scope">The scope the plan was run in; its tenant is ignored, never applied (<see cref="SqlConsoleService.SqlScope" />).</param>
    /// <param name="plan">The plan, as read from what <see cref="ISqlConsoleService.PlanAsync" /> returned.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="StudioCapabilityDeniedException"><see cref="StudioCapability.RunSql" /> is off, or the studio is read-only. Nothing is read.</exception>
    /// <exception cref="StudioNotAuthorizedException">The write policy refused the database as a whole. Nothing is read.</exception>
    Task<PlanFacts> ReadAsync(StudioScope scope, QueryPlan plan, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IPlanFactsService" />: the console's gate in front, the database browser's structure gate on every relation, and the
/// browser's row gate on the one read that is data.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate, in this order, and the order is the security property</b> (AGENTS.md D27, D32). <see cref="StudioCapabilityGuard.Require" />
/// for <c>RunSql</c>; then the scope resolved with the capability named and no tenant (<see cref="SqlConsoleService.SqlScope" />), so the
/// write policy is the one the console asks; then, before any fact is read, each relation the plan names is resolved to its oid by the
/// catalog and judged by <see cref="DatabaseGate.CanSeeStructure" /> and the classifier: a relation in a schema the visitor may not see
/// the structure of, and a hidden document type's table, is <c>‹withheld›</c> and nothing about it is asked. Only then are the
/// planner's facts, the indexes, the extended statistics and the column statistics read - structure and estimates, which the Schema
/// screen shows for the store's own schemas to anyone the store policy admits.
/// </para>
/// <para>
/// <b>Values are data, and a gate of their own.</b> <c>most_common_vals</c> and <c>histogram_bounds</c> hold values of rows. They are
/// read only for a relation whose <see cref="DatabaseAccess.RequireRowAccessAsync" /> passes - <c>BrowseDatabase</c> on, the store and
/// write policies asked with no tenant, the schema browsable, the relation one the browser reads rows of: never a Marten document or
/// event table, which D27 never reads raw - and each such read is audited as a <c>BrowseDatabase</c> read naming the relation. The
/// page's own gate is asked first and unaudited, so a visitor without <c>BrowseDatabase</c> fills the audit ring with nothing.
/// </para>
/// <para>
/// <b>Audited as part of the plan's run.</b> The plan itself is a <c>RunSql</c> run, audited by <c>PlanAsync</c>. This read adds one
/// <c>RunSql</c> entry of its own - a refusal, a failure and a success all leave one - that names no relation (its message says
/// how many were read).
/// </para>
/// </remarks>
internal sealed class PlanFactsService : IPlanFactsService
{
    /// <summary>What the audit ring calls the facts read.</summary>
    internal const string FactsAction = "RunSql plan facts";

    /// <summary>What the audit ring calls the read of a column's most common values and histogram.</summary>
    internal const string ValuesAction = "Read plan statistics values";

    /// <summary>The most relations whose values one plan reads: each is a catalog read of its own behind the row gate.</summary>
    internal const int MaxValueRelations = 10;

    private const string AuditTarget = "plan facts";

    /// <summary>The planner settings worth showing beside a plan, when the server shows them to the reading role.</summary>
    private static readonly string[] PlannerSettings =
    [
        "work_mem", "hash_mem_multiplier", "random_page_cost", "seq_page_cost", "effective_cache_size", "effective_io_concurrency",
        "default_statistics_target", "jit", "max_parallel_workers_per_gather", "plan_cache_mode",
    ];

    private readonly StudioSourceResolver resolver;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioActionLog audit;
    private readonly IStudioSettings settings;
    private readonly DatabaseAccess access;
    private readonly ServerSettingsFactsCache serverFacts;
    private readonly TimeProvider timeProvider;

    public PlanFactsService(
        StudioSourceResolver resolver,
        StudioCapabilityGuard capabilities,
        StudioActionLog audit,
        IStudioSettings settings,
        DatabaseAccess access,
        ServerSettingsFactsCache serverFacts,
        TimeProvider? timeProvider = null)
    {
        this.resolver = resolver;
        this.capabilities = capabilities;
        this.audit = audit;
        this.settings = settings;
        this.access = access;
        this.serverFacts = serverFacts;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<PlanFacts> ReadAsync(StudioScope scope, QueryPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(plan);

        // 1. The capability, before anything is resolved, recorded either way.
        try
        {
            capabilities.Require(StudioCapability.RunSql);
        }
        catch (StudioCapabilityDeniedException denied)
        {
            audit.RecordCapabilityDenied(denied, FactsAction, AuditTarget);
            throw;
        }

        // 2. The scope, with the capability named so the write policy is the one asked - the database as a whole, never a tenant.
        StudioScope sqlScope = SqlConsoleService.SqlScope(scope);
        ResolvedSource resolved;

        try
        {
            resolved = await resolver.ResolveAsync(sqlScope, nameof(StudioCapability.RunSql), cancellationToken).ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException denied)
        {
            audit.RecordScopeDenied(denied.Scope, DatabaseAccess.WritePolicyName(settings), FactsAction, AuditTarget, StudioCapability.RunSql);
            throw;
        }

        PlanFactsTargets targets = PlanFactsTargets.From(plan);
        DateTimeOffset now = timeProvider.GetUtcNow();

        try
        {
            (IReadOnlyList<PlanRelationFacts> relations, string? failure) = targets.Relations.Count == 0
                ? ([], null)
                : await ReadRelationsAsync(sqlScope, resolved, targets, cancellationToken).ConfigureAwait(false);

            (IReadOnlyList<PlanSettingFact> planner, string? settingsNote) = await ReadSettingsAsync(plan, resolved, cancellationToken).ConfigureAwait(false);

            PlanFacts facts = new(now, relations, planner)
            {
                Failure = failure,
                SettingsNote = settingsNote,
                NotRead = targets.NotRead,
                Unqualified = targets.Unqualified,
                Requested = Requested(targets),
            };

            audit.Record(
                FactsAction,
                AuditTarget,
                succeeded: failure is null,
                failure ?? ("read for " + relations.Count.ToString(CultureInfo.InvariantCulture) + (relations.Count == 1 ? " relation" : " relations")),
                StudioCapability.RunSql,
                sqlScope);

            return facts;
        }
        catch (OperationCanceledException)
        {
            audit.Record(FactsAction, AuditTarget, succeeded: false, "cancelled", StudioCapability.RunSql, sqlScope);
            throw;
        }
        catch (Exception exception) when (exception is NpgsqlException or SocketException or InvalidOperationException or TimeoutException)
        {
            // The connection itself, or a statement Postgres refused: a value, in the studio's words and the SQLSTATE - never Postgres'.
            string reason = FailureText(exception);
            audit.Record(FactsAction, AuditTarget, succeeded: false, reason, StudioCapability.RunSql, sqlScope);

            return new PlanFacts(now, [], [])
            {
                Failure = reason,
                NotRead = targets.NotRead,
                Unqualified = targets.Unqualified,
            };
        }
    }

    private async Task<(IReadOnlyList<PlanRelationFacts> Relations, string? Failure)> ReadRelationsAsync(
        StudioScope sqlScope,
        ResolvedSource resolved,
        PlanFactsTargets targets,
        CancellationToken cancellationToken)
    {
        DatabaseGateRead gateRead;

        try
        {
            gateRead = await access.GateAsync(sqlScope, resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            gateRead = new DatabaseGateRead(null, DatabaseRefusal.Unavailable, DatabaseAccess.CatalogFailure(exception));
        }

        if (!gateRead.Succeeded)
        {
            string reason = gateRead.Reason ?? "The database browser is unavailable.";

            return ([.. targets.Relations.Select(x => new PlanRelationFacts(x.Schema, x.Name, PlanRelationState.NotRead) { Note = reason })], reason);
        }

        DatabaseGate gate = gateRead.Gate;
        int timeout = (int) Math.Ceiling(settings.QueryTimeout.TotalSeconds) + 5;

        ReadOnlySqlSession session = new(new ReadOnlySqlOptions
        {
            StatementTimeout = settings.QueryTimeout,
            MaxRows = 1,
            Role = settings.SqlConsoleRole,
        });

        await using NpgsqlConnection connection = resolved.Target.CreateConnection(StudioConnectionUsage.Read);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // 1. Resolve every name to the catalog's own, with no lock and no fact: the gate is applied before anything is asked.
        string[] schemas = [.. targets.Relations.Select(static x => x.Schema)];
        string[] names = [.. targets.Relations.Select(static x => x.Name)];

        IReadOnlyList<PlanFactsResolvedRow> resolvedRows = await session.InTransactionAsync(
                connection,
                async (transaction, token) =>
                {
                    await DatabaseCatalogQueries.PinSearchPathAsync(connection, transaction, timeout, token).ConfigureAwait(false);
                    return await PlanFactsQueries.ResolveAsync(connection, transaction, schemas, names, timeout, token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);

        List<(PlanRelationRef Requested, PlanFactsResolvedRow Row)> visible = [];
        Dictionary<PlanRelationRef, PlanRelationFacts> settled = [];

        for (int i = 0; i < targets.Relations.Count; i++)
        {
            PlanRelationRef requested = targets.Relations[i];
            PlanFactsResolvedRow? row = resolvedRows.FirstOrDefault(x => x.Ordinal == i + 1);

            if (row is not { Oid: not null, Schema: { } schema, Name: { } name })
            {
                settled[requested] = new PlanRelationFacts(requested.Schema, requested.Name, PlanRelationState.NotResolved)
                {
                    Note = "Postgres has no relation of that name to resolve.",
                };
            }
            else if (!IsVisible(gate, row))
            {
                // Neither name is kept: a withheld record cannot tell anyone what they may not see.
                settled[requested] = new PlanRelationFacts(string.Empty, string.Empty, PlanRelationState.Withheld);
            }
            else
            {
                visible.Add((requested, row));
            }
        }

        // 2. Structure and estimates of what this visitor may see.
        if (visible.Count > 0)
        {
            await ReadVisibleAsync(sqlScope, resolved, gate, targets, visible, settled, connection, session, timeout, cancellationToken).ConfigureAwait(false);
        }

        return ([.. targets.Relations.Select(x => settled[x])], null);
    }

    private async Task ReadVisibleAsync(
        StudioScope sqlScope,
        ResolvedSource resolved,
        DatabaseGate gate,
        PlanFactsTargets targets,
        List<(PlanRelationRef Requested, PlanFactsResolvedRow Row)> visible,
        Dictionary<PlanRelationRef, PlanRelationFacts> settled,
        NpgsqlConnection connection,
        ReadOnlySqlSession session,
        int timeout,
        CancellationToken cancellationToken)
    {
        uint[] oids = [.. visible.Select(static x => x.Row.Oid!.Value)];
        List<uint> columnRelids = [];
        List<string> columnNames = [];

        foreach ((PlanRelationRef requested, PlanFactsResolvedRow row) in visible)
        {
            if (targets.Columns.TryGetValue(requested, out IReadOnlyList<string>? columns))
            {
                foreach (string column in columns)
                {
                    columnRelids.Add(row.Oid!.Value);
                    columnNames.Add(column);
                }
            }
        }

        (IReadOnlyList<PlanFactsRelationRow> relationRows,
            IReadOnlyList<PlanFactsIndexRow> indexRows,
            IReadOnlyList<PlanFactsExtendedRow> extendedRows,
            IReadOnlyList<PlanFactsColumnRow> columnRows) = await session.InTransactionAsync(
                connection,
                async (transaction, token) =>
                {
                    await DatabaseCatalogQueries.PinSearchPathAsync(connection, transaction, timeout, token).ConfigureAwait(false);

                    IReadOnlyList<PlanFactsRelationRow> relationsRead = await PlanFactsQueries.ReadRelationsAsync(connection, transaction, oids, timeout, token).ConfigureAwait(false);
                    IReadOnlyList<PlanFactsIndexRow> indexesRead = await PlanFactsQueries.ReadIndexesAsync(connection, transaction, oids, timeout, token).ConfigureAwait(false);
                    IReadOnlyList<PlanFactsExtendedRow> extendedRead = await PlanFactsQueries.ReadExtendedStatisticsAsync(connection, transaction, oids, timeout, token).ConfigureAwait(false);
                    IReadOnlyList<PlanFactsColumnRow> columnsRead = columnNames.Count == 0
                        ? []
                        : await PlanFactsQueries.ReadColumnsAsync(connection, transaction, columnRelids, columnNames, timeout, token).ConfigureAwait(false);

                    return (relationsRead, indexesRead, extendedRead, columnsRead);
                },
                cancellationToken)
            .ConfigureAwait(false);

        // 3. The values, which are data: each relation through the row gate, the page's own gate asked first and unaudited.
        Dictionary<uint, (PlanValuesState State, string? Note)> values = await GateValuesAsync(sqlScope, visible, targets, cancellationToken).ConfigureAwait(false);
        List<uint> allowed = [.. values.Where(static x => x.Value.State == PlanValuesState.Read).Select(static x => x.Key)];
        IReadOnlyList<PlanFactsValuesRow> valueRows = [];

        if (allowed.Count > 0)
        {
            List<uint> valueRelids = [];
            List<string> valueNames = [];

            for (int i = 0; i < columnRelids.Count; i++)
            {
                if (allowed.Contains(columnRelids[i]))
                {
                    valueRelids.Add(columnRelids[i]);
                    valueNames.Add(columnNames[i]);
                }
            }

            List<PlanFactsResolvedRow> asked = [.. visible.Where(x => allowed.Contains(x.Row.Oid!.Value)).Select(static x => x.Row)];

            try
            {
                valueRows = await session.InTransactionAsync(
                        connection,
                        async (transaction, token) =>
                        {
                            await DatabaseCatalogQueries.PinSearchPathAsync(connection, transaction, timeout, token).ConfigureAwait(false);
                            return await PlanFactsQueries.ReadValuesAsync(connection, transaction, valueRelids, valueNames, timeout, token).ConfigureAwait(false);
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Hard rule 5: a failed read of data is audited as the read it was, in the studio's words - never Postgres' - then thrown.
                string reason = exception is OperationCanceledException ? "cancelled" : FailureText(exception);

                foreach (PlanFactsResolvedRow row in asked)
                {
                    audit.Record(ValuesAction, DatabaseAccess.Target(row.Schema, row.Name), succeeded: false, reason, StudioCapability.BrowseDatabase, sqlScope);
                }

                throw;
            }

            foreach (PlanFactsResolvedRow row in asked)
            {
                audit.Record(
                    ValuesAction,
                    DatabaseAccess.Target(row.Schema, row.Name),
                    succeeded: true,
                    "most common values and histogram bounds read for a plan",
                    StudioCapability.BrowseDatabase,
                    sqlScope);
            }
        }

        foreach ((PlanRelationRef requested, PlanFactsResolvedRow row) in visible)
        {
            uint oid = row.Oid!.Value;
            PlanFactsRelationRow? facts = relationRows.FirstOrDefault(x => x.Oid == oid);

            List<PlanColumnFact> columns = [];

            foreach (IGrouping<string, PlanFactsColumnRow> byColumn in columnRows.Where(x => x.Table == oid).GroupBy(static x => x.Column, StringComparer.Ordinal))
            {
                // A plain table has one row of statistics (inherited = false); a partitioned or inheriting parent has the
                // inherited one. The row the planner uses for a scan of this relation itself is the plain one.
                PlanFactsColumnRow chosen = byColumn.FirstOrDefault(static x => !x.Inherited && x.HasStatistics)
                    ?? byColumn.FirstOrDefault(static x => x.HasStatistics)
                    ?? byColumn.First();

                PlanFactsValuesRow? stored = valueRows.FirstOrDefault(x => x.Table == oid && x.Column == chosen.Column && x.Inherited == chosen.Inherited);

                // The column's type is printed with its schema (the read pins search_path to pg_catalog): one in a withheld schema is masked.
                columns.Add(new PlanColumnFact(chosen.Column, gate.Redact(chosen.Type) ?? chosen.Type, chosen.HasStatistics, chosen.NullFraction, chosen.Distinct, chosen.Correlation)
                {
                    Values = stored is null ? null : new PlanColumnValues(stored.MostCommonValues, stored.MostCommonFrequencies, stored.HistogramBounds),
                });
            }

            PlanPartitionFact? partition = facts is { ParentSchema: { } parentSchema, ParentName: { } parentName } && IsVisible(gate, parentSchema, parentName)
                ? new PlanPartitionFact(parentSchema, parentName, (int) (facts.ParentPartitions ?? 0), facts.ParentKey)
                : null;

            (PlanValuesState valuesState, string? valuesNote) = values.TryGetValue(oid, out (PlanValuesState State, string? Note) known)
                ? known
                : (PlanValuesState.NotNeeded, null);

            settled[requested] = new PlanRelationFacts(requested.Schema, requested.Name, PlanRelationState.Read)
            {
                Kind = row.Kind,
                EstimatedRows = facts?.Tuples is { } tuples and >= 0 ? tuples : null,
                Pages = facts?.Pages,
                AllVisiblePages = facts?.AllVisible,
                LastAnalyze = facts?.LastAnalyze,
                LastAutoAnalyze = facts?.LastAutoAnalyze,
                LastVacuum = facts?.LastVacuum,
                LastAutoVacuum = facts?.LastAutoVacuum,
                ModifiedSinceAnalyze = facts?.ModifiedSinceAnalyze,
                DeadTuples = facts?.DeadTuples,
                LiveTuples = facts?.LiveTuples,
                Indexes =
                [
                    .. indexRows.Where(x => x.Table == oid).Select(static x => new PlanIndexFact(
                        x.Name, x.Keys, x.IsPrimary, x.IsUnique, x.IsValid, x.IsPartial, x.HasExpressions, x.AccessMethod, x.SizeBytes, x.Scans, x.LastScan)),
                ],
                ExtendedStatistics = [.. extendedRows.Where(x => x.Table == oid).Select(x => ExtendedStatistic(gate, x))],
                Columns = columns,
                Partition = partition,
                Values = valuesState,
                ValuesNote = valuesNote,
            };
        }
    }

    /// <summary>
    /// Which relations' values may be read: the page's own gate first, unaudited; then the row gate for each relation that has a column
    /// to read, with its refusal kept as the sentence the panel shows. A relation with no column the plan uses is not asked.
    /// </summary>
    private async Task<Dictionary<uint, (PlanValuesState State, string? Note)>> GateValuesAsync(
        StudioScope sqlScope,
        List<(PlanRelationRef Requested, PlanFactsResolvedRow Row)> visible,
        PlanFactsTargets targets,
        CancellationToken cancellationToken)
    {
        Dictionary<uint, (PlanValuesState State, string? Note)> states = [];
        List<(PlanRelationRef Requested, PlanFactsResolvedRow Row)> wanted =
        [
            .. visible.Where(x => targets.Columns.TryGetValue(x.Requested, out IReadOnlyList<string>? columns) && columns.Count > 0),
        ];

        if (wanted.Count == 0)
        {
            return states;
        }

        DatabasePolicyAnswer answer = await access.EvaluatePoliciesAsync(sqlScope, cancellationToken).ConfigureAwait(false);

        if (!answer.CapabilityEnabled || answer.Authorized != true)
        {
            foreach ((_, PlanFactsResolvedRow row) in wanted)
            {
                states[row.Oid!.Value] = (PlanValuesState.NeedsBrowse, null);
            }

            return states;
        }

        int asked = 0;

        foreach ((_, PlanFactsResolvedRow row) in wanted)
        {
            uint oid = row.Oid!.Value;

            if (asked++ >= MaxValueRelations)
            {
                states[oid] = (PlanValuesState.Refused, "The values of at most " + MaxValueRelations.ToString(CultureInfo.InvariantCulture) + " relations are read for one plan.");
                continue;
            }

            // The catalog's own names go in; a refusal is not audited, as the page asked first and the visitor asked for no rows.
            DatabaseRowAccessResult result = await access
                .RequireRowAccessAsync(sqlScope, row.Schema!, row.Name!, ValuesAction, record: false, cancellationToken)
                .ConfigureAwait(false);

            states[oid] = result.Allowed
                ? (PlanValuesState.Read, null)
                : (PlanValuesState.Refused, result.Reason);
        }

        return states;
    }

    private async Task<(IReadOnlyList<PlanSettingFact> Settings, string? Note)> ReadSettingsAsync(
        QueryPlan plan,
        ResolvedSource resolved,
        CancellationToken cancellationToken)
    {
        List<PlanSettingFact> list = [.. plan.Settings.Select(static x => new PlanSettingFact(x.Key, x.Value, null, null, FromPlan: true))];

        try
        {
            ServerSettingsFacts facts = await serverFacts.GetAsync(resolved, cancellationToken).ConfigureAwait(false);

            if (facts.Settings.Value is not { } read)
            {
                return (list, facts.Settings.Note ?? "The server's settings could not be read.");
            }

            foreach (string name in PlannerSettings)
            {
                if (read.Find(name) is { } setting && list.All(x => !string.Equals(x.Name, name, StringComparison.Ordinal)))
                {
                    list.Add(new PlanSettingFact(setting.Name, setting.Value, setting.Unit, setting.Source, FromPlan: false));
                }
            }

            // A planner method switched off for these sessions while its default is on: S09's own test.
            foreach (ServerSettingValue off in read.Rows
                .Where(static x => x.Name.StartsWith("enable_", StringComparison.Ordinal) && x.IsOff && string.Equals(x.BootValue, "on", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static x => x.Name, StringComparer.Ordinal))
            {
                if (list.All(x => !string.Equals(x.Name, off.Name, StringComparison.Ordinal)))
                {
                    list.Add(new PlanSettingFact(off.Name, off.Value, off.Unit, off.Source, FromPlan: false));
                }
            }

            return (list, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is NpgsqlException or SocketException or InvalidOperationException or TimeoutException)
        {
            return (list, "The server's settings could not be read.");
        }
    }

    /// <summary>Whether this visitor may be told about the relation: its schema's structure is theirs, and it is not a hidden type's table.</summary>
    internal static bool IsVisible(DatabaseGate gate, string schema, string name) =>
        gate.CanSeeStructure(schema) && gate.Classifier.ClassifyRelation(schema, name) is not null;

    /// <summary>
    /// Whether this visitor may be told about the relation a catalog read resolved: <see cref="IsVisible(DatabaseGate, string, string)" />
    /// of it <b>and of every table above it</b> in <c>pg_inherits</c>. A hidden document type's per-tenant partition is named
    /// <c>mt_doc_&lt;type&gt;_&lt;tenant&gt;</c>, which the classifier takes for Marten infrastructure, so it is its root that is hidden.
    /// </summary>
    internal static bool IsVisible(DatabaseGate gate, PlanFactsResolvedRow row)
    {
        if (!IsVisible(gate, row.Schema!, row.Name!))
        {
            return false;
        }

        for (int i = 0; i < row.AncestorNames.Count && i < row.AncestorSchemas.Count; i++)
        {
            if (!IsVisible(gate, row.AncestorSchemas[i], row.AncestorNames[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>An extended statistics object as this visitor is told of it: one in a schema they may not see is withheld, name and all.</summary>
    private static PlanExtendedStatistic ExtendedStatistic(DatabaseGate gate, PlanFactsExtendedRow row) =>
        gate.CanSeeStructure(row.Schema)
            ? new PlanExtendedStatistic(row.Name, row.Columns, row.Kinds) { Schema = row.Schema }
            : new PlanExtendedStatistic(PlanFacts.Withheld, row.Columns, row.Kinds) { Schema = PlanFacts.Withheld };

    /// <summary>The relations a plan named, by the names it printed, to their place in the list a read answers.</summary>
    private static Dictionary<PlanRelationRef, int> Requested(PlanFactsTargets targets)
    {
        Dictionary<PlanRelationRef, int> index = [];

        for (int i = 0; i < targets.Relations.Count; i++)
        {
            index[targets.Relations[i]] = i;
        }

        return index;
    }

    private static string FailureText(Exception exception) => exception switch
    {
        PostgresException postgres => "The facts could not be read (SQLSTATE " + postgres.SqlState + ").",
        NpgsqlException or SocketException => "The studio could not connect to this database to read the facts.",
        _ => "The facts could not be read (" + exception.GetType().Name + ").",
    };
}
