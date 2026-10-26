using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace PostgresStudio.Services.Statements;

/// <summary>What a statement row is ranked by on the Statements page.</summary>
internal enum StatementRanking
{
    /// <summary>Execution time over the window (issue #8 G6).</summary>
    TotalTime,

    /// <summary>Aurora's storage-level reads over the window (issue #8 A5) - offered only where they are read.</summary>
    StorageReads,
}

/// <summary>What a statement table is sorted by, among the top statements it shows.</summary>
internal enum StatementSort
{
    /// <summary>Execution time.</summary>
    TotalTime,

    /// <summary>Mean execution time a call.</summary>
    Mean,

    /// <summary>Calls.</summary>
    Calls,

    /// <summary>Rows.</summary>
    Rows,

    /// <summary>Shared blocks read from outside the cache.</summary>
    SharedRead,

    /// <summary>Temporary blocks written.</summary>
    Temp,

    /// <summary>Aurora's storage-level reads.</summary>
    StorageReads,
}

/// <summary>One statement as one visitor may see it.</summary>
/// <param name="QueryId">Its <c>queryid</c>.</param>
/// <param name="Text">Its text, masked for this visitor and capped, or <see langword="null" /> when none is shown.</param>
/// <param name="TextNote">Why the text is withheld, cut or missing - or <see langword="null" />.</param>
/// <param name="GroupKey">Its group's identity, as this visitor may see the group.</param>
/// <param name="GroupLabel">Its group, as this visitor may see it.</param>
/// <param name="Window">Its counters over the window, or <see langword="null" /> when it was not watched over it.</param>
/// <param name="SinceBegan">Its counters since they began.</param>
/// <param name="Share">Its share of the window's execution time, or <see langword="null" />.</param>
/// <param name="WatchedFrom">When its part of the window began, when later than the window's start.</param>
/// <param name="ParallelNote">What its parallel workers say (PostgreSQL 18+), when its plans were given fewer than they asked for.</param>
internal sealed record StatementRowView(
    long QueryId,
    string? Text,
    string? TextNote,
    string GroupKey,
    string GroupLabel,
    StatementCounts? Window,
    StatementCounts SinceBegan,
    double? Share,
    DateTimeOffset? WatchedFrom,
    string? ParallelNote)
{
    /// <summary>Its <c>queryid</c> as the page prints it: a string, because it does not fit a JavaScript number.</summary>
    public string Key => QueryId.ToString(CultureInfo.InvariantCulture);

    /// <summary>What the page ranks and sorts by: the window's counters.</summary>
    public StatementCounts? Ranked => Window;
}

/// <summary>One group of statements, summed - the page's group view.</summary>
/// <param name="Key">The group's identity.</param>
/// <param name="Label">What it is called.</param>
/// <param name="Statements">How many statements of the top ones are in it.</param>
/// <param name="Counts">Their window counters, summed.</param>
/// <param name="Share">Their share of the window's execution time, summed.</param>
internal sealed record StatementGroupRowView(string Key, string Label, int Statements, StatementCounts Counts, double? Share);

/// <summary>What the Statements page draws for one visitor.</summary>
internal sealed record StatementsView
{
    /// <summary>The page's choices of "top N".</summary>
    public static IReadOnlyList<int> TopChoices { get; } = [10, 25, 50, StatementReads.PageTop];

    /// <summary>Why nothing is shown, or <see cref="DatabaseRefusal.None" />.</summary>
    public DatabaseRefusal Refusal { get; init; }

    /// <summary>The sentence for a refusal.</summary>
    public string? Reason { get; init; }

    /// <summary>When the statement sweep read them.</summary>
    public DateTimeOffset? MeasuredAt { get; init; }

    /// <summary>Whether the read read, or which state stopped it.</summary>
    public StatementsState State { get; init; }

    /// <summary>What the extension's status is in words - not installed, not preloaded, too old - naming who can fix it on this platform.</summary>
    public string? Status { get; init; }

    /// <summary>Whether the window is one the statement history watched.</summary>
    public bool Watched { get; init; }

    /// <summary>The window, in words: "over the last 25 min the statement checks watched (…)", or since when the counters count.</summary>
    public string? WindowText { get; init; }

    /// <summary>What the statistics window says about the server's counters, when it bounds the window - or <see langword="null" />.</summary>
    public string? Basis { get; init; }

    /// <summary>What the numbers are and are not.</summary>
    public string? Caveat { get; init; }

    /// <summary>The window's execution time over every statement of the reading role here.</summary>
    public double TotalExecMs { get; init; }

    /// <summary>Every statement the read kept, the most execution time over the window first.</summary>
    public IReadOnlyList<StatementRowView> Rows { get; init; } = [];

    /// <summary>Whether Aurora's storage-level reads are read, so "rank by storage reads" is offered (A5).</summary>
    public bool StorageReads { get; init; }

    /// <summary>Why every statement's text is withheld from this visitor, when it is - or <see langword="null" />.</summary>
    public string? TextsWithheld { get; init; }

    /// <summary>The studio's own cost in this database, from the one meter the Health page reads too.</summary>
    public StudioCost? StudioCost { get; init; }

    /// <summary>The studio's own share, in words: what the meter counts, and which of the studio's statements the list holds.</summary>
    public string? StudioCostText { get; init; }

    /// <summary>What the read ran, for "Show SQL".</summary>
    public IReadOnlyList<HealthStatement> Statements { get; init; } = [];

    /// <summary>A refusal: nothing but the sentence.</summary>
    public static StatementsView Refused(DatabaseRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };

    /// <summary>
    /// The top <paramref name="count" /> rows by <paramref name="ranking" />, then sorted by <paramref name="sort" />: what the
    /// statements table draws. A row not watched over the window ranks after every watched one. Pure.
    /// </summary>
    public IReadOnlyList<StatementRowView> Top(int count, StatementRanking ranking, StatementSort sort)
    {
        IOrderedEnumerable<StatementRowView> ranked = ranking == StatementRanking.StorageReads && StorageReads
            ? Rows.Where(static x => x.Window?.StorageBlocksRead > 0).OrderByDescending(static x => x.Window!.StorageBlocksRead ?? 0)
            : Rows.OrderByDescending(static x => x.Window?.ExecMs ?? double.MinValue);

        List<StatementRowView> top = [.. ranked.ThenBy(static x => x.QueryId).Take(Math.Max(count, 0))];

        return [.. top.OrderByDescending(x => SortKey(x, sort) ?? double.MinValue).ThenBy(static x => x.QueryId)];
    }

    /// <summary>
    /// The top <paramref name="count" /> rows by <paramref name="ranking" />, summed by group, the most execution time first:
    /// the page's group view. Pure.
    /// </summary>
    public IReadOnlyList<StatementGroupRowView> Groups(int count, StatementRanking ranking)
    {
        return
        [
            .. Top(count, ranking, StatementSort.TotalTime)
                .Where(static x => x.Window is not null)
                .GroupBy(static x => x.GroupKey, StringComparer.Ordinal)
                .Select(static group => new StatementGroupRowView(
                    group.Key,
                    group.First().GroupLabel,
                    group.Count(),
                    new StatementCounts(
                        group.Sum(static x => x.Window!.Calls),
                        group.Sum(static x => x.Window!.ExecMs),
                        group.Sum(static x => x.Window!.Rows),
                        group.Sum(static x => x.Window!.SharedBlocksHit),
                        group.Sum(static x => x.Window!.SharedBlocksRead),
                        group.Sum(static x => x.Window!.TempBlocksWritten),
                        group.Sum(static x => x.Window!.IoReadMs),
                        group.Any(static x => x.Window!.StorageBlocksRead is not null) ? group.Sum(static x => x.Window!.StorageBlocksRead ?? 0) : null),
                    group.Any(static x => x.Share is not null) ? group.Sum(static x => x.Share ?? 0) : null))
                .OrderByDescending(static x => x.Counts.ExecMs)
                .ThenBy(static x => x.Label, StringComparer.Ordinal),
        ];
    }

    private static double? SortKey(StatementRowView row, StatementSort sort) => row.Window is not { } counts ? null : sort switch
    {
        StatementSort.Mean => counts.MeanMs,
        StatementSort.Calls => counts.Calls,
        StatementSort.Rows => counts.Rows,
        StatementSort.SharedRead => counts.SharedBlocksRead,
        StatementSort.Temp => counts.TempBlocksWritten,
        StatementSort.StorageReads => counts.StorageBlocksRead,
        _ => counts.ExecMs,
    };
}

/// <summary>What the Statements page reads: one database's statements, as one visitor may see them.</summary>
/// <remarks>An interface so a page can be rendered in a component test with a fake behind it (D12).</remarks>
internal interface IStatementsService
{
    /// <summary>
    /// The statements of the scope's database, for a visitor through the activity gate. Never throws but for cancellation; a
    /// refusal or a failed read is a value.
    /// </summary>
    /// <param name="scope">The visitor's scope; its tenant is dropped, because a statement belongs to no tenant.</param>
    /// <param name="cancellationToken">Cancels this caller's wait.</param>
    Task<StatementsView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Statements page's per-visitor half (W5-Q1): the shared facts (<see cref="IStatementFacts" />), shown through this
/// visitor's gates.
/// </summary>
/// <remarks>
/// <para>
/// <b>The activity gate, and only then the facts</b> (D29). Statements are other programs' source text and other sessions'
/// work, so the page is <see cref="HealthGate.Activity" />'s, asked as Database › Sessions asks its details: the capability
/// (<c>ViewDatabaseActivity</c>, off under <c>ReadOnly</c>) before anybody is asked who is asking; then the scope, resolved
/// with no tenant - the store policy, asked by the resolver; then the store policy again about the store's registered key and
/// the database's own identity when the visitor spelt either otherwise; then the write policy with the capability named,
/// against the same tenant-less resource. The facts are read only past all four, so a visitor short of the gate never causes
/// a statement sweep. A refusal is event 9450 at Debug and the page's sentence, never an audit entry: the page asks once per
/// load and poll, and a closed gate refuses nothing a person did.
/// </para>
/// <para>
/// <b>The first exception, inherited, and its fence unchanged.</b> The statements are the statement checks' own read
/// (D29's first exception): <c>pg_stat_statements</c> as the store's own role, <c>userid = current_user</c> and this database
/// (<c>StatementChecksFenceTests</c>). A text still carrying a string constant as written, or that is not a plain query or
/// write, is withheld (T02's rule); every other is shown with its comments masked, then through this visitor's own mask -
/// Database › Sessions' rule (<see cref="SessionQueryText" />), with the database browser's gate for the schemas they may not
/// see, a hidden document type and another store's tables - capped at the 4,096 characters the read reads. A group that names
/// a table in a schema this visitor may not see is shown without it.
/// </para>
/// <para>
/// <b>Audited by signature, never per poll.</b> The first read of a store's database in a circuit is one Activity entry under
/// <c>ViewDatabaseActivity</c> and one event 9452; the page's interval reads and its Refresh repeat that signature and
/// record nothing, as Database › Sessions' details do.
/// </para>
/// </remarks>
internal sealed class StatementsService : IStatementsService
{
    /// <summary>What the audit ring calls reading the Statements page.</summary>
    internal const string Action = "Read database statements";

    /// <summary>What the audit ring says was read.</summary>
    internal const string Target = "top statements by execution time (pg_stat_statements)";

    /// <summary>What a statement whose text the extension did not return says.</summary>
    internal const string TextNotReturned = "Not read: pg_stat_statements did not return its text - it may have been evicted since its counters were read.";

    /// <summary>What every statement says when this visitor's mask could not be read.</summary>
    internal const string TextsUnchecked =
        "Every statement's text is withheld: the schemas it may name could not be checked against the ones you may see.";

    // The audit ledger is keyed by store, database and what was read, as the Sessions page's is; these name "what".
    private const string LedgerSchema = "(activity)";
    private const string LedgerRelation = "statements";
    private const string LedgerKind = "top";
    private const string Signature = "top";

    private readonly IStudioSettings settings;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioAuthorization authorization;
    private readonly StudioSourceResolver resolver;
    private readonly IStatementFacts facts;
    private readonly StudioActionLog audit;
    private readonly StudioLogThrottle throttle;
    private readonly ILogger<StatementsService> logger;
    private readonly DatabaseAccess? access;
    private readonly StudioDatabaseTime meter;
    private readonly TableRowAuditLedger ledger = new();

    public StatementsService(
        IStudioSettings settings,
        StudioCapabilityGuard capabilities,
        StudioAuthorization authorization,
        StudioSourceResolver resolver,
        IStatementFacts facts,
        StudioActionLog audit,
        StudioLogThrottle throttle,
        ILogger<StatementsService> logger,
        DatabaseAccess? access = null,
        StudioDatabaseTime? meter = null)
    {
        this.settings = settings;
        this.capabilities = capabilities;
        this.authorization = authorization;
        this.resolver = resolver;
        this.facts = facts;
        this.audit = audit;
        this.throttle = throttle;
        this.logger = logger;
        this.access = access;
        this.meter = meter ?? StudioDatabaseTime.Shared;
    }

    /// <summary>How many first reads this circuit has recorded, for a test.</summary>
    internal int RecordedReads => ledger.Count;

    /// <inheritdoc />
    public async Task<StatementsView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        StudioScope asked = scope with { TenantId = null };
        StudioText text = settings.Text;

        // 1. The capability, before anybody is asked who is asking.
        if (!capabilities.IsEnabled(StudioCapability.ViewDatabaseActivity))
        {
            return Refuse(asked, capabilities.ReadOnly ? DatabaseRefusal.ReadOnly : DatabaseRefusal.CapabilityOff,
                capabilities.ReadOnly ? text.StatementsReadOnlyDenial : text.StatementsCapabilityDenial);
        }

        // 2. The scope, resolved with no tenant: the store policy, which the resolver asks before it looks anything up.
        ResolvedSource resolved;

        try
        {
            resolved = await resolver.ResolveAsync(asked, null, cancellationToken).ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException)
        {
            return Refuse(asked, DatabaseRefusal.StorePolicy, text.StatementsStorePolicyDenial);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            return StatementsView.Refused(DatabaseRefusal.Unavailable, exception.Message);
        }

        // 3. The two policies, of the store and database it resolved to, by their own names (D29).
        StudioScope whole = new(resolved.Source.Key, resolved.Target.Identity, null);

        bool samePlace = string.Equals(asked.StoreKey, whole.StoreKey, StringComparison.Ordinal)
            && string.Equals(asked.DatabaseId, whole.DatabaseId, StringComparison.Ordinal);

        if (!samePlace && !await authorization.IsAuthorizedAsync(whole, capability: null, cancellationToken).ConfigureAwait(false))
        {
            return Refuse(whole, DatabaseRefusal.StorePolicy, text.StatementsStorePolicyDenial);
        }

        if (!await authorization.IsAuthorizedAsync(whole, StudioCapability.ViewDatabaseActivity, cancellationToken).ConfigureAwait(false))
        {
            return Refuse(whole, DatabaseRefusal.WritePolicy, text.StatementsWritePolicyDenial);
        }

        // 4. Only now anything about the database: the statement sweep's read, joined or taken while it is fresh.
        ResolvedSource database = resolved with { Scope = whole };
        StatementFacts read;

        try
        {
            read = await facts.ReadAsync(database, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return StatementsView.Refused(DatabaseRefusal.Unavailable, Failure(exception, resolved));
        }

        DatabaseGateRead? gate = read.Statements.Count == 0 ? null : await GateAsync(whole, database, cancellationToken).ConfigureAwait(false);

        StatementsView view = Show(read, gate) with
        {
            StudioCost = meter.Total(CostKey(resolved.Target)),
        };

        if (read.State == StatementsState.Read)
        {
            await RecordFirstReadAsync(whole, read).ConfigureAwait(false);
        }

        return view;
    }

    /// <summary>
    /// <paramref name="read" /> as one visitor may see it - pure: every text masked through <paramref name="gate" />, the
    /// database browser's gate for this visitor (or <see langword="null" /> when it could not be read, which withholds every
    /// text and names no gated table), and the extension's status worded for the server's platform.
    /// </summary>
    internal StatementsView Show(StatementFacts read, DatabaseGateRead? gate)
    {
        ArgumentNullException.ThrowIfNull(read);

        StudioText text = settings.Text;
        StatementScreen screen = StatementScreen.Of(gate, settings.IsDocumentTypeVisible is not null);
        DatabaseGate? browser = gate is { Succeeded: true } ? gate.Gate : null;

        List<StatementRowView> rows = new(read.Statements.Count);

        foreach (StatementFact fact in read.Statements)
        {
            (string? shown, string? note) = Text(fact, screen);
            (string key, string label) = Group(fact.Group, fact.TextRead, browser);

            rows.Add(new StatementRowView(
                fact.QueryId,
                shown,
                note,
                key,
                label,
                fact.Window,
                fact.SinceBegan,
                fact.Share,
                fact.WatchedFrom,
                ParallelNote(fact)));
        }

        return new StatementsView
        {
            MeasuredAt = read.MeasuredAt,
            State = read.State,
            Status = Status(read, text),
            Watched = read.Watched,
            WindowText = read.State == StatementsState.Read ? WindowText(read) : null,
            Basis = BasisText(read),
            Caveat = read.State == StatementsState.Read ? Caveat(read, text) : null,
            TotalExecMs = read.TotalExecMs,
            Rows = rows,
            StorageReads = read.StorageReads,
            TextsWithheld = rows.Count > 0 && screen.Withheld is null ? TextsUnchecked : null,
            StudioCostText = StudioCostText(),
            Statements = read.Ran,
        };
    }

    /// <summary>
    /// The extension's status when the read did not read, in words that name who can fix it on this platform (A7): the grant,
    /// the setting and where it changes, through <see cref="PostgresGrants" /> and the server facts.
    /// </summary>
    internal static string? Status(StatementFacts read, StudioText text)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(text);

        PostgresPlatform platform = read.Platform ?? PostgresPlatform.SelfHosted;

        return read.State switch
        {
            StatementsState.NotInstalled when platform.Kind == PostgresPlatformKind.AmazonAurora =>
                "pg_stat_statements is not installed in this database, so there is nothing to show. On " + platform.Name + " its library "
                + "is loaded by default (it is in the DB cluster parameter group's shared_preload_libraries), so it needs only "
                + "CREATE EXTENSION pg_stat_statements in this database: "
                + ServerSettingsFacts.WhoCreates("pg_stat_statements", trusted: false, platform),
            StatementsState.NotInstalled =>
                "pg_stat_statements is not installed in this database, so there is nothing to show. Its library must be in "
                + "shared_preload_libraries (set in " + platform.WhereSettingsChange + "), which takes effect only when the server "
                + "restarts, and CREATE EXTENSION pg_stat_statements must be run in this database. "
                + ServerSettingsFacts.WhoCreates("pg_stat_statements", trusted: false, platform),
            StatementsState.NotPreloaded =>
                "pg_stat_statements is installed in this database, but its library is not loaded: shared_preload_libraries must name "
                + "it - set in " + platform.WhereSettingsChange + " - and the server must restart. Until then there is nothing to show.",
            StatementsState.TooOld =>
                "pg_stat_statements " + read.ExtensionVersion + " is installed in this database, and this page reads 1.8 or later "
                + "(PostgreSQL 13+); ALTER EXTENSION pg_stat_statements UPDATE in this database brings it to the server's own version.",
            StatementsState.NothingRecorded =>
                "pg_stat_statements has recorded no statement of " + text.SourcePossessive + " own role in this database yet - since its "
                + "last reset, or since the extension was installed - so there is nothing to show until the application has run.",
            StatementsState.Failed => "The statements could not be read: " + (read.Failure ?? "the statement checks did not run."),
            _ => null,
        };
    }

    /// <summary>The window, in words.</summary>
    internal static string WindowText(StatementFacts read)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (read.Watched && read.From is { } from && read.To is { } to)
        {
            return "Over the last " + TableText.Span(to - from) + " the statement checks watched (" + TableText.When(from) + " to "
                + TableText.When(to) + "): each statement's change between their first and latest samples, and its share of every "
                + "statement's.";
        }

        string since = read.From is { } began
            ? "since its counters began - " + TableText.Ago(began, read.MeasuredAt) + " for pg_stat_statements as a whole "
              + "(pg_stat_statements_info.stats_reset), later for a statement it evicted and saw again"
            : "since its counters began, at a time this pg_stat_statements does not record (it is older than 1.9)";

        return "Each statement's totals " + since + ". A window appears once the statement checks have run twice, five minutes "
            + "apart, while this page or the Health page's statement checks are open.";
    }

    /// <summary>What the statistics window says about the server's counters, when it bounds the window.</summary>
    internal static string? BasisText(StatementFacts read)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (read.Statistics is not { } statistics || !StatementFacts.RestartsTheCounters(statistics.Reason))
        {
            return null;
        }

        return statistics.LooksReset
            ? "The server's counters look reset since the last check (" + (statistics.Signal ?? statistics.ReasonText)
              + "), so the window starts again from here."
            : "The server's counters began " + statistics.SinceText + " (" + statistics.BasisText + "), "
              + statistics.ReasonText + ": the window never reaches back past then.";
    }

    /// <summary>What the numbers are and are not.</summary>
    internal static string Caveat(StatementFacts read, StudioText text)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(text);

        string evicted = read.Evicted is > 0 and { } count
            ? " pg_stat_statements evicted " + TableText.Count(count) + " entries " + (read.Watched ? "in the window" : "since its last reset")
              + " (pg_stat_statements.max), so some statements' counters are younger than that."
            : string.Empty;

        return "Read from pg_stat_statements as " + text.SourcePossessive + " own Postgres role, which holds that role's statements in "
            + "this database and only the ones that completed: another role's statements are not among them, and another "
            + "application's are when it connects as the same role. Top-level statements only: a statement a function runs is "
            + "inside its caller's time. The " + TableText.Count(read.Statements.Count) + " statements kept are the "
            + TableText.Count(StatementReads.PageTop) + " with the most execution time since their counters began"
            + (read.StorageReads ? ", the " + TableText.Count(StatementReads.PageTop) + " with the most storage reads" : string.Empty)
            + " and the statement checks' own picks, of the " + TableText.Count(read.AllStatements) + " it holds." + evicted;
    }

    /// <summary>What the studio's own line says beside its number: what the meter counts, and which of the studio's statements the list holds.</summary>
    internal string StudioCostText()
    {
        string listed = settings.SqlConsoleRole is { Length: > 0 } role
            ? "Of the studio's own statements, only its reads of pg_stat_statements are listed above: the rest run as SqlConsoleRole ("
              + role + "), whose statements this page does not read."
            : "Every statement the studio runs here is listed above among the others - it runs as " + settings.Text.SourcePossessive
              + " own role - and none is left out.";

        return "Counted from open to close of every connection the studio opens to this database (" + settings.Text.StudioCostCovers
            + "), over the last " + ((int) StudioDatabaseTime.Window.TotalMinutes).ToString(CultureInfo.InvariantCulture)
            + " minutes - the Health page's studio cost line, from the same meter. " + listed;
    }

    /// <summary>A statement's text as this visitor may read it, or why not.</summary>
    private static (string? Text, string? Note) Text(StatementFact fact, StatementScreen screen)
    {
        if (fact.TextWithheld is { } withheld)
        {
            return (null, withheld);
        }

        if (fact.Text is not { } text)
        {
            return (null, fact.TextRead ? null : TextNotReturned);
        }

        SessionStatementText shown = SessionQueryText.Present(text, fact.TextTruncated, screen, HealthStatementQueries.MaxTextCharacters);
        return (shown.Text, shown.Note);
    }

    /// <summary>
    /// A statement's group as this visitor may see it: a group that names a table in a schema they may not see - or that
    /// cannot be checked - is shown by its verb alone, under one key for all of them, never half-named (D27).
    /// </summary>
    private static (string Key, string Label) Group(StatementGroup? group, bool textRead, DatabaseGate? browser)
    {
        if (group is null)
        {
            // No text to group by, or a text that does not start with a word (a parenthesised union, say).
            return textRead ? ("|ungrouped", "not grouped") : ("|unread", "its text was not read");
        }

        if (group.Table is { GatedSchemas.Count: > 0 } table && (browser is null || table.GatedSchemas.Any(browser.IsWithheld)))
        {
            string verb = group.Verb ?? "statement";
            return (verb + "|withheld", verb + " · a table in a schema you may not see");
        }

        return (group.Key, group.Label);
    }

    /// <summary>What a statement's parallel workers say, when its plans were given fewer than they asked for (PostgreSQL 18+).</summary>
    private static string? ParallelNote(StatementFact fact) =>
        fact is { ParallelWorkersToLaunch: > 0 and { } planned, ParallelWorkersLaunched: { } launched } && launched < planned
            ? "Since its counters began, its plans asked for " + TableText.Count(planned) + " parallel workers and were given "
              + TableText.Count(launched) + " (max_parallel_workers or max_worker_processes ran out)."
            : null;

    /// <summary>A refusal, logged at Debug (event 9450) and never audited.</summary>
    private StatementsView Refuse(StudioScope scope, DatabaseRefusal refusal, string reason)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.StatementsPageRefused(settings.Text.Product, scope.StoreKey, scope.DatabaseId, refusal.ToString());
        }

        return StatementsView.Refused(refusal, reason);
    }

    /// <summary>The database browser's gate for this visitor and the database as a whole, or <see langword="null" />.</summary>
    private async Task<DatabaseGateRead?> GateAsync(StudioScope whole, ResolvedSource resolved, CancellationToken cancellationToken)
    {
        if (access is null)
        {
            return null;
        }

        try
        {
            return await access.GateAsync(whole, resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                if (settings.Text.Product == StudioProduct.PostgresStudio)
                {
                    logger.LogDebug(exception, "Postgres Studio could not read the schema gate to mask the statements of {DatabaseId}", resolved.Target.Identity);
                }
                else
                {
                    logger.LogDebug(exception, "Marten Studio could not read the schema gate to mask the statements of {DatabaseId}", resolved.Target.Identity);
                }
            }

            return null;
        }
    }

    /// <summary>One ring entry and one event 9452 for the first read of this database's statements in this circuit.</summary>
    private async Task RecordFirstReadAsync(StudioScope whole, StatementFacts read)
    {
        if (!ledger.TryRecord(TableRowAuditLedger.Key(whole.StoreKey, whole.DatabaseId, LedgerSchema, LedgerRelation, LedgerKind), Signature, out _))
        {
            return;
        }

        audit.Record(
            Action,
            Target,
            succeeded: true,
            "Statements read: " + TableText.Count(read.Statements.Count) + " statements.",
            StudioCapability.ViewDatabaseActivity,
            whole);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.StatementsRead(
                settings.Text.Product,
                await authorization.UserNameAsync().ConfigureAwait(false),
                whole.StoreKey,
                whole.DatabaseId,
                read.Statements.Count);
        }
    }

    /// <summary>The key the studio's cost meter files this database under - read off an unopened connection, which costs no I/O.</summary>
    private static string CostKey(IStudioDatabase database)
    {
        using NpgsqlConnection connection = database.CreateConnection(StudioConnectionUsage.ReadWrite);
        return StudioDatabaseTime.KeyOf(connection);
    }

    /// <summary>
    /// The sentence for a read that failed - the studio's words and the SQLSTATE, never Postgres' own - and the log line: a
    /// throttled Warning (event 9451), because the page polls.
    /// </summary>
    private string Failure(Exception exception, ResolvedSource resolved)
    {
        string storeKey = resolved.Source.Key;
        string databaseId = resolved.Target.Identity;
        PostgresException? postgres = exception as PostgresException ?? exception.InnerException as PostgresException;

        LogLevel level = throttle.WarningOrDebug("Statements.Read", storeKey, databaseId, StudioLogThrottle.KindOf(exception));
        logger.StatementsReadFailed(settings.Text.Product, level, exception, storeKey, databaseId, postgres?.SqlState);

        return PostgresFailure.IsConnectionFailure(exception)
            ? "The studio could not connect to this database."
            : "The statements could not be read" + (postgres is null
                ? " (" + exception.GetType().Name + ")."
                : " (SQLSTATE " + postgres.SqlState + ").");
    }
}
