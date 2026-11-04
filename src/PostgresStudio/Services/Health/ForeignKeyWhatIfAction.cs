using System.Globalization;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Statements;

namespace PostgresStudio.Services.Health;

/// <summary>
/// T15's on-demand action (1.2-INDEX-B): the lookup Postgres runs for every row deleted from the referenced table, planned now and
/// with a hypothetical index on the key's columns (hypopg) - planned, never run.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate is the finding's own, <see cref="HealthGate.Database" />, and declares no capability.</b> What it shows - a node type,
/// an estimated cost and row count, the child table's own index names - is what a Database-gate visitor already sees in the finding
/// and on Storage, and the one statement planned is composed by the studio from the catalog, naming a table the finding already names
/// (<see cref="ForeignKeyLookupQueries" />): nobody's text and no value. Asking <c>RunSql</c> would hide the one what-if that matters
/// most from the administrator T15 is written for. A finding about a schema the visitor may not see offers no action at all.
/// </para>
/// <para>
/// <b>The same engine, the same rule.</b> The plan and the plan under the index are made by <see cref="HypotheticalIndexReader" /> in
/// a read-only transaction as <c>SqlConsoleRole</c>, hypopg reset on the same connection before it goes back - or the session
/// ended. Only a table that is neither partitioned nor a partition is imagined (hypopg's support for those is unmeasured).
/// </para>
/// </remarks>
internal sealed class ForeignKeyWhatIfAction(HypotheticalIndexReader hypothetical, StatementPlanReader planner) : IHealthFindingAction
{
    /// <summary>The action's id.</summary>
    public const string ActionId = "t15.what-if";

    /// <summary>What the result says about the statement it planned.</summary>
    internal const string LookupCaveat =
        "The lookup is planned without the row lock Postgres' own check takes (FOR KEY SHARE), so a console role that may only SELECT can plan "
        + "it; the lock adds a node on top and changes no access path. Its parameters are typed from the key's columns, where Postgres types "
        + "them as the referenced columns, which differs only for a key across two types. A cascade's own DELETE or UPDATE … WHERE col = $1 "
        + "runs the same lookup.";

    /// <summary>What the result says about the index imagined.</summary>
    internal const string IndexNote =
        "The imagined index is on all of the key's columns, in key order. An index on fewer of them, which the finding's remedy may offer, "
        + "plans the same lookup (T15 counts both as covering).";

    /// <summary>What a partitioned table or a partition is told.</summary>
    internal const string PartitionedRefusal =
        "Not offered on a partitioned table or a partition: an index there is declared on the partitioned table, and whether hypopg's "
        + "hypothetical index on one serves a partition's scan has not been measured.";

    /// <summary>What a lookup whose plan reads a table other than the key's - through a row-level security policy - is told.</summary>
    internal const string OtherTableRefusal = "Not shown: the lookup's plan reads another table, through a row-level security policy.";

    /// <summary>What a key the studio cannot find again is told.</summary>
    internal const string NotFound = "The studio could not find this foreign key again; run the checks again.";

    /// <inheritdoc />
    public string Id => ActionId;

    /// <inheritdoc />
    public string Code => "T15";

    /// <inheritdoc />
    public string Label => "What if it had an index?";

    /// <inheritdoc />
    public string Description =>
        "Plans the lookup Postgres runs for every row deleted from the referenced table, now and with a hypothetical index on the key's "
        + "columns (hypopg). Planned, never run; audited.";

    /// <inheritdoc />
    public string AuditAction => "What-if foreign key lookup";

    /// <inheritdoc />
    public string AuditTarget => "the foreign key's lookup";

    /// <inheritdoc />
    public HealthGate Gate => HealthGate.Database;

    /// <inheritdoc />
    public StudioCapability? Capability => null;

    /// <inheritdoc />
    public bool AppliesTo(HealthFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return string.Equals(finding.Code, Code, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public async Task<HealthActionResult> RunAsync(HealthActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        IStudioDatabase database = context.Resolved.Target;

        try
        {
            // 1. hypopg, before anything is planned.
            HypotheticalIndexAvailability availability = await hypothetical.GetAvailabilityAsync(database, cancellationToken).ConfigureAwait(false);

            if (availability.Sentence is { } unavailable)
            {
                return HealthActionResult.Refused(unavailable, now);
            }

            // 2. The key, read again; never from the finding's words.
            if (context.Finding.Finding.Subject is not { Length: > 0 } subject)
            {
                return HealthActionResult.Refused(NotFound, now);
            }

            IReadOnlyList<ForeignKeyLookupKey> keys;

            await using (NpgsqlConnection connection = database.CreateConnection(StudioConnectionUsage.Read))
            {
                await connection.OpenStudioAsync(cancellationToken).ConfigureAwait(false);

                keys = await new ReadOnlySqlSession(context.Session)
                    .InTransactionAsync(
                        connection,
                        (transaction, token) => ForeignKeyLookupQueries.ReadKeyAsync(connection, transaction, subject, context.CommandTimeoutSeconds, token),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (keys.Count != 1)
            {
                return HealthActionResult.Refused(NotFound, now);
            }

            ForeignKeyLookupKey key = keys[0];

            // 3. Not on a partitioned table or a partition.
            if (key.Kind == "p" || key.IsPartition)
            {
                return HealthActionResult.Refused(PartitionedRefusal, now);
            }

            // 4-5. The index, and the lookup: both written by builders from the catalog's names.
            if (PlanWhatIfCandidates.Candidate(key.Schema, key.Table, key.Columns) is not { } candidate
                || ForeignKeyLookupQueries.Lookup(key.Schema, key.Table, key.Columns) is not { } lookup
                || StatementPlanQueries.GenericPlan(lookup, truncated: false) is not { } batch)
            {
                return HealthActionResult.Refused("A name in this foreign key cannot be printed as a statement honestly, so the studio does not plan it.", now);
            }

            HypotheticalIndex index = candidate with { Key = "t15:" + candidate.Key };

            // 6. The lookup before and after, through the engine.
            HypotheticalIndexPlanner plan = new(
                PlanAsync: async (connection, transaction, token) =>
                {
                    (string? json, _) = await StatementPlanQueries
                        .ReadPlanAsync(connection, transaction, batch, ExplainStatements.MaxPlanCharacters, context.CommandTimeoutSeconds, token)
                        .ConfigureAwait(false);
                    return json;
                },
                AfterFailureAsync: planner.ForgetAsync);

            WhatIfResult result;

            try
            {
                HypotheticalIndexRead read = await ReadAsync(database, index, plan, cancellationToken).ConfigureAwait(false);
                result = WhatIfResult.From(index, read);
            }
            catch (PostgresException exception)
            {
                result = WhatIfResult.NotCompared(index, WhatIfResult.Failed(exception.SqlState), HypotheticalIndexState.Available);
            }

            if (result.Before is null || result.After is null)
            {
                return HealthActionResult.Refused(result.Reason ?? "Not compared.", now) with { AuditMessage = result.Outcome() };
            }

            // Under row-level security the lookup's plan gains the policy's own subquery, which may read a table in a schema this visitor's
            // browser gate withholds: nothing of such a plan is drawn.
            if (ReadsAnotherTable(result.Before, key) || ReadsAnotherTable(result.After, key))
            {
                return HealthActionResult.Refused(OtherTableRefusal, now) with
                {
                    AuditMessage = "Planned, and not shown: the lookup's plan reads another table.",
                };
            }

            return Present(context, result, key, lookup, availability.Schema, now);
        }
        catch (Exception exception) when (PostgresFailure.IsConnectionFailure(exception))
        {
            return HealthActionResult.Failed(PostgresFailure.CouldNotConnect(exception), now);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return HealthActionResult.Refused("The role the studio reads as may not read the catalog this action needs.", now);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.LockNotAvailable)
        {
            return HealthActionResult.TimedOut("It ran past its statement_timeout, or a lock a plan needs was held; try again later.", now);
        }
    }

    /// <summary>The engine's read, with one retry for a prepared name a plan that could not take it back left on the pooled session.</summary>
    private async Task<HypotheticalIndexRead> ReadAsync(IStudioDatabase database, HypotheticalIndex index, HypotheticalIndexPlanner plan, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await hypothetical.ReadAsync(database, index, plan, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DuplicatePreparedStatement && attempt == 0)
            {
                // The failure path took it back; once more.
            }
        }
    }

    private static HealthActionResult Present(
        HealthActionContext context,
        WhatIfResult result,
        ForeignKeyLookupKey key,
        string lookup,
        string? hypopgSchema,
        DateTimeOffset now)
    {
        List<HealthStatement> statements =
        [
            new(ForeignKeyLookupQueries.KeySql, [ForeignKeyLookupQueries.SubjectParameter]) { Label = "the foreign key, read again" },
            new(StatementPlanQueries.GenericPlan(lookup, truncated: false)!, []) { Label = "the lookup Postgres runs, planned" },
        ];

        if (hypopgSchema is not null)
        {
            if (HypotheticalIndexQueries.Create(hypopgSchema) is { } create)
            {
                statements.Add(new HealthStatement(create, [HypotheticalIndexQueries.DefinitionParameter]) { Label = "hypopg's create call" });
            }

            if (HypotheticalIndexQueries.Reset(hypopgSchema) is { } reset)
            {
                statements.Add(new HealthStatement(reset, []) { Label = "hypopg's reset" });
            }
        }

        List<string> notes = [WhatIfResult.Caveat, LookupCaveat, IndexNote];

        if (result.Index.Note is { Length: > 0 } indexNote)
        {
            notes.Add(indexNote);
        }

        return new HealthActionResult
        {
            Outcome = HealthActionOutcome.Done,
            RanAt = now,
            Summary = result.Verdict + " Postgres runs this lookup once for every row deleted from the referenced table, and for every update of "
                + "its key - the finding counts the deletes.",
            TableLabel = "The foreign key's lookup, planned now and with the index",
            Columns = ["Plan", "Top scan", "Estimated rows", "Estimated cost"],
            Rows = [Row("Now", result.Before!, key), Row("With the index", result.After!, key)],
            Notes = notes,
            Remedies = context.Finding.Finding.Remedy is { } remedy ? [remedy] : [],
            Statements = statements,
            AuditMessage = result.Outcome(),
        };
    }

    /// <summary>Whether <paramref name="plan" /> reads any relation other than the key's own table.</summary>
    private static bool ReadsAnotherTable(QueryPlan? plan, ForeignKeyLookupKey key) =>
        plan is not null && plan.Nodes.Any(x => x.RelationName is not null && !IsKeysTable(x, key));

    private static bool IsKeysTable(PlanNode node, ForeignKeyLookupKey key) =>
        string.Equals(node.Schema, key.Schema, StringComparison.Ordinal) && string.Equals(node.RelationName, key.Table, StringComparison.Ordinal);

    private static HealthActionRow Row(string label, QueryPlan plan, ForeignKeyLookupKey key)
    {
        // Only the key's own table is named: the scan, and the index a bitmap scan names on a node of its own under it.
        PlanNode? scan = plan.Nodes.FirstOrDefault(x => IsKeysTable(x, key));
        string scanned = scan?.Description ?? plan.Root.Description;

        // A bitmap scan names its index on a node of its own, under the heap scan: say which index served it.
        if (plan.Nodes.FirstOrDefault(static x => x.IndexName is not null)?.IndexName is { } index && !scanned.Contains(index, StringComparison.Ordinal))
        {
            scanned += " using " + index;
        }

        return new HealthActionRow(
        [
            new HealthActionCell(label),
            new HealthActionCell(scanned) { Mono = true },
            new HealthActionCell(plan.Root.PlanRows is { } rows ? rows.ToString("N0", CultureInfo.InvariantCulture) : "–") { Number = true },
            new HealthActionCell(plan.Root.TotalCost is { } cost ? cost.ToString("#,0.00", CultureInfo.InvariantCulture) : "–") { Number = true },
        ]);
    }
}
