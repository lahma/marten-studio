using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;

namespace MartenStudio.Services.Json;

/// <summary>One difference between two documents.</summary>
/// <param name="Path">The JSONPath of the value that differs, for example <c>$.address.city</c>.</param>
/// <param name="Before">The value in the first document, or <see langword="null"/> when it was not there.</param>
/// <param name="After">The value in the second document, or <see langword="null"/> when it is not there.</param>
internal readonly record struct JsonDiffEntry(string Path, string? Before, string? After);

/// <summary>What changed between two documents, in the three buckets a save dialog has to show.</summary>
/// <param name="Dropped">Present before, gone after. This is the bucket that loses data.</param>
/// <param name="Changed">Present in both, with a different value.</param>
/// <param name="Added">Absent before, present after.</param>
/// <param name="Fault">
/// Why the comparison could not be made, when it could not. A faulted result is never an empty one: "we
/// could not tell" and "nothing is lost" are different answers, and only one of them is safe to put in
/// front of somebody about to press Save.
/// </param>
internal sealed record JsonDiffResult(
    ImmutableArray<JsonDiffEntry> Dropped,
    ImmutableArray<JsonDiffEntry> Changed,
    ImmutableArray<JsonDiffEntry> Added,
    string? Fault = null)
{
    /// <summary>No differences at all.</summary>
    public static JsonDiffResult Empty { get; } = new(
        ImmutableArray<JsonDiffEntry>.Empty,
        ImmutableArray<JsonDiffEntry>.Empty,
        ImmutableArray<JsonDiffEntry>.Empty);

    /// <summary>A comparison that could not be made, and the reason it could not.</summary>
    public static JsonDiffResult Faulted(string reason) => new(
        ImmutableArray<JsonDiffEntry>.Empty,
        ImmutableArray<JsonDiffEntry>.Empty,
        ImmutableArray<JsonDiffEntry>.Empty,
        reason);

    /// <summary>Whether the two documents could not be compared at all.</summary>
    public bool IsFaulted => Fault is { Length: > 0 };

    /// <summary>Whether the two documents say the same thing.</summary>
    public bool IsEmpty =>
        !IsFaulted && Dropped.IsDefaultOrEmpty && Changed.IsDefaultOrEmpty && Added.IsDefaultOrEmpty;

    /// <summary>How many differences there are in total.</summary>
    public int Count => Dropped.Length + Changed.Length + Added.Length;

    /// <summary>A one-line summary, of the kind a confirm dialog leads with.</summary>
    public string Summary()
    {
        if (IsFaulted)
        {
            return "These documents could not be compared: " + Fault;
        }

        if (IsEmpty)
        {
            return "Nothing will change.";
        }

        var parts = new List<string>(3);
        if (Dropped.Length > 0)
        {
            parts.Add(Plural(Dropped.Length, "property", "properties") + " will be dropped");
        }

        if (Changed.Length > 0)
        {
            parts.Add(Plural(Changed.Length, "value", "values") + " will change");
        }

        if (Added.Length > 0)
        {
            parts.Add(Plural(Added.Length, "property", "properties") + " will be added");
        }

        return string.Join(", ", parts) + ".";
    }

    private static string Plural(int count, string singular, string plural) =>
        count.ToString("N0", CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural);
}

/// <summary>
/// The differ behind the round-trip dialog: what a document loses by going through a CLR type.
/// </summary>
/// <remarks>
/// <para>
/// This is design decision D7 made mechanical. Marten has no untyped store API, so saving an edited
/// document means deserializing it into the document's CLR type and serializing that back - and anything
/// the type does not have a member for is gone at that moment, silently. Running the round trip first and
/// diffing the result is the only way to put that loss in front of someone before it happens.
/// </para>
/// <para>
/// The same call answers the other question the edit screen asks - what the user changed, comparing the
/// stored document with the edited one - because it is the same operation with different arguments.
/// </para>
/// </remarks>
internal static class RoundTripDiffer
{
    /// <summary>Diffs two documents. Invalid JSON on either side faults the result rather than throwing.</summary>
    /// <param name="before">The document as it is now - what was edited, or what is stored.</param>
    /// <param name="after">The document as it would be - what came back from the round trip, or the edit.</param>
    /// <remarks>
    /// A side that cannot be canonicalized comes back as <see cref="JsonDiffResult.Faulted"/>, never as
    /// <see cref="JsonDiffResult.Empty"/>. An empty result is the sentence "nothing is lost", and saying
    /// that because the comparison failed is the one answer this dialog must never give.
    /// </remarks>
    public static JsonDiffResult Diff(string? before, string? after)
    {
        if (!JsonCanonicalizer.TryCanonicalize(before, out var left, out var leftError))
        {
            return JsonDiffResult.Faulted("the first document could not be read (" + leftError + ")");
        }

        if (!JsonCanonicalizer.TryCanonicalize(after, out var right, out var rightError))
        {
            return JsonDiffResult.Faulted("the second document could not be read (" + rightError + ")");
        }

        return Diff(left, right);
    }

    /// <summary>Diffs two already-canonicalized documents.</summary>
    public static JsonDiffResult Diff(JsonNode? before, JsonNode? after)
    {
        var dropped = ImmutableArray.CreateBuilder<JsonDiffEntry>();
        var changed = ImmutableArray.CreateBuilder<JsonDiffEntry>();
        var added = ImmutableArray.CreateBuilder<JsonDiffEntry>();

        Compare("$", before, after, dropped, changed, added);

        return new JsonDiffResult(dropped.ToImmutable(), changed.ToImmutable(), added.ToImmutable());
    }

    private static void Compare(
        string path,
        JsonNode? before,
        JsonNode? after,
        ImmutableArray<JsonDiffEntry>.Builder dropped,
        ImmutableArray<JsonDiffEntry>.Builder changed,
        ImmutableArray<JsonDiffEntry>.Builder added)
    {
        if (before is JsonObject leftObject && after is JsonObject rightObject)
        {
            foreach (var pair in leftObject)
            {
                var childPath = JsonPathExpressions.AppendJsonPath(path, JsonPathSegment.ForKey(pair.Key));
                if (rightObject.TryGetPropertyValue(pair.Key, out var rightValue))
                {
                    Compare(childPath, pair.Value, rightValue, dropped, changed, added);
                }
                else
                {
                    dropped.Add(new JsonDiffEntry(childPath, JsonCanonicalizer.ToCanonicalString(pair.Value), null));
                }
            }

            foreach (var pair in rightObject)
            {
                if (leftObject.ContainsKey(pair.Key))
                {
                    continue;
                }

                var childPath = JsonPathExpressions.AppendJsonPath(path, JsonPathSegment.ForKey(pair.Key));
                added.Add(new JsonDiffEntry(childPath, null, JsonCanonicalizer.ToCanonicalString(pair.Value)));
            }

            return;
        }

        if (before is JsonArray leftArray && after is JsonArray rightArray)
        {
            var shared = Math.Min(leftArray.Count, rightArray.Count);
            for (var i = 0; i < shared; i++)
            {
                Compare(
                    JsonPathExpressions.AppendJsonPath(path, JsonPathSegment.ForIndex(i)),
                    leftArray[i],
                    rightArray[i],
                    dropped,
                    changed,
                    added);
            }

            for (var i = shared; i < leftArray.Count; i++)
            {
                dropped.Add(new JsonDiffEntry(
                    JsonPathExpressions.AppendJsonPath(path, JsonPathSegment.ForIndex(i)),
                    JsonCanonicalizer.ToCanonicalString(leftArray[i]),
                    null));
            }

            for (var i = shared; i < rightArray.Count; i++)
            {
                added.Add(new JsonDiffEntry(
                    JsonPathExpressions.AppendJsonPath(path, JsonPathSegment.ForIndex(i)),
                    null,
                    JsonCanonicalizer.ToCanonicalString(rightArray[i])));
            }

            return;
        }

        var beforeText = JsonCanonicalizer.ToCanonicalString(before);
        var afterText = JsonCanonicalizer.ToCanonicalString(after);
        if (!string.Equals(beforeText, afterText, StringComparison.Ordinal))
        {
            changed.Add(new JsonDiffEntry(path, beforeText, afterText));
        }
    }
}
