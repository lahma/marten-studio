namespace KStudio.Sample.Kafka;

/// <summary>What the judge decided about a cluster.</summary>
internal enum SampleKafkaVerdict
{
    /// <summary>The cluster has none of the sample's topics: seed it (when the host may).</summary>
    Empty,

    /// <summary>The sample seeded this cluster already (the marker topic exists): leave it alone.</summary>
    AlreadySeeded,

    /// <summary>The cluster has an <c>orders.v1</c> the sample did not seed, or one an earlier sample seeded: stop.</summary>
    Refuse,
}

/// <summary>The judge's decision, with the line to stop with when it is a refusal.</summary>
/// <param name="Verdict">The decision.</param>
/// <param name="Message">The one line to print before stopping, for <see cref="SampleKafkaVerdict.Refuse" />.</param>
internal sealed record SampleKafkaJudgement(SampleKafkaVerdict Verdict, string? Message = null);

/// <summary>
/// The marker topic rule (D-UI decision 18): a cluster is the sample's own if it carries the marker topic the seeder writes last, and a
/// cluster that has an <c>orders.v1</c> without one is somebody's - the sample stops before it browses, or seeds on top of, a topic it did not make.
/// </summary>
internal static class SampleKafkaJudge
{
    /// <summary>The line the host stops with on a foreign <c>orders.v1</c>.</summary>
    public const string ForeignOrdersTopic =
        "KStudio sample: this Kafka already has an orders.v1 topic that the sample did not seed. Point ConnectionStrings:Kafka at an empty cluster, or delete the topic.";

    /// <summary>The line the host stops with on a cluster an earlier version of the sample seeded.</summary>
    public const string EarlierSeed =
        "KStudio sample: this Kafka holds an earlier sample's topics. Delete the container (or unset KSTUDIO_SAMPLE_REUSE) so that the sample can seed it again.";

    /// <summary>Judges a cluster by the names of its topics.</summary>
    /// <param name="topics">Every topic of the cluster.</param>
    public static SampleKafkaJudgement Judge(IEnumerable<string> topics)
    {
        ArgumentNullException.ThrowIfNull(topics);

        HashSet<string> present = new(topics, StringComparer.Ordinal);

        if (present.Contains(KafkaSeedPlan.MarkerTopic))
        {
            return new SampleKafkaJudgement(SampleKafkaVerdict.AlreadySeeded);
        }

        if (present.Contains(KafkaSeedPlan.EarlierMarkerTopic))
        {
            return new SampleKafkaJudgement(SampleKafkaVerdict.Refuse, EarlierSeed);
        }

        return present.Contains(KafkaSeedPlan.OrdersTopic)
            ? new SampleKafkaJudgement(SampleKafkaVerdict.Refuse, ForeignOrdersTopic)
            : new SampleKafkaJudgement(SampleKafkaVerdict.Empty);
    }
}
