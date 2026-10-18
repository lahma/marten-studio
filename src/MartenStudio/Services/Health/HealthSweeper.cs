using System.Diagnostics;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace MartenStudio.Services.Health;

/// <summary>Which checks a sweep runs.</summary>
internal enum HealthSweepKind
{
    /// <summary>Tiers 0 and 1 - configuration, in-process state, the catalog - at <c>HealthInterval</c>.</summary>
    Regular,

    /// <summary>Tier 2, the bounded data reads, only behind "Run deeper checks" and cached for five minutes.</summary>
    Deeper,

    /// <summary>Tier 3, statements, only for a visitor through the activity gate, cached for five minutes.</summary>
    Statements,
}

/// <summary>How a check's run ended.</summary>
internal enum HealthCheckOutcome
{
    /// <summary>It ran, and its evaluator judged what it read - with or without findings.</summary>
    Checked,

    /// <summary>The host's Marten lacks a feature the check needs, so it was never run.</summary>
    NotApplicable,

    /// <summary>
    /// It could not run here for a reason that is the host's choice - a missing extension, a privilege, a
    /// statement that ran out of its budget. A value, logged at Debug.
    /// </summary>
    Unavailable,

    /// <summary>It failed for a reason nobody chose. A "could not check" row, and a throttled Warning.</summary>
    Failed,
}

/// <summary>What went wrong with a check, before it is masked for a visitor.</summary>
/// <remarks>
/// <para>
/// <b>Three kinds of words, kept apart</b> (H1 review): the report labels each by where it came from, and a
/// label that said "Postgres said:" over an exception message the studio or a library wrote would be telling a
/// visitor something untrue about it - and showing them text nothing had masked for them.
/// </para>
/// <list type="bullet">
/// <item><see cref="Reason" /> is the studio's own sentence. It names a code, a count, a type or a SQLSTATE and
/// never a subject, a tenant, a series or a schema, so it is shown at the check's gate as it is.</item>
/// <item><see cref="PostgresText" /> is Postgres' own message, from a <c>PostgresException</c> and nothing else.
/// It is shown after the report masks every withheld schema's name in it (D27).</item>
/// <item><see cref="Detail" /> is the words of an error that is neither: today only a connection failure, whose
/// message is Npgsql's or the operating system's. It is shown at <see cref="HealthGate.Database" />, because
/// it is about the database's address. Any other exception's message is not carried at all: it is the
/// author's of whatever threw, it may name what this visitor is not shown, and it is in the server log under
/// event 9222.</item>
/// </list>
/// </remarks>
/// <param name="Reason">The studio's own sentence.</param>
/// <param name="SqlState">The SQLSTATE, when Postgres said something.</param>
/// <param name="PostgresText">What Postgres said, verbatim, from a <c>PostgresException</c> only.</param>
/// <param name="Detail">A connection failure's own words, shown only at the database-wide gate.</param>
/// <param name="UnavailableReason">
/// For an <see cref="HealthCheckOutcome.Unavailable" /> row, which kind of expected state it is - so that a reader
/// that has to tell "the host chose this" from "it ran out of time" (the health export) need not parse a sentence.
/// </param>
internal sealed record HealthCheckProblem(
    string Reason,
    string? SqlState,
    string? PostgresText,
    string? Detail = null,
    HealthUnavailableReason? UnavailableReason = null);

/// <summary>Which kind of expected state kept a check from checking.</summary>
/// <remarks>
/// The Health page shows both kinds the same way - not checked, and why, at Debug in the log. The health export
/// does not (H8-fix F2): a missing privilege is the host's choice and stays Healthy, while a database whose
/// statements keep running out of time is worth somebody's look, and is Degraded.
/// </remarks>
internal enum HealthUnavailableReason
{
    /// <summary>
    /// The host's choice: a privilege the role lacks (42501), or what a gatherer said with
    /// <see cref="HealthCheckUnavailableException" /> - no extension, an old Postgres, nothing to read yet.
    /// </summary>
    HostChoice,

    /// <summary>
    /// It ran out of time: <c>statement_timeout</c> (57014), a lock wait (55P03), a client-side timeout, or the
    /// check's own budget.
    /// </summary>
    TimedOut,

    /// <summary>The sweep's budget - one <c>HealthInterval</c> - ran out before this check's turn.</summary>
    SweepBudget,
}

/// <summary>
/// A check broke one of the sweep's rules - a finding coded for another check, two findings under one key, two
/// pieces of evidence under one label, a series written twice, too many rows.
/// </summary>
/// <remarks>
/// <see cref="Shown" /> is the studio's own sentence for the page, and names the check's code and a count -
/// never a subject, a label or a series, which may name a tenant or a table, and which sit at the finding's
/// gate rather than the check's. <see cref="Exception.Message" /> may name them, and goes to the server log.
/// </remarks>
internal sealed class HealthSweepRuleException : InvalidOperationException
{
    /// <param name="shown">The page's sentence: a code and a count, nothing a gate could withhold.</param>
    /// <param name="logged">The log's sentence, which may name what broke the rule.</param>
    public HealthSweepRuleException(string shown, string logged)
        : base(logged)
    {
        Shown = shown;
    }

    /// <summary>The page's sentence.</summary>
    public string Shown { get; }
}

/// <summary>One check's part of a sweep.</summary>
/// <param name="Check">Which check.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Findings">What it found, already stamped and filtered.</param>
/// <param name="Note">What its status row says beside the outcome.</param>
/// <param name="Problem">Why it did not check, when it did not.</param>
/// <param name="Statements">What it ran.</param>
/// <param name="Duration">How long it took.</param>
internal sealed record HealthCheckResult(
    HealthCheckInfo Check,
    HealthCheckOutcome Outcome,
    IReadOnlyList<HealthFinding> Findings,
    string? Note,
    HealthCheckProblem? Problem,
    IReadOnlyList<HealthStatement> Statements,
    TimeSpan Duration);

/// <summary>One sweep of one database: every check of its kind, each with its own outcome.</summary>
/// <param name="StoreKey">The store.</param>
/// <param name="DatabaseId">The database's identity.</param>
/// <param name="Kind">Which tiers ran.</param>
/// <param name="MeasuredAt">When the facts were read.</param>
/// <param name="Duration">How long the whole sweep took.</param>
/// <param name="Results">Every check considered, in area and code order.</param>
internal sealed record HealthSweep(
    string StoreKey,
    string DatabaseId,
    HealthSweepKind Kind,
    DateTimeOffset MeasuredAt,
    TimeSpan Duration,
    IReadOnlyList<HealthCheckResult> Results)
{
    /// <summary>Every finding of every check.</summary>
    public IEnumerable<HealthFinding> Findings => Results.SelectMany(static x => x.Findings);
}

/// <summary>
/// Runs the checks of one kind against one database: gather everything, sample, then judge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three phases.</b> Every check's gatherer runs first, one after the other on one connection; then, for a
/// regular sweep, the numbers they contribute become one sample in <see cref="HealthHistory" />; then every
/// evaluator judges its facts with the history in hand. Sequential, not parallel: a sweep is a guest on
/// somebody's production database, and one connection doing one thing at a time is the cheapest guest there
/// is. A read several checks need is read once, through the sweep's memo (<see cref="HealthCheckContext.Shared{T}" />).
/// </para>
/// <para>
/// <b>One failing check never hides the others.</b> Each gatherer has a budget of its own and a
/// <c>try</c> of its own, and so does each evaluator; what fails becomes that check's status row and
/// nothing else. Nothing escapes a sweep but cancellation of the sweep itself. The sweep as a whole has a
/// budget too - one <c>HealthInterval</c> - and a check whose turn comes after it has run out is not
/// started, but listed as not checked: a slow database must not hold the one sweep its slot allows for
/// ever.
/// </para>
/// <para>
/// <b>A verdict must be drawable.</b> Two findings with one key, or one finding with two pieces of evidence
/// under one label, would be two elements under one Blazor <c>@key</c> - which ends the circuit rather than
/// the render (issue #1 was the same shape) - so such a verdict is that check's failure. The row names the code
/// and a count and the log names what was duplicated, because a key holds the finding's Subject and a label can
/// hold a name, and both sit at the finding's gate rather than the check's (<see cref="HealthSweepRuleException" />).
/// </para>
/// <para>
/// <b>Expected states are values</b> (AGENTS.md, the memory rule): a gatherer's
/// <see cref="HealthCheckUnavailableException" />, a privilege Postgres refused (42501), a statement that ran
/// out of its timeout (57014) or its lock wait (55P03) - each is an <see cref="HealthCheckOutcome.Unavailable" />
/// row and a Debug line. Anything else is a <see cref="HealthCheckOutcome.Failed" /> row and event 9222, at
/// Warning once per check, database and kind of failure in ten minutes (<see cref="StudioLogThrottle" />),
/// because a page polls this. A database that will not take a connection is one failure, not one per check.
/// </para>
/// <para>
/// <b>Hidden document types never appear.</b> A finding that names a type
/// <c>MartenStudioOptions.IsDocumentTypeVisible</c> hides is dropped here, before it is cached, because the
/// option is the process's and the cache is everybody's.
/// </para>
/// </remarks>
internal sealed class HealthSweeper
{
    /// <summary>The longest a health statement may run, whatever <c>QueryTimeout</c> says.</summary>
    internal static readonly TimeSpan MaxStatementTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long a check may take beyond its statement timeout before the sweep gives up on it.</summary>
    private static readonly TimeSpan BudgetMargin = TimeSpan.FromSeconds(10);

    private readonly HealthCheckRegistry registry;
    private readonly HealthHistory history;
    private readonly IServiceScopeFactory scopes;
    private readonly IOptions<MartenStudioOptions> options;
    private readonly StudioLogThrottle throttle;
    private readonly ILogger<HealthSweeper> logger;
    private readonly TimeProvider timeProvider;
    private readonly MartenRuntime runtime;
    private readonly IHealthStatementObserver? observer;

    public HealthSweeper(
        HealthCheckRegistry registry,
        HealthHistory history,
        IServiceScopeFactory scopes,
        IOptions<MartenStudioOptions> options,
        StudioLogThrottle throttle,
        ILogger<HealthSweeper> logger,
        TimeProvider? timeProvider = null,
        MartenRuntime? runtime = null,
        IHealthStatementObserver? observer = null)
    {
        this.registry = registry;
        this.history = history;
        this.scopes = scopes;
        this.options = options;
        this.throttle = throttle;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.runtime = runtime ?? MartenRuntime.Current;
        this.observer = observer;
    }

    /// <summary>
    /// The whole sweep's budget, when a test needs one shorter than the fifteen seconds the options allow;
    /// <see langword="null" /> - always, in a host - is one <c>EffectiveHealthInterval</c>.
    /// </summary>
    internal TimeSpan? SweepBudget { get; set; }

    /// <summary>The tiers a kind of sweep runs.</summary>
    public static IReadOnlySet<HealthCostTier> TiersOf(HealthSweepKind kind) => kind switch
    {
        HealthSweepKind.Regular => new HashSet<HealthCostTier> { HealthCostTier.InProcess, HealthCostTier.Catalog },
        HealthSweepKind.Deeper => new HashSet<HealthCostTier> { HealthCostTier.BoundedData },
        HealthSweepKind.Statements => new HashSet<HealthCostTier> { HealthCostTier.Statements },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No tiers are mapped to this kind of sweep."),
    };

    /// <summary>Sweeps one database. Never throws but for cancellation of the sweep itself.</summary>
    /// <param name="resolved">The database, resolved with no tenant by a visitor the store policy passed.</param>
    /// <param name="kind">Which tiers to run.</param>
    /// <param name="cancellationToken">Cancels the whole sweep.</param>
    public async Task<HealthSweep> SweepAsync(ResolvedScope resolved, HealthSweepKind kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        MartenStudioOptions value = options.Value;
        DateTimeOffset now = timeProvider.GetUtcNow();
        long started = Stopwatch.GetTimestamp();

        string storeKey = resolved.Registration.Key;
        string databaseId = resolved.Database.Id.Identity;
        IReadOnlySet<HealthCostTier> tiers = TiersOf(kind);

        TimeSpan statementTimeout = value.QueryTimeout < MaxStatementTimeout ? value.QueryTimeout : MaxStatementTimeout;
        ReadOnlySqlOptions sessionOptions = new()
        {
            StatementTimeout = statementTimeout,
            Role = value.SqlConsoleRole,
        };

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        await using HealthConnection connection = new(resolved.Database);

        // The whole sweep's budget: one interval. A check whose turn comes after it is not started.
        using CancellationTokenSource sweepBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sweepBudget.CancelAfter(SweepBudget ?? value.EffectiveHealthInterval);

        HealthSweepMemo memo = new();

        // Phase 1: every gatherer. All of the sweep's I/O happens here. Tier by tier - the in-process checks first, then
        // the catalog's - so that a slow catalog read can only ever cost the checks after it, and a configuration check,
        // which costs nothing, is never the one the sweep's budget runs out on. The results keep the registry's area and
        // code order, which is the order the page draws them in.
        HealthCheckDescriptor[] run = [.. registry.All.Where(x => tiers.Contains(x.Info.Tier))];
        Gathered[] gathered = new Gathered[run.Length];
        Dictionary<string, double> sample = new(StringComparer.Ordinal);
        Dictionary<string, HealthGate> sampleGates = new(StringComparer.Ordinal);

        foreach (int index in Enumerable.Range(0, run.Length).OrderBy(i => run[i].Info.Tier))
        {
            cancellationToken.ThrowIfCancellationRequested();

            HealthCheckDescriptor descriptor = run[index];
            gathered[index] = sweepBudget.IsCancellationRequested
                ? Gathered.Without(descriptor, HealthCheckOutcome.Unavailable, null, OutOfSweepBudget, [], Stopwatch.GetTimestamp())
                : await GatherAsync(descriptor).ConfigureAwait(false);
        }

        // Phase 2: one sample, from every check that read something - regular sweeps only, so that nothing a
        // gated tier reads becomes history an ungated evaluator can see.
        if (kind == HealthSweepKind.Regular)
        {
            history.Append(storeKey, databaseId, new HealthSample(now, sample) { Gates = sampleGates }, value.EffectiveHealthInterval / 2);
        }

        HealthHistoryView view = history.Read(storeKey, databaseId);

        // Phase 3: every evaluator, pure, with the history in hand - each with only the series written at or below
        // its own check's gate, so no number crosses a gate on its way from one check to another.
        Dictionary<HealthGate, HealthEvaluation> evaluations = [];
        HealthEvaluation EvaluationAt(HealthGate gate)
        {
            if (!evaluations.TryGetValue(gate, out HealthEvaluation? evaluation))
            {
                evaluation = new HealthEvaluation(now, runtime, view.VisibleAt(gate));
                evaluations[gate] = evaluation;
            }

            return evaluation;
        }

        List<HealthCheckResult> results =
            [.. gathered.Select(x => Evaluate(x, EvaluationAt(x.Descriptor.Info.Gate), storeKey, databaseId))];

        return new HealthSweep(storeKey, databaseId, kind, now, Stopwatch.GetElapsedTime(started), results);

        async Task<Gathered> GatherAsync(HealthCheckDescriptor descriptor)
        {
            HealthCheckInfo info = descriptor.Info;
            long checkStarted = Stopwatch.GetTimestamp();

            if (info.Requires is { } feature && !feature.IsAvailable(runtime))
            {
                return Gathered.Without(descriptor, HealthCheckOutcome.NotApplicable,
                    $"Needs {feature.Requirement}; this host runs {runtime.DescribeHost(feature.Package)}.", null, [], checkStarted);
            }

            HealthSqlRunner sql = new(connection, sessionOptions, observer);
            HealthCheckContext context = new(resolved, value, runtime, scope.ServiceProvider, sql, now, memo, sweepBudget.Token);

            using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(sweepBudget.Token);
            budget.CancelAfter(statementTimeout + BudgetMargin);

            try
            {
                object facts = await descriptor.Check.GatherAsync(context, budget.Token).ConfigureAwait(false);

                // Checked whole before any of it is kept, so a check that breaks the rule leaves no part behind.
                List<HealthSamplePart> parts = [.. descriptor.Check.Sample(facts)];
                HashSet<string> own = new(StringComparer.Ordinal);
                List<string> twice = [.. parts.Select(static x => x.Series).Where(series => sample.ContainsKey(series) || !own.Add(series))];

                if (twice.Count > 0)
                {
                    // The log names each series' family and how many, never a qualifier: a qualifier can be a projection, a
                    // tenant, or another database's replication slot (R01), and a log line is read by more people than the page.
                    throw new HealthSweepRuleException(
                        $"{info.Code} wrote a health series that was already written in this sweep; a series has one writer.",
                        $"{info.Code} wrote {twice.Count} health series already written in this sweep, of "
                        + string.Join(", ", twice.Select(SeriesFamily).Distinct(StringComparer.Ordinal))
                        + "; a series has one writer, and the second is this one.");
                }

                foreach (HealthSamplePart part in parts)
                {
                    sample[part.Series] = part.Value;

                    // The writer's gate, or a stricter one the part names - never a looser one.
                    sampleGates[part.Series] = part.Gate > info.Gate ? part.Gate : info.Gate;
                }

                return new Gathered(descriptor, facts, HealthCheckOutcome.Checked, null, null, sql.Statements, checkStarted);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                (HealthCheckOutcome outcome, HealthCheckProblem problem) = Classify(exception, budget.IsCancellationRequested);
                Report(info, outcome, exception, storeKey, databaseId);

                return Gathered.Without(descriptor, outcome, null, problem, sql.Statements, checkStarted);
            }
        }
    }

    /// <summary>Judges one check's facts, and stamps what it found.</summary>
    private HealthCheckResult Evaluate(Gathered gathered, HealthEvaluation evaluation, string storeKey, string databaseId)
    {
        HealthCheckInfo info = gathered.Descriptor.Info;

        if (gathered.Outcome != HealthCheckOutcome.Checked || gathered.Facts is null)
        {
            return new HealthCheckResult(info, gathered.Outcome, [], gathered.Note, gathered.Problem, gathered.Statements, gathered.Elapsed);
        }

        try
        {
            HealthVerdict verdict = gathered.Descriptor.Check.Evaluate(gathered.Facts, evaluation);

            // A key holds the finding's Subject, and a label can hold a name: both sit at the finding's gate, which can
            // be stricter than the check's that the status row is shown at. So the page's sentence names the code and
            // a count, and only the log's names what was duplicated.
            if (verdict.Findings.GroupBy(static x => x.Key, StringComparer.Ordinal).Where(static x => x.Count() > 1).ToList() is { Count: > 0 } shared)
            {
                throw new HealthSweepRuleException(
                    $"{shared.Sum(static x => x.Count())} of {info.Code}'s {verdict.Findings.Count} findings share a key with another; each finding needs its own Subject.",
                    $"{info.Code}'s evaluator produced more than one finding keyed '{shared[0].Key}'; give each its own Subject.");
            }

            List<HealthFinding> findings = [];
            foreach (HealthFinding finding in verdict.Findings)
            {
                if (!string.Equals(finding.Code, info.Code, StringComparison.Ordinal))
                {
                    throw new HealthSweepRuleException(
                        $"{info.Code}'s evaluator produced a finding coded {finding.Code}; a check speaks only for its own code.",
                        $"{info.Code}'s evaluator produced a finding coded {finding.Code}; a check speaks only for its own code.");
                }

                if (finding.Evidence.GroupBy(static x => x.Label, StringComparer.Ordinal).FirstOrDefault(static x => x.Count() > 1) is { } twice)
                {
                    throw new HealthSweepRuleException(
                        $"One of {info.Code}'s findings has {twice.Count()} pieces of evidence under one label; each label is one row.",
                        $"{info.Code}'s finding '{finding.Key}' has two pieces of evidence labelled '{twice.Key}'; each label is one row.");
                }

                if (finding.DocumentTypes.Any(type => options.Value.IsDocumentTypeVisible?.Invoke(type) == false))
                {
                    continue;
                }

                findings.Add(finding with
                {
                    Area = info.Area,
                    Gate = info.Gate,
                    CheckTitle = info.Title,
                    MeasuredAt = evaluation.Now,
                    Statements = gathered.Statements,
                });
            }

            return new HealthCheckResult(info, HealthCheckOutcome.Checked, findings, verdict.Note, null, gathered.Statements, gathered.Elapsed);
        }
        catch (Exception exception)
        {
            // An evaluator is pure, so a throw is a bug in the studio - and still only this check's row.
            Report(info, HealthCheckOutcome.Failed, exception, storeKey, databaseId);

            HealthCheckProblem problem = exception is HealthSweepRuleException rule
                ? new HealthCheckProblem("The check's verdict was refused: " + rule.Shown, null, null)
                : new HealthCheckProblem("The check's judgement failed: " + exception.GetType().Name + ". " + MessageIsLogged, null, null);

            return new HealthCheckResult(info, HealthCheckOutcome.Failed, [], null, problem, gathered.Statements, gathered.Elapsed);
        }
    }

    /// <summary>
    /// A series' family - <c>replication.slot-idle-retained:…</c> for <c>replication.slot-idle-retained:debezium</c> - which is
    /// what a log line may name of it: the part before the qualifier.
    /// </summary>
    internal static string SeriesFamily(string series)
    {
        ArgumentNullException.ThrowIfNull(series);

        int colon = series.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? series[..(colon + 1)] + "…" : series;
    }

    /// <summary>
    /// What a status row says in place of an exception's own message, which is not the studio's and is not shown.
    /// </summary>
    internal const string MessageIsLogged =
        "Its message is in the server log under event 9222 rather than here: it is not the studio's own wording, and "
        + "could name something this page withholds.";

    /// <summary>What a check whose turn came after the sweep's budget ran out is told.</summary>
    private static readonly HealthCheckProblem OutOfSweepBudget = new(
        "The sweep ran out of its budget - one HealthInterval - before this check's turn, so it was not started; it is tried again at the next interval.",
        null,
        null,
        UnavailableReason: HealthUnavailableReason.SweepBudget);

    /// <summary>Which outcome an exception is, and the problem a visitor is shown.</summary>
    internal static (HealthCheckOutcome Outcome, HealthCheckProblem Problem) Classify(Exception exception, bool budgetExpired)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is HealthCheckUnavailableException unavailable)
        {
            // The check's own sentence, written for this row.
            return (HealthCheckOutcome.Unavailable, new HealthCheckProblem(
                unavailable.Message, null, null, UnavailableReason: HealthUnavailableReason.HostChoice));
        }

        if (exception is HealthSweepRuleException rule)
        {
            return (HealthCheckOutcome.Failed, new HealthCheckProblem(rule.Shown, null, null));
        }

        if (exception is OperationCanceledException && budgetExpired)
        {
            return (HealthCheckOutcome.Unavailable, new HealthCheckProblem(
                "The check took longer than its budget and was stopped; it is tried again at the next interval.", null, null,
                UnavailableReason: HealthUnavailableReason.TimedOut));
        }

        PostgresException? postgres = exception as PostgresException ?? exception.InnerException as PostgresException;

        if (postgres is not null)
        {
            string? expected = postgres.SqlState switch
            {
                PostgresErrorCodes.InsufficientPrivilege =>
                    "The role the studio reads as may not read what this check needs, so it was not checked.",
                PostgresErrorCodes.QueryCanceled =>
                    "The check's statement ran out of its statement_timeout; it is tried again at the next interval.",
                PostgresErrorCodes.LockNotAvailable =>
                    "The check's statement waited too long for a lock somebody else holds; it is tried again at the next interval.",
                _ => null,
            };

            return expected is not null
                ? (HealthCheckOutcome.Unavailable, new HealthCheckProblem(
                    expected,
                    postgres.SqlState,
                    postgres.MessageText,
                    UnavailableReason: postgres.SqlState == PostgresErrorCodes.InsufficientPrivilege
                        ? HealthUnavailableReason.HostChoice
                        : HealthUnavailableReason.TimedOut))
                : (HealthCheckOutcome.Failed, new HealthCheckProblem(
                    "Postgres refused the check's statement (SQLSTATE " + postgres.SqlState + ").", postgres.SqlState, postgres.MessageText));
        }

        if (PostgresFailure.IsTimeout(exception))
        {
            return (HealthCheckOutcome.Unavailable, new HealthCheckProblem(
                "The check's statement timed out; it is tried again at the next interval.", null, null,
                UnavailableReason: HealthUnavailableReason.TimedOut));
        }

        if (PostgresFailure.IsConnectionFailure(exception))
        {
            // Npgsql's or the socket's words, not Postgres': carried apart, and shown at the database-wide gate.
            return (HealthCheckOutcome.Failed, new HealthCheckProblem(
                "The studio could not connect to this database.", null, null, exception.Message));
        }

        // Anything else is a bug in a check or a library, in words the studio did not write: the type is shown, and
        // the message is in the log.
        return (HealthCheckOutcome.Failed, new HealthCheckProblem(
            "The check failed: " + exception.GetType().Name + ". " + MessageIsLogged, null, null));
    }

    private void Report(HealthCheckInfo info, HealthCheckOutcome outcome, Exception exception, string storeKey, string databaseId)
    {
        if (outcome != HealthCheckOutcome.Failed)
        {
            // An expected state: the page says so, and the log only for whoever turned Debug on.
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "Marten Studio's health check {Code} was not checked on {DatabaseId}", info.Code, databaseId);
            }

            return;
        }

        // A database that will not take a connection is one problem, told once - not once per check.
        string site = PostgresFailure.IsConnectionFailure(exception) ? "Health.Connection" : "Health.Check." + info.Code;
        LogLevel level = throttle.WarningOrDebug(site, storeKey, databaseId, StudioLogThrottle.KindOf(exception));
        logger.HealthCheckFailed(level, exception, info.Code, storeKey, databaseId);
    }

    /// <summary>What one gatherer left for the evaluation phase.</summary>
    private sealed record Gathered(
        HealthCheckDescriptor Descriptor,
        object? Facts,
        HealthCheckOutcome Outcome,
        string? Note,
        HealthCheckProblem? Problem,
        IReadOnlyList<HealthStatement> Statements,
        long Started)
    {
        public TimeSpan Elapsed { get; } = Stopwatch.GetElapsedTime(Started);

        public static Gathered Without(
            HealthCheckDescriptor descriptor,
            HealthCheckOutcome outcome,
            string? note,
            HealthCheckProblem? problem,
            IReadOnlyList<HealthStatement> statements,
            long started) =>
            new(descriptor, null, outcome, note, problem, statements, started);
    }
}
