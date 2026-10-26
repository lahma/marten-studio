using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Statements;

/// <summary>One statement's counters over a span: what <c>pg_stat_statements</c> counts that the Statements page shows.</summary>
/// <param name="Calls">Calls.</param>
/// <param name="ExecMs">Execution time, in milliseconds.</param>
/// <param name="Rows">Rows returned or affected.</param>
/// <param name="SharedBlocksHit">Shared buffers found in the cache.</param>
/// <param name="SharedBlocksRead">Shared blocks read from outside the cache.</param>
/// <param name="TempBlocksWritten">Blocks spilled to temporary files.</param>
/// <param name="IoReadMs">Time reading shared blocks, in milliseconds (zero while <c>track_io_timing</c> is off).</param>
/// <param name="StorageBlocksRead">Aurora's storage-level reads (issue #8 A5); <see langword="null" /> where they are not read.</param>
internal sealed record StatementCounts(
    double Calls,
    double ExecMs,
    double Rows,
    double SharedBlocksHit,
    double SharedBlocksRead,
    double TempBlocksWritten,
    double IoReadMs,
    double? StorageBlocksRead)
{
    /// <summary>The mean execution time a call, in milliseconds; <see langword="null" /> with no calls.</summary>
    public double? MeanMs => Calls > 0 ? ExecMs / Calls : null;

    /// <summary>The counters since the statement's counters began, as read.</summary>
    public static StatementCounts Of(StatementCounterRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new StatementCounts(
            row.Calls, row.TotalExecMs, row.Rows, row.SharedBlocksHit, row.SharedBlocksRead, row.TempBlocksWritten, row.IoReadMs, row.StorageBlocksRead);
    }
}

/// <summary>One statement of the Statements page's facts - nothing in it yet shown to anybody.</summary>
internal sealed record StatementFact
{
    /// <summary><c>queryid</c>.</summary>
    public required long QueryId { get; init; }

    /// <summary>
    /// Its normalised text with every comment masked (<see cref="StatementReads.MaskComments" />) - or <see langword="null" />
    /// when it was not read, or is withheld (<see cref="TextWithheld" />). Still to be masked for its visitor.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>Whether the read cut the text at <see cref="HealthStatementQueries.MaxTextCharacters" />.</summary>
    public bool TextTruncated { get; init; }

    /// <summary>
    /// Why the text is never shown - it still carries a string constant as written, or is not a plain query or write (T02's
    /// rule, <see cref="StatementReads.Withheld" />) - or <see langword="null" />.
    /// </summary>
    public string? TextWithheld { get; init; }

    /// <summary>Whether the text call returned its text at all.</summary>
    public bool TextRead { get; init; }

    /// <summary>Its counters since they began: the entry's own <c>stats_since</c> on 17+, the extension's <c>stats_reset</c> before.</summary>
    public required StatementCounts SinceBegan { get; init; }

    /// <summary>
    /// Its counters over the facts' window: the change the statement history saw, or - for facts with no watched window -
    /// <see cref="SinceBegan" />. <see langword="null" /> when the window was watched and this statement was not: it is new to
    /// the statements the read keeps, and its counters before the window are unknown.
    /// </summary>
    public StatementCounts? Window { get; init; }

    /// <summary>
    /// When the statement's own part of a watched window began, when that was later than the window's start - an entry that
    /// was evicted and recreated, or that joined the kept statements during it - or <see langword="null" />.
    /// </summary>
    public DateTimeOffset? WatchedFrom { get; init; }

    /// <summary>When its counters began, as the extension knows it.</summary>
    public DateTimeOffset? CountersSince { get; init; }

    /// <summary>Its share of the window's execution time, 0 to 1; <see langword="null" /> without a window or a total.</summary>
    public double? Share { get; init; }

    /// <summary>Its group (<see cref="IStatementClassifier" />), or <see langword="null" /> when its text was not read.</summary>
    public StatementGroup? Group { get; init; }

    /// <summary>The judged tables it names - never a hidden document type's.</summary>
    public IReadOnlyList<StudioTable> Tables { get; init; } = [];

    /// <summary>Parallel workers its plans asked for, since its counters began (PostgreSQL 18+); <see langword="null" /> below.</summary>
    public long? ParallelWorkersToLaunch { get; init; }

    /// <summary>Parallel workers it was given, since its counters began (PostgreSQL 18+); <see langword="null" /> below.</summary>
    public long? ParallelWorkersLaunched { get; init; }
}

/// <summary>The page's own state of the read: <see cref="StatementReadState" />, and a read that failed for another reason.</summary>
internal enum StatementsState
{
    /// <summary>It read.</summary>
    Read,

    /// <summary>pg_stat_statements is not installed in this database.</summary>
    NotInstalled,

    /// <summary>It is older than 1.8.</summary>
    TooOld,

    /// <summary>Its library is not preloaded.</summary>
    NotPreloaded,

    /// <summary>It holds nothing of the reading role here yet.</summary>
    NothingRecorded,

    /// <summary>The read failed for a reason nobody chose; <see cref="StatementFacts.Failure" /> says what the sweep said.</summary>
    Failed,
}

/// <summary>
/// One database's statements as <c>pg_stat_statements</c> counts them over the advisor's window (W5-Q1, issue #8 G6) -
/// gate-free and the same for every visitor (W5-index §2.3): the facts half the Statements page and the public diagnostics
/// API both read.
/// </summary>
/// <remarks>
/// <para>
/// <b>The statement sweep's own read, never a second one.</b> The facts are the statement sweep's shared read
/// (<see cref="StatementReads.OutcomeAsync" />) and its history - the sweep T02, T22 and T23 run in, single flight per
/// database and kept for five minutes (<see cref="HealthSweepCache.DeeperTimeToLive" />) - so a Statements page open beside
/// the Health page's statement checks costs the database nothing more, and the D29 fence is the read's: as the store's own
/// role, <c>userid = current_user</c> and this database.
/// </para>
/// <para>
/// <b>The window.</b> Once the statement history holds two samples of the database's total, the window is that run
/// (<see cref="StatementWindows.AllExecSeries" />): every statement's counters are its change over it, and its share is of the
/// change in the total. Before that, every statement's counters are its totals since they began, labelled so, and its share
/// is of the extension's total. The statistics window (W5-CAP) bounds the run: after a crash, a restore, a failover or another
/// instance answering, the samples before it are another server's counters, and are not part of the window.
/// </para>
/// <para>
/// <b>Nothing here has been shown to anybody.</b> A text is comment-masked and T02's withholding is applied, but it is still
/// another program's text: masking it for a visitor, the activity gate and naming a table in a schema they may not see are
/// <c>StatementsService</c>'s to decide.
/// </para>
/// </remarks>
internal sealed record StatementFacts
{
    /// <summary>The source, by its registration key.</summary>
    public required string StoreKey { get; init; }

    /// <summary>The database, by its identity.</summary>
    public required string DatabaseId { get; init; }

    /// <summary>When the statement sweep read them, by the studio's clock.</summary>
    public required DateTimeOffset MeasuredAt { get; init; }

    /// <summary>Whether it read.</summary>
    public required StatementsState State { get; init; }

    /// <summary>The extension's installed version, when it is installed.</summary>
    public string? ExtensionVersion { get; init; }

    /// <summary>Who runs the server, when the sweep could read it.</summary>
    public PostgresPlatform? Platform { get; init; }

    /// <summary>For <see cref="StatementsState.Failed" />: the statement checks' own sentence for the failure.</summary>
    public string? Failure { get; init; }

    /// <summary>Whether the window is one the statement history watched, rather than the counters' whole life.</summary>
    public bool Watched { get; init; }

    /// <summary>When the watched window began, or - when it was not watched - when the extension's counters began, if it says.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>When the watched window ended: the latest sample.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary><c>pg_stat_statements_info.stats_reset</c> (14+).</summary>
    public DateTimeOffset? StatsReset { get; init; }

    /// <summary>Every statement's execution time over the window, which <see cref="StatementFact.Share" /> divides.</summary>
    public double TotalExecMs { get; init; }

    /// <summary>How many statements of the reading role in this database the extension holds.</summary>
    public long AllStatements { get; init; }

    /// <summary>Entries the extension evicted in the window (or since its reset, before a window), when it says.</summary>
    public double? Evicted { get; init; }

    /// <summary>Whether Aurora's storage-level reads were joined, so the page may rank by them (A5).</summary>
    public bool StorageReads { get; init; }

    /// <summary>The statistics window (W5-CAP) the statement history is bounded by, when the sweep read its signals.</summary>
    public StatisticsWindow? Statistics { get; init; }

    /// <summary>Every statement the read kept, the most execution time over the window first.</summary>
    public IReadOnlyList<StatementFact> Statements { get; init; } = [];

    /// <summary>What the read ran, for "Show SQL".</summary>
    public IReadOnlyList<HealthStatement> Ran { get; init; } = [];

    /// <summary>The span the window covers, when it was watched or its start is known.</summary>
    public TimeSpan? Span => From is { } from && (To ?? MeasuredAt) is var to && to > from ? to - from : null;

    /// <summary>
    /// The facts a statement sweep's outcome and the statement history describe. Pure, for a test as for the reader.
    /// </summary>
    /// <param name="storeKey">The source's key.</param>
    /// <param name="databaseId">The database's identity.</param>
    /// <param name="measuredAt">When the sweep ran.</param>
    /// <param name="outcome">The read's outcome, or <see langword="null" /> when it failed for a reason nobody chose.</param>
    /// <param name="failure">The sweep's sentence for that failure, when there was one.</param>
    /// <param name="history">The statement history (its own ring).</param>
    /// <param name="statistics">The statistics window, when the sweep read its signals.</param>
    /// <param name="tables">The judged tables, for grouping; <see langword="null" /> when the sweep did not read them.</param>
    /// <param name="classifier">The studio's grouping, or <see langword="null" /> for none.</param>
    public static StatementFacts Build(
        string storeKey,
        string databaseId,
        DateTimeOffset measuredAt,
        StatementReadOutcome? outcome,
        string? failure,
        HealthHistoryView history,
        StatisticsWindow? statistics,
        IStudioTableSet? tables,
        IStatementClassifier? classifier)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (outcome is null)
        {
            return new StatementFacts
            {
                StoreKey = storeKey,
                DatabaseId = databaseId,
                MeasuredAt = measuredAt,
                State = StatementsState.Failed,
                Failure = failure,
                Statistics = statistics,
            };
        }

        StatementFacts facts = new()
        {
            StoreKey = storeKey,
            DatabaseId = databaseId,
            MeasuredAt = measuredAt,
            State = outcome.State switch
            {
                StatementReadState.NotInstalled => StatementsState.NotInstalled,
                StatementReadState.TooOld => StatementsState.TooOld,
                StatementReadState.NotPreloaded => StatementsState.NotPreloaded,
                StatementReadState.NothingRecorded => StatementsState.NothingRecorded,
                _ => StatementsState.Read,
            },
            ExtensionVersion = outcome.Extension.Version,
            Platform = outcome.Platform,
            Statistics = statistics,
        };

        if (outcome.Read is not { } read)
        {
            return facts;
        }

        HealthHistoryView window = Bounded(history, statistics);
        CounterDelta? total = TableHistory.Delta(window.Trend(StatementWindows.AllExecSeries));
        bool watched = total is { Change: >= 0 } && total.Span > TimeSpan.Zero;
        double totalExec = watched ? total!.Change : read.AllExecMs;

        List<StatementFact> statements = new(read.Statements.Count);

        foreach (ObservedStatement statement in read.Statements)
        {
            StatementCounts since = StatementCounts.Of(statement.Counters);
            (StatementCounts? counts, DateTimeOffset? from) = watched
                ? WindowOf(statement, since, window, total!, read.StorageReads)
                : (since, null);

            string? withheld = statement.TextWithheld;
            string? masked = statement.Text is null ? null : StatementReads.MaskComments(statement.Text);

            statements.Add(new StatementFact
            {
                QueryId = statement.QueryId,
                Text = withheld is null ? masked : null,
                TextTruncated = statement.TextTruncated,
                TextWithheld = withheld,
                TextRead = statement.Text is not null,
                SinceBegan = since,
                Window = counts,
                WatchedFrom = from,
                CountersSince = StatementWindows.Since(read, statement),
                Share = counts is not null && totalExec > 0 ? Math.Clamp(counts.ExecMs / totalExec, 0, 1) : null,
                Group = masked is not null && tables is not null && classifier is not null ? classifier.Classify(masked, tables) : null,
                Tables = statement.Tables,
                ParallelWorkersToLaunch = statement.Counters.ParallelWorkersToLaunch,
                ParallelWorkersLaunched = statement.Counters.ParallelWorkersLaunched,
            });
        }

        CounterDelta? evicted = watched ? TableHistory.Delta(window.Trend(StatementWindows.DeallocSeries)) : null;

        return facts with
        {
            Watched = watched,
            From = watched ? total!.From : read.StatsReset,
            To = watched ? total!.To : null,
            StatsReset = read.StatsReset,
            TotalExecMs = totalExec,
            AllStatements = read.AllStatements,
            Evicted = watched ? evicted?.Change : read.Dealloc,
            StorageReads = read.StorageReads,
            Statements =
            [
                .. statements
                    .OrderByDescending(static x => x.Window?.ExecMs ?? double.MinValue)
                    .ThenByDescending(static x => x.SinceBegan.ExecMs)
                    .ThenBy(static x => x.QueryId),
            ],
            Ran = read.Ran,
        };
    }

    /// <summary>
    /// The statement history from the statistics window's start, when the window says the server's counters began again then -
    /// a crash, a restore, a failover, another cluster or another instance - so a run never spans two servers' counters.
    /// </summary>
    internal static HealthHistoryView Bounded(HealthHistoryView history, StatisticsWindow? statistics)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (statistics is null || !RestartsTheCounters(statistics.Reason))
        {
            return history;
        }

        DateTimeOffset since = statistics.Since;
        return history with { Samples = [.. history.Samples.Where(x => x.At >= since)] };
    }

    /// <summary>
    /// Whether a reset of this kind also restarted <c>pg_stat_statements</c>' counters: everything but somebody's
    /// <c>pg_stat_reset()</c>, which the extension does not see, and no reset at all.
    /// </summary>
    internal static bool RestartsTheCounters(StatisticsResetReason reason) =>
        reason is not (StatisticsResetReason.None or StatisticsResetReason.ManualReset);

    /// <summary>
    /// One statement's counters over a watched window: the change over its own unbroken run - or, for an entry that began
    /// inside the window (its own <c>stats_since</c>, 17+), everything it counts - and when its run began, if later.
    /// </summary>
    private static (StatementCounts? Counts, DateTimeOffset? From) WindowOf(
        ObservedStatement statement,
        StatementCounts since,
        HealthHistoryView history,
        CounterDelta total,
        bool storageReads)
    {
        long id = statement.QueryId;

        if (statement.Counters.StatsSince is { } began && began >= total.From)
        {
            return (since, began > total.From ? began : null);
        }

        if (TableHistory.Delta(history.Trend(StatementReads.SeriesName(ExpensiveStatementsCheck.ExecSeries, id))) is not { } exec)
        {
            return (null, null);
        }

        double Change(string prefix) => TableHistory.Delta(history.Trend(StatementReads.SeriesName(prefix, id)))?.Change ?? 0;

        StatementCounts counts = new(
            Change(ExpensiveStatementsCheck.CallsSeries),
            exec.Change,
            Change(ExpensiveStatementsCheck.RowsSeries),
            Change(StatementReads.SharedHitSeries),
            Change(StatementReads.SharedReadSeries),
            Change(StatementReads.TempSeries),
            Change(StatementReads.IoReadSeries),
            storageReads ? Change(StatementReads.StorageReadSeries) : null);

        return (counts, exec.From > total.From ? exec.From : null);
    }
}

/// <summary>The Statements page's facts read: one database's statements, gate-free (W5-index §2.3).</summary>
/// <remarks>An interface, so a page or service test hands the facts over without Postgres (D12), and the diagnostics API reads the same.</remarks>
internal interface IStatementFacts
{
    /// <summary>
    /// The facts of the database <paramref name="resolved" /> names: the statement sweep's read (run now if the cached one has
    /// expired, joined if one is running). Gate-free: the caller has resolved the scope and passed the activity gate - or is
    /// the unattended path, whose host gate is its own.
    /// </summary>
    /// <param name="resolved">The database, resolved with no tenant.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared sweep.</param>
    Task<StatementFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IStatementFacts" /> over the statement sweep: single flight and cached for five minutes because the sweep is
/// (<see cref="HealthSweepCache" />), keyed by the source's key and the database's identity.
/// </summary>
/// <param name="cache">The sweeps.</param>
/// <param name="history">The statement history.</param>
/// <param name="classifier">The studio's grouping, or <see langword="null" /> when it registers none.</param>
internal sealed class StatementFactsReader(HealthSweepCache cache, HealthHistory history, IStatementClassifier? classifier = null) : IStatementFacts
{
    /// <inheritdoc />
    public async Task<StatementFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        HealthSweep sweep = await cache.GetAsync(resolved, HealthSweepKind.Statements, resolved.Scope.DatabaseId, cancellationToken).ConfigureAwait(false);
        return From(sweep, history.Read(sweep.StoreKey, HealthHistory.RingOf(sweep.DatabaseId, HealthSweepKind.Statements)), classifier);
    }

    /// <summary>The facts a statement sweep holds, with its history. Pure.</summary>
    internal static StatementFacts From(HealthSweep sweep, HealthHistoryView history, IStatementClassifier? classifier)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(history);

        StatementReadOutcome? outcome = sweep.TryShared<StatementReadOutcome>(StatementReads.OutcomeKey, out var read) ? read : null;

        // The window as the sweep computed it: against the history before its own sample, which Compute ignores by its time.
        StatisticsWindow? statistics = sweep.TryShared<StatisticsReading>(StatisticsWindow.ReadingKey, out var reading)
            ? StatisticsWindow.Compute(reading, history)
            : null;

        IStudioTableSet? tables = sweep.TryShared<IStudioTableSet>(HealthTables.SetKey, out var set) ? set : null;

        // A read that failed for a reason nobody chose has no outcome; the checks' rows say why.
        string? failure = outcome is null
            ? sweep.Results.Select(static x => x.Problem?.Reason).FirstOrDefault(static x => x is not null)
              ?? "The statement checks did not read pg_stat_statements in this sweep."
            : null;

        return StatementFacts.Build(sweep.StoreKey, sweep.DatabaseId, sweep.MeasuredAt, outcome, failure, history, statistics, tables, classifier);
    }
}
