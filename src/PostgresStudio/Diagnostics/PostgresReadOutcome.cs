namespace PostgresStudio;

/// <summary>How reading something ended: a database, a check or a section. May gain members; treat an unknown value as <see cref="Failed" />.</summary>
public enum PostgresReadOutcome
{
    /// <summary>It was read: a check judged what it read, with or without findings.</summary>
    Read = 0,

    /// <summary>The host's PostgreSQL, platform or Marten lacks what it needs, so it was never run.</summary>
    NotApplicable = 1,

    /// <summary>The host's choice: no privilege, no extension, nothing to read yet. The reason names it.</summary>
    Unavailable = 2,

    /// <summary>A statement, the check or the sweep ran out of time.</summary>
    TimedOut = 3,

    /// <summary>A failure nobody chose; the server log has it (event 9222 for a check, 9251 for a section).</summary>
    Failed = 4,

    /// <summary>No such source, or no such database in it.</summary>
    NotFound = 5,

    /// <summary>Activity-grade, and the request did not include it: whether it found anything is not said.</summary>
    Withheld = 6,
}
