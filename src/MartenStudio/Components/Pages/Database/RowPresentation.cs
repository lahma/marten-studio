using System.Globalization;
using System.Text;
using System.Text.Json;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;
using MartenStudio.Services.Query;

namespace MartenStudio.Components.Pages.Database;

/// <summary>One foreign key out of the relation on screen, as the grid follows it from a cell.</summary>
/// <param name="Name">The constraint's name.</param>
/// <param name="Columns">This relation's columns, in constraint order.</param>
/// <param name="Schema">The parent's schema.</param>
/// <param name="Table">The parent table.</param>
/// <param name="LinkedColumns">The parent's columns, in constraint order.</param>
/// <param name="ParentKey">
/// The parent's row key, when it is known: a key that references exactly those columns links a cell straight to
/// the parent row, and any other key links to the parent's rows filtered on the values.
/// </param>
internal sealed record RowForeignKey(
    string Name,
    IReadOnlyList<string> Columns,
    string Schema,
    string Table,
    IReadOnlyList<string> LinkedColumns,
    IReadOnlyList<string>? ParentKey)
{
    /// <summary>Whether the key references the parent's row key - the same columns, in any order.</summary>
    public bool TargetsParentKey =>
        ParentKey is { Count: > 0 } key
        && key.Count == LinkedColumns.Count
        && key.All(x => LinkedColumns.Contains(x, StringComparer.Ordinal));
}

/// <summary>
/// How the Rows tab and row detail say things: a row's key as a person reads it, a filter for one value, the
/// paging note, a failure's headline, and the row as JSON for the clipboard. One place, so the grid, its
/// strip and the detail page never spell the same fact two ways.
/// </summary>
internal static class RowPresentation
{
    /// <summary>How long one value of a key may be in a breadcrumb or a chip before it is cut.</summary>
    private const int KeyValueWidth = 24;

    /// <summary>A cell kind as the cell components take it: lower-cased.</summary>
    public static string KindToken(SqlCellKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>The URL's direction token: <c>desc</c> is descending, anything else ascending - the key order.</summary>
    public static SortDirection ParseDirection(string? token) =>
        string.Equals(token, "desc", StringComparison.OrdinalIgnoreCase) ? SortDirection.Descending : SortDirection.Ascending;

    /// <summary>A direction as the URL spells it.</summary>
    public static string DirectionToken(SortDirection direction) =>
        direction == SortDirection.Descending ? "desc" : "asc";

    /// <summary>A row's key in key order, from the row's raw key values.</summary>
    /// <param name="key">The row's key values, by column.</param>
    /// <param name="rowKey">The relation's key, which says the order; without one, the dictionary's order.</param>
    public static IReadOnlyList<KeyValuePair<string, string>> KeyPairs(IReadOnlyDictionary<string, string> key, DatabaseRowKey? rowKey)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (rowKey is null)
        {
            return [.. key];
        }

        List<KeyValuePair<string, string>> pairs = [];

        foreach (string column in rowKey.Columns)
        {
            if (key.TryGetValue(column, out string? value))
            {
                pairs.Add(new KeyValuePair<string, string>(column, value));
            }
        }

        return pairs.Count == key.Count ? pairs : [.. key];
    }

    /// <summary>
    /// A key as a person reads it: <c>(QuartzScheduler, trigger-00, DEFAULT)</c>, each value cut at 24
    /// characters - or, for a one-column key, at twice that, so a uuid is read whole.
    /// </summary>
    public static string KeyTuple(IReadOnlyList<KeyValuePair<string, string>> key)
    {
        ArgumentNullException.ThrowIfNull(key);

        int width = key.Count == 1 ? KeyValueWidth * 2 : KeyValueWidth;

        return "(" + string.Join(", ", key.Select(x => Cut(x.Value, width))) + ")";
    }

    /// <summary>The key's full title: <c>sched_name = QuartzScheduler, trigger_name = trigger-00</c>.</summary>
    public static string KeyTitle(IReadOnlyList<KeyValuePair<string, string>> key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return string.Join(", ", key.Select(static x => x.Key + " = " + x.Value));
    }

    /// <summary>A key as a filter the Rows tab reads back - what "Copy key" puts on the clipboard.</summary>
    public static string KeyFilter(IReadOnlyList<KeyValuePair<string, string>> key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return RowFilterGrammar.Format(key.Select(static x => (x.Key, RowFilterOperator.Equal, (string?) x.Value)));
    }

    /// <summary>The filter that selects one value of one column: <c>col = value</c>, or <c>col is:null</c>.</summary>
    public static string FilterFor(string column, string? value) =>
        RowFilterGrammar.Format(column, value is null ? RowFilterOperator.IsNull : RowFilterOperator.Equal, value);

    /// <summary>
    /// A filter with one term taken out, by the term's span in the text as typed: what removing a chip does.
    /// The terms either side keep their spelling, quotes and all.
    /// </summary>
    public static string? RemoveTerm(string? filter, int position, int length)
    {
        if (string.IsNullOrEmpty(filter) || position < 0 || position >= filter.Length)
        {
            return filter;
        }

        int end = Math.Min(filter.Length, position + Math.Max(length, 0));
        string before = filter[..position].TrimEnd();
        string after = filter[end..].TrimStart();
        string joined = before.Length > 0 && after.Length > 0 ? before + " " + after : before + after;

        return joined.Length == 0 ? null : joined;
    }

    /// <summary>
    /// The line under a filter that points at an error: spaces to the error's position, then one <c>^</c> per
    /// character it covers.
    /// </summary>
    public static string Caret(int position, int length) =>
        new string(' ', Math.Max(position, 0)) + new string('^', Math.Max(length, 1));

    /// <summary>A filter drawn above its caret line: tabs and line breaks as spaces, so the columns line up.</summary>
    public static string CaretText(string? filter) =>
        (filter ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// The note under the toolbar: <c>~1,204 rows · keyset on (sched_name, trigger_name, trigger_group) ·
    /// next_fire_time ↑</c> - the estimate (D8), how the pages are walked, and the order.
    /// </summary>
    public static IReadOnlyList<string> PagingNote(TableRowPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        List<string> parts = [];

        if (page.EstimatedRows is { } rows)
        {
            parts.Add("~" + DatabasePresentation.Number(rows) + (rows == 1 ? " row" : " rows"));
        }
        else
        {
            parts.Add(page.Kind == DatabaseObjectKind.View ? "a view: every page runs its query" : "rows not analyzed");
        }

        if (page.Paging is { } paging)
        {
            parts.Add(paging.Mode switch
            {
                TableRowPagingMode.Key => "keyset on (" + string.Join(", ", paging.OrderColumns) + ")",
                TableRowPagingMode.Ctid => "keyset on ctid, the row's physical position",
                _ => paging.OrderColumns.Count > 0
                    ? "numbered pages, ordered by (" + string.Join(", ", paging.OrderColumns) + ")"
                    : "numbered pages",
            });

            string arrow = paging.Direction == SortDirection.Descending ? "↓" : "↑";

            // The order, when it is not already said: a sort column, or the key's own order. A walk over
            // ctid or over tiebreak columns has named its order in the part before.
            if (paging.SortColumn is { } sort)
            {
                parts.Add(sort + " " + arrow);
            }
            else if (page.RowKey is not null)
            {
                parts.Add("key order " + arrow);
            }
        }

        return parts;
    }

    /// <summary>A failed read's headline, from its SQLSTATE; the sentence under it is the service's.</summary>
    public static string FailureTitle(TableRowError error, bool rowSecurity)
    {
        ArgumentNullException.ThrowIfNull(error);

        return error.SqlState switch
        {
            "55000" => "Never refreshed",
            "42501" => "No privilege to read these rows",
            "57014" => "The read timed out",
            "42704" when rowSecurity => "Row-level security stopped the read",
            "55P03" => "The table is locked",
            "42P01" or "42703" => "The table changed under this page",
            _ when error.SqlState.StartsWith("22", StringComparison.Ordinal) => "A value does not fit its column",
            _ => "The read failed",
        };
    }

    /// <summary>A refused read's headline.</summary>
    public static string RefusalTitle(DatabaseRefusal refusal) => refusal switch
    {
        DatabaseRefusal.CapabilityOff => "Reading rows here needs Capabilities.BrowseDatabase",
        DatabaseRefusal.ReadOnly => "The studio is read-only",
        DatabaseRefusal.WritePolicy or DatabaseRefusal.StorePolicy => "Your account may not read rows here",
        DatabaseRefusal.WithheldDependency => "This view reads from a schema you cannot see",
        DatabaseRefusal.SchemaNotBrowsable => "This schema is not in BrowsableSchemas",
        DatabaseRefusal.NoPrivilege => "The reading role may not select from it",
        DatabaseRefusal.ForeignTable => "A foreign table is listed, never read",
        DatabaseRefusal.HiddenDependency => "This view reads a table the host hides",
        DatabaseRefusal.MartenOwned => "Marten's own rows are read where Marten's rules apply",
        _ => "These rows cannot be read here",
    };

    /// <summary>Whether a refusal names a gate - an option or the account - rather than a fact about the object.</summary>
    public static bool IsGate(DatabaseRefusal refusal) =>
        refusal is DatabaseRefusal.CapabilityOff or DatabaseRefusal.ReadOnly or DatabaseRefusal.WritePolicy
            or DatabaseRefusal.StorePolicy;

    /// <summary>
    /// A row as indented JSON for the clipboard: a NULL as <c>null</c>, a number and a boolean as themselves,
    /// a JSON column as its JSON when it was read whole, and everything else as the text Postgres printed.
    /// </summary>
    /// <remarks>
    /// Written with <see cref="Utf8JsonWriter" /> rather than a serializer: this is a relational row shaped into
    /// JSON for a person, not a Marten document, so the store's serializer settings (AGENTS.md hard rule 10)
    /// have nothing to say about it - and a cell cut on the server is copied as the text it is, never as JSON
    /// that would not parse.
    /// </remarks>
    public static string RowJson(IReadOnlyList<TableRowColumn> columns, IReadOnlyList<SqlCell> cells)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(cells);

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();

            for (int i = 0; i < columns.Count && i < cells.Count; i++)
            {
                SqlCell cell = cells[i];
                writer.WritePropertyName(columns[i].Name);

                switch (cell.Kind)
                {
                    case SqlCellKind.Null:
                        writer.WriteNullValue();
                        break;

                    case SqlCellKind.Boolean when bool.TryParse(cell.Text, out bool flag):
                        writer.WriteBooleanValue(flag);
                        break;

                    case SqlCellKind.Number when !cell.IsTruncated && IsJsonNumber(cell.Text):
                        writer.WriteRawValue(cell.Text, skipInputValidation: false);
                        break;

                    case SqlCellKind.Json when !cell.IsTruncated && IsJson(cell.Text):
                        writer.WriteRawValue(cell.Text, skipInputValidation: false);
                        break;

                    default:
                        writer.WriteStringValue(cell.Text);
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool IsJsonNumber(string text) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out _)
        && !text.StartsWith('+') && !text.StartsWith('.') && !text.EndsWith('.')
        && IsJson(text);

    private static bool IsJson(string text)
    {
        try
        {
            using JsonDocument _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Cut(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";
}
