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
        $"An open transaction holds offsets back: the last stable offset is below the high watermark on {KafkaDisplay.Count(partitions, "partition")}.";

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

    // ------------------------------------------------------------------------------------------------
    // Values, compare and pruning (UI-C, D-PAYLOAD decisions 1, 4, 5 and 7)
    // ------------------------------------------------------------------------------------------------

    /// <summary>What a search says when it reads values and one of its topics does not allow the visitor to: whole, and naming no topic.</summary>
    public const string SearchValuesRefusedTopic = "This search reads record values, and one of its topics does not allow you to read them.";

    /// <summary>What a start says when the visitor's access to values fell between the plan and the start.</summary>
    public const string GrantChanged = "Your access changed since this search was planned. Search again.";

    /// <summary>The value part of a record whose topic's capability policy refused <c>ReadPayloads</c>.</summary>
    public const string ValueNotAvailable = "The value of this record is not available to you.";

    /// <summary>The value part of a record of a search that read no values on a topic that would have allowed them.</summary>
    public const string ValueNotReadBySearch =
        "This search could not read values on every topic it named. Open the record page to read this record's value.";

    /// <summary>The value part of a tombstone.</summary>
    public const string ValueTombstone = "Tombstone: a null value, which deletes this key on a compacted topic.";

    /// <summary>The value part of a record whose value has no bytes.</summary>
    public const string ValueEmpty = "Empty: the value has 0 bytes.";

    /// <summary>The value part of a value a payload rule could not be applied to.</summary>
    /// <param name="rule">The rule's name.</param>
    public static string ValueWithheld(string rule) => $"Value withheld: rule {rule} could not be applied.";

    /// <summary>The value part of a value that could not be decoded.</summary>
    /// <param name="reason">Why, in words.</param>
    public static string ValueUndecodable(string reason) => $"The value could not be decoded: {reason}.";

    /// <summary>The line above a CloudEvent's data.</summary>
    public const string ValueCloudEventData = "The event's data:";

    /// <summary>The line a record page adds to a cut value.</summary>
    public const string ValueCutOnRecordPage = "Values above KafkaStudioOptions.Limits.MaxDecodeBytes are not shown whole.";

    /// <summary>The line a search's detail adds to a cut value, before the link.</summary>
    public const string ValueCutOpenRecordPage = "Open the record page to read it whole.";

    /// <summary>The help line in place of the value syntax when <c>ReadPayloads</c> is off.</summary>
    public const string SearchSyntaxNeedsReadPayloads = "value and $.json.paths need KafkaStudioOptions.Capabilities.ReadPayloads.";

    /// <summary>The help line for <c>value</c> and JSON paths when they are available.</summary>
    public const string SearchSyntaxValues =
        "Values: value (the text of a value), and $.json.paths into a JSON value or a CloudEvent's data: $.a.b, $.a[0], $.a[*].b; with = in ^= ~ and, on numbers, < <= > >=.";

    /// <summary>The button that searches again without partition pruning.</summary>
    public const string SearchAllPartitions = "Search all partitions";

    /// <summary>The line above the compare table of two records.</summary>
    public const string CompareHeading = "Compare";

    /// <summary>A masked part, shown wherever a value is shown.</summary>
    /// <param name="rule">The rule's name.</param>
    public static string Masked(string rule) => $"••• (redacted by {rule})";

    /// <summary>Why a value could not be decoded, in words.</summary>
    /// <param name="reason">The engine's reason.</param>
    public static string UndecodableReason(KStudio.Core.Decoding.KafkaUndecodableReason reason) => reason switch
    {
        KStudio.Core.Decoding.KafkaUndecodableReason.InvalidJson => "it is not valid JSON",
        KStudio.Core.Decoding.KafkaUndecodableReason.TooLarge => "it is larger than KafkaStudioOptions.Limits.MaxDecodeBytes",
        KStudio.Core.Decoding.KafkaUndecodableReason.InvalidUtf8 => "it is not valid UTF-8",
        KStudio.Core.Decoding.KafkaUndecodableReason.InvalidBase64 => "its data_base64 is not valid base64",
        KStudio.Core.Decoding.KafkaUndecodableReason.DecoderFailed => "the topic's decoder failed",
        KStudio.Core.Decoding.KafkaUndecodableReason.DecoderRefused => "the topic's decoder refused it",
        KStudio.Core.Decoding.KafkaUndecodableReason.NotACloudEvent => "the topic declares CloudEvents and this is not one",
        _ => reason.ToString(),
    };

    /// <summary>The row of the neighbours table for an offset with no record.</summary>
    public const string NoRecordAtThisOffset = "no record at this offset";

    /// <summary>The help line under "Include uncommitted records".</summary>
    public static string UncommittedHelp => new KStudio.Core.Windows.WindowNote.IncludesUncommitted().Text;

    /// <summary>The line beside a "Copy as predicate" the page refuses because the literal would be longer than the parser reads.</summary>
    public const string CopyLiteralTooLong = "Not copied as a predicate: the value is longer than the 1,000 characters a search literal may have.";

    /// <summary>The kcat line telling the visitor to add their own credentials.</summary>
    public const string KcatCredentialsHint = "# add your own credentials (-X sasl.username=… -X sasl.password=…)";

    /// <summary>The shell's variable a kcat line names when the bootstrap servers are not shown.</summary>
    public const string KcatBootstrapVariable = "\"$BOOTSTRAP\"";

    /// <summary>The notice for topics a pasted link named and the visitor may not search.</summary>
    /// <param name="count">How many were dropped.</param>
    public static string LinkTopicsDropped(int count) =>
        $"This link named {count} topic(s) you cannot search here; the search below leaves them out.";

    /// <summary>The line that says a continued search replaced the matches.</summary>
    /// <param name="records">Records read by the search it continues.</param>
    public static string ContinuedAfter(long records) =>
        $"Continued after offsets read by the previous search ({KafkaDisplay.Count(records, "record")}).";

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
    public static string Refusal(KStudio.Core.Scanning.KafkaScanRefusal refusal, KafkaScanLimits limits) => Refusal(refusal, spent: null);

    /// <summary>
    /// The sentence for a start the engine refused, with the allowance that was spent: the process's, or the one a profile gave the cluster (D-M5
    /// decision 4.5). The engine says which (<see cref="KStudio.Core.Scanning.KafkaSpentBudget" />); a refusal that does not says no number, because
    /// the plan's capped value is the lower of the two whichever ran out.
    /// </summary>
    /// <param name="refusal">Why.</param>
    /// <param name="spent">The allowance that has no bytes left, when the refusal is <c>OwnerBudgetSpent</c>.</param>
    public static string Refusal(KStudio.Core.Scanning.KafkaScanRefusal refusal, KStudio.Core.Scanning.KafkaSpentBudget? spent) => refusal switch
    {
        KStudio.Core.Scanning.KafkaScanRefusal.ProcessBusy => "Two searches are already running in this application. Try again when one has finished.",
        KStudio.Core.Scanning.KafkaScanRefusal.OwnerBusy => "Another search of yours is running, in this tab or another. Stop it, or wait for it to finish.",
        KStudio.Core.Scanning.KafkaScanRefusal.OwnerBudgetSpent =>
            $"You have read {Allowance(spent)}. It frees up as the hour rolls on.",
        KStudio.Core.Scanning.KafkaScanRefusal.ProfileForbids =>
            "This cluster's profile forbids this: Production forbids export and live tail even when they are granted. The host sets the profile with KafkaClusterBuilder.Profile in AddCluster.",
        KStudio.Core.Scanning.KafkaScanRefusal.NeedsConfirmation => NeedsConfirmation,
        KStudio.Core.Scanning.KafkaScanRefusal.PredicateInvalid => PredicateInvalid,
        KStudio.Core.Scanning.KafkaScanRefusal.ShuttingDown => "The application is shutting down.",
        _ => SearchFailed,
    };

    // ------------------------------------------------------------------------------------------------
    // Export and profiles (UI-EXPORT, D45 and D47, decisions 2 and 4.6 of D-M5)
    // ------------------------------------------------------------------------------------------------

    /// <summary>What an export is called in a disabled control's sentence.</summary>
    public const string ExportingRecords = "Exporting records";

    /// <summary>What a live tail is called in a disabled control's sentence.</summary>
    public const string LiveTailAction = "Live tail";

    /// <summary>The audit action of an export.</summary>
    public const string ExportAction = "ExportRecords";

    /// <summary>The line under every export format: the file is what the page shows.</summary>
    /// <param name="records">How many records the file would hold.</param>
    public static string ExportAsShown(int records) =>
        $"{KafkaDisplay.Count(records, "record")}, as shown: masked parts stay masked";

    /// <summary>
    /// What a control says where a cluster's profile forbids an action (D-M5 decision 4.6): one sentence that names the setting a host would change
    /// and says that granting the capability is not enough.
    /// </summary>
    /// <param name="action">The action in the sentence's first words: <see cref="ExportingRecords" /> or <see cref="LiveTailAction" />.</param>
    /// <param name="entry">The cluster.</param>
    public static string ProfileForbids(string action, KafkaClusterEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return $"{action} is off on {entry.DisplayName}: its profile is Production, which forbids export and live tail even when they are granted. "
            + "The host sets the profile with KafkaClusterBuilder.Profile in AddCluster.";
    }

    /// <summary>The "predicate" of an export from the record page: it was opened by its place, not found by a search.</summary>
    public const string RecordExportAuditText = "(none: the record page)";

    /// <summary>The refusal of an export with no records.</summary>
    public const string ExportNothing = "There are no records to save.";

    /// <summary>The refusal of records that are not the source's.</summary>
    public const string ExportRecordsNotFromTopics = "The records do not belong to these topics.";

    /// <summary>The refusal of a value saved from more or fewer than one record.</summary>
    public const string ExportValueFromOneRecord = "A value is saved from one record.";

    /// <summary>The refusal of a third export at once.</summary>
    public const string ExportsBusy = "Two exports are being saved; try again in a moment.";

    /// <summary>The refusal of an export whose reader lost access since the records were read.</summary>
    public const string ExportGrantChanged = "Your access changed since these records were read. Search again.";

    /// <summary>The audit message of an export that ended (cancelled, or a policy or the delivery threw) before any entry was written: the browser may have saved the file.</summary>
    public const string ExportAbandoned = "The export ended before the browser confirmed the file.";

    /// <summary>The audit message of a file the browser never received.</summary>
    public const string ExportNotDelivered = "The file did not reach the browser.";

    /// <summary>The page's line when the export failed outside the engine.</summary>
    public const string ExportFailed = "The export failed. The application's log has the reason.";

    /// <summary>The audit target of an export: <c>export of orders.v1 (search 2026-11-12 10:00Z – 11:00Z)</c>, <c>export of 2 topics (live tail)</c>, <c>export of orders.v1/3/1200</c>.</summary>
    /// <param name="source">Where the records came from.</param>
    /// <param name="topics">The source's topics.</param>
    /// <param name="from">The search's window start, for a search.</param>
    /// <param name="to">The search's window end, for a search.</param>
    /// <param name="place">The record's <c>topic/partition/offset</c>, for a record.</param>
    public static string ExportTarget(KafkaExportSource source, IReadOnlyList<string> topics, DateTimeOffset? from, DateTimeOffset? to, string? place)
    {
        System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
        string named = topics.Count == 1 ? topics[0] : string.Create(invariant, $"{topics.Count} topics");

        switch (source)
        {
            case KafkaExportSource.Record:
                return "export of " + (place ?? named);

            case KafkaExportSource.Tail:
                return "export of " + named + " (live tail)";

            default:
                if (from is { } start && to is { } end)
                {
                    const string Full = "yyyy-MM-dd HH:mm'Z'";
                    DateTimeOffset a = start.ToUniversalTime();
                    DateTimeOffset b = end.ToUniversalTime();
                    string endText = a.Date == b.Date ? b.ToString("HH:mm'Z'", invariant) : b.ToString(Full, invariant);
                    return "export of " + named + " (search " + a.ToString(Full, invariant) + " – " + endText + ")";
                }

                return "export of " + named + " (search)";
        }
    }

    /// <summary>The audit message of an export that was written (D-M5 decision 2.7).</summary>
    /// <param name="written">How many records the file holds.</param>
    /// <param name="bytes">The file's size.</param>
    /// <param name="format">The format's name.</param>
    /// <param name="valuesIncluded">Whether the file holds values.</param>
    /// <param name="leftOut">How many records were left out at the size limit.</param>
    /// <param name="source">Where the records came from.</param>
    /// <param name="sourceRecords">The records the source read.</param>
    /// <param name="sourceBytes">The bytes the source read.</param>
    /// <param name="predicate">The source plan's audit text.</param>
    public static string ExportMessage(int written, long bytes, string format, bool valuesIncluded, int leftOut, KafkaExportSource source, long sourceRecords, long sourceBytes, string predicate)
    {
        string from = source switch
        {
            KafkaExportSource.Tail => "live tail",
            KafkaExportSource.Record => "record",
            _ => "search",
        };
        string values = valuesIncluded ? "; values included" : string.Empty;
        string limited = leftOut > 0 ? $"; {KafkaDisplay.Number(leftOut)} left out at the size limit" : string.Empty;

        return $"{KafkaDisplay.Count(written, "record")} ({KafkaDisplay.Bytes(bytes)}) saved as {format}{values}{limited}; "
            + $"from a {from} that read {KafkaDisplay.Count(sourceRecords, "record")} and {KafkaDisplay.Bytes(sourceBytes)}; predicate: {predicate}";
    }

    /// <summary>The page's line after an export was handed to the browser.</summary>
    /// <param name="fileName">The file's name.</param>
    public static string ExportSaved(string fileName) => "Saved " + fileName + ".";

    /// <summary>The page's line when the file reached the size limit.</summary>
    /// <param name="written">How many records the file holds.</param>
    /// <param name="total">How many records there were.</param>
    /// <param name="maxBytes">The limit.</param>
    public static string ExportLimited(int written, int total, long maxBytes) =>
        $"Saved {KafkaDisplay.Number(written)} of {KafkaDisplay.Number(total)} records: the file reached KafkaStudioOptions.Limits.MaxExportBytes ({KafkaDisplay.Bytes(maxBytes)}).";

    /// <summary>The Settings page's sentence about a cluster's profile, from the limits it comes to.</summary>
    /// <param name="profile">The profile, or <see langword="null" /> for none.</param>
    /// <param name="limits">The cluster's capped limits.</param>
    public static string ProfileSummary(KafkaProfile? profile, KafkaScanLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        string caps = $"scans capped at {KafkaDisplay.Bytes(limits.MaxBytesPerScan)}, {KafkaDisplay.Number(limits.MaxRecordsPerScan)} records and {SpanText(limits.MaxScanDuration)}, "
            + $"{KafkaDisplay.Bytes(limits.BytesPerSecond)}/s and {KafkaDisplay.Bytes(limits.OwnerBytesPerHour)} an hour per visitor on this cluster";

        return profile switch
        {
            null => "None: the host's limits.",
            KafkaProfile.Development => "Development: the host's limits.",
            KafkaProfile.Test => "Test: " + caps + ".",
            _ => "Production: " + caps + "; export and live tail are off.",
        };
    }

    /// <summary>"your hourly allowance of 2 GiB" or "your hourly allowance on this cluster of 512 MiB": the allowance that ran out; with none named, no number.</summary>
    /// <param name="spent">The allowance that has no bytes left, when the engine said which.</param>
    public static string Allowance(KStudio.Core.Scanning.KafkaSpentBudget? spent) => spent switch
    {
        null => "your hourly allowance",
        { Scope: KStudio.Core.Scanning.KafkaBudgetScope.Cluster } => $"your hourly allowance on this cluster of {KafkaDisplay.Bytes(spent.BytesPerHour)}",
        _ => $"your hourly allowance of {KafkaDisplay.Bytes(spent.BytesPerHour)}",
    };

    /// <summary>What a profile does, for its chip's tooltip: Development caps nothing beyond the host's own limits, so it must not say it lowers them.</summary>
    /// <param name="profile">The profile.</param>
    public static string ProfileChipTitle(KafkaProfile profile) => profile switch
    {
        KafkaProfile.Development => "This cluster's profile is Development: it caps nothing; the limits are the ones the host set (Settings shows them).",
        KafkaProfile.Test => "This cluster's profile is Test: it caps each scan at KStudio's default limits (Settings shows them).",
        _ => "This cluster's profile is Production: it caps scans, the byte rate and the hourly allowance, and turns export and live tail off (Settings shows them).",
    };

    /// <summary>The Settings page's warning for a cluster with no profile (M5 decisions, OPEN-1).</summary>
    public const string NoProfileWarning =
        "This cluster has no profile; its limits are the engine's defaults. Set KafkaClusterBuilder.Profile(...) to cap them.";

    /// <summary>The allowance line of the search form for a cluster whose own allowance is the lower one.</summary>
    /// <param name="left">What the visitor has left.</param>
    /// <param name="perHour">The cluster's allowance an hour.</param>
    /// <param name="profile">The cluster's profile.</param>
    public static string ClusterAllowance(long left, long perHour, KafkaProfile profile) =>
        $"{KafkaDisplay.Bytes(left)} of this cluster's {KafkaDisplay.Bytes(perHour)} an hour left ({profile} profile)";

    private static string SpanText(TimeSpan span) =>
        span.TotalMinutes >= 1 && span.Seconds == 0
            ? $"{(int)span.TotalMinutes} {((int)span.TotalMinutes == 1 ? "minute" : "minutes")}"
            : $"{(int)span.TotalSeconds} seconds";

    // ------------------------------------------------------------------------------------------------
    // Related records and duplicates (UI-D, D42, decisions 3 and 4 of D-CORR)
    // ------------------------------------------------------------------------------------------------

    /// <summary>The page's lead: what it searches and what it uses.</summary>
    public const string RelatedLead =
        "Searches the declared topics you may read on this cluster for records that share a correlation value with this one. "
        + "Only values this page already shows you are used: a field that is redacted, cut or absent is never a key.";

    /// <summary>The Keys panel when the anchor offers nothing to search for.</summary>
    public const string RelatedNoKeys =
        "This record has no correlation value KStudio can use: its correlation fields are absent, redacted or cut.";

    /// <summary>The page for a link that names neither a record nor a value to look for.</summary>
    public const string RelatedLinkInvalid = "This link names no record or value to look for.";

    /// <summary>The refusal of a value too long to be searched.</summary>
    public const string RelatedValueTooLong = "The value this link names is longer than the 1,000 characters a search can hold.";

    /// <summary>The refusal of a search in which no topic has a field to compare.</summary>
    public const string RelatedNothingToSearch = "No topic has a field to compare for these values, so there is nothing to search.";

    /// <summary>The sentence that names the set of topics searched by default.</summary>
    public const string RelatedScope = "the declared topics you may read on this cluster";

    /// <summary>The 9601 reason of a related search whose lanes all had nothing to compare.</summary>
    public const string RelatedNothingReason = "no topic had a field to compare";

    /// <summary>The line about the topics a default search left out.</summary>
    /// <param name="count">How many readable declared topics were not searched.</param>
    public static string RelatedMoreTopics(int count) =>
        $"{count} more declared {(count == 1 ? "topic was" : "topics were")} not searched; choose the topics to search.";

    /// <summary>The refusal of a search with every topic unticked.</summary>
    public const string RelatedChooseTopic = "Choose at least one topic to search.";

    /// <summary>The refusal of a seed no readable declared topic has a field for.</summary>
    /// <param name="label">What the link names: <c>document</c> or <c>stream</c>.</param>
    public static string RelatedNoSeedTopic(string label) => $"No declared topic you may read on this cluster has a field for this {label}.";

    /// <summary>The lane of a topic with no query.</summary>
    /// <param name="reasons">Why, in words that name no value.</param>
    public static string RelatedNothingToCompare(IReadOnlyList<string> reasons) =>
        "Nothing to compare on this topic" + (reasons.Count == 0 ? "." : ": " + string.Join(" ", reasons));

    /// <summary>The state of a lane Kafka refused.</summary>
    /// <param name="text">The engine's sentence.</param>
    public static string RelatedNotReadable(string text) => "not readable: " + text;

    /// <summary>The audit target of a related-records scan: <c>records related to orders/2/1042, 3 topics, 2026-11-12 10:00Z – 11:00Z</c>.</summary>
    /// <param name="subject">What the search is about.</param>
    /// <param name="topics">How many topics it names.</param>
    /// <param name="from">The window's start (UTC).</param>
    /// <param name="to">The window's end (UTC).</param>
    public static string RelatedTarget(KafkaRelatedSubject subject, int topics, DateTimeOffset from, DateTimeOffset to)
    {
        System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
        const string Full = "yyyy-MM-dd HH:mm'Z'";
        string what = subject.SeedLabel is { } seed
            ? "a " + seed + " value"
            : string.Create(invariant, $"{subject.AnchorTopic}/{subject.AnchorPartition}/{subject.AnchorOffset}");

        return string.Create(
            invariant,
            $"records related to {what}, {topics} {(topics == 1 ? "topic" : "topics")}, {from.ToUniversalTime().ToString(Full, invariant)} – {to.ToUniversalTime().ToString(Full, invariant)}");
    }

    /// <summary>The heading of the duplicates panel.</summary>
    /// <param name="groups">How many groups.</param>
    public static string DuplicatesHeading(int groups) =>
        $"Possible duplicates: {groups} {(groups == 1 ? "group" : "groups")}";

    /// <summary>The sentence under the duplicates heading.</summary>
    /// <param name="count">How many records were compared.</param>
    public static string DuplicatesScope(int count) =>
        $"Compared among the {KafkaDisplay.Count(count, "record")} found. A copy outside these results is not seen.";

    /// <summary>The note on a group compared by its key and value as shown.</summary>
    public const string DuplicatesComparedAsShown = "Compared as shown: a masked part counts as equal to any other mask.";

    /// <summary>The words of the button a Marten Studio record screen draws to look a record up in Kafka (D44). The studio never spells it.</summary>
    public const string FindInKafka = "Find in Kafka";

    /// <summary>Said instead of Marten links on a record whose <c>tenant-id</c> header is hidden: the address would carry the hidden value.</summary>
    public const string LinksTenantHidden = "No links to Marten records: this record's tenant-id is redacted or cut, and a link would carry it.";

    /// <summary>The heading of the line that links a record to the host's own tracing or log tool.</summary>
    public const string OpenIn = "Open in";

    /// <summary>The heading of a topic page's facts from a cluster source.</summary>
    /// <param name="source">The source's name, for example <c>Wolverine</c>.</param>
    public static string DiscoveredHeading(string source) => "Discovered from " + source;

    /// <summary>The sentence under the discovered facts: what they are and are not.</summary>
    public const string DiscoveredLead =
        "Read from the application's Wolverine configuration when KStudio first needed it. They describe how the application uses the topic, not what it holds.";

    /// <summary>The heading of the Settings page's section on what a cluster source listed and could not list.</summary>
    /// <param name="sources">The names of the sources, for example <c>Wolverine</c>.</param>
    public static string DiscoveryHeading(string sources) => "Discovered from " + sources;

    /// <summary>The sentence under the Settings page's discovery section.</summary>
    public const string DiscoveryLead =
        "KStudio read the application's configuration once, and started, connected and wrote nothing. Discovery adds no redaction: declare rules for every topic that carries personal data.";

    /// <summary>What the Settings page says of a cluster a source listed.</summary>
    /// <param name="source">The source's name.</param>
    public static string DiscoveryListed(string source) => "This cluster was listed from the application's " + source + " configuration.";

    /// <summary>The heading of what a source did not list.</summary>
    public const string DiscoveryNotesHeading = "Not listed";

    /// <summary>The role a discovered topic has, in words.</summary>
    /// <param name="role">The role.</param>
    public static string DiscoveredRole(KStudio.Core.Clusters.KafkaTopicRole role) => role switch
    {
        KStudio.Core.Clusters.KafkaTopicRole.Listener => "Listener",
        KStudio.Core.Clusters.KafkaTopicRole.Sender => "Sender",
        KStudio.Core.Clusters.KafkaTopicRole.ListenerAndSender => "Listener and sender",
        KStudio.Core.Clusters.KafkaTopicRole.DeadLetter => "Dead letters",
        _ => role.ToString(),
    };
}
