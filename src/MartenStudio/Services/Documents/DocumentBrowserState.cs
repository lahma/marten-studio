namespace MartenStudio.Services.Documents;

/// <summary>
/// What the documents browser remembers for the life of one circuit: which red verdicts the visitor has
/// accepted, and where they were in the list when they opened a document.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, so it is per browser tab and never per process. Two things live here rather than on a
/// component because both have to survive navigating away and back: "Run anyway" is an answer to a
/// question the visitor should not be asked twice for the same collection in the same session, and the
/// detail page's prev/next arrows need the list that was on screen when the document was opened.
/// </para>
/// <para>
/// It holds ids and a URL, never rows: remembering a page of documents would mean holding every
/// document's JSON for as long as the circuit lives.
/// </para>
/// </remarks>
internal sealed class DocumentBrowserState
{
    private readonly HashSet<string> acceptedVerdicts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The collection the remembered page belongs to.</summary>
    public string? PageAlias { get; private set; }

    /// <summary>The ids on that page, in the order they were shown.</summary>
    public IReadOnlyList<string> PageIds { get; private set; } = [];

    /// <summary>The list URL that produced it, so "back to the list" returns to the same place.</summary>
    public string? PageUrl { get; private set; }

    /// <summary>Whether the visitor has already accepted a red verdict for this collection.</summary>
    public bool HasAcceptedVerdict(string? alias) =>
        alias is not null && acceptedVerdicts.Contains(alias);

    /// <summary>Records that they have.</summary>
    public void AcceptVerdict(string alias) => acceptedVerdicts.Add(alias);

    /// <summary>Forgets the acceptance, which is what changing the search term does.</summary>
    public void ForgetVerdict(string alias) => acceptedVerdicts.Remove(alias);

    /// <summary>Remembers the page that is on screen, for the detail page's prev/next.</summary>
    public void RememberPage(string alias, IReadOnlyList<string> ids, string url)
    {
        ArgumentNullException.ThrowIfNull(ids);

        PageAlias = alias;
        PageIds = ids;
        PageUrl = url;
    }

    /// <summary>
    /// The id before and after <paramref name="id"/> on the remembered page, when it is the same
    /// collection and the document is on it.
    /// </summary>
    public (string? Previous, string? Next) Neighbours(string alias, string id)
    {
        if (!string.Equals(PageAlias, alias, StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        for (var i = 0; i < PageIds.Count; i++)
        {
            if (!string.Equals(PageIds[i], id, StringComparison.Ordinal))
            {
                continue;
            }

            return (i > 0 ? PageIds[i - 1] : null, i + 1 < PageIds.Count ? PageIds[i + 1] : null);
        }

        return (null, null);
    }
}
