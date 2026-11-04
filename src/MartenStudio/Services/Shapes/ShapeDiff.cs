using System.Text.Json;
using System.Text.Json.Nodes;

using PostgresStudio.Services.Json;

namespace MartenStudio.Services.Shapes;

/// <summary>
/// One disagreement between a stored item and its round trip. A path, a kind and the JSON kinds involved: there is no field here
/// that can carry a value, which is how the check stays value-free by construction (D-SHAPE S6).
/// </summary>
/// <param name="Path">The normalized path, array indices written <c>[*]</c>, cut at <see cref="ShapeCheckLimits.MaxPathLength" />.</param>
/// <param name="Kind">What is wrong.</param>
/// <param name="PairedPath">The path the value comes back under, for <see cref="ShapeMemberKind.Respelled" />.</param>
/// <param name="StoredKind">The stored value's JSON kind, for <see cref="ShapeMemberKind.Retyped" />.</param>
/// <param name="ReadKind">The round trip's JSON kind, for <see cref="ShapeMemberKind.Retyped" />.</param>
internal readonly record struct ShapeMember(
    string Path,
    ShapeMemberKind Kind,
    string? PairedPath = null,
    JsonValueKind? StoredKind = null,
    JsonValueKind? ReadKind = null);

/// <summary>
/// Compares a stored item with what the store's own reader and writer make of it, and classifies every difference
/// (D-SHAPE S6).
/// </summary>
/// <remarks>
/// <para>
/// The walk is <see cref="RoundTripDiffer" />'s - both sides canonicalized first, so <c>1.50</c> against <c>1.5</c> and a
/// different member order are no change - with a different output: a finding counts <em>items</em>, so every array index is
/// <c>[*]</c> and an item contributes a given (path, kind) once however many elements show it.
/// </para>
/// <para>
/// A stored <c>null</c> the round trip leaves out is not reported: a serializer that ignores nulls when writing loses nothing by
/// it. A stored <c>null</c> where the round trip writes a value is <see cref="ShapeMemberKind.Defaulted" />: the stored item
/// has nothing there, and the next save writes the default.
/// </para>
/// </remarks>
internal static class ShapeDiff
{
    /// <summary>Canonicalizes both sides and compares them.</summary>
    /// <param name="storedJson">The stored item.</param>
    /// <param name="roundTrippedJson">What the serializer writes after reading it.</param>
    /// <exception cref="JsonException">Either side is not JSON, or is nested deeper than the canonicalizer reads.</exception>
    public static IReadOnlyList<ShapeMember> Compare(string storedJson, string roundTrippedJson)
    {
        ArgumentNullException.ThrowIfNull(storedJson);
        ArgumentNullException.ThrowIfNull(roundTrippedJson);

        return Compare(JsonCanonicalizer.Canonicalize(storedJson), JsonCanonicalizer.Canonicalize(roundTrippedJson));
    }

    /// <summary>Compares two canonicalized items.</summary>
    /// <param name="stored">The stored item, canonicalized.</param>
    /// <param name="roundTripped">The round trip, canonicalized.</param>
    public static IReadOnlyList<ShapeMember> Compare(JsonNode? stored, JsonNode? roundTripped)
    {
        Collector members = new();
        Walk("$", stored, roundTripped, members);
        return members.Items;
    }

    /// <summary>The kind of a canonicalized node; <c>true</c> and <c>false</c> are one kind, a boolean.</summary>
    internal static JsonValueKind KindOf(JsonNode node) => node switch
    {
        JsonObject => JsonValueKind.Object,
        JsonArray => JsonValueKind.Array,
        _ => node.GetValueKind() is JsonValueKind.False ? JsonValueKind.True : node.GetValueKind(),
    };

    private static void Walk(string path, JsonNode? stored, JsonNode? read, Collector members)
    {
        if (stored is JsonObject storedObject && read is JsonObject readObject)
        {
            WalkObject(path, storedObject, readObject, members);
            return;
        }

        if (stored is JsonArray storedArray && read is JsonArray readArray)
        {
            string element = path + "[*]";
            int shared = Math.Min(storedArray.Count, readArray.Count);

            for (int i = 0; i < shared; i++)
            {
                Walk(element, storedArray[i], readArray[i], members);
            }

            for (int i = shared; i < storedArray.Count; i++)
            {
                if (storedArray[i] is not null)
                {
                    members.Add(new ShapeMember(element, ShapeMemberKind.Dropped));
                }
            }

            for (int i = shared; i < readArray.Count; i++)
            {
                members.Add(new ShapeMember(element, ShapeMemberKind.Defaulted));
            }

            return;
        }

        Classify(path, stored, read, members);
    }

    private static void WalkObject(string path, JsonObject stored, JsonObject read, Collector members)
    {
        List<(string Key, JsonNode Value)> dropped = [];
        List<(string Key, JsonNode? Value)> defaulted = [];

        foreach (KeyValuePair<string, JsonNode?> pair in stored)
        {
            if (read.TryGetPropertyValue(pair.Key, out JsonNode? readValue))
            {
                Walk(Child(path, pair.Key), pair.Value, readValue, members);
            }
            else if (pair.Value is not null)
            {
                dropped.Add((pair.Key, pair.Value));
            }
        }

        foreach (KeyValuePair<string, JsonNode?> pair in read)
        {
            if (!stored.ContainsKey(pair.Key))
            {
                defaulted.Add((pair.Key, pair.Value));
            }
        }

        // A dropped and a defaulted sibling that are one value under two spellings are one finding, not two.
        bool[] spelled = new bool[defaulted.Count];

        foreach ((string key, JsonNode value) in dropped)
        {
            int match = -1;

            for (int i = 0; i < defaulted.Count; i++)
            {
                if (!spelled[i]
                    && defaulted[i].Value is not null
                    && string.Equals(key, defaulted[i].Key, StringComparison.OrdinalIgnoreCase)
                    && JsonCanonicalizer.ValueEquals(value, defaulted[i].Value))
                {
                    match = i;
                    break;
                }
            }

            if (match < 0)
            {
                members.Add(new ShapeMember(Child(path, key), ShapeMemberKind.Dropped));
                continue;
            }

            spelled[match] = true;
            members.Add(new ShapeMember(Child(path, key), ShapeMemberKind.Respelled, Child(path, defaulted[match].Key)));
        }

        for (int i = 0; i < defaulted.Count; i++)
        {
            if (!spelled[i])
            {
                members.Add(new ShapeMember(Child(path, defaulted[i].Key), ShapeMemberKind.Defaulted));
            }
        }
    }

    private static void Classify(string path, JsonNode? stored, JsonNode? read, Collector members)
    {
        if (stored is null)
        {
            if (read is not null)
            {
                members.Add(new ShapeMember(path, ShapeMemberKind.Defaulted));
            }

            return;
        }

        if (read is null)
        {
            members.Add(new ShapeMember(path, ShapeMemberKind.Nulled));
            return;
        }

        JsonValueKind storedKind = KindOf(stored);
        JsonValueKind readKind = KindOf(read);

        if (storedKind != readKind)
        {
            members.Add(new ShapeMember(path, ShapeMemberKind.Retyped, null, storedKind, readKind));
        }
        else if (!JsonCanonicalizer.ValueEquals(stored, read))
        {
            members.Add(new ShapeMember(path, ShapeMemberKind.Rewritten));
        }
    }

    private static string Child(string parent, string key) => JsonPathExpressions.AppendJsonPath(parent, JsonPathSegment.ForKey(key));

    /// <summary>The members of one item, each (path, kind, pair) once.</summary>
    private sealed class Collector
    {
        private readonly HashSet<(string, ShapeMemberKind, string?)> seen = [];

        public List<ShapeMember> Items { get; } = [];

        public void Add(ShapeMember member)
        {
            ShapeMember bounded = member with
            {
                Path = Cut(member.Path),
                PairedPath = member.PairedPath is null ? null : Cut(member.PairedPath),
            };

            if (seen.Add((bounded.Path, bounded.Kind, bounded.PairedPath)))
            {
                Items.Add(bounded);
            }
        }

        private static string Cut(string path) =>
            path.Length <= ShapeCheckLimits.MaxPathLength ? path : path[..ShapeCheckLimits.MaxPathLength];
    }
}
