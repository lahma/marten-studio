using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health;

/// <summary>
/// What a finding offers to read beyond what its sweep already did, on demand, for the visitor who presses it (1.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>An action is one visitor's, never the sweep's.</b> The sweep is visitor-less and shared; an action runs under
/// <em>this</em> visitor's gates, once per press, and its result is drawn for them and dropped. It is keyed by the check's
/// code and offered beside a finding, never carried on it: nothing about an action can be cached in a sweep, mapped into
/// the diagnostics API or exported - the types live in this assembly, which the engine cannot reference (D31).
/// </para>
/// <para>
/// <b>An action never writes.</b> Anything that changes state belongs to its own screen and capability, not to the Health
/// page. An action runs its statements only inside <see cref="ReadOnlySqlSession" />, under the studio's
/// <c>QueryTimeout</c> as <c>statement_timeout</c>, as the console's role.
/// </para>
/// </remarks>
internal interface IHealthFindingAction
{
    /// <summary>A stable, ordinal name - <c>e06.longest-streams</c>. Never in an address, never persisted.</summary>
    string Id { get; }

    /// <summary>The check it belongs to - <c>E06</c>.</summary>
    string Code { get; }

    /// <summary>The button, verb first - "Find the 20 longest streams".</summary>
    string Label { get; }

    /// <summary>What it reads, what it costs and that it is audited, in one or two sentences.</summary>
    string Description { get; }

    /// <summary>What the Activity ring calls a press - "Find the longest streams".</summary>
    string AuditAction { get; }

    /// <summary>What a press reads, never a value - "the event store's streams (mt_streams)".</summary>
    string AuditTarget { get; }

    /// <summary>The least gate a press runs under; the finding's own is asked as well, and the stricter wins.</summary>
    HealthGate Gate { get; }

    /// <summary>
    /// The capability a press requires, or <see langword="null" /> for none. A capability is declared by an action that reads what a
    /// capability guards: other sessions or statements, rows of a non-Marten table, visitor-shaped SQL. Its write policy is
    /// asked after the capability, as every capability's is (hard rule 5).
    /// </summary>
    StudioCapability? Capability { get; }

    /// <summary>Whether this finding offers the action. Pure: no I/O, no policy.</summary>
    /// <param name="finding">The finding.</param>
    bool AppliesTo(HealthFinding finding);

    /// <summary>
    /// Runs the action, after every gate the runner asks has passed. An expected state is an outcome, never an exception; an
    /// unexpected exception is the runner's to log and answer.
    /// </summary>
    /// <param name="context">What the runner resolved and asked.</param>
    /// <param name="cancellationToken">Cancelled when the page is left.</param>
    Task<HealthActionResult> RunAsync(HealthActionContext context, CancellationToken cancellationToken);
}

/// <summary>What the runner hands an action: the scope as selected, the gates it may still ask, and its cost bounds.</summary>
/// <param name="Resolved">
/// The resolver's own record for the scope as the visitor selected it (a studio's checks read its subtype - MartenStudio's
/// <c>ResolvedScope</c>), tenant and all.
/// </param>
/// <param name="Whole">The database as a whole: the registered key, the database's identity and no tenant (D29).</param>
/// <param name="Finding">The finding as this visitor's report shows it.</param>
/// <param name="Gate">This visitor's gates, asked lazily; the same implementation the report asked.</param>
/// <param name="Session">The read-only session's options: <c>QueryTimeout</c> as the statement timeout and the console's role.</param>
/// <param name="CommandTimeoutSeconds">The command timeout to set: the statement timeout, rounded up, and five seconds.</param>
internal sealed record HealthActionContext(
    ResolvedSource Resolved,
    StudioScope Whole,
    HealthReportFinding Finding,
    HealthVisitorGate Gate,
    ReadOnlySqlOptions Session,
    int CommandTimeoutSeconds);

/// <summary>How a press ended.</summary>
internal enum HealthActionOutcome
{
    /// <summary>It ran and has a result to draw.</summary>
    Done = 0,

    /// <summary>It was not run, for a reason the visitor can act on or at least read.</summary>
    Refused = 1,

    /// <summary>It was cut by <c>statement_timeout</c> or a lock that would not come.</summary>
    TimedOut = 2,

    /// <summary>It failed for a reason nobody chose; the server log has the details.</summary>
    Failed = 3,
}

/// <summary>
/// What a press answered: a summary and a bounded table, or a sentence. Held by the page and nowhere else.
/// </summary>
internal sealed record HealthActionResult
{
    /// <summary>The most rows a result carries; the runner cuts the rest.</summary>
    public const int MaxRows = 100;

    /// <summary>How it ended.</summary>
    public required HealthActionOutcome Outcome { get; init; }

    /// <summary>When it was read.</summary>
    public required DateTimeOffset RanAt { get; init; }

    /// <summary>How long it took, as the runner measured it; <see langword="null" /> for a press that never ran.</summary>
    public TimeSpan? Took { get; init; }

    /// <summary>For a refusal, a timeout or a failure: why, in words that name no value read.</summary>
    public string? Reason { get; init; }

    /// <summary>For a result: what it found, in a sentence.</summary>
    public string? Summary { get; init; }

    /// <summary>For a result: what its table is, for the scroll region's label.</summary>
    public string? TableLabel { get; init; }

    /// <summary>The table's headings.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>The table's rows, at most <see cref="MaxRows" />.</summary>
    public IReadOnlyList<HealthActionRow> Rows { get; init; } = [];

    /// <summary>What the result leaves out, or cannot say.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Remedies, held to the rules every finding's are (never DDL, never a privilege change).</summary>
    public IReadOnlyList<HealthRemedy> Remedies { get; init; } = [];

    /// <summary>What it ran, for "Show SQL": text and parameter names, never values.</summary>
    public IReadOnlyList<HealthStatement> Statements { get; init; } = [];

    /// <summary>What the audit entry says: counts only, never a value read. <see langword="null" /> to use <see cref="Reason" />.</summary>
    public string? AuditMessage { get; init; }

    /// <summary>The capability the audit entry is filed under, when it differs from the action's own.</summary>
    public StudioCapability? AuditCapability { get; init; }

    /// <summary>A refusal.</summary>
    /// <param name="reason">Why, in words.</param>
    /// <param name="at">When.</param>
    public static HealthActionResult Refused(string reason, DateTimeOffset at) =>
        new() { Outcome = HealthActionOutcome.Refused, RanAt = at, Reason = reason };

    /// <summary>A statement that ran out of time, or of patience for a lock.</summary>
    /// <param name="reason">Why, in words.</param>
    /// <param name="at">When.</param>
    public static HealthActionResult TimedOut(string reason, DateTimeOffset at) =>
        new() { Outcome = HealthActionOutcome.TimedOut, RanAt = at, Reason = reason };

    /// <summary>A failure nobody chose.</summary>
    /// <param name="reason">Why, in words that carry no message from the server.</param>
    /// <param name="at">When.</param>
    public static HealthActionResult Failed(string reason, DateTimeOffset at) =>
        new() { Outcome = HealthActionOutcome.Failed, RanAt = at, Reason = reason };
}

/// <summary>One row of a result's table.</summary>
/// <param name="Cells">Its cells, one per column.</param>
internal sealed record HealthActionRow(IReadOnlyList<HealthActionCell> Cells);

/// <summary>One cell of a result's table, drawn as text (Blazor encodes it).</summary>
/// <param name="Text">What it says.</param>
internal sealed record HealthActionCell(string Text)
{
    /// <summary>A link to a studio screen, which carries the active scope as every finding's link does.</summary>
    public HealthLink? Link { get; init; }

    /// <summary>Drawn in the monospace face.</summary>
    public bool Mono { get; init; }

    /// <summary>Offers a copy button for the text.</summary>
    public bool Copy { get; init; }

    /// <summary>Right-aligned, as a number.</summary>
    public bool Number { get; init; }

    /// <summary>A time, drawn in the visitor's selected zone instead of <see cref="Text" />.</summary>
    public DateTimeOffset? Time { get; init; }
}

/// <summary>What a finding offers, for its card: the button, and what pressing it does.</summary>
/// <param name="Id">The action's id.</param>
/// <param name="Label">The button.</param>
/// <param name="Description">What it reads and costs.</param>
internal sealed record HealthActionOffer(string Id, string Label, string Description)
{
    /// <summary>
    /// The least gate a press runs under (<see cref="IHealthFindingAction.Gate" />): what lets the page leave out an offer whose
    /// gate its own report already says the visitor does not pass. The runner asks it again on every press all the same.
    /// </summary>
    public HealthGate Gate { get; init; }
}
