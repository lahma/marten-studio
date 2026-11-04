using System.Diagnostics;

using Marten;
using Marten.Schema;
using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;

namespace MartenStudio.Services.Shapes;

/// <summary>
/// The documents' shape check (D-SHAPE): reads a bounded sample of one collection in scope with the studio's own SQL, pushes each
/// document through the store's serializer and back, and reports what does not fit - paths, kinds, counts and ids, never a value.
/// </summary>
/// <remarks>
/// <para>
/// <b>A plain read under the store policy</b> (the orchestrator's decision under D21): <c>ResolveAsync(scope, null)</c> on every call,
/// before the cache, with no capability and no audit entry, as the documents list is read. Every item it reads is one the visitor can
/// already open one by one under the same resolved scope and the same predicates. An alias that is not a visible registered type -
/// hidden, unknown, Marten's own or a discovered table - gets one identical sentence before any database read.
/// </para>
/// <para>
/// One connection, one read-only transaction (<see cref="ReadOnlySqlSession" />), <c>statement_timeout</c> of at most five seconds
/// and a fifteen-second wall budget, past which the result is partial and never a throw. Nothing here ensures storage, runs a LINQ
/// terminal or loads a document through a session (hard rules 10 and 14); the serializer is <c>store.Options.Serializer()</c>.
/// </para>
/// </remarks>
internal sealed class DocumentShapeCheckService : IDocumentShapeCheck
{
    private const string Kind = "documents";

    private readonly IOptions<MartenStudioOptions> options;
    private readonly StudioScopeResolver resolver;
    private readonly ColumnCatalog columnCatalog;
    private readonly ShapeCheckCache cache;
    private readonly ILogger<DocumentShapeCheckService> logger;
    private readonly TimeProvider timeProvider;

    /// <summary>The service a host's container builds.</summary>
    public DocumentShapeCheckService(
        IOptions<MartenStudioOptions> options,
        StudioScopeResolver resolver,
        ColumnCatalog columnCatalog,
        ShapeCheckCache cache,
        ILogger<DocumentShapeCheckService> logger)
        : this(options, resolver, columnCatalog, cache, logger, TimeProvider.System)
    {
    }

    internal DocumentShapeCheckService(
        IOptions<MartenStudioOptions> options,
        StudioScopeResolver resolver,
        ColumnCatalog columnCatalog,
        ShapeCheckCache cache,
        ILogger<DocumentShapeCheckService> logger,
        TimeProvider timeProvider)
    {
        this.options = options;
        this.resolver = resolver;
        this.columnCatalog = columnCatalog;
        this.cache = cache;
        this.logger = logger;
        this.timeProvider = timeProvider;
    }

    /// <summary>The wall budget of one check; a test shortens it to prove the partial result.</summary>
    internal TimeSpan Budget { get; init; } = ShapeCheckLimits.Budget;

    /// <inheritdoc />
    public async Task<ShapeCheckResult> CheckAsync(StudioScope scope, string alias, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(alias);

        // The store policy, on every call, before the cache: a cached result is only ever handed to someone who has just passed it.
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        if (!TryFind(resolved, alias, out IDocumentType? documentType, out string? subclassAlias))
        {
            return ShapeCheckResult.NotChecked(
                alias.Length <= ShapeCheckLimits.MaxSubjectLength ? alias : alias[..ShapeCheckLimits.MaxSubjectLength],
                ShapeCheckText.NoSuchDocumentType(alias.Length <= ShapeCheckLimits.MaxSubjectLength ? alias : alias[..ShapeCheckLimits.MaxSubjectLength]),
                timeProvider.GetUtcNow());
        }

        string subject = (subclassAlias ?? documentType.Alias).ToLowerInvariant();

        return await cache
            .GetAsync(
                resolved,
                Kind,
                subject,
                force,
                token => RunAsync(resolved, documentType, subclassAlias, subject, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private bool TryFind(ResolvedScope resolved, string alias, out IDocumentType documentType, out string? subclassAlias)
    {
        documentType = null!;
        subclassAlias = null;

        if (alias.Length is 0 or > ShapeCheckLimits.MaxSubjectLength)
        {
            return false;
        }

        foreach (IDocumentType candidate in DocumentDataService.VisibleDocumentTypesOf(resolved.Store, options.Value.IsDocumentTypeVisible))
        {
            if (string.Equals(candidate.Alias, alias, StringComparison.OrdinalIgnoreCase))
            {
                documentType = candidate;
                return true;
            }

            foreach (SubClassMapping subclass in candidate.SubClasses)
            {
                if (string.Equals(subclass.Alias, alias, StringComparison.OrdinalIgnoreCase))
                {
                    documentType = candidate;
                    subclassAlias = subclass.Alias;
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<ShapeCheckResult> RunAsync(
        ResolvedScope resolved,
        IDocumentType documentType,
        string? subclassAlias,
        string subject,
        CancellationToken shared)
    {
        long started = Stopwatch.GetTimestamp();
        RunState state = new();

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(shared);
        budget.CancelAfter(Budget);

        TimeSpan statementTimeout = options.Value.QueryTimeout < ShapeCheckLimits.StatementTimeout
            ? options.Value.QueryTimeout
            : ShapeCheckLimits.StatementTimeout;

        if (statementTimeout <= TimeSpan.Zero)
        {
            statementTimeout = TimeSpan.FromMilliseconds(1);
        }

        ShapeCheckResult result;

        try
        {
            result = await ReadAsync(resolved, documentType, subclassAlias, subject, state, statementTimeout, started, budget.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = Partial(subject, state, ShapeCheckText.BudgetRanOut(Budget), started);
        }
        catch (Exception exception) when (PostgresFailure.IsTimeout(exception))
        {
            result = Partial(subject, state, ShapeCheckText.StatementTimedOut(statementTimeout), started);
        }
        catch (Exception exception) when (PostgresFailure.IsStudioBusy(exception))
        {
            result = Failed(subject, state, "the studio's share of the database's connections is in use. Try again in a moment.", started);
        }
        catch (Exception exception) when (PostgresFailure.IsConnectionFailure(exception))
        {
            result = Failed(subject, state, PostgresFailure.CouldNotConnect(exception), started);
        }
        catch (Exception exception)
        {
            // Never the exception's message or the exception: a Postgres error can quote a value (an id the cast refused).
            string reason = exception is PostgresException postgres ? "SQLSTATE " + postgres.SqlState : exception.GetType().Name;

            logger.LogWarning("Marten Studio could not check the shapes of '{Subject}': {Reason}", StudioLogValue.Cap(subject), reason);
            result = Failed(subject, state, "PostgreSQL or the reader refused a step (" + reason + ").", started);
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.DocumentShapesChecked(
                StudioLogValue.Cap(subject),
                result.Examined,
                result.Types.Sum(static x => x.Failures.Count + x.Members.Count + x.MoreFailures + x.MoreMembers),
                result.Coverage.ToString());
        }

        return result;
    }

    private async Task<ShapeCheckResult> ReadAsync(
        ResolvedScope resolved,
        IDocumentType documentType,
        string? subclassAlias,
        string subject,
        RunState state,
        TimeSpan statementTimeout,
        long started,
        CancellationToken token)
    {
        string? tenantId = resolved.TenantId;
        int commandTimeout = (int) Math.Ceiling(statementTimeout.TotalSeconds) + 5;

        await using NpgsqlConnection connection = MartenStudioDatabase.CreateConnection(resolved.Database, ConnectionUsage.Read);
        await connection.OpenStudioAsync(token).ConfigureAwait(false);

        DocumentTableInfo table = DocumentTableInfo.FromDocumentType(documentType);

        TableColumns physical = await columnCatalog.GetAsync(connection, table.Schema, table.Table, token).ConfigureAwait(false);

        if (!physical.Exists)
        {
            return ShapeCheckResult.NotChecked(subject, ShapeCheckText.TableMissing, timeProvider.GetUtcNow());
        }

        table = table.WithPhysicalColumns(physical);

        if (table.SoftDeleteEnabled)
        {
            state.LeftOut.Add(ShapeCheckText.SoftDeletedLeftOut);
        }

        if (ProjectionOwnedDocuments.IsProjectionOwned(documentType as DocumentMapping))
        {
            state.Remedy = ShapeCheckText.ProjectionOwned;
        }

        ISerializer serializer = resolved.Store.Options.Serializer();
        ReadOnlySqlSession session = new(new ReadOnlySqlOptions { StatementTimeout = statementTimeout });

        await session.InTransactionAsync(
                connection,
                async (transaction, cancel) =>
                {
                    await ReadSampleAsync(
                            connection, transaction, table, documentType, serializer, tenantId, subclassAlias, state, commandTimeout, cancel)
                        .ConfigureAwait(false);

                    return 0;
                },
                token)
            .ConfigureAwait(false);

        ShapeCoverage coverage = state.Complete ? ShapeCoverage.Complete : ShapeCoverage.Sampled;

        return new ShapeCheckResult(
            subject,
            coverage,
            timeProvider.GetUtcNow(),
            Stopwatch.GetElapsedTime(started),
            state.Examined,
            state.TooLarge,
            state.Lowest,
            state.Highest,
            state.Spread,
            state.Reason,
            state.Remedy,
            state.LeftOut,
            state.Aggregator.Build(),
            null,
            null,
            state.Statements);
    }

    private static async Task ReadSampleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DocumentTableInfo table,
        IDocumentType documentType,
        ISerializer serializer,
        string? tenantId,
        string? subclassAlias,
        RunState state,
        int commandTimeout,
        CancellationToken token)
    {
        // Phase 0: is the collection small enough to read whole?
        int size;

        using (NpgsqlCommand probe = DocumentShapeQueries.BuildSizeProbe(
                   table, tenantId, subclassAlias, ShapeCheckLimits.SampleSize + 1, commandTimeout))
        {
            Attach(probe, connection, transaction, state);
            size = Convert.ToInt32(await probe.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }

        state.Complete = size <= ShapeCheckLimits.SampleSize;

        // Phase 1: keys and stored sizes.
        List<KeyRow> keys = [];
        bool spreadAttempted = false;

        using (NpgsqlCommand command = state.Complete
                   ? DocumentShapeQueries.BuildCompleteKeys(table, tenantId, subclassAlias, ShapeCheckLimits.SampleSize, commandTimeout)
                   : DocumentShapeQueries.BuildSampledKeys(
                       table, tenantId, subclassAlias, ShapeCheckLimits.Edge, ShapeCheckLimits.Spread, commandTimeout))
        {
            spreadAttempted = !state.Complete && DocumentShapeQueries.CanSpread(table);
            Attach(command, connection, transaction, state);

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                object id = reader.GetValue(0);

                keys.Add(new KeyRow(
                    id,
                    Text(id),
                    await reader.IsDBNullAsync(1, token).ConfigureAwait(false) ? null : reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt32(3)));
            }
        }

        if (!state.Complete)
        {
            if (!DocumentShapeQueries.CanSpread(table))
            {
                state.Reason = ShapeCheckText.IdTypeCannotBeSpread;
            }
            else if (spreadAttempted && !keys.Exists(static x => x.Part == DocumentShapeQueries.PartSpread))
            {
                state.Reason = ShapeCheckText.NotAnalysed;
            }
        }

        // Lowest, then the spread, then highest; a key found twice keeps the first of those it was found by.
        Dictionary<(string?, string), int> parts = [];
        List<KeyRow> chosen = [];

        foreach (KeyRow key in keys.OrderBy(static x => PartOrder(x.Part)))
        {
            if (!parts.TryAdd((key.Tenant, key.IdText), key.Part))
            {
                continue;
            }

            if (key.StoredBytes > ShapeCheckLimits.MaxItemBytes)
            {
                state.TooLarge++;
                continue;
            }

            chosen.Add(key);
        }

        if (chosen.Count == 0)
        {
            return;
        }

        // Phase 2: the chosen rows by key.
        using NpgsqlCommand rows = DocumentShapeQueries.BuildRows(
            table,
            tenantId,
            subclassAlias,
            [.. chosen.Select(static x => x.Id)],
            [.. chosen.Select(static x => x.Tenant)],
            ShapeCheckLimits.MaxItemBytes,
            commandTimeout);

        Attach(rows, connection, transaction, state);

        await using NpgsqlDataReader rowReader = await rows.ExecuteReaderAsync(token).ConfigureAwait(false);

        while (await rowReader.ReadAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();

            string idText = Text(rowReader.GetValue(0));
            string? tenant = await rowReader.IsDBNullAsync(4, token).ConfigureAwait(false) ? null : rowReader.GetString(4);

            if (await rowReader.IsDBNullAsync(1, token).ConfigureAwait(false))
            {
                state.TooLarge++;
                continue;
            }

            string json = rowReader.GetString(1);
            string? rowAlias = await rowReader.IsDBNullAsync(3, token).ConfigureAwait(false) ? null : rowReader.GetString(3);

            ShapeItemResult item = ShapeRoundTrip.Check(serializer, documentType, rowAlias, json);

            state.Aggregator.Add(item.TypeName, item.FullTypeName, new ShapeItemKey(idText, tenant), item.Failure, item.Members);
            state.Examined++;

            switch (parts.GetValueOrDefault((tenant, idText)))
            {
                case DocumentShapeQueries.PartLowest:
                    state.Lowest++;
                    break;
                case DocumentShapeQueries.PartHighest:
                    state.Highest++;
                    break;
                case DocumentShapeQueries.PartSpread:
                    state.Spread++;
                    break;
                default:
                    break;
            }
        }
    }

    private static void Attach(NpgsqlCommand command, NpgsqlConnection connection, NpgsqlTransaction transaction, RunState state)
    {
        command.Connection = connection;
        command.Transaction = transaction;
        state.Statements.Add(new ShapeStatement(command.CommandText, DocumentShapeQueries.ParameterNames(command)));
    }

    private static int PartOrder(int part) => part switch
    {
        DocumentShapeQueries.PartLowest => 0,
        DocumentShapeQueries.PartSpread => 1,
        DocumentShapeQueries.PartHighest => 2,
        _ => 0,
    };

    private static string Text(object id) => Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private ShapeCheckResult Partial(string subject, RunState state, string reason, long started) =>
        new(
            subject,
            ShapeCoverage.Partial,
            timeProvider.GetUtcNow(),
            Stopwatch.GetElapsedTime(started),
            state.Examined,
            state.TooLarge,
            state.Lowest,
            state.Highest,
            state.Spread,
            reason,
            state.Remedy,
            state.LeftOut,
            state.Aggregator.Build(),
            null,
            null,
            state.Statements);

    private ShapeCheckResult Failed(string subject, RunState state, string reason, long started) =>
        ShapeCheckResult.Failed(
            subject, ShapeCheckText.Failed(reason), timeProvider.GetUtcNow(), Stopwatch.GetElapsedTime(started), state.Statements);

    private sealed record KeyRow(object Id, string IdText, string? Tenant, long StoredBytes, int Part);

    /// <summary>What a run has read so far, so that a stopped run can still say what it found.</summary>
    private sealed class RunState
    {
        public ShapeAggregator Aggregator { get; } = new();

        public List<ShapeStatement> Statements { get; } = [];

        public List<string> LeftOut { get; } = [];

        public bool Complete { get; set; }

        public int Examined { get; set; }

        public int TooLarge { get; set; }

        public int Lowest { get; set; }

        public int Highest { get; set; }

        public int Spread { get; set; }

        public string? Reason { get; set; }

        public string? Remedy { get; set; }
    }
}
