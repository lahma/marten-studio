namespace PostgresStudio;

/// <summary>
/// How much a finding matters. Worst first: <c>severity &lt;= Warning</c> is "Critical or Warning". May gain members; treat
/// an unknown value as <see cref="Warning" />.
/// </summary>
public enum PostgresSeverity
{
    /// <summary>Something is broken now, or will be soon, and somebody should act today.</summary>
    Critical = 0,

    /// <summary>Something is wrong or risky, and is worth fixing on purpose.</summary>
    Warning = 1,

    /// <summary>A suggestion: a setting that would let the application, or the studio, tell you more.</summary>
    Info = 2,
}
