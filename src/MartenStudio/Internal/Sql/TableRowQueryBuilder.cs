using System.Globalization;
using System.Text;

using MartenStudio.Services.Database;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>How one cell is read.</summary>
internal enum TableRowCellShape
{
    /// <summary>
    /// A small fixed-width scalar - an integer, a float, a boolean, a uuid, a timestamp - read as the value
    /// itself, because it cannot be large and the grid wants it typed (a timestamptz in the chosen zone).
    /// </summary>
    Native,

    /// <summary>Anything else, read as the first characters of its text form.</summary>
    Text,

    /// <summary><c>numeric</c> and <c>money</c>: text, exactly as Postgres prints it, shown as a number.</summary>
    Number,

    /// <summary><c>date</c>, <c>time</c>, <c>timetz</c>, <c>interval</c>: text, shown as a time.</summary>
    Temporal,

    /// <summary><c>json</c> and <c>jsonb</c>: the first characters of the text, under a larger cap.</summary>
    Json,

    /// <summary>An array: the first characters of its text form.</summary>
    Array,

    /// <summary><c>bytea</c>: the first bytes, and the octet length.</summary>
    Binary,
}

/// <summary>The caps one read applies: per cell, and over the whole read.</summary>
/// <remarks>
/// <para>
/// <b>Bytes, not characters.</b> Every text-form cap is a number of UTF-8 bytes of the text kept - what the
/// page holds and the circuit sends - never a number of characters, which is up to four times as many
/// bytes. The server cuts first, by characters (<c>substring</c> counts them, and slices a TOASTed value
/// rather than reading it whole), to one character past the cap; the reader then cuts that to the byte cap
/// on a character boundary, and anything it dropped is a cut.
/// </para>
/// <para>
/// <b>And a budget for the whole read.</b> A page of 500 rows of twenty wide columns is 40 MB of cells even
/// when each is capped. Once the cells read so far hold <see cref="Budget" /> bytes, every further cell is
/// cut to <see cref="OverBudget" /> bytes and marked cut - its whole value is still one expansion away.
/// </para>
/// </remarks>
/// <param name="Text">How many bytes of a text-form cell are kept.</param>
/// <param name="Json">How many bytes of a <c>json</c>/<c>jsonb</c> cell are kept.</param>
/// <param name="Binary">How many bytes of a <c>bytea</c> cell are kept.</param>
/// <param name="Budget">How many bytes of cell text one read keeps before it starts cutting every cell short.</param>
/// <param name="OverBudget">How many bytes a cell keeps once the budget is spent.</param>
internal sealed record TableRowCaps(int Text, int Json, int Binary, int Budget = int.MaxValue, int OverBudget = 64)
{
    /// <summary>
    /// A grid cell: a kilobyte of text, four of JSON, sixty-four bytes of binary - and a mebibyte for the
    /// whole page, past which cells keep sixty-four bytes.
    /// </summary>
    public static TableRowCaps List { get; } = new(1024, 4 * 1024, 64, 1024 * 1024, 64);

    /// <summary>
    /// Row detail: 256 KB of text or JSON per cell, four kilobytes of binary - and two mebibytes for the whole
    /// row, past which cells keep 256 bytes.
    /// </summary>
    public static TableRowCaps Detail { get; } = new(256 * 1024, 256 * 1024, 4 * 1024, 2 * 1024 * 1024, 256);
}

/// <summary>One column to read: the catalog's name and its type.</summary>
/// <param name="Name">The column, as the catalog spells it.</param>
/// <param name="Type">Its type, as <c>format_type</c> spells it.</param>
internal sealed record TableRowColumnRead(string Name, string Type)
{
    /// <summary>How its cells are read.</summary>
    public TableRowCellShape Shape => TableRowQueryBuilder.ShapeOf(Type);
}

/// <summary>One parameter: its name, its wire type and its value. Values never reach the SQL text.</summary>
/// <param name="Name">The name, without the <c>@</c>.</param>
/// <param name="Type">How Npgsql sends it; every cursor, key and filter value is <see cref="NpgsqlDbType.Unknown" />.</param>
/// <param name="Value">The value.</param>
internal sealed record TableRowParameter(string Name, NpgsqlDbType Type, object Value);

/// <summary>Where one cell's expressions are in the select list.</summary>
/// <param name="Column">The column.</param>
/// <param name="Type">Its type.</param>
/// <param name="Shape">How it was read.</param>
/// <param name="ValueOrdinal">The value's (or its cut text's) ordinal.</param>
/// <param name="LengthOrdinal">The full length's ordinal, or <c>-1</c> for a <see cref="TableRowCellShape.Native" /> cell.</param>
internal sealed record TableRowCellLayout(string Column, string Type, TableRowCellShape Shape, int ValueOrdinal, int LengthOrdinal);

/// <summary>What a reader of a built statement needs to know about its select list.</summary>
/// <param name="KeyColumns">The row key's columns, in key order.</param>
/// <param name="KeyOrdinals">The raw <c>::text</c> of each of <paramref name="KeyColumns" />.</param>
/// <param name="SortOrdinal">The raw <c>::text</c> of the sort column, or <c>-1</c>.</param>
/// <param name="LocatorOrdinal">The raw <c>ctid::text</c>, or <c>-1</c>.</param>
/// <param name="Cells">The cells, in display order.</param>
/// <param name="ValueOrdinals">For the key read of <see cref="TableRowQueryBuilder.BuildKeyRead" />, the raw <c>::text</c> of each wanted column.</param>
internal sealed record TableRowLayout(
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<int> KeyOrdinals,
    int SortOrdinal,
    int LocatorOrdinal,
    IReadOnlyList<TableRowCellLayout> Cells,
    IReadOnlyList<int> ValueOrdinals)
{
    /// <summary>A statement with nothing to lay out - a count, an existence check.</summary>
    public static TableRowLayout Scalar { get; } = new([], [], -1, -1, [], []);
}

/// <summary>A built statement: its text, its parameters, and where its columns are.</summary>
/// <param name="Sql">The exact text sent - the "Show SQL" disclosure's.</param>
/// <param name="Parameters">The parameters.</param>
/// <param name="Layout">Where each part of a row is.</param>
internal sealed record TableRowStatement(string Sql, IReadOnlyList<TableRowParameter> Parameters, TableRowLayout Layout)
{
    /// <summary>The parameter names as the SQL spells them - what "Show SQL" shows, never the values.</summary>
    public IReadOnlyList<string> ParameterNames => [.. Parameters.Select(static x => "@" + x.Name)];

    /// <summary>A command for this statement, enlisted in <paramref name="transaction" />.</summary>
    public NpgsqlCommand CreateCommand(NpgsqlConnection connection, NpgsqlTransaction transaction, int commandTimeoutSeconds)
    {
        var command = new NpgsqlCommand(Sql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };

        foreach (TableRowParameter parameter in Parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(parameter.Name, parameter.Type) { Value = parameter.Value });
        }

        return command;
    }
}

/// <summary>Everything one page read is built from.</summary>
internal sealed record TableRowListSpec
{
    /// <summary>The relation's schema, as the catalog spells it.</summary>
    public required string Schema { get; init; }

    /// <summary>The relation's name, as the catalog spells it.</summary>
    public required string Name { get; init; }

    /// <summary>The cells to read, in display order.</summary>
    public required IReadOnlyList<TableRowColumnRead> Columns { get; init; }

    /// <summary>How the page is walked.</summary>
    public required TableRowPagingMode Paging { get; init; }

    /// <summary>
    /// The row key's columns: read raw for every row, and the keyset (or the offset's tiebreak) when there
    /// is one. Empty for a relation with no key.
    /// </summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>Whether to read <c>ctid::text</c> - a heap relation with no key.</summary>
    public bool SelectLocator { get; init; }

    /// <summary>
    /// For offset paging over a relation with neither a key nor a <c>ctid</c> (a view): the columns that
    /// make the order deterministic, after the sort column.
    /// </summary>
    public IReadOnlyList<string> TieBreakColumns { get; init; } = [];

    /// <summary>The sort column, or <see langword="null" /> for the key order.</summary>
    public string? SortColumn { get; init; }

    /// <summary>Whether the sort (or the key order) is descending.</summary>
    public bool Descending { get; init; }

    /// <summary>Where a keyset walk continues from.</summary>
    public TableRowCursor? Cursor { get; init; }

    /// <summary>The offset, for offset paging.</summary>
    public int Offset { get; init; }

    /// <summary>The rows the page shows; one more is asked for, to know whether there is a next page.</summary>
    public required int PageSize { get; init; }

    /// <summary>The filter, resolved.</summary>
    public IReadOnlyList<RowFilterTerm> Filter { get; init; } = [];

    /// <summary>The cell caps.</summary>
    public TableRowCaps Caps { get; init; } = TableRowCaps.List;
}

/// <summary>
/// Every statement the database browser runs against a non-Marten relation's rows (AGENTS.md hard
/// rule 4): the page read, the row and cell reads, the exact count, and the two reference checks.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is interpolated.</b> Quoted identifiers - the relation's schema and name and its column names,
/// every one of them as the catalog spells it (the caller hands in the grant's names, never a URL's) - and
/// keywords and operators this class chooses. Nothing else: no value, no type name. Every cursor, key and
/// filter value is a parameter sent as untyped text (<see cref="NpgsqlDbType.Unknown" />), so Postgres
/// resolves it against the column and applies the column's own input function - which is what makes an
/// enum label, a domain, a <c>citext</c>, a <c>bpchar</c> and <c>007</c> in a text column all compare the way
/// they would in <c>psql</c>. The one cast written is <c>::text</c>, to read a value's text form.
/// </para>
/// <para>
/// <b>Every built-in is <c>pg_catalog.</c>-qualified</b> - <c>substring</c>, <c>octet_length</c>,
/// <c>lower</c>, <c>strpos</c>, <c>count</c> - so a reading role's <c>search_path</c> cannot put a function
/// of its own in front of them.
/// </para>
/// <para>
/// <b>Cells are cut on the server.</b> A text-form cell is <c>substring(col::text, 1, @cap)</c> beside
/// <c>octet_length(col::text)</c>, a <c>bytea</c> is <c>substring(col, 1, @bytes)</c> beside
/// <c>octet_length(col)</c>: a 40 MB value never crosses the wire to be drawn as a kilobyte. A text cap is
/// a number of UTF-8 bytes (<see cref="TableRowCaps" />): the server sends one character more than that
/// many, which is never fewer bytes than the cap, and the reader cuts it to the cap on a character
/// boundary - so a cut is known from a value that happens to fit exactly (a byte cap needs no such margin:
/// the octet length says whether bytes were cut). <c>substring</c> rather than <c>left</c>, because on a
/// column stored <c>EXTERNAL</c> <c>substring</c> reads only the TOAST chunks it needs; and
/// <c>octet_length</c> of a text column is answered from the TOAST header without detoasting it. A
/// <c>json</c> or <c>jsonb</c> column's text is rendered once per row, by a lateral subquery both
/// expressions read (<see cref="Laterals" />), rather than once for the cut and again for the length.
/// </para>
/// <para>
/// <b>The keyset is read raw.</b> The row key's columns, the sort column and <c>ctid</c> are selected a
/// second time as bare <c>::text</c>, apart from the cells, so a cursor is never built out of a cut value.
/// </para>
/// <para>
/// <b>Two texts for a nullable sort, never <c>@p is null</c>.</b> A walk sorted
/// <c>s nulls last, key</c> continues with <c>(s &gt; @s or s is null or (s = @s and key &gt; @key))</c> from a
/// row whose <c>s</c> was set, and with <c>(s is null and key &gt; @key)</c> from one whose <c>s</c> was NULL.
/// Folding the two into one text with <c>@s is null</c> leaves an untyped parameter Postgres cannot type
/// (<c>42P18</c>), and would anyway ask the planner to plan both walks at once.
/// </para>
/// </remarks>
internal static class TableRowQueryBuilder
{
    /// <summary>The largest offset a page may start at: past it Postgres counts rows only to discard them.</summary>
    public const int MaxOffset = DocumentQueryBuilder.MaxOffset;

    /// <summary>
    /// How many pointing rows an inbound count looks at before it stops - one more than it reports, so
    /// "1,000+" is a fact.
    /// </summary>
    public const int InboundCap = RelationshipQueries.DefaultInboundCap;

    /// <summary>The alias of the relation being read.</summary>
    private const string Alias = "t";

    /// <summary>What follows the relation's name in a <c>from</c>.</summary>
    private const string AsAlias = " as " + Alias + "\n";

    private static readonly HashSet<string> NativeTypes = new(StringComparer.Ordinal)
    {
        "smallint", "integer", "bigint", "real", "double precision", "boolean", "uuid", "oid",
        "timestamp without time zone", "timestamp with time zone",
    };

    private static readonly HashSet<string> TemporalTypes = new(StringComparer.Ordinal)
    {
        "date", "time without time zone", "time with time zone", "interval",
    };

    /// <summary>How a column of <paramref name="type" /> (a <c>format_type</c> spelling) is read.</summary>
    public static TableRowCellShape ShapeOf(string? type)
    {
        string normalized = Normalize(type);

        if (normalized.EndsWith("[]", StringComparison.Ordinal))
        {
            return TableRowCellShape.Array;
        }

        return normalized switch
        {
            "bytea" => TableRowCellShape.Binary,
            "json" or "jsonb" => TableRowCellShape.Json,
            "numeric" or "money" => TableRowCellShape.Number,
            _ when NativeTypes.Contains(normalized) => TableRowCellShape.Native,
            _ when TemporalTypes.Contains(normalized) || normalized.StartsWith("interval ", StringComparison.Ordinal)
                => TableRowCellShape.Temporal,
            _ => TableRowCellShape.Text,
        };
    }

    /// <summary>What a column's cells are, for the grid's styling.</summary>
    public static SqlCellKind KindOf(string? type)
    {
        string normalized = Normalize(type);

        return ShapeOf(type) switch
        {
            TableRowCellShape.Binary => SqlCellKind.Binary,
            TableRowCellShape.Json => SqlCellKind.Json,
            TableRowCellShape.Array => SqlCellKind.Array,
            TableRowCellShape.Number => SqlCellKind.Number,
            TableRowCellShape.Temporal => SqlCellKind.Timestamp,
            TableRowCellShape.Native => normalized switch
            {
                "boolean" => SqlCellKind.Boolean,
                "uuid" => SqlCellKind.Uuid,
                "timestamp without time zone" or "timestamp with time zone" => SqlCellKind.Timestamp,
                _ => SqlCellKind.Number,
            },
            _ => SqlCellKind.Text,
        };
    }

    /// <summary>The page read.</summary>
    /// <exception cref="ArgumentException">The spec does not hold together - a cursor for another walk, say.</exception>
    public static TableRowStatement BuildList(TableRowListSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentOutOfRangeException.ThrowIfLessThan(spec.PageSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(spec.Offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(spec.Offset, MaxOffset);

        if (spec.Paging == TableRowPagingMode.Key && spec.KeyColumns.Count == 0)
        {
            throw new ArgumentException("Keyset paging needs a key.", nameof(spec));
        }

        if (spec.Paging == TableRowPagingMode.Ctid && !spec.SelectLocator)
        {
            throw new ArgumentException("ctid paging reads the ctid.", nameof(spec));
        }

        var parameters = new Parameters();
        var sql = new StringBuilder("select ");
        var items = new SelectList(sql);

        List<int> keyOrdinals = [.. spec.KeyColumns.Select(column => items.Add(Column(column) + "::text"))];

        bool keyset = spec.Paging != TableRowPagingMode.Offset;
        int sortOrdinal = keyset && spec.SortColumn is { } sorted ? items.Add(Column(sorted) + "::text") : -1;
        int locatorOrdinal = spec.SelectLocator ? items.Add(Alias + ".ctid::text") : -1;

        var laterals = new Laterals();
        List<TableRowCellLayout> cells = [.. spec.Columns.Select(column => Cell(items, parameters, laterals, column, spec.Caps))];

        sql.Append("\nfrom ").Append(SqlIdentifier.Qualify(spec.Schema, spec.Name)).Append(AsAlias);
        laterals.AppendTo(sql);

        List<string> predicates = [.. spec.Filter.Select((term, i) => Predicate(term, i + 1, parameters))];

        if (keyset && spec.Cursor is { } cursor)
        {
            predicates.Add(Keyset(spec, cursor, parameters));
        }
        else if (spec.Cursor is not null)
        {
            throw new ArgumentException("An offset page takes no cursor.", nameof(spec));
        }

        AppendWhere(sql, predicates);

        List<string> order = OrderBy(spec);

        if (order.Count > 0)
        {
            sql.Append("order by ").Append(string.Join(", ", order)).Append('\n');
        }

        sql.Append("limit ").Append(parameters.Add("limit", NpgsqlDbType.Integer, spec.PageSize + 1));

        if (!keyset)
        {
            sql.Append(" offset ").Append(parameters.Add("offset", NpgsqlDbType.Integer, spec.Offset));
        }

        return new TableRowStatement(
            sql.ToString(),
            parameters.All,
            new TableRowLayout(spec.KeyColumns, keyOrdinals, sortOrdinal, locatorOrdinal, cells, []));
    }

    /// <summary>One row by its key (or, for a relation with none, by its <c>ctid</c>), every column read under <paramref name="caps" />.</summary>
    /// <param name="schema">The relation's schema, as the catalog spells it.</param>
    /// <param name="name">The relation's name, as the catalog spells it.</param>
    /// <param name="columns">The cells to read.</param>
    /// <param name="keyColumns">The key columns - or empty, and <paramref name="locator" /> given.</param>
    /// <param name="keyValues">The key values, raw text, in key order.</param>
    /// <param name="locator">A <c>ctid</c> to read by instead of a key.</param>
    /// <param name="caps">The caps.</param>
    public static TableRowStatement BuildRow(
        string schema,
        string name,
        IReadOnlyList<TableRowColumnRead> columns,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> keyValues,
        string? locator,
        TableRowCaps caps)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(caps);

        var parameters = new Parameters();
        var sql = new StringBuilder("select ");
        var items = new SelectList(sql);

        List<int> keyOrdinals = [.. keyColumns.Select(column => items.Add(Column(column) + "::text"))];

        var laterals = new Laterals();
        List<TableRowCellLayout> cells = [.. columns.Select(column => Cell(items, parameters, laterals, column, caps))];

        sql.Append("\nfrom ").Append(SqlIdentifier.Qualify(schema, name)).Append(AsAlias);
        laterals.AppendTo(sql);
        AppendWhere(sql, ByKey(keyColumns, keyValues, locator, parameters));
        sql.Append("limit 1");

        return new TableRowStatement(sql.ToString(), parameters.All, new TableRowLayout(keyColumns, keyOrdinals, -1, -1, cells, []));
    }

    /// <summary>
    /// One cell of one row, whole, up to <paramref name="cap" /> bytes: the first <paramref name="cap" /> bytes
    /// of a <c>bytea</c>, or one character more than <paramref name="cap" /> of anything else's text, which the
    /// reader cuts to <paramref name="cap" /> UTF-8 bytes (see <see cref="TableRowCaps" />).
    /// </summary>
    /// <param name="schema">The relation's schema.</param>
    /// <param name="name">The relation's name.</param>
    /// <param name="column">The column.</param>
    /// <param name="keyColumns">The key columns - or empty, and <paramref name="locator" /> given.</param>
    /// <param name="keyValues">The key values, raw text, in key order.</param>
    /// <param name="locator">A <c>ctid</c> to read by instead of a key.</param>
    /// <param name="cap">How many bytes to keep.</param>
    public static TableRowStatement BuildCell(
        string schema,
        string name,
        TableRowColumnRead column,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> keyValues,
        string? locator,
        int cap)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);

        var parameters = new Parameters();
        var sql = new StringBuilder("select ");
        var items = new SelectList(sql);

        string reference = Column(column.Name);
        var laterals = new Laterals();
        int value;
        int length;

        if (column.Shape == TableRowCellShape.Binary)
        {
            string bytes = parameters.Add("cap", NpgsqlDbType.Integer, cap);
            value = items.Add("pg_catalog.substring(" + reference + ", 1, " + bytes + ")");
            length = items.Add("pg_catalog.octet_length(" + reference + ")");
        }
        else
        {
            // A JSON value is rendered to text once, by a lateral of its own, and both expressions read that.
            string text = column.Shape == TableRowCellShape.Json ? laterals.Add(reference) : reference + "::text";
            string chars = parameters.Add("cap", NpgsqlDbType.Integer, checked(cap + 1));
            value = items.Add("pg_catalog.substring(" + text + ", 1, " + chars + ")");
            length = items.Add("pg_catalog.octet_length(" + text + ")");
        }

        sql.Append("\nfrom ").Append(SqlIdentifier.Qualify(schema, name)).Append(AsAlias);
        laterals.AppendTo(sql);
        AppendWhere(sql, ByKey(keyColumns, keyValues, locator, parameters));
        sql.Append("limit 1");

        return new TableRowStatement(
            sql.ToString(),
            parameters.All,
            new TableRowLayout(
                [],
                [],
                -1,
                -1,
                [new TableRowCellLayout(column.Name, column.Type, column.Shape == TableRowCellShape.Binary ? TableRowCellShape.Binary : TableRowCellShape.Text, value, length)],
                []));
    }

    /// <summary>
    /// The raw <c>::text</c> of <paramref name="wanted" /> for one row by its key - the values a reference
    /// check compares with, never cut.
    /// </summary>
    /// <param name="schema">The relation's schema.</param>
    /// <param name="name">The relation's name.</param>
    /// <param name="keyColumns">The key columns.</param>
    /// <param name="keyValues">The key values, raw text.</param>
    /// <param name="wanted">The columns to read.</param>
    public static TableRowStatement BuildKeyRead(
        string schema,
        string name,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> keyValues,
        IReadOnlyList<string> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);

        if (keyColumns.Count == 0)
        {
            throw new ArgumentException("A key read needs a key.", nameof(keyColumns));
        }

        var parameters = new Parameters();
        var sql = new StringBuilder("select ");
        var items = new SelectList(sql);

        List<int> keyOrdinals = [.. keyColumns.Select(column => items.Add(Column(column) + "::text"))];
        List<int> valueOrdinals = [.. wanted.Select(column => items.Add(Column(column) + "::text"))];

        sql.Append("\nfrom ").Append(SqlIdentifier.Qualify(schema, name)).Append(AsAlias);
        AppendWhere(sql, ByKey(keyColumns, keyValues, null, parameters));
        sql.Append("limit 1");

        return new TableRowStatement(sql.ToString(), parameters.All, new TableRowLayout(keyColumns, keyOrdinals, -1, -1, [], valueOrdinals));
    }

    /// <summary>
    /// Whether the parent row a foreign key names exists: <c>exists</c>, which stops at the first match,
    /// against the parent as the catalog names it.
    /// </summary>
    /// <param name="schema">The parent's schema.</param>
    /// <param name="table">The parent table.</param>
    /// <param name="columns">The parent's columns the key points at.</param>
    /// <param name="values">This row's values for them, raw text.</param>
    public static TableRowStatement BuildExists(
        string schema,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> values)
    {
        var parameters = new Parameters();

        string sql = "select exists (\n    select 1\n    from " + SqlIdentifier.Qualify(schema, table) + " as p\n    where "
            + string.Join("\n      and ", Equalities("p", columns, values, parameters)) + ")";

        return new TableRowStatement(sql, parameters.All, TableRowLayout.Scalar);
    }

    /// <summary>
    /// How many rows of a pointing table reference one row, bounded: <c>count(*)</c> over at most
    /// <paramref name="cap" /> matches, so a table of ten million says "1,000+" rather than scanning.
    /// </summary>
    /// <param name="schema">The pointing table's schema.</param>
    /// <param name="table">The pointing table.</param>
    /// <param name="columns">Its foreign key's columns.</param>
    /// <param name="values">The referenced row's values, raw text.</param>
    /// <param name="cap">How many matches to look at.</param>
    public static TableRowStatement BuildInboundCount(
        string schema,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> values,
        int cap = InboundCap)
    {
        var parameters = new Parameters();
        List<string> equalities = Equalities("c", columns, values, parameters);

        string sql = "select pg_catalog.count(*)\nfrom (\n    select 1\n    from " + SqlIdentifier.Qualify(schema, table) + " as c\n    where "
            + string.Join("\n      and ", equalities) + "\n    limit " + parameters.Add("cap", NpgsqlDbType.Integer, Math.Max(cap, 1))
            + ") as bounded";

        return new TableRowStatement(sql, parameters.All, TableRowLayout.Scalar);
    }

    /// <summary>
    /// The exact count: the whole relation, or - with a filter - at most <paramref name="cap" /> matching
    /// rows, so a filtered count is bounded the way an inbound count is.
    /// </summary>
    /// <param name="schema">The relation's schema.</param>
    /// <param name="name">The relation's name.</param>
    /// <param name="filter">The filter; empty for the whole relation.</param>
    /// <param name="cap">For a filtered count, how many matches to look at.</param>
    public static TableRowStatement BuildCount(string schema, string name, IReadOnlyList<RowFilterTerm> filter, long cap)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var parameters = new Parameters();
        string relation = SqlIdentifier.Qualify(schema, name) + " as " + Alias;

        if (filter.Count == 0)
        {
            return new TableRowStatement("select pg_catalog.count(*)\nfrom " + relation, [], TableRowLayout.Scalar);
        }

        List<string> predicates = [.. filter.Select((term, i) => Predicate(term, i + 1, parameters))];

        string sql = "select pg_catalog.count(*)\nfrom (\n    select 1\n    from " + relation + "\n    where "
            + string.Join("\n      and ", predicates) + "\n    limit "
            + parameters.Add("cap", NpgsqlDbType.Bigint, Math.Max(cap, 1)) + ") as bounded";

        return new TableRowStatement(sql, parameters.All, TableRowLayout.Scalar);
    }

    private static TableRowCellLayout Cell(
        SelectList items,
        Parameters parameters,
        Laterals laterals,
        TableRowColumnRead column,
        TableRowCaps caps)
    {
        string reference = Column(column.Name);
        TableRowCellShape shape = column.Shape;

        switch (shape)
        {
            case TableRowCellShape.Native:
                return new TableRowCellLayout(column.Name, column.Type, shape, items.Add(reference), -1);

            case TableRowCellShape.Binary:
            {
                // No +1 here: octet_length says whether bytes were cut, and bytes have no code points.
                string bytes = parameters.Once("bytes", NpgsqlDbType.Integer, caps.Binary);
                int value = items.Add("pg_catalog.substring(" + reference + ", 1, " + bytes + ")");
                int length = items.Add("pg_catalog.octet_length(" + reference + ")");
                return new TableRowCellLayout(column.Name, column.Type, shape, value, length);
            }

            default:
            {
                string cap = shape == TableRowCellShape.Json
                    ? parameters.Once("jsonCap", NpgsqlDbType.Integer, checked(caps.Json + 1))
                    : parameters.Once("cap", NpgsqlDbType.Integer, checked(caps.Text + 1));

                // A jsonb value has no text until jsonb_out writes one, and two expressions over
                // "payload"::text would write it twice: a JSON column's text comes from a lateral of its own.
                string text = shape == TableRowCellShape.Json ? laterals.Add(reference) : reference + "::text";

                int value = items.Add("pg_catalog.substring(" + text + ", 1, " + cap + ")");
                int length = items.Add("pg_catalog.octet_length(" + text + ")");
                return new TableRowCellLayout(column.Name, column.Type, shape, value, length);
            }
        }
    }

    private static string Keyset(TableRowListSpec spec, TableRowCursor cursor, Parameters parameters)
    {
        bool ctid = spec.Paging == TableRowPagingMode.Ctid;

        if (cursor.IsCtid != ctid)
        {
            throw new ArgumentException("The cursor walks a different order.", nameof(spec));
        }

        IReadOnlyList<string> keyColumns = ctid ? [] : spec.KeyColumns;
        int expected = ctid ? 1 : keyColumns.Count;

        if (cursor.Key.Count != expected)
        {
            throw new ArgumentException("The cursor has " + cursor.Key.Count.ToString(CultureInfo.InvariantCulture)
                + " key values and the walk has " + expected.ToString(CultureInfo.InvariantCulture) + ".", nameof(spec));
        }

        if (!string.Equals(cursor.SortColumn, spec.SortColumn, StringComparison.Ordinal) || cursor.Descending != spec.Descending)
        {
            throw new ArgumentException("The cursor belongs to a different sort.", nameof(spec));
        }

        string keyExpression;
        string keyParameters;

        if (ctid)
        {
            keyExpression = Alias + ".ctid";
            keyParameters = parameters.Add("c", NpgsqlDbType.Unknown, cursor.Key[0]);
        }
        else if (keyColumns.Count == 1)
        {
            keyExpression = Column(keyColumns[0]);
            keyParameters = parameters.Add("k1", NpgsqlDbType.Unknown, cursor.Key[0]);
        }
        else
        {
            keyExpression = "(" + string.Join(", ", keyColumns.Select(Column)) + ")";
            keyParameters = "(" + string.Join(", ", cursor.Key.Select((value, i) =>
                parameters.Add("k" + (i + 1).ToString(CultureInfo.InvariantCulture), NpgsqlDbType.Unknown, value))) + ")";
        }

        if (spec.SortColumn is not { } sortColumn)
        {
            // The key alone: a plain row-value comparison, in the key's own direction.
            return keyExpression + (spec.Descending ? " < " : " > ") + keyParameters;
        }

        string sort = Column(sortColumn);

        // The key tiebreak is always ascending, whatever the sort's direction: that is how OrderBy writes it.
        if (cursor.SortValue is null)
        {
            return "(" + sort + " is null and " + keyExpression + " > " + keyParameters + ")";
        }

        string value = parameters.Add("s", NpgsqlDbType.Unknown, cursor.SortValue);

        return "(" + sort + (spec.Descending ? " < " : " > ") + value
            + " or " + sort + " is null"
            + " or (" + sort + " = " + value + " and " + keyExpression + " > " + keyParameters + "))";
    }

    private static List<string> OrderBy(TableRowListSpec spec)
    {
        List<string> order = [];
        string direction = spec.Descending ? " desc" : " asc";

        if (spec.SortColumn is { } sortColumn)
        {
            order.Add(Column(sortColumn) + direction + " nulls last");
        }

        // With no sort column the key is the order, in the asked direction; after a sort column it is a
        // tiebreak, always ascending - which is what the keyset predicate assumes.
        string tail = spec.SortColumn is null && spec.Descending ? " desc" : string.Empty;

        if (spec.Paging == TableRowPagingMode.Ctid || (spec.KeyColumns.Count == 0 && spec.SelectLocator))
        {
            order.Add(Alias + ".ctid" + tail);
        }
        else if (spec.KeyColumns.Count > 0)
        {
            order.AddRange(spec.KeyColumns.Select(column => Column(column) + tail));
        }
        else
        {
            order.AddRange(spec.TieBreakColumns
                .Where(column => !string.Equals(column, spec.SortColumn, StringComparison.Ordinal))
                .Select(column => Column(column) + tail));
        }

        // Empty only for an offset page of a view none of whose columns can be ordered: there is nothing to
        // order it by, and the page says so.
        if (order.Count == 0 && spec.Paging != TableRowPagingMode.Offset)
        {
            throw new ArgumentException("A keyset page needs an order.", nameof(spec));
        }

        return order;
    }

    private static string Predicate(RowFilterTerm term, int index, Parameters parameters)
    {
        string column = Column(term.Column);

        if (term.Operator == RowFilterOperator.IsNull)
        {
            return column + " is null";
        }

        if (term.Operator == RowFilterOperator.IsNotNull)
        {
            return column + " is not null";
        }

        string value = parameters.Add("f" + index.ToString(CultureInfo.InvariantCulture), NpgsqlDbType.Unknown, term.Value ?? string.Empty);

        return term.Operator switch
        {
            RowFilterOperator.Contains =>
                "pg_catalog.strpos(pg_catalog.lower(" + column + "::text), pg_catalog.lower(" + value + ")) > 0",
            RowFilterOperator.Equal => column + " = " + value,
            RowFilterOperator.NotEqual => column + " <> " + value,
            RowFilterOperator.LessThan => column + " < " + value,
            RowFilterOperator.LessThanOrEqual => column + " <= " + value,
            RowFilterOperator.GreaterThan => column + " > " + value,
            RowFilterOperator.GreaterThanOrEqual => column + " >= " + value,
            _ => throw new ArgumentException(term.Operator + " is not an operator the builder emits.", nameof(term)),
        };
    }

    private static List<string> ByKey(IReadOnlyList<string> keyColumns, IReadOnlyList<string> keyValues, string? locator, Parameters parameters)
    {
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(keyValues);

        if (keyColumns.Count == 0)
        {
            return locator is null
                ? throw new ArgumentException("A row is read by its key or by its ctid.", nameof(locator))
                : [Alias + ".ctid = " + parameters.Add("c", NpgsqlDbType.Unknown, locator)];
        }

        return Equalities(Alias, keyColumns, keyValues, parameters, "k");
    }

    private static List<string> Equalities(
        string alias,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> values,
        Parameters parameters,
        string prefix = "v")
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(values);

        if (columns.Count == 0 || columns.Count != values.Count)
        {
            throw new ArgumentException("Every column needs exactly one value.", nameof(values));
        }

        return
        [
            .. columns.Select((column, i) => alias + "." + SqlIdentifier.Quote(column) + " = "
                + parameters.Add(prefix + (i + 1).ToString(CultureInfo.InvariantCulture), NpgsqlDbType.Unknown, values[i])),
        ];
    }

    private static void AppendWhere(StringBuilder sql, List<string> predicates)
    {
        for (int i = 0; i < predicates.Count; i++)
        {
            sql.Append(i == 0 ? "where " : "  and ").Append(predicates[i]).Append('\n');
        }
    }

    private static string Column(string name) => Alias + "." + SqlIdentifier.Quote(name);

    private static string Normalize(string? type)
    {
        if (string.IsNullOrEmpty(type))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(type.Length);
        int depth = 0;

        foreach (char c in type)
        {
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth = Math.Max(depth - 1, 0);
            }
            else if (depth == 0)
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>The select list, one item per line, counting ordinals as it goes.</summary>
    private sealed class SelectList(StringBuilder sql)
    {
        private int count;

        public int Add(string expression)
        {
            if (count > 0)
            {
                sql.Append(",\n       ");
            }

            sql.Append(expression);
            return count++;
        }
    }

    /// <summary>
    /// One <c>cross join lateral (select t."col"::text as v offset 0) as jN</c> per JSON column: its text,
    /// rendered once per row and read twice - by the cut and by the length.
    /// </summary>
    /// <remarks>
    /// <c>offset 0</c> is what makes it once: without it the planner pulls the subquery up into the outer
    /// query and substitutes the expression back into both places that read <c>jN.v</c>. The lateral reads
    /// only the row the outer query is on, so a filter, a keyset predicate and a key lookup still apply to
    /// <c>t</c> before it, and the aliases are the builder's own - never an identifier from anywhere else.
    /// </remarks>
    private sealed class Laterals
    {
        private readonly List<string> clauses = [];

        /// <summary>Adds a lateral for <paramref name="reference" /> and answers the expression that reads its text.</summary>
        public string Add(string reference)
        {
            string alias = "j" + (clauses.Count + 1).ToString(CultureInfo.InvariantCulture);
            clauses.Add("cross join lateral (select " + reference + "::text as v offset 0) as " + alias);
            return alias + ".v";
        }

        /// <summary>Writes the laterals after the <c>from</c>, one per line.</summary>
        public void AppendTo(StringBuilder sql)
        {
            foreach (string clause in clauses)
            {
                sql.Append(clause).Append('\n');
            }
        }
    }

    /// <summary>The parameters, named once each.</summary>
    private sealed class Parameters
    {
        private readonly List<TableRowParameter> all = [];

        public IReadOnlyList<TableRowParameter> All => all;

        public string Add(string name, NpgsqlDbType type, object value)
        {
            if (all.Any(x => string.Equals(x.Name, name, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Parameter @" + name + " is already bound.");
            }

            all.Add(new TableRowParameter(name, type, value));
            return "@" + name;
        }

        /// <summary>A parameter shared by every cell that needs it - one cap, however many columns.</summary>
        public string Once(string name, NpgsqlDbType type, object value)
        {
            if (!all.Any(x => string.Equals(x.Name, name, StringComparison.Ordinal)))
            {
                all.Add(new TableRowParameter(name, type, value));
            }

            return "@" + name;
        }
    }
}
