using System.Collections.Immutable;

namespace MartenStudio.Services.Json;

/// <summary>Which half of a row a match landed in.</summary>
internal enum JsonMatchTarget
{
    /// <summary>The member name.</summary>
    Key = 0,

    /// <summary>The value.</summary>
    Value,
}

/// <summary>One hit of the search term.</summary>
/// <param name="NodeId">The node the hit is on.</param>
/// <param name="Target">Whether the hit is in the key or in the value.</param>
/// <param name="Start">Where the hit starts in that text.</param>
/// <param name="Length">How long the hit is.</param>
internal readonly record struct JsonMatch(int NodeId, JsonMatchTarget Target, int Start, int Length);

/// <summary>
/// Finding a term in the flat model, over keys and values, in the order a reader would meet them.
/// </summary>
/// <remarks>
/// Search runs over the model rather than over the rendered markup, which is the whole reason the viewer
/// can find something inside a collapsed subtree and then expand the way to it. It walks
/// <see cref="JsonModel.DocumentOrder"/> rather than the node array, because the node array is
/// breadth-first and "next match" has to mean the next one <em>down the page</em>.
/// </remarks>
internal static class JsonSearch
{
    /// <summary>Every occurrence of <paramref name="term"/> in keys and values, in document order.</summary>
    public static ImmutableArray<JsonMatch> Find(JsonModel model, string? term)
    {
        if (!model.HasNodes || string.IsNullOrEmpty(term))
        {
            return ImmutableArray<JsonMatch>.Empty;
        }

        var matches = ImmutableArray.CreateBuilder<JsonMatch>();

        foreach (var id in model.DocumentOrder)
        {
            var node = model.Nodes[id];

            if (node.Key is { Length: > 0 } key)
            {
                AddAll(matches, id, JsonMatchTarget.Key, key, term);
            }

            if (!node.IsContainer && node.Display.Length > 0)
            {
                AddAll(matches, id, JsonMatchTarget.Value, node.Display, term);
            }
        }

        return matches.ToImmutable();
    }

    /// <summary>
    /// How many matches sit below each node, so a collapsed container can wear a badge saying how much
    /// it is hiding.
    /// </summary>
    public static Dictionary<int, int> CountByAncestor(JsonModel model, ImmutableArray<JsonMatch> matches)
    {
        var counts = new Dictionary<int, int>();
        if (!model.HasNodes || matches.IsDefaultOrEmpty)
        {
            return counts;
        }

        // A node with several hits counts once: the badge says how many rows are hidden below, not how
        // many characters matched, because expanding reveals rows.
        var nodes = new HashSet<int>();
        foreach (var match in matches)
        {
            nodes.Add(match.NodeId);
        }

        foreach (var nodeId in nodes)
        {
            foreach (var ancestor in model.AncestorsOf(nodeId))
            {
                counts[ancestor] = counts.GetValueOrDefault(ancestor) + 1;
            }
        }

        return counts;
    }

    /// <summary>Splits <paramref name="text"/> into alternating unmatched and matched runs, for highlighting.</summary>
    /// <remarks>
    /// Returned as runs rather than as markup: a component that builds its own HTML string is a component
    /// that has to get escaping right on somebody else's data, and here that data is a document from the
    /// store.
    /// </remarks>
    public static ImmutableArray<JsonTextRun> Highlight(string text, string? term)
    {
        if (string.IsNullOrEmpty(text))
        {
            return ImmutableArray<JsonTextRun>.Empty;
        }

        if (string.IsNullOrEmpty(term))
        {
            return [new JsonTextRun(text, false)];
        }

        var runs = ImmutableArray.CreateBuilder<JsonTextRun>();
        var index = 0;

        while (index < text.Length)
        {
            var hit = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase);
            if (hit < 0)
            {
                runs.Add(new JsonTextRun(text[index..], false));
                break;
            }

            if (hit > index)
            {
                runs.Add(new JsonTextRun(text[index..hit], false));
            }

            runs.Add(new JsonTextRun(text.Substring(hit, term.Length), true));
            index = hit + term.Length;
        }

        return runs.ToImmutable();
    }

    private static void AddAll(
        ImmutableArray<JsonMatch>.Builder matches,
        int nodeId,
        JsonMatchTarget target,
        string text,
        string term)
    {
        var index = 0;
        while (index <= text.Length - term.Length)
        {
            var hit = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase);
            if (hit < 0)
            {
                return;
            }

            matches.Add(new JsonMatch(nodeId, target, hit, term.Length));
            index = hit + term.Length;
        }
    }
}

/// <summary>A run of text and whether it is part of a search hit.</summary>
/// <param name="Text">The characters.</param>
/// <param name="IsMatch">Whether they are highlighted.</param>
internal readonly record struct JsonTextRun(string Text, bool IsMatch);
