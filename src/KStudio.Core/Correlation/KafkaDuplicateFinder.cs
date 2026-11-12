using System.Runtime.InteropServices;

using KStudio.Core.Scanning;

namespace KStudio.Core.Correlation;

/// <summary>What two records had in common for them to be called copies (D42, decision 4 of D-CORR).</summary>
internal enum KafkaDuplicateBasis
{
    /// <summary>The <c>id</c> header: Wolverine's envelope id.</summary>
    EnvelopeId,

    /// <summary>The CloudEvents <c>source</c> and <c>id</c> attributes.</summary>
    CloudEvent,

    /// <summary>The key and the whole value, compared as shown.</summary>
    KeyAndValue,
}

/// <summary>Records on one topic that look like one message delivered more than once.</summary>
/// <param name="Basis">What they share.</param>
/// <param name="Topic">The topic.</param>
/// <param name="Copies">At least two, by timestamp, then partition and offset.</param>
/// <param name="ComparedAsShown">Whether the values were compared as shown, a mask included: equal masks compare equal.</param>
internal sealed record KafkaDuplicateGroup(KafkaDuplicateBasis Basis, string Topic, IReadOnlyList<KafkaMatch> Copies, bool ComparedAsShown)
{
    /// <summary>How many copies.</summary>
    public int Count => Copies.Count;

    /// <summary>The first copy's time.</summary>
    public DateTimeOffset First => Copies[0].Timestamp;

    /// <summary>The last copy's time.</summary>
    public DateTimeOffset Last => Copies[^1].Timestamp;
}

/// <summary>
/// Groups the matches already held (a search's results or a timeline's) that look like one message delivered more than once. It reads nothing,
/// asks no gate and records no audit, because it rearranges what the visitor already has: it uses only fields the match shows as literals, so
/// the panel reveals no equality the visitor could not see. Two records are copies only on the same topic.
/// </summary>
internal static class KafkaDuplicateFinder
{
    /// <summary>The identity header.</summary>
    private const string IdHeader = "id";

    /// <summary>Finds the groups. One pass with dictionaries: linear time and memory over strings the matches already hold.</summary>
    /// <param name="matches">The matches.</param>
    public static IReadOnlyList<KafkaDuplicateGroup> Find(IReadOnlyList<KafkaMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);

        // The key holds the strings the matches already hold, in parts: nothing is concatenated, so the pass copies no value.
        Dictionary<BucketKey, List<KafkaMatch>> buckets = new(IdentityComparer.Instance);

        foreach (KafkaMatch match in matches)
        {
            // A record is compared by the first basis it has itself: an envelope id, else CloudEvents' identity, else its key and value.
            if (Identity(match) is not { } identity)
            {
                continue;
            }

            ref List<KafkaMatch>? copies = ref CollectionsMarshal.GetValueRefOrAddDefault(buckets, new BucketKey(match.Topic, identity.Basis, identity.First, identity.Second), out _);
            (copies ??= []).Add(match);
        }

        List<KafkaDuplicateGroup> groups = [];

        foreach ((BucketKey bucket, List<KafkaMatch> copies) in buckets)
        {
            if (copies.Count < 2)
            {
                continue;
            }

            (string topic, KafkaDuplicateBasis basis) = (bucket.Topic, bucket.Basis);
            KafkaMatch[] ordered = [.. copies.OrderBy(static x => x.Timestamp).ThenBy(static x => x.Partition).ThenBy(static x => x.Offset)];
            bool asShown = basis == KafkaDuplicateBasis.KeyAndValue && ordered.Any(static x => x.ValueMaskedBy.Count > 0);
            groups.Add(new KafkaDuplicateGroup(basis, topic, ordered, asShown));
        }

        return [.. groups.OrderByDescending(static x => x.Count).ThenBy(static x => x.First).ThenBy(static x => x.Topic, StringComparer.Ordinal)];
    }

    private static (KafkaDuplicateBasis Basis, string First, string? Second)? Identity(KafkaMatch match)
    {
        if (Literal(match.Headers.FirstOrDefault(static x => string.Equals(x.Name, IdHeader, StringComparison.OrdinalIgnoreCase))?.Value) is { } envelope)
        {
            return (KafkaDuplicateBasis.EnvelopeId, envelope, null);
        }

        string? source = Literal(Attribute(match, "source"));
        string? id = Literal(Attribute(match, "id"));

        if (source is not null && id is not null)
        {
            return (KafkaDuplicateBasis.CloudEvent, source, id);
        }

        // The value counts only if it is whole and was read: a cut or withheld value, or one never read, says nothing about equality.
        if (match.ReadWithPayload
            && match.ValueWithheldBy is null
            && !match.ValueTruncated
            && match.ValueText is { } value
            && match.Key.IsLiteral
            && match.Key.Text is { } key)
        {
            return (KafkaDuplicateBasis.KeyAndValue, key, value);
        }

        return null;
    }

    private static KafkaMatchField? Attribute(KafkaMatch match, string name) =>
        match.CloudEventAttributes.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? Literal(KafkaMatchField? field) => field is { IsLiteral: true, Text.Length: > 0 } ? field.Value.Text : null;

    /// <summary>What a bucket is keyed on: strings the matches already hold, in parts.</summary>
    private readonly record struct BucketKey(string Topic, KafkaDuplicateBasis Basis, string First, string? Second);

    /// <summary>
    /// Equal when every part is equal ordinally. The hash reads a value's length and a bounded sample of it (its start, its end and 64 places
    /// between), not all of it: hashing every value of 500 matches of 64 KiB would read 30 MB to find what a length and a sample already
    /// separate, and equal values are then confirmed by one vectorized comparison.
    /// </summary>
    private sealed class IdentityComparer : IEqualityComparer<BucketKey>
    {
        private const int Edge = 128;
        private const int Strides = 64;

        public static IdentityComparer Instance { get; } = new();

        public bool Equals(BucketKey x, BucketKey y) =>
            x.Basis == y.Basis
            && string.Equals(x.Topic, y.Topic, StringComparison.Ordinal)
            && string.Equals(x.First, y.First, StringComparison.Ordinal)
            && string.Equals(x.Second, y.Second, StringComparison.Ordinal);

        public int GetHashCode(BucketKey identity)
        {
            HashCode hash = new();
            hash.Add(identity.Topic, StringComparer.Ordinal);
            hash.Add(identity.Basis);
            hash.Add(Sample(identity.First));
            hash.Add(identity.Second is null ? 0 : Sample(identity.Second));
            return hash.ToHashCode();
        }

        private static int Sample(string text)
        {
            if (text.Length <= (2 * Edge) + Strides)
            {
                return string.GetHashCode(text, StringComparison.Ordinal);
            }

            HashCode hash = new();
            hash.Add(text.Length);
            hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(text.AsSpan(0, Edge)));
            hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(text.AsSpan(text.Length - Edge, Edge)));
            int step = (text.Length - (2 * Edge)) / Strides;

            for (int i = 0; i < Strides; i++)
            {
                hash.Add(text[Edge + (i * step)]);
            }

            return hash.ToHashCode();
        }
    }
}
