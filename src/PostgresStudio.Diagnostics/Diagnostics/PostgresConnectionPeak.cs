namespace PostgresStudio;

/// <summary>One application's own peak in the connection history's window. Activity-grade: it names an application.</summary>
public sealed record PostgresConnectionPeak
{
    /// <summary>Its <c>application_name</c>, or <see langword="null" /> for sessions that set none.</summary>
    public string? ApplicationName { get; init; }

    /// <summary>The most connections it held in one sample.</summary>
    public int Peak { get; init; }

    /// <summary>When that was, the first time.</summary>
    public DateTimeOffset At { get; init; }
}
