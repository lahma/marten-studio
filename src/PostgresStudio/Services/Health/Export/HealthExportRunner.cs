using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PostgresStudio.Services.Health.Export;

/// <summary>How much one export run may do.</summary>
/// <param name="MaxDatabases">The most databases one run checks; the rest are counted as skipped.</param>
/// <param name="Budget">
/// How long one run may take in all, or <see langword="null" /> for <see cref="BudgetFor" />'s default. A
/// database the run has not finished when it runs out is counted as timed out, which reports Degraded: a
/// database that cannot be checked in this long is worth somebody's look.
/// </param>
internal sealed record HealthExportLimits(int MaxDatabases, TimeSpan? Budget = null)
{
    /// <summary>The least a run is given by default, however short the interval.</summary>
    internal static readonly TimeSpan MinimumBudget = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Twenty-five databases, and the default budget. A run is sequential - one database, one connection, one
    /// statement at a time, a guest on somebody's production server - so the cap is what keeps a
    /// database-per-tenant host with hundreds of tenants from turning one probe into hundreds of sweeps.
    /// </summary>
    public static HealthExportLimits Default { get; } = new(25);

    /// <summary>
    /// The run's budget: <see cref="Budget" /> when one was given, otherwise the larger of two minutes and one
    /// <paramref name="interval" />.
    /// </summary>
    /// <remarks>
    /// Never less than one interval by default, because each sweep has a budget of its own of exactly one
    /// interval (<see cref="HealthSweeper" />) and lists the checks it had no time for as not checked. A run
    /// budget shorter than that would cut short a sweep the Health page lets finish, and call a database Degraded
    /// that the page shows nothing wrong with.
    /// </remarks>
    /// <param name="interval">The advisor's cadence, <c>EffectiveHealthInterval</c>.</param>
    public TimeSpan BudgetFor(TimeSpan interval) =>
        Budget ?? (interval > MinimumBudget ? interval : MinimumBudget);
}

/// <summary>
/// The export's one run per interval: every store and database the studio would list, swept at tiers 0 and 1
/// through <see cref="HealthSweeper" />, summarised as codes and counts, and served to every probe.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered only by <c>AddHealthChecks().AddMartenStudio()</c></b>, never by <c>AddMartenStudio()</c>,
/// and it runs nothing on its own: no timer, no hosted service. A run starts when a probe asks and there is no
/// result yet, or the one there is has been held for an interval.
/// </para>
/// <para>
/// <b>A probe waits only for a first or a long-stale result</b> (H8-fix F1). Once a run has completed, every probe
/// is answered with the latest result at once - a health endpoint that makes its caller wait out a sweep of
/// twenty-five databases is a health endpoint an orchestrator times out - and a probe that finds that result
/// older than an interval starts the next run as it goes, for the probes after it. A result older than two
/// intervals is not served at all (H8-fix2): the probe that finds it waits for a fresh run, as the first probe
/// does, bounded by the registration's <c>timeout</c>. So a probe after a long quiet spell is never told about
/// a database as it was an hour ago. Ages are counted from when a run <em>finished</em>; the data's
/// <c>measuredAt</c> says when it began.
/// </para>
/// <para>
/// <b>Single flight, by construction.</b> There is one run at a time, held as a task under a lock: a probe that
/// arrives while the first run is in flight waits for that one, however long it takes - a slow run is never
/// joined by a second. The run belongs to no probe: it is started on the thread pool under no caller's
/// cancellation and with the probe's execution context suppressed (F6), so it is not traced as part of the first
/// request that happened to arrive, and a probe that gives up cancels only its own wait. It ends when it ends,
/// when its budget runs out, or when the application stops (F7) - and in every case it leaves its result and an
/// empty slot behind, so a stopping host is never left holding a run that will not finish.
/// </para>
/// <para>
/// <b>The sweeper directly, never a visitor's cache.</b> <see cref="HealthSweepCache" /> is the pages' - keyed by
/// what a visitor resolved, counted by a test that proves a studio nobody has open sweeps nothing - and the
/// export neither fills it nor reads it. Its sweeps are ordinary regular sweeps all the same, so they add
/// samples to <see cref="HealthHistory" /> like a page's do.
/// </para>
/// <para>
/// <b>No visitor, so no authorization - and so nothing that would need one.</b> A store and database are resolved
/// here without asking a policy, which is safe only because what leaves this class is
/// <see cref="HealthExportSummary" />: codes and counts, built from nothing a finding wrote.
/// </para>
/// <para>
/// <b>The databases are the ones the sources already hold</b> (<see cref="IStudioSourceProvider.SweepTargetsAsync" />;
/// MartenStudio's are the ones each tenancy holds), so the export's own listing never makes a master-table, sharded or
/// single-server tenancy apply, seed, query or create anything. The provider is a circuit's service, so a run resolves it
/// from a scope of its own and disposes the scope when it ends.
/// One thing inside the sweep still enumerates, and it is Marten's: with a projection coordinator hosted in this
/// process, the daemon checks ask that coordinator for its daemons (<c>AllDaemonsAsync()</c>, once per sweep),
/// which enumerates the store as the coordinator's own leadership loop does on every cycle. A probe can be the
/// first enumerator only in the moment before that loop's first cycle - and always under
/// <c>MartenDaemonModeIsSolo()</c>, a testing coordinator that has no loop.
/// </para>
/// <para>
/// <b>The studio's own services are looked up lazily</b>, from the root container, so a host that added the
/// health check but not <c>AddMartenStudio()</c> gets a Degraded result that says so, rather than a probe that
/// throws.
/// </para>
/// </remarks>
internal sealed class HealthExportRunner
{
    /// <summary>The cadence when the studio's own options cannot be read: <c>HealthInterval</c>'s default.</summary>
    private static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider services;
    private readonly TimeProvider timeProvider;
    private readonly HealthExportLimits limits;
    private readonly ILogger logger;
    private readonly StudioLogThrottle? throttle;
    private readonly CancellationToken stopping;
    private readonly StudioText registeredText;
    private readonly Lock gate = new();

    private Task<HealthExportSummary>? running;
    private HealthExportSummary? completed;
    private DateTimeOffset completedAt;
    private int runCount;

    /// <param name="services">The root container, read lazily for the studio's own services.</param>
    /// <param name="timeProvider">The host's clock, or the system's.</param>
    /// <param name="limits">How much a run may do; <see cref="HealthExportLimits.Default" /> unless a test says otherwise.</param>
    /// <param name="logger">Where a failure of the export itself goes.</param>
    /// <param name="throttle">The studio's log throttle, which <c>AddMartenStudio()</c> registers.</param>
    /// <param name="lifetime">The host's lifetime, whose stopping ends a run in flight.</param>
    /// <param name="registeredText">
    /// The words of the studio whose <c>AddHealthChecks().Add*Studio()</c> registered this runner, for the one sentence
    /// said when that studio's own services are missing: MartenStudio's when <see langword="null" />, which is what its
    /// registration leaves it. A studio that registers the runner itself hands its own.
    /// </param>
    public HealthExportRunner(
        IServiceProvider services,
        TimeProvider? timeProvider = null,
        HealthExportLimits? limits = null,
        ILogger<HealthExportRunner>? logger = null,
        StudioLogThrottle? throttle = null,
        IHostApplicationLifetime? lifetime = null,
        StudioText? registeredText = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        this.registeredText = registeredText ?? StudioText.Marten;
        this.services = services;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.limits = limits ?? HealthExportLimits.Default;
        this.logger = (ILogger?) logger ?? NullLogger.Instance;
        this.throttle = throttle;
        stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;
    }

    /// <summary>How many runs have started, for a test and for nothing else.</summary>
    internal int RunCount => Volatile.Read(ref runCount);

    /// <summary>
    /// The words the export's own sentences and its 9270 are in: the studio's settings' when they are registered, and
    /// otherwise those of the studio whose health-check registration made this runner (<c>registeredText</c>) - which
    /// is the only one that can name the <c>Add*Studio()</c> call a host forgot.
    /// </summary>
    internal StudioText Text
    {
        get
        {
            try
            {
                return services.GetService<IStudioSettings>()?.Text ?? registeredText;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Options that fail validation are the run's to report; the words do not depend on them.
                return registeredText;
            }
        }
    }

    /// <summary>
    /// How long a result is held before a probe starts the next run: the studio's <c>EffectiveHealthInterval</c>,
    /// the advisor's own cadence, or a minute when the studio's options cannot be read.
    /// </summary>
    public TimeSpan Interval
    {
        get
        {
            try
            {
                return services.GetService<IStudioSettings>()?.EffectiveHealthInterval ?? FallbackInterval;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Options that fail validation are the run's to report; the cache still needs a cadence.
                return FallbackInterval;
            }
        }
    }

    /// <summary>
    /// The latest result, at once, while it is under two intervals old - starting the next run once it is one
    /// interval old - and otherwise the run in flight, started now if there is none. Never throws but for
    /// cancellation of this caller's wait.
    /// </summary>
    /// <param name="cancellationToken">Cancels this caller's wait, never the run.</param>
    public async Task<HealthExportSummary> GetAsync(CancellationToken cancellationToken = default)
    {
        TimeSpan interval = Interval;
        Task<HealthExportSummary> work;

        lock (gate)
        {
            TimeSpan age = timeProvider.GetUtcNow() - completedAt;

            // Served at once while it is under two intervals old - or whatever its age once the application is
            // stopping, since no run starts then.
            if (completed is not null && (age < interval * 2 || stopping.IsCancellationRequested))
            {
                if (running is null && age >= interval && !stopping.IsCancellationRequested)
                {
                    running = Start();
                }

                return completed;
            }

            // No result, or one too old to tell anybody about: wait for the run in flight, or a new one.
            running ??= Start();
            work = running;
        }

        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The run in flight, or a completed task when there is none - for a test that waits for a refresh.</summary>
    internal Task WhenIdleAsync()
    {
        lock (gate)
        {
            return running ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// Logs a failure of the export itself as event 9270: a Warning once per kind of failure in ten minutes, and
    /// Debug in between, because an orchestrator probes every few seconds.
    /// </summary>
    internal void ReportFailure(Exception exception)
    {
        LogLevel level = StudioLogThrottle.LevelOrWarning(throttle, "Health.Export", null, null, StudioLogThrottle.KindOf(exception));

        if (Text.Product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioHealthExportFailed(level, exception);
        }
        else
        {
            logger.HealthExportFailed(level, exception);
        }
    }

    /// <summary>
    /// Starts a run on the thread pool, with the caller's execution context suppressed so that the run is no
    /// request's child. Called under the lock; the run takes the lock again only once it has finished.
    /// </summary>
    private Task<HealthExportSummary> Start()
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(() => RunAndKeepAsync(), CancellationToken.None);
        }
    }

    /// <summary>One run, kept as the latest result whatever it found. Never throws.</summary>
    private async Task<HealthExportSummary> RunAndKeepAsync()
    {
        Interlocked.Increment(ref runCount);

        DateTimeOffset started = timeProvider.GetUtcNow();
        HealthExportSummary summary;

        try
        {
            summary = await RunAsync(started).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A fault in the studio itself. The endpoint is told the exception's type and nothing it said: its
            // message could name a schema, a table or a connection, and the endpoint has no visitor to ask.
            ReportFailure(exception);
            summary = HealthExportSummary.Broken(started, exception.GetType().Name, Text);
        }

        lock (gate)
        {
            completed = summary;
            completedAt = timeProvider.GetUtcNow();
            running = null;
        }

        return summary;
    }

    /// <summary>Every source, every database it already holds, one regular sweep each, in order.</summary>
    private async Task<HealthExportSummary> RunAsync(DateTimeOffset started)
    {
        HealthSweeper? sweeper = services.GetService<HealthSweeper>();
        IStudioSettings? settings = services.GetService<IStudioSettings>();

        if (sweeper is null || settings is null)
        {
            return HealthExportSummary.NotRegistered(started, Text);
        }

        // The source provider is a circuit's service - it caches discovered tenants per circuit - and a run has no circuit,
        // so it gets a scope of its own, disposed when the run ends, and never the root container's.
        await using AsyncServiceScope scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();

        if (scope.ServiceProvider.GetService<IStudioSourceProvider>() is not { } sources)
        {
            return HealthExportSummary.NotRegistered(started, settings.Text);
        }

        if (sources.Registrations().Count == 0)
        {
            return HealthExportSummary.NoStores(started, settings.Text);
        }

        // The run's own budget, on top of each sweep's (one interval each), so that a store of many slow databases
        // cannot hold the one run every probe is answered from for ever - and the host's stopping, so that it
        // cannot outlive the application either.
        using CancellationTokenSource timer = new(limits.BudgetFor(settings.EffectiveHealthInterval), timeProvider);
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, stopping);

        List<HealthSweep> sweeps = [];
        int attempted = 0;
        int skipped = 0;
        int timedOut = 0;
        int notReached = 0;
        int notEnumerated = 0;
        int unnamed = 0;

        // Streamed, and never cancelled by the budget: the enumeration goes on past the budget and the cap only to count
        // what was left out, and it names databases, never connects to them.
        await foreach (StudioSweepTarget target in sources.SweepTargetsAsync(CancellationToken.None).ConfigureAwait(false))
        {
            switch (target.Outcome)
            {
                case StudioSweepTargetOutcome.SourceNotReached:
                    // The source has logged why, throttled (9210 or 9213); the endpoint says only that a store was not reached.
                    notReached++;
                    break;

                case StudioSweepTargetOutcome.Unnamed:
                    // Held, but under an id the tenancy would correct before it looked in its cache: not looked up.
                    unnamed += target.Count;
                    break;

                case StudioSweepTargetOutcome.SourceNotEnumerated:
                    // An expected state, and the host's own: its tenancy holds no database in this process yet, or is one
                    // the studio cannot ask without making it build.
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug(
                            "Marten Studio's health export left store {StoreKey} out: its tenancy holds no database it can name without building one ({Shape})",
                            target.Source.Key,
                            target.Detail);
                    }

                    notEnumerated++;
                    break;

                case StudioSweepTargetOutcome.Database when target.Resolved is { } resolved:
                    if (attempted >= limits.MaxDatabases)
                    {
                        skipped++;
                        break;
                    }

                    if (budget.IsCancellationRequested)
                    {
                        timedOut++;
                        break;
                    }

                    attempted++;

                    try
                    {
                        sweeps.Add(await sweeper.SweepAsync(resolved, HealthSweepKind.Regular, budget.Token).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (budget.IsCancellationRequested)
                    {
                        timedOut++;
                    }

                    break;
            }
        }

        // A run the application's stopping cut short is said as that, not as databases that were slow.
        return stopping.IsCancellationRequested
            ? HealthExportSummary.Stopping(started, settings.Text)
            : HealthExportSummary.From(started, sweeps, skipped, timedOut, notReached, notEnumerated, unnamed);
    }
}
