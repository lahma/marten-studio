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

    // ------------------------------------------------------------------------------------------------
    // Search, the record page and the record detail (UI-B, D-UI decisions 10 to 16)
    // ------------------------------------------------------------------------------------------------

    /// <summary>What a search says when its request names a topic the visitor may not use: whole, and naming nothing.</summary>
    public const string SearchNamesRefusedTopic = "This search names a topic that is not available to you.";

    /// <summary>A search with no topic, or more than <see cref="MaxSearchTopics" />.</summary>
    public const string SearchTopicCount = "A search reads 1 to 20 topics.";

    /// <summary>The most topics one search reads.</summary>
    public const int MaxSearchTopics = 20;

    /// <summary>The longest search text the page accepts.</summary>
    public const int MaxPredicateLength = 4_096;

    /// <summary>A search text above <see cref="MaxPredicateLength" />.</summary>
    public const string PredicateTooLong = "The search text is longer than 4,096 characters.";

    /// <summary>A search whose name is not a topic name.</summary>
    public const string SearchTopicInvalid = "A topic name in this search is not valid.";

    /// <summary>The line under a plan with errors.</summary>
    public const string PredicateInvalid = "The search text has errors; see above.";

    /// <summary>A start that needed the confirmation and did not get it.</summary>
    public const string NeedsConfirmation = "This search needs your confirmation first.";

    /// <summary>The 9601 reason of a dialog the visitor cancelled.</summary>
    public const string NotConfirmed = "not confirmed";

    /// <summary>The page's line when the search failed outside the engine.</summary>
    public const string SearchFailed = "The search failed. The application's log has the reason.";

    /// <summary>The record page for a link whose partition, offset or topic is not valid.</summary>
    public const string RecordLinkInvalid = "This record link is not valid.";

    /// <summary>The line that says why the value is not shown.</summary>
    public const string ValueNotShown = "The value is not shown: reading values needs ReadPayloads, which arrives in a later release.";

    /// <summary>The row of the neighbours table for an offset with no record.</summary>
    public const string NoRecordAtThisOffset = "no record at this offset";

    /// <summary>The help line under "Include uncommitted records".</summary>
    public const string UncommittedHelp = "Includes uncommitted records: a record may belong to a transaction that is later aborted";

    /// <summary>The line beside a "Copy as predicate" the page refuses because the literal would be longer than the parser reads.</summary>
    public const string CopyLiteralTooLong = "Not copied as a predicate: the value is longer than the 1,000 characters a search literal may have.";

    /// <summary>The kcat line telling the visitor to add their own credentials.</summary>
    public const string KcatCredentialsHint = "# add your own credentials (-X sasl.username=… -X sasl.password=…)";

    /// <summary>The shell's variable a kcat line names when the bootstrap servers are not shown.</summary>
    public const string KcatBootstrapVariable = "'$BOOTSTRAP'";

    /// <summary>The notice for topics a pasted link named and the visitor may not search.</summary>
    /// <param name="count">How many were dropped.</param>
    public static string LinkTopicsDropped(int count) =>
        $"This link named {count} topic(s) you cannot search here; the search below leaves them out.";

    /// <summary>The line that says a continued search replaced the matches.</summary>
    /// <param name="records">Records read by the search it continues.</param>
    public static string ContinuedAfter(long records) =>
        $"Continued after offsets read by the previous search ({KafkaDisplay.Number(records)} records).";

    /// <summary>The audit target of a search: <c>orders.v1, payments.v1 · 2026-11-12 10:00Z – 11:00Z</c>.</summary>
    /// <param name="topics">The topics.</param>
    /// <param name="from">The window's start (UTC).</param>
    /// <param name="to">The window's end (UTC).</param>
    public static string SearchTarget(IReadOnlyList<string> topics, DateTimeOffset from, DateTimeOffset to)
    {
        const string Full = "yyyy-MM-dd HH:mm'Z'";
        System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
        DateTimeOffset start = from.ToUniversalTime();
        DateTimeOffset end = to.ToUniversalTime();
        string endText = start.Date == end.Date ? end.ToString("HH:mm'Z'", invariant) : end.ToString(Full, invariant);

        return string.Join(", ", topics) + " · " + start.ToString(Full, invariant) + " – " + endText;
    }

    /// <summary>What the audit says of a search text that did not parse: nobody can know which literal was on a redacted field.</summary>
    /// <param name="length">The text's length.</param>
    public static string PredicateDidNotParse(int length) => $"(a predicate that did not parse, {length} characters)";

    /// <summary>The sentence for a start the engine refused (D-UI decision 11).</summary>
    /// <param name="refusal">Why.</param>
    /// <param name="limits">The limits the plan ran under.</param>
    public static string Refusal(KStudio.Core.Scanning.KafkaScanRefusal refusal, KafkaScanLimits limits) => refusal switch
    {
        KStudio.Core.Scanning.KafkaScanRefusal.ProcessBusy => "Two searches are already running in this application. Try again when one has finished.",
        KStudio.Core.Scanning.KafkaScanRefusal.OwnerBusy => "Another search of yours is running, in this tab or another. Stop it, or wait for it to finish.",
        KStudio.Core.Scanning.KafkaScanRefusal.OwnerBudgetSpent =>
            $"You have read your hourly allowance of {KafkaDisplay.Bytes(limits.OwnerBytesPerHour)}. It frees up as the hour rolls on.",
        KStudio.Core.Scanning.KafkaScanRefusal.NeedsConfirmation => NeedsConfirmation,
        KStudio.Core.Scanning.KafkaScanRefusal.PredicateInvalid => PredicateInvalid,
        KStudio.Core.Scanning.KafkaScanRefusal.ShuttingDown => "The application is shutting down.",
        _ => SearchFailed,
    };
}
