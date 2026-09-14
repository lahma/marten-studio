using System.Collections.Immutable;
using System.Text.Json;

namespace MartenStudio.Services.Json;

/// <summary>
/// What a JSON scalar turned out to <em>mean</em>, decided once while the flat model is built.
/// </summary>
/// <remarks>
/// Semantic detection happens at build time and never at render time: a row is rendered many times while
/// the circuit lives, and running a GUID parse and three date formats per row per render is how a viewer
/// that felt instant on a twenty-property document becomes unusable on a two-thousand-property one.
/// </remarks>
internal enum JsonSemanticKind
{
    /// <summary>Nothing special; render as a plain token of its kind.</summary>
    None = 0,

    /// <summary>An ISO-8601 date or date-time. <see cref="JsonViewNode.Display"/> keeps the ISO value verbatim.</summary>
    DateTime,

    /// <summary>A GUID in any of the formats <see cref="System.Guid.TryParse(string, out System.Guid)"/> accepts.</summary>
    Guid,

    /// <summary>An absolute <c>http</c> or <c>https</c> URL, and nothing else - no other scheme is ever linked.</summary>
    Url,

    /// <summary>A string that looks like base64 and is long enough to be payload rather than a word.</summary>
    Base64,

    /// <summary>A string longer than <see cref="JsonModelOptions.LongString"/>; rendered truncated with an inline expander.</summary>
    LongText,
}

/// <summary>
/// One node of the flat JSON model: a value, where it sits, and the expressions that address it.
/// </summary>
/// <remarks>
/// <para>
/// The model is a single immutable array walked breadth-first, so a node's children are contiguous at
/// <see cref="ChildStart"/> and there is no per-node collection to allocate. Rendering is a
/// <c>foreach</c> over the visible slice of that array rather than a recursive component tree, which is
/// what keeps a deep document from turning into thousands of component instances with their own
/// lifecycles and parameter diffing.
/// </para>
/// <para>
/// <see cref="Raw"/> is populated only for numbers, booleans and nulls, where it is a handful of
/// characters. A string's text lives in <see cref="Display"/> and its JSON literal is reconstructed on
/// demand; a container carries neither, because holding every container's raw text would store the whole
/// document once per level of nesting.
/// </para>
/// </remarks>
/// <param name="Id">Index of this node in <see cref="JsonModel.Nodes"/>. The root is always <c>0</c>.</param>
/// <param name="ParentId">Index of the parent node, or <c>-1</c> for the root.</param>
/// <param name="Depth">Nesting depth; the root is <c>0</c>.</param>
/// <param name="Key">The object member name, or <see langword="null"/> for the root and for array elements.</param>
/// <param name="Index">The array element index, or <c>-1</c> when this node is not an array element.</param>
/// <param name="Kind">The JSON kind of the value.</param>
/// <param name="Raw">The raw JSON text for numbers, booleans and nulls; empty for strings and containers.</param>
/// <param name="Display">The readable value: a string's unescaped text, a number's digits, <c>true</c>, <c>false</c>, <c>null</c>; empty for containers.</param>
/// <param name="Semantic">What the scalar turned out to mean.</param>
/// <param name="ChildStart">Index of the first child in <see cref="JsonModel.Nodes"/>, or <c>-1</c> when there are none.</param>
/// <param name="ChildCount">Number of children.</param>
/// <param name="Path">The dotted path used for display and CLR resolution, for example <c>address.city</c> or <c>items[0].sku</c>.</param>
/// <param name="JsonPath">The JSONPath expression, for example <c>$.address.city</c>.</param>
/// <param name="PgExpression">The Postgres expression that yields this node, for example <c>data -&gt; 'address' -&gt;&gt; 'city'</c>.</param>
internal readonly record struct JsonViewNode(
    int Id,
    int ParentId,
    int Depth,
    string? Key,
    int Index,
    JsonValueKind Kind,
    string Raw,
    string Display,
    JsonSemanticKind Semantic,
    int ChildStart,
    int ChildCount,
    string Path,
    string JsonPath,
    string PgExpression)
{
    /// <summary>Whether this node is an object or an array.</summary>
    public bool IsContainer => Kind is JsonValueKind.Object or JsonValueKind.Array;

    /// <summary>Whether this node is an object or array that actually has children to expand.</summary>
    public bool HasChildren => IsContainer && ChildCount > 0;

    /// <summary>Whether this node is an element of an array.</summary>
    public bool IsArrayElement => Index >= 0;
}

/// <summary>
/// The thresholds that decide how much of a document the viewer turns into a tree, and how much of that
/// tree it shows at once.
/// </summary>
/// <remarks>
/// Every one of these is a clamp rather than a preference. A Marten document is whatever the host put in
/// it, and the studio is a guest in the host's process: a forty-megabyte document has to degrade to "here
/// is the first megabyte of text" rather than allocate a million-node model on the circuit's behalf.
/// </remarks>
internal sealed record JsonModelOptions
{
    /// <summary>The defaults; every value is the one in the approved plan.</summary>
    public static JsonModelOptions Default { get; } = new();

    /// <summary>Above this many nodes the document is shown as raw text instead of a tree.</summary>
    public int MaxNodesForTree { get; init; } = 20_000;

    /// <summary>Above this many UTF-8 bytes the document is shown as raw text instead of a tree.</summary>
    public int MaxBytesForTree { get; init; } = 512 * 1024;

    /// <summary>Above this many UTF-8 bytes even the raw view is truncated.</summary>
    public int MaxBytesForRaw { get; init; } = 4 * 1024 * 1024;

    /// <summary>How many levels are expanded when the viewer first renders.</summary>
    public int AutoExpandDepth { get; init; } = 2;

    /// <summary>The most rows auto-expansion is allowed to produce.</summary>
    public int AutoExpandNodeBudget { get; init; } = 200;

    /// <summary>How many more children a windowed container reveals per click.</summary>
    public int ChildWindow { get; init; } = 100;

    /// <summary>A container with more children than this is windowed rather than rendered whole.</summary>
    public int LargeContainer { get; init; } = 200;

    /// <summary>A string longer than this is marked <see cref="JsonSemanticKind.LongText"/> and rendered truncated.</summary>
    public int LongString { get; init; } = 120;
}

/// <summary>
/// A parsed JSON document as a flat, immutable node array - or the reason there is not one.
/// </summary>
internal sealed class JsonModel
{
    /// <summary>A model with no nodes, used before any JSON has been supplied.</summary>
    public static JsonModel Empty { get; } = new(
        ImmutableArray<JsonViewNode>.Empty,
        ImmutableArray<int>.Empty,
        totalBytes: 0,
        nodeCount: 0,
        truncated: false,
        reason: null,
        error: null);

    internal JsonModel(
        ImmutableArray<JsonViewNode> nodes,
        ImmutableArray<int> documentOrder,
        int totalBytes,
        int nodeCount,
        bool truncated,
        string? reason,
        string? error)
    {
        Nodes = nodes;
        DocumentOrder = documentOrder;
        TotalBytes = totalBytes;
        NodeCount = nodeCount;
        Truncated = truncated;
        Reason = reason;
        Error = error;
    }

    /// <summary>The nodes, breadth-first, so that every node's children are contiguous.</summary>
    public ImmutableArray<JsonViewNode> Nodes { get; }

    /// <summary>Node ids in document order, which is the order a reader sees and the order search walks.</summary>
    public ImmutableArray<int> DocumentOrder { get; }

    /// <summary>The size of the source document in UTF-8 bytes.</summary>
    public int TotalBytes { get; }

    /// <summary>How many nodes the document has - which is more than <see cref="Nodes"/> holds when <see cref="Truncated"/>.</summary>
    public int NodeCount { get; }

    /// <summary>Whether the document was too large to model as a tree, in which case <see cref="Nodes"/> is empty.</summary>
    public bool Truncated { get; }

    /// <summary>Why the tree was not built, in words a user can act on.</summary>
    public string? Reason { get; }

    /// <summary>The parse failure, when the source was not valid JSON at all.</summary>
    public string? Error { get; }

    /// <summary>Whether the source failed to parse.</summary>
    public bool HasError => Error is not null;

    /// <summary>Whether there is a tree to render.</summary>
    public bool HasNodes => !Nodes.IsDefaultOrEmpty;

    /// <summary>The root node, or <see langword="null"/> when there is no tree.</summary>
    public JsonViewNode? Root => HasNodes ? Nodes[0] : null;

    /// <summary>The path segments from the root down to <paramref name="nodeId"/>, outermost first.</summary>
    /// <remarks>
    /// Recovered by walking <see cref="JsonViewNode.ParentId"/> rather than stored per node: the copy
    /// menu is the only caller, it runs once per click, and storing a segment list on twenty thousand
    /// nodes to save that walk would cost more than the whole rest of the model.
    /// </remarks>
    public ImmutableArray<JsonPathSegment> SegmentsOf(int nodeId)
    {
        if (!HasNodes || nodeId < 0 || nodeId >= Nodes.Length)
        {
            return ImmutableArray<JsonPathSegment>.Empty;
        }

        var reversed = new List<JsonPathSegment>();
        var current = Nodes[nodeId];
        while (current.ParentId >= 0)
        {
            reversed.Add(current.Key is not null
                ? JsonPathSegment.ForKey(current.Key)
                : JsonPathSegment.ForIndex(current.Index));
            current = Nodes[current.ParentId];
        }

        reversed.Reverse();
        return [.. reversed];
    }

    /// <summary>The ids of every ancestor of <paramref name="nodeId"/>, nearest first.</summary>
    public IEnumerable<int> AncestorsOf(int nodeId)
    {
        if (!HasNodes || nodeId < 0 || nodeId >= Nodes.Length)
        {
            yield break;
        }

        var parent = Nodes[nodeId].ParentId;
        while (parent >= 0)
        {
            yield return parent;
            parent = Nodes[parent].ParentId;
        }
    }
}
