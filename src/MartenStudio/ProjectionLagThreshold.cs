namespace MartenStudio;

/// <summary>
/// How far behind the event sequence's high-water mark one projection may fall, in events, before Marten Studio colours
/// it amber and red. Set per projection name in <see cref="MartenStudioOptions.ProjectionThresholds" />.
/// </summary>
/// <remarks>
/// <para>
/// Lag is the high-water mark minus a shard's own position. A shard is amber above <see cref="WarningBehind" /> events and
/// red above <see cref="CriticalBehind" />; the same two numbers draw the lines on its chart and are what the falling-behind
/// finding (D04) measures its lag against. Without an entry the studio uses its own 1,000 and 100,000, unless a progression
/// row carries thresholds somebody wrote into <c>warning_behind_threshold</c> and <c>critical_behind_threshold</c> (Marten
/// creates those two columns and writes neither); an entry here wins over both.
/// </para>
/// <para>
/// Both must be greater than zero and <see cref="CriticalBehind" /> at least <see cref="WarningBehind" />, or the host does
/// not start. The entry applies to every store of the application that has a projection of that name.
/// </para>
/// </remarks>
/// <param name="WarningBehind">Amber above this many events behind. Greater than zero.</param>
/// <param name="CriticalBehind">Red above this many events behind. At least <paramref name="WarningBehind" />.</param>
public sealed record ProjectionLagThreshold(long WarningBehind, long CriticalBehind);
