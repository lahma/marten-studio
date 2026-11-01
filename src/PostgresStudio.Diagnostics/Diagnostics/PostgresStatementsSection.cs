namespace PostgresStudio;

/// <summary>
/// The top statements of this database by execution time, from <c>pg_stat_statements</c>. Activity-grade as a whole: other
/// programs' SQL.
/// </summary>
/// <remarks>
/// Read as the source's own role, fenced to <c>userid = current_user</c> and this database, so it shows only that role's
/// statements. A text that still carries a string constant as written, or that is not a plain query or write, is withheld;
/// comments are masked; a text is capped at 4,096 characters.
/// </remarks>
public sealed record PostgresStatementsSection
{
    /// <summary>Whether it was read; when not, the other properties keep their defaults.</summary>
    public PostgresReadOutcome Outcome { get; init; }

    /// <summary>Why not: the extension not installed or not preloaded, a grant missing (named), no run yet.</summary>
    public string? Reason { get; init; }

    /// <summary>When the statement checks read it.</summary>
    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary><c>pg_stat_statements_info.stats_reset</c>, when the extension has it.</summary>
    public DateTimeOffset? StatisticsSince { get; init; }

    /// <summary>The window the figures cover: a change between two reads, or the time since the counters began.</summary>
    public TimeSpan? Window { get; init; }

    /// <summary>All statements' execution time in the window, which <see cref="PostgresStatement.ShareOfTime" /> divides.</summary>
    public double TotalMilliseconds { get; init; }

    /// <summary>The statements, the most execution time first.</summary>
    public IReadOnlyList<PostgresStatement> Top { get; init; } = [];
}
