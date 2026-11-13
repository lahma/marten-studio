namespace KStudio;

/// <summary>Where to look: offsets <c>[FromOffset, ToOffset)</c> of one partition.</summary>
/// <param name="Topic">The topic. A hint about a topic the search did not name is dropped unseen.</param>
/// <param name="Partition">The partition.</param>
/// <param name="FromOffset">The first offset, inclusive.</param>
/// <param name="ToOffset">One past the last offset.</param>
public sealed record KafkaIndexHint(string Topic, int Partition, long FromOffset, long ToOffset);
