namespace KStudio.Services;

/// <summary>
/// KStudio's own sentences (D34): the host studio's <c>StudioText</c> does not change, so none of its words are borrowed. One place,
/// so that a rename of the product is one edit.
/// </summary>
internal static class KafkaStudioText
{
    /// <summary>The module's key: its audit scope kind, and its segment under the studio's mount.</summary>
    public const string Key = "kafka";

    /// <summary>The product on first prominent use: the rail's credit line, the capability heading, the README.</summary>
    public const string ProductName = "KStudio for Apache Kafka®";

    /// <summary>The capability chip's group heading.</summary>
    public const string CapabilitiesHeading = "KStudio for Apache Kafka® reads";

    /// <summary>The sentence the anonymous banner adds after the host studio's own.</summary>
    public const string ExposedSentence =
        "KStudio's pages show the clusters, topics, partitions and consumer groups this application's Kafka credentials can describe, "
        + "and whatever its capabilities read.";

    /// <summary>The disclaimer every Kafka page ends with.</summary>
    public const string Trademark =
        "Apache Kafka is a trademark of the Apache Software Foundation. KStudio is not affiliated with or endorsed by the Apache Software Foundation.";

    /// <summary>The Activity entry of a refusal by a cluster or topic policy: it names neither, so a refusal says nothing it should not.</summary>
    public const string RefusedEntry = "Refused by the cluster or topic policy.";

    /// <summary>What the page says when the policy refused the cluster the visitor is on.</summary>
    public const string ClusterNotAuthorized = "You are not authorized to view this cluster. Pick another one from the cluster list above.";

    /// <summary>What a request naming a refused topic says: whole, and naming nothing.</summary>
    public const string RequestRefused = "This request was refused by the cluster or topic policy.";

    /// <summary>What a catalog page says when a poll failed and the last data is kept (D-UI decision 7).</summary>
    public const string CouldNotRefresh = "Could not refresh: showing what was last read. The next refresh will try again.";

    /// <summary>What a catalog page says when its first read threw; the engine returns errors as values, so this is a bug or a dying circuit, and the log has it.</summary>
    public const string ReadFailed = "Could not read from the cluster. The application's log has the reason.";

    /// <summary>A declared topic metadata does not list.</summary>
    public const string TopicNotListed = "Not listed: it does not exist, or this application's credentials cannot describe it";

    /// <summary>The topic page for a name that is invalid, missing or refused: one sentence for all three, so a refusal reads like a miss.</summary>
    /// <param name="name">The visitor's own text from the address; written through Blazor's encoding.</param>
    public static string TopicNotAvailable(string name) => $"Topic {name} was not found, or is not available to you.";

    /// <summary>The topic page with no <c>topic</c> in its address.</summary>
    public const string TopicMissing = "No topic was named. Open one from the Topics page.";

    /// <summary>The open-transaction warning of a topic page.</summary>
    /// <param name="partitions">How many partitions have one.</param>
    public static string OpenTransaction(int partitions) =>
        $"An open transaction holds offsets back: the last stable offset is below the high watermark on {partitions} partition(s).";

    /// <summary>A topic page with no declared consumer group.</summary>
    public const string NoConsumerGroups =
        "No consumer groups are declared for this cluster: add KafkaClusterBuilder.ConsumerGroup(\"…\") to show their lag here.";

    /// <summary>The lead paragraph of the cluster settings page.</summary>
    public const string SettingsLead =
        "The settings this application handed KStudio for this cluster, one by one. A secret shows only whether it is set; a path is shown and the "
        + "file is never read; a setting KStudio does not know is listed with its value withheld.";

    /// <summary>The refusal of a host that registered no studio, at start.</summary>
    public const string NoStudio =
        "AddKafkaStudio() was called in an application that registered no studio. KStudio is a module of Marten Studio or Postgres Studio: "
        + "call services.AddMartenStudio() or services.AddPostgresStudio() as well, and map that studio with app.MapMartenStudio() or "
        + "app.MapPostgresStudio(); the Kafka pages are served under its path, at /kafka.";

    /// <summary>The refusal of a host that enabled a capability no policy answers, at start.</summary>
    /// <param name="capabilities">The capabilities enabled, comma separated.</param>
    /// <param name="readOnlyOption">The host studio's master switch, spelled as its options spell it.</param>
    public static string CapabilityWithoutPolicy(string capabilities, string readOnlyOption) =>
        $"KStudio has {capabilities} enabled and no policy decides who may use them: every visitor admitted to the studio could read the "
        + "records of every topic this application's credentials can read. Set KafkaStudioOptions.CapabilityAuthorizationPolicy (asked for each "
        + "capability use, once per topic), or KafkaStudioOptions.ClusterAuthorizationPolicy (asked for every read, once per topic), or turn the "
        + $"capabilities off; {readOnlyOption} = true turns them all off.";
}
