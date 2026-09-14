using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MartenStudio.Services.Json;

/// <summary>
/// Turns a JSON string into the flat model the viewer renders, in one walk, and lets go of the
/// <see cref="JsonDocument"/> before it returns.
/// </summary>
/// <remarks>
/// <para>
/// The walk is breadth-first on purpose. It makes every node's children a contiguous run in the node
/// array, which is what lets a row know its children without a list of its own and lets the renderer
/// window a large container by taking a slice.
/// </para>
/// <para>
/// The <see cref="JsonDocument"/> is disposed before the model is handed back. A <c>JsonElement</c> is a
/// view over pooled memory owned by its document: keeping one in a model that a Blazor circuit holds for
/// minutes would either pin that buffer for the life of the circuit or - worse, once the document is
/// disposed and its buffer returned to the pool - read somebody else's data. Everything the model keeps
/// is a copied string.
/// </para>
/// </remarks>
internal static class JsonModelBuilder
{
    /// <summary>
    /// The parse depth limit. Matches the plan; a document nested deeper than this is pathological and
    /// the failure is a caught <see cref="JsonException"/> rather than a stack overflow.
    /// </summary>
    private const int MaxDepth = 64;

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = MaxDepth };

    /// <summary>Builds the flat model for <paramref name="json"/>.</summary>
    /// <param name="json">The document text.</param>
    /// <param name="options">Thresholds; <see cref="JsonModelOptions.Default"/> when omitted.</param>
    /// <param name="column">The jsonb column the Postgres expressions start from.</param>
    /// <param name="force">Ignore the tree size thresholds, because the user asked for the tree anyway.</param>
    public static JsonModel Build(
        string? json,
        JsonModelOptions? options = null,
        string column = JsonPathExpressions.DefaultColumn,
        bool force = false)
    {
        options ??= JsonModelOptions.Default;

        if (string.IsNullOrWhiteSpace(json))
        {
            return JsonModel.Empty;
        }

        var totalBytes = Encoding.UTF8.GetByteCount(json);

        if (!force && totalBytes > options.MaxBytesForTree)
        {
            return Refused(
                totalBytes,
                0,
                FormatBytes(totalBytes) + " is over the " + FormatBytes(options.MaxBytesForTree) +
                " limit for building a tree, so this is the raw document.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, ParseOptions);
            return Walk(document.RootElement, totalBytes, options, column, force);
        }
        catch (JsonException ex)
        {
            return new JsonModel(
                ImmutableArray<JsonViewNode>.Empty,
                ImmutableArray<int>.Empty,
                totalBytes,
                nodeCount: 0,
                truncated: false,
                reason: null,
                error: ex.Message);
        }
    }

    /// <summary>A size in the units a reader thinks in, used in every threshold message.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }

        return (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>
    /// The first <paramref name="maxChars"/> characters of <paramref name="text"/>, never splitting a
    /// surrogate pair.
    /// </summary>
    /// <remarks>
    /// A <see cref="string"/> is UTF-16, so an emoji or any character outside the basic multilingual plane
    /// is two chars. Cutting between them leaves a lone surrogate, which is not a character at all: it
    /// renders as a replacement glyph, breaks the byte arithmetic that sizes the "… +n B" hint, and can be
    /// rejected outright on the way back out through UTF-8. One character short is always the right
    /// answer.
    /// </remarks>
    public static string Cut(string text, int maxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        var length = maxChars;
        if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
        {
            length--;
        }

        return text[..length];
    }

    private static JsonModel Refused(int totalBytes, int nodeCount, string reason) =>
        new(ImmutableArray<JsonViewNode>.Empty,
            ImmutableArray<int>.Empty,
            totalBytes,
            nodeCount,
            truncated: true,
            reason: reason,
            error: null);

    private static JsonModel Walk(JsonElement root, int totalBytes, JsonModelOptions options, string column, bool force)
    {
        var pending = new List<Pending>();
        var queue = new Queue<int>();

        pending.Add(Create(root, ownerId: -1, depth: 0, nodeKey: null, nodeIndex: -1, path: string.Empty, jsonPath: "$", pg: column, modelOptions: options));
        if (pending[0].Kind is JsonValueKind.Object or JsonValueKind.Array)
        {
            queue.Enqueue(0);
        }

        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            var parent = pending[parentId];
            var childStart = pending.Count;
            var childCount = 0;

            if (parent.Kind == JsonValueKind.Object)
            {
                foreach (var property in parent.Element.EnumerateObject())
                {
                    AddChild(property.Value, parentId, parent, JsonPathSegment.ForKey(property.Name), property.Name, -1);
                    childCount++;
                }
            }
            else
            {
                var position = 0;
                foreach (var item in parent.Element.EnumerateArray())
                {
                    AddChild(item, parentId, parent, JsonPathSegment.ForIndex(position), null, position);
                    position++;
                    childCount++;
                }
            }

            parent.ChildStart = childCount == 0 ? -1 : childStart;
            parent.ChildCount = childCount;
            pending[parentId] = parent;

            if (!force && pending.Count > options.MaxNodesForTree)
            {
                return Refused(
                    totalBytes,
                    pending.Count,
                    "This document has more than " + options.MaxNodesForTree.ToString("N0", CultureInfo.InvariantCulture) +
                    " nodes, so this is the raw document.");
            }
        }

        var nodes = ImmutableArray.CreateBuilder<JsonViewNode>(pending.Count);
        foreach (var item in pending)
        {
            nodes.Add(new JsonViewNode(
                item.Id,
                item.ParentId,
                item.Depth,
                item.Key,
                item.Index,
                item.Kind,
                item.Raw,
                item.Display,
                item.Semantic,
                item.ChildStart,
                item.ChildCount,
                item.Path,
                item.JsonPath,
                item.Pg));
        }

        var built = nodes.MoveToImmutable();
        return new JsonModel(built, DocumentOrder(built), totalBytes, built.Length, truncated: false, reason: null, error: null);

        void AddChild(JsonElement value, int ownerId, in Pending owner, JsonPathSegment step, string? childKey, int childIndex)
        {
            var isContainer = value.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
            var child = Create(
                value,
                ownerId,
                owner.Depth + 1,
                childKey,
                childIndex,
                JsonPathExpressions.AppendDotPath(owner.Path, step),
                JsonPathExpressions.AppendJsonPath(owner.JsonPath, step),
                JsonPathExpressions.AppendPostgres(owner.Pg, step, asText: !isContainer),
                options);

            pending.Add(child);
            if (isContainer)
            {
                queue.Enqueue(child.Id);
            }
        }

        Pending Create(
            JsonElement element,
            int ownerId,
            int depth,
            string? nodeKey,
            int nodeIndex,
            string path,
            string jsonPath,
            string pg,
            JsonModelOptions modelOptions)
        {
            var kind = element.ValueKind;
            var raw = string.Empty;
            var display = string.Empty;
            var semantic = JsonSemanticKind.None;

            switch (kind)
            {
                case JsonValueKind.String:
                    display = element.GetString() ?? string.Empty;
                    semantic = DetectSemantic(display, modelOptions.LongString);
                    break;
                case JsonValueKind.Number:
                    raw = element.GetRawText();
                    display = raw;
                    break;
                case JsonValueKind.True:
                    raw = "true";
                    display = raw;
                    break;
                case JsonValueKind.False:
                    raw = "false";
                    display = raw;
                    break;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    raw = "null";
                    display = raw;
                    break;
                default:
                    break;
            }

            return new Pending
            {
                Id = pending.Count,
                ParentId = ownerId,
                Depth = depth,
                Key = nodeKey,
                Index = nodeIndex,
                Kind = kind,
                Raw = raw,
                Display = display,
                Semantic = semantic,
                ChildStart = -1,
                ChildCount = 0,
                Path = path,
                JsonPath = jsonPath,
                Pg = pg,
                Element = element,
            };
        }
    }

    /// <summary>Node ids in the order a reader sees them, which is depth-first, not the array's order.</summary>
    private static ImmutableArray<int> DocumentOrder(ImmutableArray<JsonViewNode> nodes)
    {
        if (nodes.IsDefaultOrEmpty)
        {
            return ImmutableArray<int>.Empty;
        }

        var order = ImmutableArray.CreateBuilder<int>(nodes.Length);
        var stack = new Stack<int>();
        stack.Push(0);

        while (stack.Count > 0)
        {
            var id = stack.Pop();
            order.Add(id);

            var node = nodes[id];
            for (var i = node.ChildCount - 1; i >= 0; i--)
            {
                stack.Push(node.ChildStart + i);
            }
        }

        return order.Count == order.Capacity ? order.MoveToImmutable() : order.ToImmutable();
    }

    /// <summary>What a string turned out to be. The order is the precedence; the first match wins.</summary>
    internal static JsonSemanticKind DetectSemantic(string text, int longStringThreshold)
    {
        if (text.Length == 0)
        {
            return JsonSemanticKind.None;
        }

        if (text.Length is >= 32 and <= 68 && Guid.TryParse(text, out _))
        {
            return JsonSemanticKind.Guid;
        }

        if (LooksLikeIso8601(text))
        {
            return JsonSemanticKind.DateTime;
        }

        if (LooksLikeWebUrl(text))
        {
            return JsonSemanticKind.Url;
        }

        if (LooksLikeBase64(text))
        {
            return JsonSemanticKind.Base64;
        }

        return text.Length > longStringThreshold ? JsonSemanticKind.LongText : JsonSemanticKind.None;
    }

    /// <summary>
    /// A date, a date-time, or neither. Deliberately shape-first: <see cref="DateTime.TryParse(string, out DateTime)"/>
    /// alone would happily read "3" and "May" as dates and paint half a document with calendar glyphs.
    /// </summary>
    private static bool LooksLikeIso8601(string text)
    {
        if (text.Length < 10)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                return false;
            }
        }

        if (text[4] != '-' || text[7] != '-')
        {
            return false;
        }

        if (!char.IsAsciiDigit(text[5]) || !char.IsAsciiDigit(text[6]) ||
            !char.IsAsciiDigit(text[8]) || !char.IsAsciiDigit(text[9]))
        {
            return false;
        }

        if (text.Length == 10)
        {
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
        }

        if (text[10] is not ('T' or 't' or ' '))
        {
            return false;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
    }

    /// <summary>
    /// Only <c>http</c> and <c>https</c> are ever turned into links. A <c>javascript:</c> or <c>data:</c>
    /// value in somebody's document must render as text, not as something a reader can click.
    /// </summary>
    private static bool LooksLikeWebUrl(string text)
    {
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// A guess, and labelled as one in the UI ("binary?"). Length and alphabet are checked rather than
    /// decoded: a lower-case hex digest is a multiple of four and a valid base64 alphabet too, so mixed
    /// case or actual base64 punctuation is required before a value is called binary.
    /// </summary>
    private static bool LooksLikeBase64(string text)
    {
        if (text.Length < 32 || text.Length % 4 != 0)
        {
            return false;
        }

        var padding = 0;
        var hasUpper = false;
        var hasLower = false;
        var hasSymbol = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '=')
            {
                padding++;
                if (padding > 2 || i < text.Length - 2)
                {
                    return false;
                }

                continue;
            }

            if (padding > 0)
            {
                return false;
            }

            if (char.IsAsciiLetterUpper(c))
            {
                hasUpper = true;
            }
            else if (char.IsAsciiLetterLower(c))
            {
                hasLower = true;
            }
            else if (char.IsAsciiDigit(c))
            {
                // Digits say nothing either way.
            }
            else if (c is '+' or '/' or '-' or '_')
            {
                hasSymbol = true;
            }
            else
            {
                return false;
            }
        }

        return hasSymbol || padding > 0 || (hasUpper && hasLower);
    }

    /// <summary>The mutable shape a node has while the walk is still filling in its children.</summary>
    private struct Pending
    {
        public int Id;
        public int ParentId;
        public int Depth;
        public string? Key;
        public int Index;
        public JsonValueKind Kind;
        public string Raw;
        public string Display;
        public JsonSemanticKind Semantic;
        public int ChildStart;
        public int ChildCount;
        public string Path;
        public string JsonPath;
        public string Pg;
        public JsonElement Element;
    }
}
