using System.Globalization;
using System.Text.RegularExpressions;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Services.Health.Checks;

/// <summary>A statement T02 planned, or why it did not.</summary>
/// <param name="QueryId">Which statement.</param>
/// <param name="Scans">The plan's scans, outermost first; empty when there is no plan.</param>
/// <param name="NotPlanned">Why there is no plan, in the studio's words; <see langword="null" /> when there is one.</param>
internal sealed record StatementPlan(long QueryId, IReadOnlyList<ExplainPlanScan> Scans, string? NotPlanned);

/// <summary>What T02 reads.</summary>
/// <param name="Read">The statement checks' shared read.</param>
/// <param name="Plans">The plans of the statements worth planning, at most <see cref="ExpensiveStatementsCheck.MaxPlans" />.</param>
/// <param name="Indexes">Every index on the store's tables, read only when a plan reads a whole document table.</param>
internal sealed record ExpensiveStatementFacts(StatementRead Read, IReadOnlyList<StatementPlan> Plans, IReadOnlyList<IndexFacts> Indexes);

/// <summary>
/// T02: the store's own statements that cost the database the most - by the time they take, and by the time each
/// call takes - with their plan and the index advice it supports.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tier 3, behind the activity gate.</b> It reads <c>pg_stat_statements</c> through the statement checks' shared read
/// (<see cref="StatementReads" />) - as the store's own role, for that role's statements in this database - and runs
/// only when a visitor through <c>ViewDatabaseActivity</c> asks for the statement checks.
/// </para>
/// <para>
/// <b>A window, when there is one.</b> It samples each statement's calls, time and rows into the statement history, and
/// once two statement checks have run it judges the change between them. Before that it judges the totals since the
/// statement's counters began - the entry's own <c>stats_since</c> on PostgreSQL 17, the extension's
/// <c>stats_reset</c> before it - and says so.
/// </para>
/// <para>
/// <b>What the plan can and cannot say.</b> The plan is the generic plan, in <c>FORMAT JSON</c>, of the text
/// <c>pg_stat_statements</c> recorded (prepared and explained under <c>force_generic_plan</c> -
/// <see cref="HealthStatementQueries.Explain" /> says why not <c>GENERIC_PLAN</c> - never <c>ANALYZE</c>, a plain
/// <c>select</c> only, as <c>SqlConsoleRole</c>). In that text a plannable statement's constants are placeholders -
/// and Marten writes a JSON member's name into its SQL as a constant, <c>d.data -&gt;&gt; 'Color'</c>, which is recorded
/// as <c>d.data -&gt;&gt; $2</c>. (Not every statement's constants are replaced: a utility statement can keep its own as
/// written, which is why such a text is withheld rather than shown - <see cref="StatementReads.Withheld" />.)
/// Measured on PostgreSQL 17: with the key a parameter, the generic plan of a filter on it is a sequential scan even
/// where an expression index on <c>(data -&gt;&gt; 'Color')</c> exists, and with the key a literal it is an index scan.
/// So a sequential scan filtering on <c>data -&gt;&gt; $n</c> is never taken as a missing index: the finding says the
/// member cannot be named and links to the Query page's advisor, which can. What the plan does say soundly is a scan
/// filtering on a duplicated column, a containment (<c>@&gt;</c>) test, a JSON path written with literal keys - and a
/// read of the whole table with no filter at all - and those go to the existing <see cref="IndexAdvisor" />, whose
/// verdict is the remedy.
/// </para>
/// </remarks>
[HealthCheck("T02", HealthArea.Tables, HealthCostTier.Statements, "Expensive statements", Gate = HealthGate.Activity)]
internal sealed class ExpensiveStatementsCheck : HealthCheck<ExpensiveStatementFacts>
{
    /// <summary>A statement that runs this share of wall-clock time or more is worth an Info: 2% is 72 seconds an hour.</summary>
    public const double LoadInfo = 0.02;

    /// <summary>... and a Warning at this share: 10% is six minutes an hour.</summary>
    public const double LoadWarning = 0.10;

    /// <summary>
    /// The least execution time, in milliseconds, a share is judged on. A statement whose counters began a few seconds ago
    /// has run for a large share of a short life after any burst - 100 ms in a two-second-old entry is 5% - and a share
    /// of so little is not a cost.
    /// </summary>
    public const double MinExecutionMs = 10_000;

    /// <summary>A statement that averages this many milliseconds a call or more is a Warning...</summary>
    public const double SlowMeanMs = 500;

    /// <summary>... over at least this many calls.</summary>
    public const int SlowMeanCalls = 10;

    /// <summary>The most findings one sweep makes.</summary>
    public const int MaxFindings = 5;

    /// <summary>The most statements one sweep plans.</summary>
    public const int MaxPlans = 5;

    /// <summary>A statement is planned when its totals since its counters began reach this share of wall-clock time...</summary>
    public const double PlanLoadFloor = 0.01;

    /// <summary>... or this mean, in milliseconds.</summary>
    public const double PlanMeanFloorMs = 100;

    /// <summary>Rows per call from which a read is called out as one that returns everything.</summary>
    public const double ManyRowsPerCall = 1000;

    /// <summary>The series of a statement's calls.</summary>
    internal const string CallsSeries = "statements.calls:";

    /// <summary>The series of a statement's execution time, in milliseconds.</summary>
    internal const string ExecSeries = "statements.exec-ms:";

    /// <summary>The series of a statement's rows.</summary>
    internal const string RowsSeries = "statements.rows:";

    /// <summary>What the caveat says about a generic plan, whenever a finding carries one.</summary>
    internal const string GenericPlanCaveat =
        " The plan is PostgreSQL's generic plan for the text as pg_stat_statements recorded it, in which the statement's "
        + "constants - the JSON member names Marten writes into its SQL included - are placeholders; a filter on data ->> $n "
        + "can never use an expression index in that plan, so a sequential scan filtering on one does not show that the table "
        + "lacks an index.";

    /// <inheritdoc />
    public override async Task<ExpensiveStatementFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        StatementRead read = await StatementReads.ReadAsync(context).ConfigureAwait(false);
        List<StatementPlan> plans = [];

        foreach (ObservedStatement statement in PlanCandidates(read))
        {
            plans.Add(await PlanAsync(context, statement, cancellationToken).ConfigureAwait(false));
        }

        bool readsWholeDocumentTable = plans.Any(plan => plan.Scans.Any(scan =>
            IsSequential(scan) && TableOf(read.Statements.First(x => x.QueryId == plan.QueryId), scan) is { Kind: MartenTableKind.Document }));

        IReadOnlyList<IndexFacts> indexes = readsWholeDocumentTable
            ? await MartenTables.IndexesAsync(context).ConfigureAwait(false)
            : [];

        return new ExpensiveStatementFacts(read, plans, indexes);
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(ExpensiveStatementFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        StatementRead read = facts.Read;

        yield return new HealthSamplePart(StatementWindows.AllExecSeries, read.AllExecMs);
        yield return new HealthSamplePart(StatementWindows.AllWalSeries, read.AllWalBytes);

        if (read.Dealloc is { } dealloc)
        {
            yield return new HealthSamplePart(StatementWindows.DeallocSeries, dealloc);
        }

        foreach (ObservedStatement statement in read.Statements.Where(static x => x.NamesStoreTable && x.IsWorkload))
        {
            yield return new HealthSamplePart(CallsSeries + statement.Key, statement.Counters.Calls);
            yield return new HealthSamplePart(ExecSeries + statement.Key, statement.Counters.TotalExecMs);
            yield return new HealthSamplePart(RowsSeries + statement.Key, statement.Counters.Rows);
        }
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ExpensiveStatementFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        StatementRead read = facts.Read;
        List<(ObservedStatement Statement, StatementWindow Window, HealthSeverity Severity, bool Slow)> expensive = [];

        foreach (ObservedStatement statement in read.Statements.Where(static x => x.NamesStoreTable && x.IsWorkload))
        {
            StatementWindow window = WindowOf(read, statement, evaluation.History);

            if (window.Calls <= 0)
            {
                continue;
            }

            bool slow = window.MeanMs >= SlowMeanMs && window.Calls >= SlowMeanCalls;
            bool heavy = window.Load >= LoadInfo && window.ExecMs >= MinExecutionMs;

            if (slow || heavy)
            {
                expensive.Add((statement, window, slow || window.Load >= LoadWarning ? HealthSeverity.Warning : HealthSeverity.Info, slow));
            }
        }

        int storeStatements = read.Statements.Count(static x => x.NamesStoreTable && x.IsWorkload);

        if (expensive.Count == 0)
        {
            return HealthVerdict.Quiet(
                "No statement of the store's own role on its tables ran " + TableText.Percent(LoadInfo) + " of the time (over "
                + StatementWindows.Milliseconds(MinExecutionMs) + " of execution at least) or averaged "
                + StatementWindows.Milliseconds(SlowMeanMs) + " a call (" + TableText.Count(storeStatements) + " of the "
                + TableText.Count(read.Statements.Count) + " most expensive statements name one of its tables).");
        }

        HealthFinding[] findings =
        [
            .. expensive
                .OrderBy(static x => x.Severity)
                .ThenByDescending(static x => x.Window.Load ?? 0)
                .ThenByDescending(static x => x.Window.MeanMs ?? 0)
                .Take(MaxFindings)
                .Select(x => Finding(facts, x.Statement, x.Window, x.Severity, x.Slow, evaluation)),
        ];

        return new HealthVerdict(findings);
    }

    /// <summary>The window T02 judges a statement over: its own samples, or its totals since its counters began.</summary>
    internal static StatementWindow WindowOf(StatementRead read, ObservedStatement statement, HealthHistoryView history) =>
        StatementWindows.Of(
            read,
            history,
            (CallsSeries + statement.Key, ExecSeries + statement.Key, RowsSeries + statement.Key),
            statement.Counters.Calls,
            statement.Counters.TotalExecMs,
            statement.Counters.Rows,
            StatementWindows.Since(read, statement));

    /// <summary>
    /// The statements worth a plan: the store's workload, with its text read and not withheld, whose totals since their
    /// counters began reach <see cref="PlanLoadFloor" /> over half of <see cref="MinExecutionMs" />, or
    /// <see cref="PlanMeanFloorMs" /> - the most expensive <see cref="MaxPlans" />.
    /// </summary>
    /// <remarks>
    /// A withheld text is never planned: a plan of it would put what the text hides - a literal JSON key, a constant
    /// Postgres prints in a filter - into the Plan evidence, the index advice and the remedy, which are shown where the
    /// text itself is not.
    /// </remarks>
    internal static IEnumerable<ObservedStatement> PlanCandidates(StatementRead read)
    {
        ArgumentNullException.ThrowIfNull(read);

        return read.Statements
            .Where(static x => x.NamesStoreTable && x.IsWorkload && x.TextWithheld is null)
            .Select(x => (Statement: x, Window: StatementWindows.Of(read, HealthHistoryView.Empty, ("-", "-", "-"), x.Counters.Calls,
                x.Counters.TotalExecMs, x.Counters.Rows, StatementWindows.Since(read, x))))
            .Where(static x => (x.Window.Load >= PlanLoadFloor && x.Window.ExecMs >= MinExecutionMs / 2)
                               || (x.Window.MeanMs >= PlanMeanFloorMs && x.Window.Calls >= SlowMeanCalls))
            .OrderByDescending(static x => x.Window.Load ?? 0)
            .ThenByDescending(static x => x.Window.MeanMs ?? 0)
            .Take(MaxPlans)
            .Select(static x => x.Statement);
    }

    /// <summary>What the Plan evidence of a statement whose text is withheld says.</summary>
    internal const string NotPlannedWithheld =
        "Not planned: its text is withheld, and a plan of it would show what the text hides.";

    /// <summary>The longest the studio waits to take its prepared statement back after a plan that did not finish.</summary>
    internal static readonly TimeSpan ForgetBound = TimeSpan.FromSeconds(5);

    /// <summary>
    /// One statement's plan, or why there is none - and, on every way out, nothing of the studio's left on the session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A prepared statement outlives everything but its session.</b> It survives the rollback of the transaction it
    /// was prepared in, and Npgsql's reset of a pooled connection skips <c>DISCARD ALL</c> while Npgsql has statements of
    /// its own prepared on it - so a plan batch that stops between its <c>prepare</c> and its <c>deallocate</c> would hand
    /// the host's pool a session carrying the studio's statement. So every way out that is not the batch's own end -
    /// a refusal from Postgres, a cancelled sweep, a broken connection - takes it back (<see cref="ForgetAsync" />, which
    /// also says what is left when even that fails: at most the one name, on that one session).
    /// </para>
    /// <para>
    /// <b>After the rollback, not inside it.</b> A batch that failed has aborted its transaction, which refuses every
    /// statement but a rollback (<c>25P02</c>); the runner rolls it back, and the deallocate is a statement of its own on
    /// the same connection, in a transaction of its own. Measured live: after a plan that succeeds, one that fails in its
    /// <c>explain</c> and one cancelled there, the same backend holds no prepared statement of the studio's.
    /// </para>
    /// </remarks>
    internal static async Task<StatementPlan> PlanAsync(
        HealthCheckContext context,
        ObservedStatement statement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(statement);

        var query = HealthStatementQueries.Explain(statement.QueryId, statement.Text!, statement.TextTruncated);

        if (query is null)
        {
            return new StatementPlan(statement.QueryId, [], RefusalText(HealthStatementQueries.Refusal(statement.Text!, statement.TextTruncated)));
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                IReadOnlyList<string> plan = await context.Sql.QueryAsync(query, HealthStatementQueries.ReadPlan, cancellationToken).ConfigureAwait(false);
                return new StatementPlan(statement.QueryId, plan.Count == 0 ? [] : ExplainPlanScans.Read(plan[0]), null);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DuplicatePreparedStatement && attempt == 0)
            {
                // The name is on this session already - left by a batch that could not take it back. Take it back, and plan
                // once more.
                await ForgetAsync(context, statement.QueryId).ConfigureAwait(false);
            }
            catch (PostgresException exception)
            {
                await ForgetAsync(context, statement.QueryId).ConfigureAwait(false);

                // One statement's plan is that statement's evidence, not the check's failure: the others are still judged.
                context.Services.GetService<ILoggerFactory>()?.CreateLogger<ExpensiveStatementsCheck>()
                    .StatementPlanFailed(exception, statement.QueryId, context.StoreKey, context.DatabaseId, exception.SqlState);

                return new StatementPlan(statement.QueryId, [], "Not planned: Postgres refused the plan (SQLSTATE " + exception.SqlState + ")"
                    + exception.SqlState switch
                    {
                        PostgresErrorCodes.InsufficientPrivilege => " - SqlConsoleRole, which the studio plans as, may not read a table it names.",
                        PostgresErrorCodes.IndeterminateDatatype => " - the type of one of its placeholders cannot be told from the statement alone.",
                        PostgresErrorCodes.QueryCanceled => " - planning it ran out of the statement_timeout.",
                        PostgresErrorCodes.LockNotAvailable => " - somebody holds a lock on a table it names that a plan has to wait for.",
                        _ => ".",
                    });
            }
            catch (Exception)
            {
                // A cancelled sweep, a broken connection, anything else: the prepared statement must not outlive the plan.
                await ForgetAsync(context, statement.QueryId).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>
    /// Takes the plan's prepared statement back - under a bound of its own and never the sweep's token, because a
    /// cancelled sweep is exactly the one that has to - in a read-only transaction, and when that fails once more
    /// outside any transaction. If both fail, the session keeps the one name, and that is said in a Debug line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The residual, plainly.</b> If taking the plan back fails twice - the server stalled past both bounds, say -
    /// that session keeps at most the one <see cref="HealthStatementQueries.PlanStatementName" /> name, and goes back to
    /// the host's pool with it: a prepared read, which runs nothing unless somebody executes it by that name. Npgsql's
    /// reset removes it when it next resets that session with
    /// <c>DISCARD ALL</c> (which it does while it has no prepared statements of its own on it), and so does the studio's
    /// next plan on that session, whose <c>prepare</c> meets the name (<c>42P05</c>), deallocates it and plans again.
    /// </para>
    /// <para>
    /// <b>What it does not do, deliberately.</b> It does not clear the pool - Npgsql's <c>ClearPool</c> finds no pool for
    /// a data source's connection, and one that did would close the host's idle connections, which registering the
    /// studio must never do. It does not terminate the backend, and it does not run <c>DISCARD ALL</c> or
    /// <c>DEALLOCATE ALL</c>: both are refused in a transaction block, and both would drop Npgsql's own prepared
    /// statements on a connection that is the host's.
    /// </para>
    /// </remarks>
    private static async Task ForgetAsync(HealthCheckContext context, long queryId)
    {
        using (CancellationTokenSource bound = new(ForgetBound))
        {
            try
            {
                await context.Sql.QueryAsync(HealthStatementQueries.Deallocate(), static _ => 0, bound.Token).ConfigureAwait(false);
                return;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidSqlStatementName)
            {
                // Never prepared, or the batch's own deallocate ran: there is nothing to take back.
                return;
            }
            catch (Exception)
            {
                // It may still be on the session: once more, as a session-level statement of its own.
            }
        }

        using CancellationTokenSource again = new(ForgetBound);

        try
        {
            await context.Sql.ExecuteOutsideTransactionAsync(HealthStatementQueries.Deallocate(), again.Token).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidSqlStatementName)
        {
            // Gone after all.
        }
        catch (Exception exception)
        {
            context.Services.GetService<ILoggerFactory>()?.CreateLogger<ExpensiveStatementsCheck>()
                .StatementPlanNotTakenBack(exception, queryId, context.StoreKey, context.DatabaseId, HealthStatementQueries.PlanStatementName);
        }
    }

    private static string RefusalText(StatementPlanRefusal refusal) => refusal switch
    {
        StatementPlanRefusal.Truncated =>
            "Not planned: its text is longer than the " + TableText.Count(HealthStatementQueries.MaxTextCharacters) + " characters the studio reads.",
        StatementPlanRefusal.NotASelect =>
            "Not planned: it is not a plain select, and planning a write takes the lock its execution would.",
        StatementPlanRefusal.LockingClause =>
            "Not planned: it locks the rows it reads (FOR UPDATE or FOR SHARE), and planning it takes that lock's table lock.",
        _ => "Not planned: it is not a single statement the studio's console guard would run.",
    };

    private static HealthFinding Finding(
        ExpensiveStatementFacts facts,
        ObservedStatement statement,
        StatementWindow window,
        HealthSeverity severity,
        bool slow,
        HealthEvaluation evaluation)
    {
        StatementRead read = facts.Read;
        string about = StatementWindows.About(statement);
        double mean = window.MeanMs ?? 0;

        double allExec = window.Watched && TableHistory.Delta(evaluation.History.Trend(StatementWindows.AllExecSeries)) is { } allDelta
            ? allDelta.Change
            : read.AllExecMs;

        List<HealthEvidence> evidence =
        [
            statement.Evidence("Statement"),
            new("Calls", TableText.Count(window.Calls)),
            new("Execution time", StatementWindows.Milliseconds(window.ExecMs)
                + (window.Load is { } load ? ", " + LoadText(load) : string.Empty)),
            new("Mean per call", StatementWindows.Milliseconds(mean)),
        ];

        if (allExec > 0)
        {
            evidence.Add(new HealthEvidence("Share of the role's statement time", TableText.Percent(Math.Min(1, window.ExecMs / allExec))));
        }

        double rowsPerCall = window.Calls > 0 ? window.Rows / window.Calls : 0;
        evidence.Add(new HealthEvidence("Rows per call", rowsPerCall.ToString(rowsPerCall < 10 ? "0.0" : "N0", CultureInfo.InvariantCulture)
            + (rowsPerCall >= ManyRowsPerCall
                ? " - a read that returns this many rows each time is often a ToListAsync() with no Take() or paging"
                : string.Empty)));

        if (statement.Counters.TempBlocksWritten > 0)
        {
            evidence.Add(new HealthEvidence("Spilled to disk", TableText.Count(statement.Counters.TempBlocksWritten)
                + " temporary blocks written since its counters began (~" + TableText.Bytes(statement.Counters.TempBlocksWritten * 8192)
                + " at the default block size): it sorts or hashes in temporary files; C08 counts the database's"));
        }

        if (statement.Counters.WalBytes > 0 && read.AllWalBytes > 0)
        {
            evidence.Add(new HealthEvidence("Share of the role's WAL", TableText.Percent(Math.Min(1, statement.Counters.WalBytes / read.AllWalBytes))
                + " since its counters began (" + TableText.Bytes((long) statement.Counters.WalBytes) + ")"));
        }

        // A withheld text is never planned (PlanCandidates), and a plan is never read for one even if it were.
        StatementPlan? plan = statement.TextWithheld is null ? facts.Plans.FirstOrDefault(x => x.QueryId == statement.QueryId) : null;
        StatementPlanAdvice.Reading? reading = plan is { NotPlanned: null } ? StatementPlanAdvice.Read(plan.Scans, statement, facts.Indexes) : null;

        evidence.Add(new HealthEvidence("Plan", reading?.Summary
            ?? plan?.NotPlanned
            ?? (statement.TextWithheld is not null ? NotPlannedWithheld : null)
            ?? "Not planned in this check: a plan is read for at most " + MaxPlans.ToString(CultureInfo.InvariantCulture)
               + " statements a sweep, the ones whose totals since their counters began cross the line, and this one crossed it in the window."));

        if (reading is { Verdicts.Count: > 0 })
        {
            evidence.Add(new HealthEvidence("Index advice", string.Join(" ", reading.Verdicts.Select(static x => x.Subject + ": " + x.Verdict.Reason))));
        }

        evidence.Add(new HealthEvidence("Window", StatementWindows.Describe(window, read.ReadAt)));

        MartenTable? document = statement.Tables.FirstOrDefault(static x => x.Kind == MartenTableKind.Document);

        return new HealthFinding
        {
            Code = "T02",
            Severity = severity,
            Subject = statement.Key,
            Title = slow
                ? "A statement on " + about + " averages " + StatementWindows.Milliseconds(mean) + " a call"
                : "A statement on " + about + " spent " + StatementWindows.Milliseconds(window.ExecMs) + " executing in "
                  + TableText.Span(window.Span ?? TimeSpan.Zero),
            WhyItMatters = slow
                ? "Every call of this statement holds a connection from the pool, and a snapshot that vacuum cannot clean up behind, for "
                  + "as long as it runs; a request that makes it waits that long. A statement this slow is most often a query over a "
                  + "large document table with no index for its filter, or one that reads far more rows than it returns."
                : "This is where the store's own role spends its database time. Time a statement spends executing is time the "
                  + "server's processors, disks and buffer cache spend on it, and it grows with the table: the cheapest database time to "
                  + "win back is usually here, with an index or a narrower query.",
            Evidence = evidence,
            Caveat = StatementWindows.Caveat(read, window, evaluation.History) + (reading is not null ? GenericPlanCaveat : string.Empty),
            Remedy = Remedy(reading, document),
            Link = document is { Alias: { } alias }
                ? new HealthLink(
                    "Open the index advisor in Query",
                    "query",
                    [new KeyValuePair<string, string?>("mode", "marten"), new KeyValuePair<string, string?>("type", alias)])
                : null,
            DocumentTypes = statement.DocumentTypes,
            GatedSchemas = statement.GatedSchemas,
        };
    }

    /// <summary>
    /// A load as a person reads it: a share of the window up to the whole of it, and above that the number of
    /// connections' worth of time it took - several connections running it at once add up to more than the window.
    /// </summary>
    internal static string LoadText(double load) =>
        load <= 1
            ? TableText.Percent(load) + " of the window's time"
            : load.ToString("0.0", CultureInfo.InvariantCulture) + " times the window's length - several connections ran it at once";

    private static HealthRemedy? Remedy(StatementPlanAdvice.Reading? reading, MartenTable? document)
    {
        if (reading is null)
        {
            return null;
        }

        List<string> lines = [.. reading.Verdicts.Select(static x => x.Verdict.Suggestion).OfType<string>().Distinct(StringComparer.Ordinal)];

        if (lines.Count > 0)
        {
            return new HealthRemedy(
                string.Join("\n", lines),
                "From the studio's index advisor, for the filters the plan reads every row of the table to apply. Every write maintains "
                + "every index, so add the one this statement needs and no other.");
        }

        if (reading.UnnamedMember && document is { TypeName: { } type, Alias: { } alias })
        {
            return new HealthRemedy(
                "// The member this statement filters on, which pg_stat_statements does not name -\n"
                + "// if no index on that member exists already:\n"
                + "opts.Schema.For<" + type + ">().Index(x => x.FilteredMember);\n"
                + "// or, where it is also sorted on or compared as a column:\n"
                + "opts.Schema.For<" + type + ">().Duplicate(x => x.FilteredMember);",
                "pg_stat_statements records this statement's constants as placeholders - the JSON member names Marten writes into "
                + "its SQL included - so neither the plan nor the advisor can say which member this filters on, nor whether an index "
                + "on it exists already: a generic plan cannot use one for a placeholder key. Query in Marten mode, on the " + alias
                + " collection, judges a where clause and names it; add the line only if it finds no index on that member.");
        }

        return null;
    }

    private static bool IsSequential(ExplainPlanScan scan) => scan.NodeType.Equals("Seq Scan", StringComparison.Ordinal);

    /// <summary>The statement's own table a scan reads, by the name a plan prints - never a table it does not name.</summary>
    internal static MartenTable? TableOf(ObservedStatement statement, ExplainPlanScan scan) =>
        scan.Relation is { } relation
            ? statement.Tables.FirstOrDefault(x => string.Equals(x.Table, relation, StringComparison.OrdinalIgnoreCase))
            : null;
}

/// <summary>What T02 reads out of a plan: which of the statement's own tables it scans and how, and the advisor's verdicts.</summary>
internal static partial class StatementPlanAdvice
{
    /// <summary>The most scans a plan's summary describes.</summary>
    public const int MaxDescribed = 4;

    /// <summary>What a plan says.</summary>
    /// <param name="Summary">The scans of the statement's own tables, in words.</param>
    /// <param name="Verdicts">The advisor's verdicts on what a whole-table read filters on - only the ones that need an index.</param>
    /// <param name="UnnamedMember">Whether a whole-table read filters on a JSON member the recorded text does not name.</param>
    internal sealed record Reading(string Summary, IReadOnlyList<(string Subject, IndexVerdict Verdict)> Verdicts, bool UnnamedMember);

    /// <summary>Reads <paramref name="scans" /> for <paramref name="statement" />. Pure.</summary>
    public static Reading Read(IReadOnlyList<ExplainPlanScan> scans, ObservedStatement statement, IReadOnlyList<IndexFacts> indexes)
    {
        ArgumentNullException.ThrowIfNull(scans);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(indexes);

        List<string> described = [];
        List<(string Subject, IndexVerdict Verdict)> verdicts = [];
        int others = 0;
        bool unnamed = false;

        foreach (ExplainPlanScan scan in scans)
        {
            if (ExpensiveStatementsCheck.TableOf(statement, scan) is not { } table)
            {
                // A partition (named after a tenant), another store's table, a hidden type's: counted, never named.
                others += scan.Relation is null ? 0 : 1;
                continue;
            }

            string how = scan.Index is { } index ? scan.NodeType + " using " + index + " on " + table.Table : scan.NodeType + " on " + table.Table;

            if (scan.NodeType.Equals("Seq Scan", StringComparison.Ordinal) && table is { Kind: MartenTableKind.Document, DocumentType: { } type })
            {
                DocumentTableInfo info = DocumentTableInfo.FromDocumentType(type);
                Filter filter = FilterOf(scan.Filter, info);
                unnamed |= filter.UnnamedMember;

                how += filter.Members.Count == 0 && !filter.UnnamedMember
                    ? ", reading every row with no filter on it"
                    : ", reading every row to filter on " + string.Join(", ", filter.Members.Select(static x => x.Subject)
                        .Concat(filter.UnnamedMember ? ["a JSON member the recorded statement does not name"] : []));

                IReadOnlyList<PostgresIndex> onTable =
                    [.. indexes.Where(x => string.Equals(x.Table.Key, table.Key, StringComparison.OrdinalIgnoreCase)).Select(static x => new PostgresIndex(x.Row.Name, x.Row.Definition))];

                foreach ((string subject, DocumentPredicate predicate) in filter.Members)
                {
                    IndexVerdict verdict = IndexAdvisor.Evaluate(info, onTable, predicate);

                    if (verdict.Level != IndexVerdictLevel.Green && verdict.Suggestion is not null)
                    {
                        verdicts.Add((subject, verdict));
                    }
                }
            }

            if (described.Count < MaxDescribed)
            {
                described.Add(how);
            }
        }

        string summary = described.Count == 0
            ? "The plan reads none of the store's tables directly"
            : string.Join("; ", described);

        if (others > 0)
        {
            summary += "; and " + others.ToString(CultureInfo.InvariantCulture) + (others == 1 ? " scan" : " scans")
                + " of a partition, or of a table this store does not declare or the studio does not show, not named here";
        }

        return new Reading(summary + ".", verdicts, unnamed);
    }

    /// <summary>What a whole-table read filters on, as far as its deparsed filter says.</summary>
    /// <param name="Members">Each member it filters on, with the predicate the advisor judges.</param>
    /// <param name="UnnamedMember">Whether it filters on a JSON member whose key is a placeholder.</param>
    internal sealed record Filter(IReadOnlyList<(string Subject, DocumentPredicate Predicate)> Members, bool UnnamedMember);

    /// <summary>
    /// The members <paramref name="filter" /> - a plan's deparsed <c>Filter</c> - reads: a duplicated column by its name, a
    /// JSON path whose keys are literals, a containment test; and whether it reads a JSON member through a placeholder.
    /// </summary>
    internal static Filter FilterOf(string? filter, DocumentTableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (string.IsNullOrWhiteSpace(filter))
        {
            return new Filter([], false);
        }

        // Postgres parenthesises every operator it deparses; none of the shapes below needs them.
        string flat = filter.Replace('(', ' ').Replace(')', ' ');
        List<(string Subject, DocumentPredicate Predicate)> members = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        bool unnamed = false;

        void Add(string subject, DocumentPredicate predicate)
        {
            if (seen.Add(subject + "\u001f" + predicate.GetType().Name))
            {
                members.Add((subject, predicate));
            }
        }

        foreach (Match match in JsonChain().Matches(flat))
        {
            List<string> path = [];
            bool placeholder = false;

            foreach (Match step in JsonStep().Matches(match.Groups["chain"].Value))
            {
                string key = step.Groups["key"].Value;

                if (key.StartsWith('$'))
                {
                    placeholder = true;
                    break;
                }

                path.Add(key[1..^1].Replace("''", "'", StringComparison.Ordinal));
            }

            if (placeholder)
            {
                unnamed = true;
                continue;
            }

            bool like = Like().IsMatch(flat[(match.Index + match.Length)..]);
            string subject = string.Join('.', path);

            Add(subject, like
                ? new DocumentPredicate.FieldLike(path, string.Empty)
                : new DocumentPredicate.FieldCompare(path, ComparisonOperator.Equal, new SearchValue.Text(string.Empty)));
        }

        foreach (Match match in JsonPathOperator().Matches(flat))
        {
            if (!match.Groups["path"].Success)
            {
                unnamed = true;
                continue;
            }

            string[] path = match.Groups["path"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (path.Length > 0)
            {
                Add(string.Join('.', path), new DocumentPredicate.FieldCompare(path, ComparisonOperator.Equal, new SearchValue.Text(string.Empty)));
            }
        }

        if (Containment().IsMatch(flat))
        {
            Add("JSON containment (@>)", new DocumentPredicate.Contains("{}"));
        }

        // A duplicated column by its own name, outside any literal - which is where a JSON key would be.
        string unquoted = Literal().Replace(flat, "''");

        foreach (DuplicatedColumnInfo column in table.DuplicatedColumns)
        {
            if (Regex.IsMatch(
                    unquoted,
                    "(?<![A-Za-z0-9_$])(?:[A-Za-z_][A-Za-z0-9_]*\\.)?" + Regex.Escape(column.ColumnName) + "(?![A-Za-z0-9_$])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)))
            {
                Add(column.MemberPath + " (duplicated column " + column.ColumnName + ")",
                    new DocumentPredicate.FieldCompare(column.MemberPath.Split('.'), ComparisonOperator.Equal, new SearchValue.Text(string.Empty)));
            }
        }

        return new Filter(members, unnamed);
    }

    /// <summary><c>data -&gt; 'A' -&gt;&gt; 'B'</c>, a key a literal or a placeholder, optionally qualified by an alias.</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_$])(?:[A-Za-z_][A-Za-z0-9_]*\.)?data(?<chain>(?:\s*->>?\s*(?:'(?:[^']|'')*'|\$[0-9]+)(?:\s*::\s*[a-z]+)?)+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex JsonChain();

    /// <summary>One step of a <see cref="JsonChain" />.</summary>
    [GeneratedRegex(@"->>?\s*(?<key>'(?:[^']|'')*'|\$[0-9]+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex JsonStep();

    /// <summary><c>data #&gt;&gt; '{A,B}'</c>, or with a placeholder for the path.</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_$])(?:[A-Za-z_][A-Za-z0-9_]*\.)?data\s*#>>?\s*(?:'\{(?<path>[^}']*)\}'|\$[0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex JsonPathOperator();

    /// <summary><c>data @&gt;</c>: a containment test on the whole document.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_$])(?:[A-Za-z_][A-Za-z0-9_]*\.)?data\s*@>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Containment();

    /// <summary>What a LIKE (<c>~~</c>) or ILIKE (<c>~~*</c>) after a JSON path looks like, casts allowed between.</summary>
    [GeneratedRegex(@"^\s*(?:::\s*[a-z]+\s*)*~~", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Like();

    /// <summary>A string literal.</summary>
    [GeneratedRegex("'(?:[^']|'')*'", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Literal();
}
