using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Statements;

/// <summary>
/// What the index advisor is asked: every table its statements name, or one - and, for one, the visitor's own index on it.
/// </summary>
internal sealed record IndexAdviceRequest
{
    /// <summary>
    /// A <see cref="StudioTable.Key" /> (<c>schema.table</c>), or <see langword="null" /> for every table. It must be one a statement
    /// the visitor may see names; the advisor compares, and never composes SQL from it.
    /// </summary>
    public string? Table { get; init; }

    /// <summary>
    /// The visitor's own index on <see cref="Table" />: one to <see cref="HypotheticalIndexCandidates.MaxColumns" /> columns, in order.
    /// Each must be one of the columns the statements' own plans compare on that table, which this run's plans decide.
    /// </summary>
    public IReadOnlyList<string> Columns { get; init; } = [];
}

/// <summary>One statement an index was tried against: its counts and the planner's cost without and with the index.</summary>
/// <param name="QueryId">Its <c>queryid</c>, which the page links to the drill-in by.</param>
/// <param name="Calls">Its calls over the window (or since its counters began).</param>
/// <param name="Before">The planner's total cost of its generic plan now.</param>
/// <param name="After">The planner's total cost with the index imagined, or <see langword="null" /> when that plan could not be compared.</param>
/// <param name="Used">Whether the plan with the index reads it.</param>
internal sealed record IndexAdviceStatement(long QueryId, double Calls, double Before, double? After, bool Used);

/// <summary>One index the advisor tried, with what it came to.</summary>
internal sealed record IndexAdviceItem
{
    /// <summary>The index that was imagined.</summary>
    public required HypotheticalIndex Index { get; init; }

    /// <summary>The table it is on, as <see cref="StudioTable.Key" /> spells it.</summary>
    public required string TableKey { get; init; }

    /// <summary>
    /// What to do about it: the <c>StoreOptions</c> line of a Marten index, <c>CREATE INDEX CONCURRENTLY</c> for any other table - or
    /// <see langword="null" /> for a table named like Marten's that no store declares a line for. Nothing is ever run.
    /// </summary>
    public string? Remedy { get; init; }

    /// <summary>What to know before acting on <see cref="Remedy" />.</summary>
    public string? RemedyNote { get; init; }

    /// <summary>Rows inserted plus rows updated since the table's counters began: the writes that would have maintained the index.</summary>
    public long? TableWrites { get; init; }

    /// <summary>Calls times the planner's cost saved, summed over the statements it helps.</summary>
    public double Saved { get; init; }

    /// <summary><see cref="Saved" /> over the workload: every tried statement's calls times its cost now.</summary>
    public double Share { get; init; }

    /// <summary>How many statements it helps (<see cref="IndexAdvisor.MinSaving" />).</summary>
    public int Helped { get; init; }

    /// <summary>The statements it was tried against.</summary>
    public IReadOnlyList<IndexAdviceStatement> Statements { get; init; } = [];

    /// <summary>"Also tried (a, b), no better" - or "extends (a)" - when a longer index on the same table was folded in or kept beside it.</summary>
    public string? FoldedNote { get; init; }

    /// <summary>Whether it is the visitor's own index.</summary>
    public bool Own { get; init; }
}

/// <summary>What the Statements page's index advisor shows one visitor (1.2-INDEX-A).</summary>
internal sealed record IndexAdviceView
{
    /// <summary>Why nothing was done, or <see cref="DatabaseRefusal.None" />: the Statements page's own gate.</summary>
    public DatabaseRefusal Refusal { get; init; }

    /// <summary>
    /// The gate's sentence for a refusal - or, past the gate, why there is no advice: another run going, the extension's state, a
    /// table no statement names, a failure. A sentence in the studio's words, never Postgres'.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>hypopg's sentence when it is not installed or the role the studio plans as may not use it; nothing was planned.</summary>
    public string? Unavailable { get; init; }

    /// <summary>The table the request named, as accepted.</summary>
    public string? Table { get; init; }

    /// <summary>The indexes that would be used and help, the most saved first.</summary>
    public IReadOnlyList<IndexAdviceItem> Recommended { get; init; } = [];

    /// <summary>The indexes that were tried and would not be used, or would not help.</summary>
    public IReadOnlyList<IndexAdviceItem> NotUsed { get; init; } = [];

    /// <summary>The indexes that were not tried, each as "caption: reason".</summary>
    public IReadOnlyList<string> NotTried { get; init; } = [];

    /// <summary>The statements that were passed over, counted by reason - never listed.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    /// <summary>For a request naming a table: the columns the tried plans compare on it, which the visitor's own index is chosen from.</summary>
    public IReadOnlyList<string> ComparedColumns { get; init; } = [];

    /// <summary>How many statements were planned and compared.</summary>
    public int StatementsTried { get; init; }

    /// <summary>How many statements the run set out to plan.</summary>
    public int StatementsSelected { get; init; }

    /// <summary>How many different indexes the plans suggested, before the cap.</summary>
    public int CandidatesFound { get; init; }

    /// <summary>How many indexes were imagined.</summary>
    public int CandidatesTried { get; init; }

    /// <summary>How many plans were made under an imagined index.</summary>
    public int PlansRun { get; init; }

    /// <summary>Every tried statement's calls times its cost now: what <see cref="IndexAdviceItem.Share" /> divides by.</summary>
    public double Workload { get; init; }

    /// <summary>Which bound stopped the run, in words, or <see langword="null" />.</summary>
    public string? Stopped { get; init; }

    /// <summary>When the run finished.</summary>
    public DateTimeOffset? RanAt { get; init; }

    /// <summary>How long it took.</summary>
    public TimeSpan? Took { get; init; }

    /// <summary>What ran, for "Show SQL": the shapes of the statements - never a text, a name or a value.</summary>
    public IReadOnlyList<HealthStatement> Statements { get; init; } = [];

    /// <summary>
    /// What the run came to, in counts alone: what its audit entry says, and what a card action's entry carries. Never a name, a
    /// text or a cost.
    /// </summary>
    public string? AuditOutcome { get; init; }

    /// <summary>A refusal: nothing but the sentence.</summary>
    public static IndexAdviceView Refused(DatabaseRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };
}
