using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>One statement the statement checks read: its counters, its text, and the store's tables it names.</summary>
/// <param name="Counters">What <c>pg_stat_statements</c> counts for it.</param>
/// <param name="Text">Its normalised text, or <see langword="null" /> when the text read did not return it.</param>
/// <param name="TextTruncated">Whether the read cut the text.</param>
/// <param name="Tables">The judged tables of this source it names - never a hidden document type's.</param>
internal sealed record ObservedStatement(
    StatementCounterRow Counters,
    string? Text,
    bool TextTruncated,
    IReadOnlyList<StudioTable> Tables)
{
    /// <summary>Its <c>queryid</c>.</summary>
    public long QueryId => Counters.QueryId;

    /// <summary>Its <c>queryid</c> as a finding's subject and a series qualifier.</summary>
    public string Key => QueryId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether it names a table this store declares.</summary>
    public bool NamesStoreTable => Tables.Count > 0;

    /// <summary>The document types of the document tables it names.</summary>
    public IReadOnlyList<Type> DocumentTypes => [.. Tables.SelectMany(static x => x.DocumentTypes).Distinct()];

    /// <summary>The schemas outside the store's own of the tables it names (<see cref="HealthFinding.GatedSchemas" />).</summary>
    public IReadOnlyList<string> GatedSchemas => [.. Tables.SelectMany(static x => x.GatedSchemas).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Whether it is the application's workload rather than a utility statement: Marten's own migrations
    /// (<c>create table</c>, <c>do $$ … $$</c>), a transaction's <c>begin</c>, an <c>explain</c> - which
    /// <c>pg_stat_statements</c> records too, and which name the store's tables without being what it spends its time on.
    /// </summary>
    public bool IsWorkload => Text is not null && StatementReads.IsWorkload(Text);

    /// <summary>
    /// Why its text must not be shown, or <see langword="null" /> when it may be (masked for its visitor): a text that
    /// still carries a string or dollar-quoted constant as written, or that is not a plain query or write
    /// (<see cref="StatementReads.Withheld" />).
    /// </summary>
    public string? TextWithheld => Text is null ? null : StatementReads.Withheld(Text);

    /// <summary>
    /// The statement as evidence: its text with every comment masked (<see cref="StatementReads.MaskComments" />), masked
    /// again for its visitor at show time - or, when the text must not be shown at all, why not. Behind the activity gate
    /// either way.
    /// </summary>
    /// <param name="label">What the evidence is called.</param>
    public HealthEvidence Evidence(string label) =>
        TextWithheld is { } withheld
            ? new HealthEvidence(label, withheld, HealthGate.Activity)
            : HealthEvidence.StatementText(label, StatementReads.MaskComments(Text!), TextTruncated);
}

/// <summary>Whether the statement read could read, and - when it could not - which expected state stopped it.</summary>
internal enum StatementReadState
{
    /// <summary>It read.</summary>
    Read,

    /// <summary><c>pg_stat_statements</c> is not installed in this database.</summary>
    NotInstalled,

    /// <summary>It is installed at a version older than 1.8.</summary>
    TooOld,

    /// <summary>It is installed, and its library is not in <c>shared_preload_libraries</c> (<c>55000</c>).</summary>
    NotPreloaded,

    /// <summary>It holds no statement of the store's own role in this database yet.</summary>
    NothingRecorded,
}

/// <summary>
/// The statement read's outcome as a value - what it read, or which expected state stopped it - so that a page that shows
/// the extension's status (the Statements page, W5-Q1) can word it, where the checks throw it as their row's reason.
/// </summary>
/// <param name="State">Whether it read.</param>
/// <param name="Extension">The extension and the server, as <see cref="HealthStatementQueries.Extension" /> read them.</param>
/// <param name="Read">What it read, for <see cref="StatementReadState.Read" />; <see langword="null" /> otherwise.</param>
internal sealed record StatementReadOutcome(StatementReadState State, StatementExtension Extension, StatementRead? Read)
{
    /// <summary>Who runs the server, as the sweep's one platform read said - <see langword="null" /> when it could not be read.</summary>
    public PostgresPlatform? Platform { get; init; }

    /// <summary>What Postgres said when the state is one Postgres raised (<see cref="StatementReadState.NotPreloaded" />).</summary>
    public Exception? Cause { get; init; }

    /// <summary>The sentence a check's row says for a state that is not <see cref="StatementReadState.Read" />: H7's, unchanged.</summary>
    public string? Reason => ReasonIn(StudioText.Marten);

    /// <summary><see cref="Reason" /> in the words of the studio <paramref name="text" /> is - whose role recorded nothing.</summary>
    /// <param name="text">The studio's words.</param>
    public string? ReasonIn(StudioText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return State switch
        {
            StatementReadState.NotInstalled => StatementReads.NotInstalled,
            StatementReadState.TooOld => StatementReads.TooOld(Extension),
            StatementReadState.NotPreloaded => StatementReads.NotPreloaded,
            StatementReadState.NothingRecorded => text.NothingRecorded,
            _ => null,
        };
    }
}

/// <summary>What one statement sweep read of <c>pg_stat_statements</c>, shared by T02, T22 and T23.</summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Extension">The extension and the server.</param>
/// <param name="AllStatements">How many statements of the store's role in this database the extension holds.</param>
/// <param name="AllExecMs">Their execution time, summed, in milliseconds.</param>
/// <param name="AllWalBytes">Their WAL, summed.</param>
/// <param name="Dealloc">Entries the extension evicted since its last reset, or <see langword="null" /> below 1.9.</param>
/// <param name="StatsReset">When its counters were last reset, or <see langword="null" /> below 1.9.</param>
/// <param name="Statements">The statements the read kept - at most <see cref="HealthStatementQueries.MaxTexts" />.</param>
internal sealed record StatementRead(
    DateTimeOffset ReadAt,
    StatementExtension Extension,
    long AllStatements,
    double AllExecMs,
    double AllWalBytes,
    long? Dealloc,
    DateTimeOffset? StatsReset,
    IReadOnlyList<ObservedStatement> Statements)
{
    /// <summary>How many statements had their text read.</summary>
    public int TextsRead => Statements.Count(static x => x.Text is not null);

    /// <summary>Whether Aurora's storage-level reads were joined (issue #8 A5), so a statement's <c>StorageBlocksRead</c> means something.</summary>
    public bool StorageReads { get; init; }

    /// <summary>What the read ran, in order, for "Show SQL" on a page that shows it (W5-Q1).</summary>
    public IReadOnlyList<HealthStatement> Ran { get; init; } = [];
}

/// <summary>
/// The statement checks' one read of <c>pg_stat_statements</c>: the counters without text, then the text of the few
/// statements the counters kept, then which of the store's tables each names - once per sweep for all three checks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three statements, at most, and usually two.</b> Where the extension is (a catalog read, as <c>SqlConsoleRole</c>);
/// the counters, <c>showtext := false</c>, kept to each check's top few and never more than
/// <see cref="HealthStatementQueries.MaxTexts" />; and the text of those - both as the store's own role
/// (<see cref="HealthSqlRunner.QueryAsApplicationRoleAsync{T}" /> says why). The text call costs the whole query-text
/// file however few ids it names - the extension fetches every entry's text before the <c>queryid</c> filter sees a row
/// - so it asks only for the texts <see cref="StatementTextCache" /> does not already hold, and a sweep whose kept
/// statements are all known makes no text call at all.
/// </para>
/// <para>
/// <b>Not every text is shown.</b> The extension replaces the constants of a plannable statement with <c>$n</c>, and
/// not those of every statement: a utility statement can keep them as written - <c>copy (select … where data->>'ssn' =
/// '123-45-6789') to stdout</c> on PostgreSQL 17, <c>CALL</c>, <c>DECLARE</c> and <c>select … into</c> on 13–15. Read
/// as the store's own role, such a text could show a visitor a value that the host's narrowed <c>SqlConsoleRole</c>
/// would have hidden on Database › Sessions, so a text that still carries a string or dollar-quoted constant, whose
/// first word is not <c>select</c>, <c>with</c>, <c>insert</c>, <c>update</c>, <c>delete</c>, <c>merge</c>,
/// <c>values</c> or <c>table</c>, or that is a <c>select … into</c>, is withheld: the finding stays, and its Statement
/// says why it is not shown (<see cref="Withheld" />). It is judged on <see cref="SqlLexer" />'s tokens, so a comment
/// cannot hide a literal and a literal cannot fake a keyword. <b>The residual:</b> a <em>numeric</em> constant in a
/// text Postgres kept un-normalised - an entry re-created after an eviction or a reset has no jumble state, and keeps
/// every constant as written - is shown, because a digit run is also <c>order by 1</c> and a type's <c>(10)</c>.
/// </para>
/// <para>
/// <b>Comments are never shown.</b> A comment is another application's free text - a sqlcommenter tag such as
/// <c>/* user='alice@example.com' */</c> from any application that connects as the same role - so every comment of a
/// shown text is printed as <c>/* ‹withheld› */</c> (<see cref="MaskComments" />).
/// </para>
/// <para>
/// <b>Expected absences are values.</b> The extension not installed in this database, a version older than 1.8, the
/// library not in <c>shared_preload_libraries</c> (the function then fails with 55000), and a role with no statement
/// recorded here yet are each <see cref="HealthCheckUnavailableException" /> - logged at Debug, and said on the page
/// in words that name what would enable the checks.
/// </para>
/// <para>
/// <b>A table is matched by name, against this store's visible tables only.</b> The text is the extension's
/// normalised form, in which a plannable statement's constants are <c>$n</c> and every identifier is as Marten wrote
/// it - qualified, <c>studio_sample.mt_doc_order</c> - so a table it names is found by its qualified name, or by its bare name when
/// the text does not qualify it. A hidden document type is not in the set (<see cref="HealthTables" />), so it is never
/// a statement's table here; a statement that names one as well is shown with its text withheld by the visitor's
/// screen.
/// </para>
/// <para>
/// <b>The Statements page reads the same read</b> (W5-Q1, issue #8 G6). Its hundred statements with the most execution time
/// since their counters began - and on Aurora its hundred with the most storage-level reads, A5 - are shares of the one
/// counters statement (<see cref="Selection" />), so the page adds no read of <c>pg_stat_statements</c> of its own: it takes
/// this read from the statement sweep (<see cref="OutcomeAsync" />). For the same page the read also reads the server's
/// platform (whether Aurora's storage reads may be joined) and the statistics window's signals (W5-CAP), which a statement
/// sweep then carries in its own history, and every kept statement's counters are sampled (<see cref="Sample" />), so the
/// page has a window for each.
/// </para>
/// </remarks>
internal static partial class StatementReads
{
    /// <summary>The memo key of the shared read.</summary>
    internal const string Key = "statements.read";

    /// <summary>The memo key of the read's outcome as a value (<see cref="OutcomeAsync" />): what a statement sweep keeps for the Statements page.</summary>
    internal const string OutcomeKey = "statements.outcome";

    /// <summary>How many statements the Statements page ranks: its largest choice of "top N" (W5-Q1).</summary>
    internal const int PageTop = 100;

    /// <summary>The series of a statement's shared blocks hit.</summary>
    internal const string SharedHitSeries = "statements.shared-hit:";

    /// <summary>The series of a statement's shared blocks read.</summary>
    internal const string SharedReadSeries = "statements.shared-read:";

    /// <summary>The series of a statement's temporary blocks written.</summary>
    internal const string TempSeries = "statements.temp-written:";

    /// <summary>The series of a statement's time reading shared blocks, in milliseconds.</summary>
    internal const string IoReadSeries = "statements.io-read-ms:";

    /// <summary>The series of a statement's Aurora storage-level reads (A5).</summary>
    internal const string StorageReadSeries = "statements.storage-read:";

    /// <summary>
    /// The fewest guarded calls in the window a guarded write's conflict rate is judged on - the optimistic-concurrency
    /// check's own floor (T22's <c>MinCalls</c>, a check of MartenStudio's own, which a test holds equal to this), so that
    /// the read keeps the guarded writes that check will judge and no others.
    /// </summary>
    internal const int GuardedCallsFloor = 1000;

    /// <summary>
    /// What the counters read keeps: each check's top few, by its own ranking - shares that sum to
    /// <see cref="HealthStatementQueries.MaxTexts" />, so the limit after them never drops one check's picks for
    /// another's (<see cref="StatementSelection.SharesFit" />).
    /// </summary>
    internal static readonly StatementSelection Selection = new(
        TopByLoad: 10,
        TopByMean: 4,
        MeanCalls: ExpensiveStatementsCheck.SlowMeanCalls,
        TopGuarded: 3,
        GuardedCalls: GuardedCallsFloor,
        TopJit: 3,
        JitCalls: JitShareCheck.MinCalls,
        Max: HealthStatementQueries.MaxTexts)
    {
        PageTopByLoad = PageTop,
        TopByStorage = PageTop,
    };

    /// <summary>What the status row says when the extension is not installed in this database.</summary>
    internal const string NotInstalled =
        "pg_stat_statements is not installed in this database, so the statement checks have nothing to read. A DBA enables "
        + "it by adding pg_stat_statements to shared_preload_libraries, restarting PostgreSQL, and creating the extension in "
        + "this database; managed platforms switch it on in their parameter settings.";

    /// <summary>What the status row says when the library is not preloaded.</summary>
    internal const string NotPreloaded =
        "pg_stat_statements is installed in this database but its library is not loaded: it must be in "
        + "shared_preload_libraries, which takes a PostgreSQL restart. Until then the statement checks have nothing to read.";

    /// <summary>The read, once per sweep for every statement check that asks.</summary>
    /// <exception cref="HealthCheckUnavailableException">An expected state stopped it: its row's reason (<see cref="StatementReadOutcome.Reason" />).</exception>
    public static Task<StatementRead> ReadAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Shared(Key, async _ =>
        {
            StatementReadOutcome outcome = await OutcomeAsync(context).ConfigureAwait(false);
            if (outcome.Read is { } read)
            {
                return read;
            }

            string reason = outcome.ReasonIn(context.Settings.Text)!;

            throw outcome.Cause is { } cause
                ? new HealthCheckUnavailableException(reason, cause)
                : new HealthCheckUnavailableException(reason);
        });
    }

    /// <summary>
    /// The read's outcome, once per sweep: what it read, or the expected state that stopped it, as a value. A statement sweep
    /// keeps it among its shared reads, which is where the Statements page takes it from (W5-Q1).
    /// </summary>
    public static Task<StatementReadOutcome> OutcomeAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(OutcomeKey, token => ReadCoreAsync(context, token));
    }

    /// <summary>What a status row says for an extension too old to read.</summary>
    internal static string TooOld(StatementExtension extension) =>
        "pg_stat_statements " + extension.Version + " is installed in this database, and the statement checks read 1.8 or later "
        + "(PostgreSQL 13+); a DBA can update the extension in this database to the server's own version.";

    /// <summary>
    /// The counters of every kept statement, for the statement history: calls, time and rows (T02's three, which it judges a
    /// window over), and the shared blocks, temporary blocks, read time and Aurora's storage reads the Statements page shows
    /// over the same window (W5-Q1). Written by T02, the one check every statement sweep runs, because a series has one
    /// writer.
    /// </summary>
    /// <remarks>
    /// A series name holds the statement's <c>queryid</c>, and the same statement is sampled every sweep, so the names are
    /// shared between samples rather than built anew for each (<see cref="SeriesName" />): a sample of two hundred statements
    /// is a dictionary of numbers, not of a thousand new strings.
    /// </remarks>
    public static IEnumerable<HealthSamplePart> Sample(StatementRead read)
    {
        ArgumentNullException.ThrowIfNull(read);

        foreach (ObservedStatement statement in read.Statements)
        {
            StatementCounterRow row = statement.Counters;
            long id = statement.QueryId;

            yield return new HealthSamplePart(SeriesName(ExpensiveStatementsCheck.CallsSeries, id), row.Calls);
            yield return new HealthSamplePart(SeriesName(ExpensiveStatementsCheck.ExecSeries, id), row.TotalExecMs);
            yield return new HealthSamplePart(SeriesName(ExpensiveStatementsCheck.RowsSeries, id), row.Rows);
            yield return new HealthSamplePart(SeriesName(SharedHitSeries, id), row.SharedBlocksHit);
            yield return new HealthSamplePart(SeriesName(SharedReadSeries, id), row.SharedBlocksRead);
            yield return new HealthSamplePart(SeriesName(TempSeries, id), row.TempBlocksWritten);
            yield return new HealthSamplePart(SeriesName(IoReadSeries, id), row.IoReadMs);

            if (row.StorageBlocksRead is { } storage)
            {
                yield return new HealthSamplePart(SeriesName(StorageReadSeries, id), storage);
            }
        }
    }

    /// <summary>The most series names <see cref="SeriesName" /> holds before it starts again: about twenty sweeps' worth of churn.</summary>
    internal const int MaxSeriesNames = 8192;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Prefix, long QueryId), string> SeriesNames = new();

    /// <summary>
    /// <paramref name="prefix" /> and <paramref name="queryId" /> as one series name, the same string instance for the same
    /// statement every sweep - bounded, and started again when full.
    /// </summary>
    internal static string SeriesName(string prefix, long queryId)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        if (SeriesNames.Count >= MaxSeriesNames)
        {
            SeriesNames.Clear();
        }

        return SeriesNames.GetOrAdd((prefix, queryId), static key => key.Prefix + key.QueryId.ToString(CultureInfo.InvariantCulture));
    }

    private static async Task<StatementReadOutcome> ReadCoreAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        int ranBefore = context.Sql.Statements.Count;

        IReadOnlyList<StatementExtension> found = await context.Sql
            .QueryAsync(HealthStatementQueries.Extension(), HealthStatementQueries.ReadExtension, cancellationToken)
            .ConfigureAwait(false);

        StatementExtension extension = found[0];

        if (!extension.Installed)
        {
            return new StatementReadOutcome(StatementReadState.NotInstalled, extension, null) { Platform = await PlatformAsync(context).ConfigureAwait(false) };
        }

        if (!extension.Supported)
        {
            return new StatementReadOutcome(StatementReadState.TooOld, extension, null) { Platform = await PlatformAsync(context).ConfigureAwait(false) };
        }

        // Who runs the server - whether Aurora's storage reads may be joined (A5), and how the page words the extension's
        // status - and since when the counters count (W5-CAP), which a statement sweep then carries in its own history.
        PostgresPlatform? platform = await PlatformAsync(context).ConfigureAwait(false);
        await StatisticsAsync(context).ConfigureAwait(false);

        bool storageReads = extension.AuroraStatements && platform?.Kind == PostgresPlatformKind.AmazonAurora;
        IReadOnlyList<StatementCounterRow> rows;

        try
        {
            rows = await context.Sql
                .QueryAsApplicationRoleAsync(HealthStatementQueries.Counters(extension, Selection, storageReads), HealthStatementQueries.ReadCounters, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ObjectNotInPrerequisiteState)
        {
            // "pg_stat_statements must be loaded via shared_preload_libraries": the host's configuration, not a fault.
            return new StatementReadOutcome(StatementReadState.NotPreloaded, extension, null) { Platform = platform, Cause = exception };
        }

        if (rows.Count == 0)
        {
            return new StatementReadOutcome(StatementReadState.NothingRecorded, extension, null) { Platform = platform };
        }

        // One row per statement: the counters statement already folds a queryid's entries below 1.9, and this is the belt -
        // a page keyed by queryid and a history series named by it each fail outright on a second row.
        rows = OnePerStatement(rows);

        IStudioTableSet set = await HealthTables.SetAsync(context).ConfigureAwait(false);

        // The texts the process already holds, and a call for the rest only: every text call costs the extension its
        // whole text file, however few ids it names.
        StatementTextCache? cache = context.Services.GetService<StatementTextCache>();
        Dictionary<long, StatementTextRow> byId = [];
        List<long> missing = [];

        foreach (StatementCounterRow row in rows)
        {
            if (cache is not null && cache.TryGet(context.StoreKey, context.DatabaseId, row.QueryId, out StatementTextRow? known))
            {
                byId[row.QueryId] = known;
            }
            else
            {
                missing.Add(row.QueryId);
            }
        }

        if (missing.Count > 0)
        {
            IReadOnlyList<StatementTextRow> texts = await context.Sql
                .QueryAsApplicationRoleAsync(HealthStatementQueries.Texts(extension, missing), HealthStatementQueries.ReadText, cancellationToken)
                .ConfigureAwait(false);

            foreach (StatementTextRow text in texts)
            {
                byId.TryAdd(text.QueryId, text);
            }

            cache?.Remember(context.StoreKey, context.DatabaseId, texts, kept: [.. rows.Select(static x => x.QueryId)]);
        }

        List<ObservedStatement> statements = new(rows.Count);
        foreach (StatementCounterRow row in rows)
        {
            statements.Add(byId.TryGetValue(row.QueryId, out StatementTextRow? text)
                ? new ObservedStatement(row, text.Text, text.Truncated, Named(text.Text, set))
                : new ObservedStatement(row, null, false, []));
        }

        StatementCounterRow first = rows[0];

        StatementRead read = new(context.Now, extension, first.AllStatements, first.AllExecMs, first.AllWalBytes, first.Dealloc, first.StatsReset, statements)
        {
            StorageReads = storageReads,
            Ran = [.. context.Sql.Statements.Skip(ranBefore)],
        };

        return new StatementReadOutcome(StatementReadState.Read, extension, read) { Platform = platform };
    }

    /// <summary>
    /// <paramref name="rows" /> with each <c>queryid</c> once, its first row kept - the counters statement ranks them, so the
    /// first is the one it ranked higher. The statement folds a <c>queryid</c>'s entries itself
    /// (<see cref="StatementExtension.FoldsNested" />), so this changes nothing unless a server returns what it should not;
    /// then it keeps one row rather than adding the two, which could count one execution twice. Pure.
    /// </summary>
    internal static IReadOnlyList<StatementCounterRow> OnePerStatement(IReadOnlyList<StatementCounterRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        HashSet<long> seen = new(rows.Count);
        List<StatementCounterRow>? kept = null;

        for (int i = 0; i < rows.Count; i++)
        {
            if (seen.Add(rows[i].QueryId))
            {
                kept?.Add(rows[i]);
            }
            else
            {
                kept ??= [.. rows.Take(i)];
            }
        }

        return kept ?? rows;
    }

    /// <summary>
    /// The server's platform, through the sweep's one read of it - or <see langword="null" /> when it cannot be read, which
    /// joins no Aurora column and words the status for a server of somebody's own.
    /// </summary>
    private static async Task<PostgresPlatform?> PlatformAsync(HealthCheckContext context)
    {
        try
        {
            PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);
            return server.Platform;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The statistics window's signals (W5-CAP), read through the sweep's memo so that a statement sweep computes its window and
    /// carries it in the statement history - what the Statements page bounds its window by. A failure here costs only that
    /// bound, never the read.
    /// </summary>
    private static async Task StatisticsAsync(HealthCheckContext context)
    {
        try
        {
            await StatisticsWindow.ReadingAsync(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // No window from this sweep: the page says since when the extension's own counters count instead.
        }
    }

    /// <summary>The tables of <paramref name="set" /> that <paramref name="text" /> names, in the order it names them.</summary>
    internal static IReadOnlyList<StudioTable> Named(string text, IStudioTableSet set)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(set);

        List<StudioTable> named = [];

        foreach (Match match in Name().Matches(text))
        {
            string first = Part(match, "q1", "u1");
            StudioTable? table = match.Groups["u2"].Success || match.Groups["q2"].Success
                ? set.Find(first, Part(match, "q2", "u2"))
                : null;

            // A bare name - a text that does not qualify its tables - is this store's table only if exactly one of its
            // tables has that name.
            if (table is null && !(match.Groups["u2"].Success || match.Groups["q2"].Success))
            {
                List<StudioTable> bare = [.. set.Tables.Where(x => string.Equals(x.Table, first, StringComparison.OrdinalIgnoreCase))];
                table = bare.Count == 1 ? bare[0] : null;
            }

            if (table is not null && !named.Contains(table))
            {
                named.Add(table);
            }
        }

        return named;
    }

    /// <summary>The target table of a write - <c>insert into x</c>, <c>update x</c> - among <paramref name="set" />'s.</summary>
    internal static StudioTable? Target(string name, IStudioTableSet set)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(set);

        IReadOnlyList<StudioTable> named = Named(name, set);
        return named.Count == 1 ? named[0] : null;
    }

    /// <summary>What a statement whose text keeps a string constant as it was written says in place of the text.</summary>
    internal const string WithheldLiteral =
        "Withheld: pg_stat_statements kept a string constant in this statement as it was written, so its text can carry a "
        + "value the studio does not show. Its numbers are here; the statement is in pg_stat_statements under its queryid.";

    /// <summary>What a statement that is not a plain query or write says in place of the text.</summary>
    internal const string WithheldUtility =
        "Withheld: this is not a plain query or write (a COPY, CALL, DECLARE, FETCH or SELECT ... INTO, say), and "
        + "pg_stat_statements can keep such a statement's constants as they were written, so its text can carry a value the "
        + "studio does not show.";

    /// <summary>What a statement whose text cannot be read as SQL says in place of the text.</summary>
    internal const string WithheldUnreadable = "Withheld: its text could not be read as SQL, so it cannot be checked for values.";

    /// <summary>What every comment of a shown text is printed as.</summary>
    internal const string CommentMask = "/* " + WithheldNames.Token + " */";

    /// <summary>The first words of a statement whose constants the extension replaces, and whose text may be shown.</summary>
    private static readonly HashSet<string> ShownStatements = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "with", "insert", "update", "delete", "merge", "values", "table",
    };

    /// <summary>The words before a top-level <c>into</c> that make it a write's target rather than a <c>select … into</c>.</summary>
    private static readonly HashSet<string> WritesInto = new(StringComparer.OrdinalIgnoreCase) { "insert", "merge" };

    /// <summary>
    /// The first words of a statement that is not the workload: DDL, a transaction's control, a setting, a plan.
    /// </summary>
    private static readonly HashSet<string> UtilityWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "create", "alter", "drop", "truncate", "comment", "grant", "revoke", "do", "lock", "set", "reset", "show", "begin",
        "start", "commit", "end", "rollback", "savepoint", "release", "prepare", "deallocate", "discard", "listen",
        "unlisten", "notify", "vacuum", "analyze", "analyse", "reindex", "cluster", "refresh", "checkpoint", "load",
        "security", "import", "reassign", "explain",
    };

    /// <summary>
    /// Why a text the store's own role read must not be shown, or <see langword="null" /> when it may be: on
    /// <see cref="SqlLexer" />'s tokens, a text that still carries a string literal (<c>'…'</c>, <c>E'…'</c>,
    /// <c>U&amp;'…'</c>) or a dollar-quoted body; whose first word is not one of a plain query or write; that is a
    /// <c>select … into</c> (a top-level <c>into</c> not after <c>insert</c> or <c>merge</c>, which PostgreSQL 13–15 record
    /// as a utility statement with its constants as written); or that cannot be read at all. A numeric constant is not
    /// caught: in a normalised text a digit run is also <c>order by 1</c> or a type's <c>(10)</c>.
    /// </summary>
    internal static string? Withheld(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        SqlLexer lexer = new(text, reportLiterals: true);
        bool first = true;
        SqlToken previous = default;

        while (lexer.TryRead(out SqlToken token))
        {
            if (token.Kind == SqlTokenKind.Unreadable)
            {
                return WithheldUnreadable;
            }

            if (first)
            {
                first = false;
                previous = token;

                if (token.Kind != SqlTokenKind.Word || !ShownStatements.Contains(token.Text))
                {
                    return WithheldUtility;
                }

                continue;
            }

            if (token.Kind == SqlTokenKind.Literal)
            {
                return WithheldLiteral;
            }

            if (token.Depth == 0 && token.IsWord("into") && !(previous.Kind == SqlTokenKind.Word && WritesInto.Contains(previous.Text)))
            {
                return WithheldUtility;
            }

            previous = token;
        }

        return first ? WithheldUnreadable : null;
    }

    /// <summary>
    /// <paramref name="text" /> with every comment printed as <see cref="CommentMask" /> - a <c>--</c> comment keeping
    /// its line break - and nothing else changed: a comment marker inside a string literal or a quoted identifier is
    /// not a comment, and stays.
    /// </summary>
    /// <remarks>
    /// A comment is free text another application wrote, and "Show SQL"'s per-visitor mask reads names, not prose: a
    /// sqlcommenter tag (<c>/* user='alice@example.com',route='/orders' */</c>) passes every rule a statement does. The
    /// statements this is asked of have passed <see cref="Withheld" />, so they read whole; a text that does not is
    /// returned as it came, because a withheld text is never shown.
    /// </remarks>
    internal static string MaskComments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        SqlLexer lexer = new(text, reportLiterals: false, reportComments: true);
        StringBuilder? masked = null;
        int copied = 0;

        while (lexer.TryRead(out SqlToken token))
        {
            if (token.Kind != SqlTokenKind.Comment)
            {
                continue;
            }

            masked ??= new StringBuilder(text.Length);
            masked.Append(text, copied, token.Start - copied).Append(CommentMask);
            copied = token.End;
        }

        return masked is null ? text : masked.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>
    /// Whether <paramref name="text" />'s first word, past any comment, starts a statement of the workload - read on
    /// <see cref="SqlLexer" />'s tokens, so a comment ends where Postgres ends it.
    /// </summary>
    internal static bool IsWorkload(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        SqlLexer lexer = new(text);

        return !lexer.TryRead(out SqlToken first) || first.Kind != SqlTokenKind.Word || !UtilityWords.Contains(first.Text);
    }

    private static string Part(Match match, string quoted, string bare) =>
        match.Groups[quoted].Success ? match.Groups[quoted].Value.Replace("\"\"", "\"", StringComparison.Ordinal) : match.Groups[bare].Value;

    /// <summary>A name, optionally qualified, either part optionally quoted - and not part of a longer bare name.</summary>
    [GeneratedRegex(
        "(?<![A-Za-z0-9_$\"])(?:\"(?<q1>(?:[^\"]|\"\")+)\"|(?<u1>[A-Za-z_][A-Za-z0-9_$]*))(?:\\s*\\.\\s*(?:\"(?<q2>(?:[^\"]|\"\")+)\"|(?<u2>[A-Za-z_][A-Za-z0-9_$]*)))?",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Name();
}

/// <summary>A statement's counters over the window the advisor can judge.</summary>
/// <param name="Calls">Calls in the window.</param>
/// <param name="ExecMs">Execution time in the window, in milliseconds.</param>
/// <param name="Rows">Rows in the window.</param>
/// <param name="Span">How long the window is, or <see langword="null" /> when it began at a time nobody knows.</param>
/// <param name="From">When it began, when that is known.</param>
/// <param name="Watched">Whether it is the advisor's own window (two statement checks) rather than the counters' whole life.</param>
internal sealed record StatementWindow(double Calls, double ExecMs, double Rows, TimeSpan? Span, DateTimeOffset? From, bool Watched)
{
    /// <summary>The share of wall-clock time it spent executing: 0.1 is six minutes an hour.</summary>
    public double? Load => Span is { TotalSeconds: > 0 } span ? ExecMs / 1000 / span.TotalSeconds : null;

    /// <summary>Its mean execution time, in milliseconds.</summary>
    public double? MeanMs => Calls > 0 ? ExecMs / Calls : null;
}

/// <summary>Windows over the statement history, and the words the statement findings share. Pure.</summary>
internal static class StatementWindows
{
    /// <summary>The series T02 writes the database's total execution time to.</summary>
    internal const string AllExecSeries = "statements.all-exec-ms";

    /// <summary>The series T02 writes the database's total WAL to.</summary>
    internal const string AllWalSeries = "statements.all-wal-bytes";

    /// <summary>The series T02 writes the extension's evictions to.</summary>
    internal const string DeallocSeries = "statements.dealloc";

    /// <summary>
    /// A counter's change over the history, or its value since the counters began when the history cannot say yet.
    /// </summary>
    /// <param name="read">The sweep's read.</param>
    /// <param name="history">The statement history.</param>
    /// <param name="series">The three series - calls, time, rows - with the statement's key appended.</param>
    /// <param name="calls">The cumulative calls.</param>
    /// <param name="execMs">The cumulative time.</param>
    /// <param name="rows">The cumulative rows.</param>
    /// <param name="since">When the entry's counters began, when known.</param>
    public static StatementWindow Of(
        StatementRead read,
        HealthHistoryView history,
        (string Calls, string ExecMs, string Rows) series,
        double calls,
        double execMs,
        double rows,
        DateTimeOffset? since)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(history);

        if (TableHistory.Delta(history.Trend(series.Calls)) is { } windowCalls
            && TableHistory.Delta(history.Trend(series.ExecMs)) is { } windowExec
            && TableHistory.Delta(history.Trend(series.Rows)) is { } windowRows)
        {
            return new StatementWindow(windowCalls.Change, windowExec.Change, windowRows.Change, windowCalls.Span, windowCalls.From, true);
        }

        DateTimeOffset? from = since ?? read.StatsReset;
        return new StatementWindow(calls, execMs, rows, from is { } at && read.ReadAt > at ? read.ReadAt - at : null, from, false);
    }

    /// <summary>When the counters of one statement began, as the extension knows it.</summary>
    public static DateTimeOffset? Since(StatementRead read, ObservedStatement statement) =>
        statement.Counters.StatsSince ?? read.StatsReset;

    /// <summary>"over the last 25 min (09:00 to 09:25 UTC)", or what the counters' whole life is.</summary>
    public static string Describe(StatementWindow window, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.Watched && window.Span is { } watched && window.From is { } from)
        {
            return "over the last " + TableText.Span(watched) + " the advisor watched (" + TableText.When(from) + " to " + TableText.When(from + watched) + ")";
        }

        return window.From is { } began
            ? "since its counters began, " + TableText.Ago(began, now)
            : "since its counters began, at a time this pg_stat_statements does not record (it is older than 1.9)";
    }

    /// <summary>The caveat every statement finding carries: where it was read, over what, and what it cannot see.</summary>
    /// <param name="read">The statement read.</param>
    /// <param name="window">The window the finding measured.</param>
    /// <param name="history">The advisor's samples.</param>
    /// <param name="text">The studio's words for whose role it read as, or <see langword="null" /> for MartenStudio's.</param>
    public static string Caveat(StatementRead read, StatementWindow window, HealthHistoryView history, StudioText? text = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(history);
        text ??= StudioText.Marten;

        string when = window.Watched
            ? "Measured " + Describe(window, read.ReadAt) + ", between two statement checks."
            : "Measured " + Describe(window, read.ReadAt) + ": the advisor has not run the statement checks twice yet, so these "
              + "are the totals since then rather than a window it watched.";

        string evicted = string.Empty;

        if (TableHistory.Delta(history.Trend(DeallocSeries)) is { Change: > 0 } dealloc)
        {
            evicted = " pg_stat_statements evicted " + TableText.Count(dealloc.Change) + " entries in the window (pg_stat_statements.max), "
                + "so a statement's counters may have restarted inside it.";
        }
        else if (!window.Watched && read.Dealloc is > 0 and { } total)
        {
            evicted = " pg_stat_statements has evicted " + TableText.Count(total) + " entries since its last reset (pg_stat_statements.max), "
                + "so some counters are younger than that.";
        }

        return when
            + " Read from pg_stat_statements as " + text.SourcePossessive + " own role, which holds that role's statements in this database and only "
            + "the ones that completed: another role's statements are not among them, and another application's are when it "
            + "connects as the same role." + evicted;
    }

    /// <summary>Milliseconds as a person reads them: 0.8 ms, 120 ms, 2.4 s, 3.1 min.</summary>
    public static string Milliseconds(double ms) => ms switch
    {
        < 1 => ms.ToString("0.00", CultureInfo.InvariantCulture) + " ms",
        < 1000 => ms.ToString("0", CultureInfo.InvariantCulture) + " ms",
        < 60_000 => (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s",
        < 3_600_000 => (ms / 60_000).ToString("0.0", CultureInfo.InvariantCulture) + " min",
        _ => (ms / 3_600_000).ToString("0.0", CultureInfo.InvariantCulture) + " h",
    };

    /// <summary>What a statement is about, in a title: "the Order collection", "the event store's mt_events".</summary>
    public static string About(ObservedStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        StudioTable first = statement.Tables[0];
        string named = first.Kind switch
        {
            StudioTableKind.DocumentTable when first.TypeName is { } type => "the " + type + " collection",
            StudioTableKind.DocumentTable => "the document table " + first.Table,
            StudioTableKind.EventTable => "the event store's " + first.Table,
            StudioTableKind.Projection => "the Marten-managed table " + first.Table,
            StudioTableKind.Relational => "the table " + first.Table,
            _ => (first.Library ?? "Marten") + "'s " + first.Table,
        };

        return statement.Tables.Count == 1
            ? named
            : named + " and " + (statement.Tables.Count - 1).ToString(CultureInfo.InvariantCulture)
              + (statement.Tables.Count == 2 ? " other table" : " other tables");
    }
}
