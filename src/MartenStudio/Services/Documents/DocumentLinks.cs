using System.Buffers.Text;
using System.Globalization;
using System.Text;

using MartenStudio.Components;
using MartenStudio.Services.Query;

using Microsoft.AspNetCore.WebUtilities;

namespace MartenStudio.Services.Documents;

/// <summary>
/// Builds the documents browser's URLs, and reads them back.
/// </summary>
/// <remarks>
/// Every piece of state the page has is in the URL (plan §3.2, D9): the scope, the search, the sort, the
/// cursor, the page size, the chosen columns, the deleted tri-state and the paging mode. That is not
/// tidiness — it is what makes a link somebody pastes into a chat reopen the same screen, and it is why
/// document ids travel as <c>?id=</c> rather than as a path segment: the hand-rolled router has no
/// catch-all segment and Marten ids are frequently strings containing <c>/</c>.
/// </remarks>
internal static class DocumentLinks
{
    /// <summary>The documents root.</summary>
    public const string Root = "documents";

    /// <summary>The link to the collections rail with nothing selected.</summary>
    public static string ToIndex(MartenStudioOptions options, StudioScope? scope) =>
        WithScope(StudioLink.To(options, Root), scope);

    /// <summary>The link to one collection's list.</summary>
    public static string ToCollection(
        MartenStudioOptions options,
        StudioScope? scope,
        string alias,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        var url = WithScope(StudioLink.To(options, Root + "/" + Uri.EscapeDataString(alias)), scope);

        if (extra is null)
        {
            return url;
        }

        foreach (var pair in extra)
        {
            url = QueryHelpers.AddQueryString(url, pair.Key, pair.Value ?? string.Empty);
        }

        return url;
    }

    /// <summary>The link to one document's detail page.</summary>
    public static string ToDocument(
        MartenStudioOptions options,
        StudioScope? scope,
        string alias,
        string id,
        string? from = null)
    {
        var url = WithScope(StudioLink.To(options, Root + "/" + Uri.EscapeDataString(alias) + "/doc"), scope);

        url = QueryHelpers.AddQueryString(url, "id", id);

        return from is null ? url : QueryHelpers.AddQueryString(url, "from", from);
    }

    /// <summary>
    /// The link to the query page for this collection and filter.
    /// </summary>
    /// <remarks>
    /// The query page arrives in a later packet (plan §3.2). The link is built now because the shape of it
    /// is part of this screen's contract — "Open in Query" has to carry the collection and the filter the
    /// list is showing, or it is a button that loses the user's work.
    /// </remarks>
    public static string ToQuery(MartenStudioOptions options, StudioScope? scope, string alias, string? where)
    {
        var url = WithScope(StudioLink.To(options, "query"), scope);

        url = QueryHelpers.AddQueryString(url, "type", alias);

        return string.IsNullOrWhiteSpace(where) ? url : QueryHelpers.AddQueryString(url, "where", where);
    }

    /// <summary>The link to a stream's detail page, for the "view stream" affordance.</summary>
    public static string ToStream(MartenStudioOptions options, StudioScope? scope, string id)
    {
        var url = WithScope(StudioLink.To(options, "events/streams/s"), scope);

        return QueryHelpers.AddQueryString(url, "id", id);
    }

    /// <summary>Appends <c>?store=</c>, <c>?db=</c> and <c>?tenant=</c> when there is a scope to append.</summary>
    public static string WithScope(string url, StudioScope? scope)
    {
        if (scope is null)
        {
            return url;
        }

        if (!string.IsNullOrEmpty(scope.StoreKey))
        {
            url = QueryHelpers.AddQueryString(url, "store", scope.StoreKey);
        }

        if (!string.IsNullOrEmpty(scope.DatabaseId))
        {
            url = QueryHelpers.AddQueryString(url, "db", scope.DatabaseId);
        }

        if (!string.IsNullOrEmpty(scope.TenantId))
        {
            url = QueryHelpers.AddQueryString(url, "tenant", scope.TenantId);
        }

        return url;
    }

    /// <summary>The tri-state as it is spelled in <c>?deleted=</c>.</summary>
    public static string DeletedToken(DeletedFilter filter) => filter switch
    {
        DeletedFilter.Include => "include",
        DeletedFilter.Only => "only",
        _ => "exclude",
    };

    /// <summary>Reads <c>?deleted=</c>, defaulting to live documents only.</summary>
    public static DeletedFilter ParseDeleted(string? token) => token?.ToLowerInvariant() switch
    {
        "include" => DeletedFilter.Include,
        "only" => DeletedFilter.Only,
        _ => DeletedFilter.Exclude,
    };

    /// <summary>Reads <c>?dir=</c>, defaulting to descending.</summary>
    public static SortDirection ParseDirection(string? token) =>
        string.Equals(token, "asc", StringComparison.OrdinalIgnoreCase)
            ? SortDirection.Ascending
            : SortDirection.Descending;

    /// <summary>The direction as it is spelled in <c>?dir=</c>.</summary>
    public static string DirectionToken(SortDirection direction) =>
        direction == SortDirection.Ascending ? "asc" : "desc";

    /// <summary>Reads <c>?cols=</c>, a comma-separated list of column keys.</summary>
    public static IReadOnlyList<string> ParseColumns(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Writes <c>?cols=</c>.</summary>
    public static string FormatColumns(IEnumerable<string> keys) => string.Join(',', keys);

    /// <summary>
    /// Encodes a keyset cursor into one opaque <c>?cursor=</c> value.
    /// </summary>
    /// <remarks>
    /// Opaque rather than readable because both halves are arbitrary text — a sort value could be a
    /// timestamp, a name with a comma in it or a null — and a delimiter that a value can contain is a
    /// delimiter that eventually splits in the wrong place. Base64url of the two lengths and the two
    /// values can encode anything, including the difference between a null sort value and an empty one.
    /// </remarks>
    public static string Encode(DocumentKeysetCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        var builder = new StringBuilder();

        builder.Append(cursor.SortValue is null ? 'n' : 'v');

        if (cursor.SortValue is { } sort)
        {
            builder.Append(sort.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(sort);
        }
        else
        {
            builder.Append("0:");
        }

        builder.Append(cursor.LastId);

        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    /// <summary>Reads a <c>?cursor=</c> value, or <see langword="null"/> when it is not one.</summary>
    public static DocumentKeysetCursor? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));

            if (text.Length < 2)
            {
                return null;
            }

            var hasSort = text[0] == 'v';
            var colon = text.IndexOf(':', StringComparison.Ordinal);

            if (colon < 1 ||
                !int.TryParse(text[1..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) ||
                length < 0 ||
                colon + 1 + length > text.Length)
            {
                return null;
            }

            var sort = hasSort ? text.Substring(colon + 1, length) : null;
            var id = text[(colon + 1 + length)..];

            return id.Length == 0 ? null : new DocumentKeysetCursor(sort, id);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
