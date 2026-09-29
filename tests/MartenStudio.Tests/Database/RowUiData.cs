using Bunit;

using MartenStudio.Components.Pages.Database;
using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

namespace MartenStudio.Tests.Database;

/// <summary>
/// What the Rows tab's and row detail's page tests need beyond <see cref="FakeTableRows" />: a context with a
/// settled scope, the tab rendered at a URL the way the object page renders it, and the few row shapes the
/// Quartz sample does not have - a keyless heap table, timestamps with and without a zone, a bigint that is
/// not a time.
/// </summary>
internal static class RowUiData
{
    /// <summary>A context with the default store settled, the way the layout settles it on a real circuit.</summary>
    public static async Task<StudioComponentContext> ContextAsync()
    {
        StudioComponentContext context = new();
        await context.ReadyAsync();
        return context;
    }

    /// <summary>The Rows tab of one relation, at <paramref name="query" /> after the tab's own parameters.</summary>
    public static IRenderedComponent<TableRowsTab> RenderTab(
        StudioComponentContext context,
        string query = "",
        string schema = FakeTableRows.Schema,
        string name = FakeTableRows.Table,
        DatabaseObjectDetail? detail = null)
    {
        context.Navigate(TabUrl(schema, name) + (query.Length == 0 ? string.Empty : "&" + query));

        return context.Render<TableRowsTab>(parameters => parameters
            .Add(x => x.Schema, schema)
            .Add(x => x.Name, name)
            .Add(x => x.Detail, (object?) (detail ?? FakeDatabaseObjects.Detail(schema, name))));
    }

    /// <summary>The object page's Rows tab, as the address bar spells it.</summary>
    public static string TabUrl(string schema, string name) =>
        "marten/database/object?schema=" + Uri.EscapeDataString(schema) + "&name=" + Uri.EscapeDataString(name) + "&tab=rows";

    /// <summary>One column, as the header draws it.</summary>
    public static TableRowColumn Column(
        string name,
        int position,
        string type,
        int? key = null,
        IReadOnlyList<TableRowColumnReference>? references = null,
        bool sortable = true) =>
        new(
            name,
            position,
            type,
            Nullable: key is null,
            Sortable: sortable,
            Shown: true,
            TableRowQueryBuilder.KindOf(type),
            key,
            references ?? [],
            IndexVerdictLevel.Red,
            "No index covers " + name + "; filtering or sorting on it reads every row.",
            null);

    /// <summary>A loaded page of exactly these columns and rows.</summary>
    public static TableRowPage PageOf(
        IReadOnlyList<TableRowColumn> columns,
        IReadOnlyList<TableRow> rows,
        DatabaseRowKey? key = null,
        TableRowPagingMode mode = TableRowPagingMode.Key,
        string schema = "legacy",
        string name = "events") =>
        new()
        {
            State = TableRowPageState.Loaded,
            Schema = schema,
            Name = name,
            Kind = DatabaseObjectKind.Table,
            Ownership = new DatabaseObjectOwnership(DatabaseObjectOwner.Other),
            RowKey = key,
            EstimatedRows = rows.Count,
            Columns = columns,
            AvailableColumns = columns,
            Rows = rows,
            Paging = new TableRowPaging(mode, key?.Columns ?? (mode == TableRowPagingMode.Ctid ? ["ctid"] : []), null, SortDirection.Ascending, 0, null),
            Sql = "select 1",
        };

    /// <summary>A text cell.</summary>
    public static SqlCell Text(string value, bool truncated = false, int? fullLength = null) =>
        new(value, SqlCellKind.Text, truncated, fullLength ?? value.Length);

    /// <summary>A number cell.</summary>
    public static SqlCell Number(long value) =>
        new(value.ToString(System.Globalization.CultureInfo.InvariantCulture), SqlCellKind.Number, false, 8);

    /// <summary>
    /// A heap table with no key: two rows found by their ctid, a <c>timestamptz</c>, a <c>timestamp</c>, a
    /// bigint that is not a time although its values are the size of ticks, and a long message cut on the server.
    /// </summary>
    public static TableRowPage Keyless() => PageOf(
        [
            Column("happened_at", 1, "timestamp with time zone"),
            Column("logged_local", 2, "timestamp without time zone"),
            Column("retry_count", 3, "bigint"),
            Column("message", 4, "text"),
        ],
        [
            new TableRow(null, "(0,1)",
            [
                new SqlCell("2026-09-29T14:03:00.0000000Z", SqlCellKind.Timestamp, false, 28),
                new SqlCell("2026-09-29T14:03:00.0000000", SqlCellKind.Timestamp, false, 27),
                Number(FakeTableRows.BaseTicks),
                Text(new string('x', 1024) + "…", truncated: true, fullLength: 5000),
            ]),
            new TableRow(null, "(0,2)",
            [
                SqlCell.Null,
                SqlCell.Null,
                Number(FakeTableRows.BaseTicks + TimeSpan.TicksPerHour),
                Text("short"),
            ]),
        ],
        key: null,
        mode: TableRowPagingMode.Ctid,
        schema: "legacy",
        name: "audit_log");
}
