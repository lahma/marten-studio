namespace PostgresStudio;

/// <summary>Client sessions counted by state: the database's, or one application's.</summary>
public sealed record PostgresConnectionCounts
{
    /// <summary>The application name, <c>""</c> for sessions that set none, null for the database's total.</summary>
    public string? ApplicationName { get; init; }

    /// <summary>Every session, whatever the role may see of it.</summary>
    public int Total { get; init; }

    /// <summary>Running a statement.</summary>
    public int Active { get; init; }

    /// <summary>Idle, outside a transaction.</summary>
    public int Idle { get; init; }

    /// <summary>Idle inside a transaction, aborted ones included.</summary>
    public int IdleInTransaction { get; init; }

    /// <summary>Waiting on a lock.</summary>
    public int Waiting { get; init; }

    /// <summary>
    /// Sessions whose state the role may not see: another role's, without <c>pg_read_all_stats</c>. Counted in
    /// <see cref="Total" /> and in no state.
    /// </summary>
    public int StateUnknown { get; init; }

    /// <summary>The longest open transaction among them, or null when none is open (or none the role may see).</summary>
    public TimeSpan? OldestTransaction { get; init; }
}
