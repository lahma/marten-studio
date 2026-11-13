namespace KStudio;

/// <summary>An index's answer: hints, or none (which says nothing about where the record is not).</summary>
public sealed class KafkaIndexAnswer
{
    private KafkaIndexAnswer(IReadOnlyList<KafkaIndexHint> ranges) => Ranges = ranges;

    /// <summary>The answer that points nowhere: the search reads its whole window.</summary>
    public static KafkaIndexAnswer None { get; } = new([]);

    /// <summary>The offset ranges the index named, in the order it named them.</summary>
    public IReadOnlyList<KafkaIndexHint> Ranges { get; }

    /// <summary>An answer that names where records may be.</summary>
    /// <param name="hints">The hints; none at all is <see cref="None" />.</param>
    /// <returns>The answer.</returns>
    public static KafkaIndexAnswer Hints(IEnumerable<KafkaIndexHint> hints)
    {
        ArgumentNullException.ThrowIfNull(hints);

        KafkaIndexHint[] copy = [.. hints.Where(static x => x is not null)];
        return copy.Length == 0 ? None : new KafkaIndexAnswer(copy);
    }
}
