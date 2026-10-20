namespace MartenStudio.Services.Query;

/// <summary>
/// The Marten where clause's rows, exported through the shared <see cref="QueryExport" />: C# 14 static extension
/// members, so the Query page and its tests keep calling <c>QueryExport.ToJson(rows)</c> with the Marten mode's rows.
/// </summary>
/// <remarks>
/// The shared export takes a document as its id and its JSON - all it ever read of a <see cref="MartenQueryRow" /> -
/// so the row type, which is MartenStudio's, stays out of the shared assembly. Nothing is reformatted on the way: the
/// JSON is the <c>data</c> column exactly as Marten wrote it.
/// </remarks>
internal static class MartenQueryExportBridges
{
    extension(QueryExport)
    {
        /// <summary>The Marten mode's rows as a JSON array of <c>{ id, document }</c>.</summary>
        /// <param name="rows">The rows the where clause returned.</param>
        public static string ToJson(IReadOnlyList<MartenQueryRow> rows) => QueryExport.ToJson(Documents(rows));

        /// <summary>The Marten mode's rows as two CSV columns.</summary>
        /// <param name="rows">The rows the where clause returned.</param>
        public static string ToCsv(IReadOnlyList<MartenQueryRow> rows) => QueryExport.ToCsv(Documents(rows));
    }

    private static List<(string Id, string Json)> Documents(IReadOnlyList<MartenQueryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        List<(string Id, string Json)> documents = new(rows.Count);

        foreach (MartenQueryRow row in rows)
        {
            documents.Add((row.Id, row.Json));
        }

        return documents;
    }
}
