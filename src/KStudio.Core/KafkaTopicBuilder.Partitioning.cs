namespace KStudio;

public sealed partial class KafkaTopicBuilder
{
    /// <summary>The partitioner the host declared, when it declared one.</summary>
    internal KafkaPartitioner? PartitionerValue { get; private set; }

    /// <summary>
    /// Declares how the topic's producers choose a partition from a key. It is a claim, not a fact: before a key search reads fewer
    /// partitions, the engine samples the window, and one record that disagrees turns pruning off for the topic.
    /// </summary>
    /// <param name="partitioner">The partitioner.</param>
    /// <returns>This builder.</returns>
    public KafkaTopicBuilder Partitioner(KafkaPartitioner partitioner)
    {
        ArgumentNullException.ThrowIfNull(partitioner);
        PartitionerValue = partitioner;
        return this;
    }
}
