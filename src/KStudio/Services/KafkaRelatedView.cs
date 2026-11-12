using System.Globalization;

using KStudio.Core.Correlation;
using KStudio.Core.Scanning;

namespace KStudio.Services;

/// <summary>One lane of the timeline as the page draws it.</summary>
/// <param name="Topic">The topic.</param>
/// <param name="Matches">How many records were found on it.</param>
/// <param name="First">The time of its first record, or <see langword="null" /> when there is none.</param>
/// <param name="State">The lane's state in words: <c>not searched yet</c>, <c>reading</c>, <c>read</c>, <c>stopped: …</c>, <c>not readable: …</c> or <c>nothing to compare: …</c>.</param>
/// <param name="Rank">The lane's group in the order of decision 3.5: 0 the anchor's topic, 1 a lane with records, 2 a lane without, 3 a lane that is not readable.</param>
internal sealed record KafkaTimelineLane(string Topic, int Matches, DateTimeOffset? First, string State, int Rank);

/// <summary>One dot of the time strip.</summary>
/// <param name="Lane">The lane's index in the strip.</param>
/// <param name="Percent">How far along the window the record is, 0 to 100.</param>
/// <param name="IsAnchor">Whether this is the record the search is about.</param>
internal sealed record KafkaTimelineDot(int Lane, double Percent, bool IsAnchor);

/// <summary>What the strip draws.</summary>
/// <param name="Lanes">The lanes' topics, in the lanes' order.</param>
/// <param name="Dots">A dot for each record found.</param>
/// <param name="Summary">The text beside the strip, which is the accessible form of it.</param>
internal sealed record KafkaTimelineModel(IReadOnlyList<string> Lanes, IReadOnlyList<KafkaTimelineDot> Dots, string Summary);

/// <summary>
/// What the related-records page derives from the matches it holds (decision 3.5 of D-CORR), pure. Everything here reads only the matches'
/// already-redacted fields, so it inherits D40.
/// </summary>
internal static class KafkaRelatedView
{
    /// <summary>The words for a lane whose only possible match was on a redacted field that may be searched.</summary>
    public const string RedactedField = "a redacted field";

    /// <summary>What a record was found by: <c>{kind} ({field})</c> for each literal field of the record whose value is in the key set, or <see cref="RedactedField" /> when only a masked field can have matched.</summary>
    /// <param name="match">The record found.</param>
    /// <param name="fields">The correlation fields of its topic.</param>
    /// <param name="keys">The values searched for.</param>
    public static IReadOnlyList<string> MatchedBy(KafkaMatch match, IReadOnlyList<CorrelationField> fields, CorrelationKeySet keys)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(keys);

        List<string> found = [];

        foreach (CorrelationValue own in CorrelationExtractor.Extract(match, fields).Values)
        {
            if (keys.Of(own.Kind).Any(x => string.Equals(x.Value, own.Value, StringComparison.Ordinal)))
            {
                string text = own.Kind + " (" + own.From + ")";
                if (!found.Contains(text, StringComparer.Ordinal))
                {
                    found.Add(text);
                }
            }
        }

        return found.Count > 0 ? found : [RedactedField];
    }

    /// <summary>The records in the order of the table: by time, then topic, partition and offset.</summary>
    /// <param name="matches">The records found.</param>
    public static IEnumerable<KafkaMatch> InOrder(IEnumerable<KafkaMatch> matches) =>
        matches.OrderBy(static x => x.Timestamp).ThenBy(static x => x.Topic, StringComparer.Ordinal).ThenBy(static x => x.Partition).ThenBy(static x => x.Offset);

    /// <summary>A record's place in the table, for <c>@key</c>.</summary>
    /// <param name="match">The record.</param>
    public static string RowKey(KafkaMatch match) => $"{match.Topic}/{match.Partition}/{match.Offset}";

    /// <summary>
    /// The lanes in the order of decision 3.5: the anchor's topic, lanes with records by the time of their first, lanes with none by name, and lanes
    /// that are not readable by name.
    /// </summary>
    /// <param name="lanes">The lanes the search prepared.</param>
    /// <param name="matches">The records found so far.</param>
    /// <param name="result">The finished scan, or <see langword="null" /> while there is none.</param>
    /// <param name="running">Whether the scan is running.</param>
    /// <param name="started">Whether a scan was ever started.</param>
    /// <param name="anchorTopic">The anchor's topic, or <see langword="null" /> for a seed.</param>
    public static IReadOnlyList<KafkaTimelineLane> Lanes(
        IReadOnlyList<KafkaRelatedLane> lanes,
        IReadOnlyList<KafkaMatch> matches,
        KafkaScanResult? result,
        bool running,
        bool started,
        string? anchorTopic)
    {
        ArgumentNullException.ThrowIfNull(lanes);
        ArgumentNullException.ThrowIfNull(matches);

        ILookup<string, KafkaMatch> byTopic = matches.ToLookup(static x => x.Topic, StringComparer.Ordinal);
        List<KafkaTimelineLane> drawn = [];

        foreach (KafkaRelatedLane lane in lanes)
        {
            string topic = lane.Topic.Value;
            KafkaMatch[] found = [.. byTopic[topic]];
            DateTimeOffset? first = found.Length == 0 ? null : found.Min(static x => x.Timestamp);
            string? refusal = result?.TopicRefusals.FirstOrDefault(x => string.Equals(x.Topic, topic, StringComparison.Ordinal))?.Text;

            string state;
            int rank;

            if (!lane.IsScanned)
            {
                state = KafkaStudioText.RelatedNothingToCompare(lane.Query.Notes);
                rank = 2;
            }
            else if (refusal is not null)
            {
                state = KafkaStudioText.RelatedNotReadable(refusal);
                rank = 3;
            }
            else
            {
                state = StateOf(topic, result, running, started);
                rank = found.Length > 0 ? 1 : 2;
            }

            if (string.Equals(topic, anchorTopic, StringComparison.Ordinal) && rank != 3)
            {
                rank = 0;
            }

            drawn.Add(new KafkaTimelineLane(topic, found.Length, first, state, rank));
        }

        return [.. drawn
            .OrderBy(static x => x.Rank)
            .ThenBy(static x => x.Rank == 1 ? x.First : null)
            .ThenBy(static x => x.Topic, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The strip: one row per lane, a dot per record at <c>(timestamp − from) / (to − from)</c>, and the anchor marked. It is decorative; the summary
    /// beside it is its text.
    /// </summary>
    /// <param name="lanes">The lanes, in the order drawn.</param>
    /// <param name="matches">The records found.</param>
    /// <param name="from">The window's start.</param>
    /// <param name="to">The window's end.</param>
    /// <param name="anchor">The record the search is about, or <see langword="null" />.</param>
    public static KafkaTimelineModel Strip(
        IReadOnlyList<KafkaTimelineLane> lanes, IReadOnlyList<KafkaMatch> matches, DateTimeOffset from, DateTimeOffset to, KafkaMatch? anchor)
    {
        ArgumentNullException.ThrowIfNull(lanes);
        ArgumentNullException.ThrowIfNull(matches);

        double span = Math.Max(1d, (to - from).TotalMilliseconds);
        Dictionary<string, int> index = [];

        for (int i = 0; i < lanes.Count; i++)
        {
            index[lanes[i].Topic] = i;
        }

        List<KafkaTimelineDot> dots = [];

        foreach (KafkaMatch match in matches)
        {
            if (index.TryGetValue(match.Topic, out int lane))
            {
                double percent = Math.Clamp((match.Timestamp - from).TotalMilliseconds / span * 100d, 0d, 100d);
                bool isAnchor = anchor is not null && RowKey(anchor) == RowKey(match);
                dots.Add(new KafkaTimelineDot(lane, percent, isAnchor));
            }
        }

        return new KafkaTimelineModel([.. lanes.Select(static x => x.Topic)], dots, Summary(lanes, matches));
    }

    /// <summary>The text beside the strip: <c>23 records on 3 topics from 14:02:11 to 14:09:40</c> (UTC).</summary>
    /// <param name="lanes">The lanes.</param>
    /// <param name="matches">The records found.</param>
    public static string Summary(IReadOnlyList<KafkaTimelineLane> lanes, IReadOnlyList<KafkaMatch> matches)
    {
        if (matches.Count == 0)
        {
            return "No related records found.";
        }

        int topics = lanes.Count(static x => x.Matches > 0);
        DateTimeOffset first = matches.Min(static x => x.Timestamp);
        DateTimeOffset last = matches.Max(static x => x.Timestamp);
        CultureInfo c = CultureInfo.InvariantCulture;

        return $"{KafkaDisplay.Count(matches.Count, "record")} on {KafkaDisplay.Count(topics, "topic")} from {first.UtcDateTime.ToString("HH:mm:ss", c)} to {last.UtcDateTime.ToString("HH:mm:ss", c)} UTC";
    }

    private static string StateOf(string topic, KafkaScanResult? result, bool running, bool started)
    {
        if (result is null)
        {
            return running ? "reading" : started ? "stopped" : "not searched yet";
        }

        KafkaPartitionResult[] partitions = [.. result.Partitions.Where(x => string.Equals(x.Topic, topic, StringComparison.Ordinal))];

        if (partitions.Any(static x => x.State == PartitionEndState.NotReached))
        {
            return "stopped: " + result.StopReason;
        }

        KafkaPartitionResult? other = partitions.FirstOrDefault(static x => x.State is PartitionEndState.NotAuthorized or PartitionEndState.OffsetGap or PartitionEndState.Stalled or PartitionEndState.StoppedAtLso);

        return other is null ? "read" : "read, but " + KafkaSearchText.State(other.State);
    }
}
