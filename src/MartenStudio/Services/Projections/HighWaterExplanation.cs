using MartenStudio.Services.Health;

namespace MartenStudio.Services.Projections;

/// <summary>One session that could be holding the high-water mark, as the Explain names it.</summary>
/// <param name="Pid">Its backend.</param>
/// <param name="User">Its role.</param>
/// <param name="Application">Its <c>application_name</c>.</param>
/// <param name="ClientAddress">Where it connects from.</param>
/// <param name="TransactionStart">When its transaction began - before the mark was pinned, or it would not be listed.</param>
/// <param name="State">What it is doing (<c>active</c>, <c>idle in transaction</c>...).</param>
/// <param name="StateChange">When that last changed.</param>
/// <param name="Wait">What it is waiting on, when it is.</param>
/// <param name="Query">
/// The start of its current (or last) statement - masked for the visitor's withheld schemas (D27) once it reaches
/// a page (<see cref="ExplainQueryText" />).
/// </param>
/// <param name="HasTransactionId">Whether it has written in its transaction.</param>
/// <param name="HoldsEventsWriteLock">Whether it holds <c>RowExclusiveLock</c> on <c>mt_events</c> or a partition: mid-append.</param>
/// <param name="RuledOutBecause">Why Marten does not count it as a possible reserver, or <see langword="null" /> when it does.</param>
/// <param name="QueryTruncated">Whether the read cut <paramref name="Query" />.</param>
internal sealed record HighWaterSuspect(
    int Pid,
    string? User,
    string? Application,
    string? ClientAddress,
    DateTimeOffset? TransactionStart,
    string? State,
    DateTimeOffset? StateChange,
    string? Wait,
    string? Query,
    bool HasTransactionId,
    bool HoldsEventsWriteLock,
    string? RuledOutBecause,
    bool QueryTruncated = false);

/// <summary>One prepared transaction in this database.</summary>
/// <param name="Gid">Its global identifier.</param>
/// <param name="Owner">The role that prepared it.</param>
/// <param name="Prepared">When.</param>
internal sealed record PreparedTransactionInfo(string Gid, string? Owner, DateTimeOffset Prepared);

/// <summary>
/// What the D01 Explain found: the questions Marten's own gap-liveness probe asks, put to the database once, when
/// somebody asks.
/// </summary>
/// <remarks>
/// Counts and ages are for anybody the store policy allows for the database as a whole; the sessions and prepared
/// transactions themselves are filled in only for a visitor through <c>ViewDatabaseActivity</c>
/// (<see cref="DetailsShown" />), and are empty otherwise, with <see cref="DetailsWithheldReason" /> saying what it
/// would take.
/// </remarks>
internal sealed record HighWaterExplanation
{
    /// <summary>Why nothing was asked, or <see langword="null" /> when the questions were put.</summary>
    public string? Unavailable { get; init; }

    /// <summary>Whether the mark is pinned behind a gap right now.</summary>
    public bool IsPinned { get; init; }

    /// <summary>The high-water mark.</summary>
    public long? Mark { get; init; }

    /// <summary>When Marten first saw it pinned, in server time.</summary>
    public DateTimeOffset? PinnedSince { get; init; }

    /// <summary>For how long, in server time.</summary>
    public TimeSpan? PinnedFor { get; init; }

    /// <summary>When the questions were asked, in server time.</summary>
    public DateTimeOffset? AskedAt { get; init; }

    /// <summary>Transactions in this database open since before the pin that Marten counts as possible reservers.</summary>
    public int OpenTransactions { get; init; }

    /// <summary>Of those, the ones mid-append: holding <c>RowExclusiveLock</c> on <c>mt_events</c>.</summary>
    public int EventsWriteLockHolders { get; init; }

    /// <summary>Of those, the ones that have written in their transaction.</summary>
    public int Writers { get; init; }

    /// <summary>Old transactions Marten rules out: idle since before the allocation fence, or the daemon's own lock sessions.</summary>
    public int RuledOut { get; init; }

    /// <summary>Write transactions of this database whose start this role cannot see (another role, no <c>pg_read_all_stats</c>).</summary>
    public int HiddenWriters { get; init; }

    /// <summary>Prepared transactions in this database prepared before the pin.</summary>
    public int PreparedBeforePin { get; init; }

    /// <summary>All prepared transactions in this database.</summary>
    public int PreparedTransactions { get; init; }

    /// <summary>The age of the oldest transaction counted in <see cref="OpenTransactions" />.</summary>
    public TimeSpan? OldestOpenTransaction { get; init; }

    /// <summary>Whether <see cref="Sessions" /> and <see cref="Prepared" /> are filled in for this visitor.</summary>
    public bool DetailsShown { get; init; }

    /// <summary>What it would take to see them, naming the setting or the policy.</summary>
    public string? DetailsWithheldReason { get; init; }

    /// <summary>The sessions, oldest transaction first - only with <see cref="DetailsShown" />.</summary>
    public IReadOnlyList<HighWaterSuspect> Sessions { get; init; } = [];

    /// <summary>The prepared transactions, oldest first - only with <see cref="DetailsShown" />.</summary>
    public IReadOnlyList<PreparedTransactionInfo> Prepared { get; init; } = [];

    /// <summary>What was run, for "Show SQL" (D14): text and parameter names, never values.</summary>
    public IReadOnlyList<HealthStatement> Statements { get; init; } = [];

    /// <summary>The answer in one line, for a person: who might be holding it.</summary>
    public string Summary => Unavailable ?? (!IsPinned
        ? "The high-water mark is not pinned behind a gap right now."
        : OpenTransactions + PreparedBeforePin + HiddenWriters == 0
            ? "No transaction that Marten would count as a reserver is open: the daemon should skip this gap at its next poll. If it does not, nothing is running it (D02)."
            : string.Join(", ", Parts()) + " could still fill the gap.");

    /// <summary>An explanation that asked nothing, and why.</summary>
    public static HighWaterExplanation Refused(string reason) => new() { Unavailable = reason };

    private IEnumerable<string> Parts()
    {
        if (OpenTransactions > 0)
        {
            yield return Count(OpenTransactions, "transaction") + " open since before the pin"
                + (EventsWriteLockHolders > 0 ? " (" + Count(EventsWriteLockHolders, "mid-append") + ")" : string.Empty);
        }

        if (PreparedBeforePin > 0)
        {
            yield return Count(PreparedBeforePin, "prepared transaction");
        }

        if (HiddenWriters > 0)
        {
            yield return Count(HiddenWriters, "write transaction") + " of another role, whose start this role cannot see,";
        }
    }

    private static string Count(int count, string what) =>
        count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + what + (count == 1 || what.EndsWith("append", StringComparison.Ordinal) ? string.Empty : "s");
}
