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
