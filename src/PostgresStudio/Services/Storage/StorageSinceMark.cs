using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Live;
using PostgresStudio.Services.Schema;

namespace PostgresStudio.Services.Storage;

/// <summary>
/// What the Storage page's Mark remembers (1.2-H-A, X6): the database's size and each table's size and writes, for the tables the
/// page named to its visitor - numbers only.
/// </summary>
/// <param name="At">When the facts were read (the view's <c>MeasuredAt</c>), not when the visitor pressed Mark.</param>
/// <param name="DatabaseBytes">The database's measured size, when it was measured.</param>
/// <param name="Tables">The named tables, by <c>schema.name</c>; a table the page withheld is never in it.</param>
internal sealed record StorageMark(DateTimeOffset At, long? DatabaseBytes, IReadOnlyDictionary<string, StorageMarkRow> Tables);

/// <summary>One table at a mark.</summary>
/// <param name="TotalBytes">Heap, indexes and TOAST, from <c>relpages</c>.</param>
/// <param name="Inserts"><c>n_tup_ins</c>.</param>
/// <param name="Updates"><c>n_tup_upd</c>.</param>
/// <param name="Deletes"><c>n_tup_del</c>.</param>
internal sealed record StorageMarkRow(long TotalBytes, long? Inserts, long? Updates, long? Deletes);

/// <summary>One table's column, counted from a mark.</summary>
/// <param name="Text">"+12.0 MiB · 3,400 rows written", "–" for a withheld table, "not listed at your mark".</param>
/// <param name="Title">The writes split into inserted, updated and deleted, or <see langword="null" />.</param>
internal sealed record StorageRowSince(string Text, string? Title);

/// <summary>The Storage page counted from a mark: <see cref="Rows" /> parallel the view's tables.</summary>
/// <param name="Invalid">Why the mark no longer holds, or <see langword="null" /> while it does; then nothing else is set.</param>
/// <param name="Note">A caveat while it holds, or <see langword="null" />.</param>
/// <param name="DatabaseChange">The database's size change, signed ("+34 MiB"), or <see langword="null" /> when either size is unknown.</param>
/// <param name="Rows">One per table of the view, in its order.</param>
internal sealed record StorageSince(string? Invalid, string? Note, string? DatabaseChange, IReadOnlyList<StorageRowSince> Rows);

/// <summary>The Storage page counted from a mark. Pure.</summary>
/// <remarks>
/// A table's size is the catalog's page count, which moves only when a <c>VACUUM</c> or <c>ANALYZE</c> recounts, so the size
/// change says "no size change counted yet" at zero; the writes are the live <c>n_tup_*</c> counters. Gauges that reset
/// themselves are not marked. Only a table the page names to the visitor is in a mark or has a column: a withheld table is "–".
/// </remarks>
internal static class StorageSinceMark
{
    /// <summary>The page's name in <see cref="SinceMarks" />.</summary>
    public const string Page = "storage";

    /// <summary>
    /// The mark of <paramref name="view" />, or <see langword="null" /> when it is not a read. Numbers only, and only for the
    /// tables the page names.
    /// </summary>
    /// <param name="view">The storage as the page shows it now.</param>
    public static StorageMark? Take(StorageView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.Refusal != DatabaseRefusal.None || view.MeasuredAt is not { } at)
        {
            return null;
        }

        Dictionary<string, StorageMarkRow> tables = new(StringComparer.Ordinal);

        foreach (StorageTableView table in view.Tables)
        {
            if (table is { Schema: { } schema, Name: { } name })
            {
                tables[schema + "." + name] = new StorageMarkRow(table.TotalBytes, table.Inserts, table.Updates, table.Deletes);
            }
        }

        return new StorageMark(at, view.DatabaseBytes, tables);
    }

    /// <summary><paramref name="view" /> counted from <paramref name="mark" />.</summary>
    /// <param name="view">The storage as the page shows it now.</param>
    /// <param name="mark">The mark.</param>
    public static StorageSince Apply(StorageView view, StorageMark mark)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(mark);

        if (!SinceMarks.Holds(mark.At, view.Window, out string? text))
        {
            return new StorageSince(text, null, null, []);
        }

        List<StorageRowSince> rows = new(view.Tables.Count);

        foreach (StorageTableView table in view.Tables)
        {
            rows.Add(Counted(table, mark));
        }

        return new StorageSince(
            null,
            text,
            view.DatabaseBytes is { } now && mark.DatabaseBytes is { } then ? (now == then ? "no change" : Signed(now - then)) : null,
            rows);
    }

    private static StorageRowSince Counted(StorageTableView table, StorageMark mark)
    {
        if (table is not { Schema: { } schema, Name: { } name })
        {
            return new StorageRowSince("–", null);
        }

        if (!mark.Tables.TryGetValue(schema + "." + name, out StorageMarkRow? held))
        {
            return new StorageRowSince("not listed at your mark", null);
        }

        long size = table.TotalBytes - held.TotalBytes;
        string sizeText = size == 0 ? "no size change counted yet" : Signed(size);

        long? inserted = Change(table.Inserts, held.Inserts);
        long? updated = Change(table.Updates, held.Updates);
        long? deleted = Change(table.Deletes, held.Deletes);

        if (inserted is < 0 || updated is < 0 || deleted is < 0)
        {
            return new StorageRowSince(sizeText + " · counters reset since your mark", null);
        }

        if (inserted is null && updated is null && deleted is null)
        {
            return new StorageRowSince(sizeText, null);
        }

        long written = (inserted ?? 0) + (updated ?? 0) + (deleted ?? 0);
        string rowsText = written == 0 ? "no rows written" : TableText.Count(written) + (written == 1 ? " row written" : " rows written");

        return new StorageRowSince(
            sizeText + " · " + rowsText,
            TableText.Count(inserted ?? 0) + " inserted, " + TableText.Count(updated ?? 0) + " updated, " + TableText.Count(deleted ?? 0)
            + " deleted since your mark");
    }

    /// <summary>The counter now less then; <see langword="null" /> when either is unread; negative when the counter went down.</summary>
    private static long? Change(long? now, long? then) => now is { } a && then is { } b ? a - b : null;

    private static string Signed(long bytes) =>
        bytes < 0 ? "-" + SchemaFormat.Bytes(-bytes) : "+" + SchemaFormat.Bytes(bytes);
}
