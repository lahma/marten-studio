namespace MartenStudio.Services.Database;

/// <summary>
/// What the database browser's Rows tab remembers for the life of one circuit: which withheld reads the
/// visitor has chosen to run anyway, where they were in a keyset walk, and the page of rows they opened a row
/// from - the <c>DocumentBrowserState</c> of the relational side.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, so it is per browser tab and never per process. It lives outside the components because all three
/// facts have to survive navigating away and back: "Run anyway" is an answer the visitor should not be asked
/// for twice for the same filter, the Prev button needs the cursors of the pages before this one after a
/// round trip through a row's detail, and that detail's previous/next arrows need the rows that were on
/// screen when it was opened.
/// </para>
/// <para>
/// It holds row keys, cursors and a URL, never rows: a page of cells is what the circuit should let go of as
/// soon as the grid is drawn. A key is the one part of a row a link already carries in plain text.
/// </para>
/// </remarks>
internal sealed class DatabaseBrowserState
{
    private const char Separator = '\u001f';

    private readonly HashSet<string> acceptedReads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string?>> cursorHistory = new(StringComparer.Ordinal);

    /// <summary>The relation the remembered page belongs to, as <see cref="RelationKey" /> spells it.</summary>
    public string? PageRelation { get; private set; }

    /// <summary>The keys of the rows on that page, in the order they were shown.</summary>
    public IReadOnlyList<IReadOnlyList<KeyValuePair<string, string>>> PageKeys { get; private set; } = [];

    /// <summary>The Rows tab URL that produced it, so "back to the rows" returns to the same page.</summary>
    public string? PageUrl { get; private set; }

    /// <summary>The key one relation is remembered under.</summary>
    public static string RelationKey(string schema, string name) => schema + Separator + name;

    /// <summary>
    /// Whether the visitor has already said "Run anyway" for this read - the relation, the filter and the sort
    /// that made the service withhold it.
    /// </summary>
    public bool HasAcceptedRead(string schema, string name, string? filter, string? sort, string? direction) =>
        acceptedReads.Contains(ReadKey(schema, name, filter, sort, direction));

    /// <summary>Records that they have. A different filter or sort is a different question and is asked again.</summary>
    public void AcceptRead(string schema, string name, string? filter, string? sort, string? direction) =>
        acceptedReads.Add(ReadKey(schema, name, filter, sort, direction));

    /// <summary>
    /// The cursors of the pages before the current one in this relation's keyset walk, oldest first - what Prev
    /// pops. A new filter, sort or page size is a new walk and clears it.
    /// </summary>
    public List<string?> CursorHistory(string schema, string name)
    {
        string key = RelationKey(schema, name);

        if (!cursorHistory.TryGetValue(key, out List<string?>? history))
        {
            history = [];
            cursorHistory[key] = history;
        }

        return history;
    }

    /// <summary>Remembers the page that is on screen, for row detail's previous and next.</summary>
    /// <param name="schema">The relation's schema.</param>
    /// <param name="name">The relation's name.</param>
    /// <param name="keys">Each row's key, in the order shown; a page of a relation with no key remembers nothing.</param>
    /// <param name="url">The Rows tab URL, relative, that shows this page.</param>
    public void RememberPage(
        string schema,
        string name,
        IReadOnlyList<IReadOnlyList<KeyValuePair<string, string>>> keys,
        string url)
    {
        ArgumentNullException.ThrowIfNull(keys);

        PageRelation = RelationKey(schema, name);
        PageKeys = keys;
        PageUrl = url;
    }

    /// <summary>
    /// The keys of the rows before and after <paramref name="key" /> on the remembered page, when it is the same
    /// relation and the row is on it. Keys compare column by column, whatever order the link wrote them in.
    /// </summary>
    public (IReadOnlyList<KeyValuePair<string, string>>? Previous, IReadOnlyList<KeyValuePair<string, string>>? Next) Neighbours(
        string schema,
        string name,
        IReadOnlyList<KeyValuePair<string, string>> key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!string.Equals(PageRelation, RelationKey(schema, name), StringComparison.Ordinal) || key.Count == 0)
        {
            return (null, null);
        }

        for (int i = 0; i < PageKeys.Count; i++)
        {
            if (!SameKey(PageKeys[i], key))
            {
                continue;
            }

            return (i > 0 ? PageKeys[i - 1] : null, i + 1 < PageKeys.Count ? PageKeys[i + 1] : null);
        }

        return (null, null);
    }

    /// <summary>Whether two keys name the same row: the same columns with the same values, in any order.</summary>
    public static bool SameKey(IReadOnlyList<KeyValuePair<string, string>> left, IReadOnlyList<KeyValuePair<string, string>> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> pair in left)
        {
            bool found = false;

            foreach (KeyValuePair<string, string> other in right)
            {
                if (string.Equals(pair.Key, other.Key, StringComparison.Ordinal))
                {
                    found = string.Equals(pair.Value, other.Value, StringComparison.Ordinal);
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadKey(string schema, string name, string? filter, string? sort, string? direction) =>
        string.Join(Separator, schema, name, filter?.Trim() ?? string.Empty, sort ?? string.Empty, direction ?? string.Empty);
}
