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
/// <param name="Reason">The studio's own sentence.</param>
/// <param name="SqlState">The SQLSTATE, when Postgres said something.</param>
/// <param name="PostgresText">
/// What Postgres (or Npgsql) said, verbatim. Never shown as it is: the report masks every withheld schema's
/// name in it for the visitor reading (D27), or withholds it outright when that cannot be decided.
/// </param>
internal sealed record HealthCheckProblem(string Reason, string? SqlState, string? PostgresText);

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
/// the render (issue #1 was the same shape) - so such a verdict is that check's failure, with a message that
/// says what was duplicated.
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

        // Phase 1: every gatherer, in order. All of the sweep's I/O happens here.
        List<Gathered> gathered = [];
        Dictionary<string, double> sample = new(StringComparer.Ordinal);

        foreach (HealthCheckDescriptor descriptor in registry.All.Where(x => tiers.Contains(x.Info.Tier)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            gathered.Add(sweepBudget.IsCancellationRequested
                ? Gathered.Without(descriptor, HealthCheckOutcome.Unavailable, null, OutOfSweepBudget, [], Stopwatch.GetTimestamp())
                : await GatherAsync(descriptor).ConfigureAwait(false));
        }

        // Phase 2: one sample, from every check that read something - regular sweeps only, so that nothing a
        // gated tier reads becomes history an ungated evaluator can see.
        if (kind == HealthSweepKind.Regular)
        {
            history.Append(storeKey, databaseId, new HealthSample(now, sample), value.EffectiveHealthInterval / 2);
        }

        HealthHistoryView view = history.Read(storeKey, databaseId);

        // Phase 3: every evaluator, pure, with the history in hand.
        HealthEvaluation evaluation = new(now, runtime, view);
        List<HealthCheckResult> results = [.. gathered.Select(x => Evaluate(x, evaluation, storeKey, databaseId))];

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

                foreach (HealthSamplePart part in descriptor.Check.Sample(facts))
                {
                    if (!sample.TryAdd(part.Series, part.Value))
                    {
                        throw new InvalidOperationException(
                            $"Health series '{part.Series}' was written twice in one sweep; {info.Code} is the second writer.");
                    }
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

            HashSet<string> keys = new(StringComparer.Ordinal);
            List<HealthFinding> findings = [];
            foreach (HealthFinding finding in verdict.Findings)
            {
                if (!string.Equals(finding.Code, info.Code, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"{info.Code}'s evaluator produced a finding coded {finding.Code}; a check speaks only for its own code.");
                }

                if (!keys.Add(finding.Key))
                {
                    throw new InvalidOperationException(
                        $"{info.Code}'s evaluator produced two findings keyed '{finding.Key}'; give each its own Subject.");
                }

                if (finding.Evidence.GroupBy(static x => x.Label, StringComparer.Ordinal).FirstOrDefault(static x => x.Count() > 1) is { } twice)
                {
                    throw new InvalidOperationException(
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

            return new HealthCheckResult(
                info,
                HealthCheckOutcome.Failed,
                [],
                null,
                new HealthCheckProblem("The check's judgement failed: " + exception.GetType().Name + ".", null, exception.Message),
                gathered.Statements,
                gathered.Elapsed);
        }
    }

    /// <summary>What a check whose turn came after the sweep's budget ran out is told.</summary>
    private static readonly HealthCheckProblem OutOfSweepBudget = new(
        "The sweep ran out of its budget - one HealthInterval - before this check's turn, so it was not started; it is tried again at the next interval.",
        null,
        null);

    /// <summary>Which outcome an exception is, and the problem a visitor is shown.</summary>
    internal static (HealthCheckOutcome Outcome, HealthCheckProblem Problem) Classify(Exception exception, bool budgetExpired)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is HealthCheckUnavailableException unavailable)
        {
            return (HealthCheckOutcome.Unavailable, new HealthCheckProblem(unavailable.Message, null, null));
        }

        if (exception is OperationCanceledException && budgetExpired)
        {
            return (HealthCheckOutcome.Unavailable, new HealthCheckProblem(
                "The check took longer than its budget and was stopped; it is tried again at the next interval.", null, null));
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
                ? (HealthCheckOutcome.Unavailable, new HealthCheckProblem(expected, postgres.SqlState, postgres.MessageText))
                : (HealthCheckOutcome.Failed, new HealthCheckProblem(
                    "Postgres refused the check's statement (SQLSTATE " + postgres.SqlState + ").", postgres.SqlState, postgres.MessageText));
        }

        if (PostgresFailure.IsTimeout(exception))
        {
            return (HealthCheckOutcome.Unavailable, new HealthCheckProblem(
                "The check's statement timed out; it is tried again at the next interval.", null, null));
        }

        if (PostgresFailure.IsConnectionFailure(exception))
        {
            return (HealthCheckOutcome.Failed, new HealthCheckProblem(
                "The studio could not connect to this database.", null, exception.Message));
        }

        return (HealthCheckOutcome.Failed, new HealthCheckProblem(
            "The check failed: " + exception.GetType().Name + ".", null, exception.Message));
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
