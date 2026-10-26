namespace PostgresStudio;

/// <summary>One statement's counters over the section's window.</summary>
public sealed record PostgresStatement
{
    /// <summary><c>queryid</c>. Written as a string in JSON, because it does not fit a JavaScript number.</summary>
    public long QueryId { get; init; }

    /// <summary>The normalised text, comments masked and capped; null when withheld.</summary>
    public string? Text { get; init; }

    /// <summary>Whether the text was cut.</summary>
    public bool TextTruncated { get; init; }

    /// <summary>Why the text is withheld (it still carries a literal, or is not a plain query or write), or null.</summary>
    public string? TextWithheld { get; init; }

    /// <summary>Calls.</summary>
    public long Calls { get; init; }

    /// <summary>Execution time, in milliseconds.</summary>
    public double TotalMilliseconds { get; init; }

    /// <summary>The mean execution time a call, in milliseconds.</summary>
    public double MeanMilliseconds { get; init; }

    /// <summary>Its share of every statement's execution time in the window, 0 to 1.</summary>
    public double ShareOfTime { get; init; }

    /// <summary>Rows returned or affected.</summary>
    public long Rows { get; init; }

    /// <summary>Shared buffers found in the cache.</summary>
    public long SharedBlocksHit { get; init; }

    /// <summary>Shared blocks read from outside the cache.</summary>
    public long SharedBlocksRead { get; init; }

    /// <summary>Blocks spilled to temporary files.</summary>
    public long TempBlocksWritten { get; init; }

    /// <summary>Aurora's storage-level reads, from <c>aurora_stat_statements</c>; null elsewhere.</summary>
    public long? StorageBlocksRead { get; init; }
}
