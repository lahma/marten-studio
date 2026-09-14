using System.Buffers;
using System.Text;
using System.Text.Json;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Query;

/// <summary>
/// Turns a page of results into the file somebody asked for: CSV for a spreadsheet, JSON for everything
/// else.
/// </summary>
/// <remarks>
/// <para>
/// It exports <em>what is on screen</em> and says nothing about the rest. The row cap has already stopped
/// the reader by the time anything gets here, so an export is a page and not a dump - which is the honest
/// behaviour for a console whose whole design is that it never streams a table into a browser.
/// </para>
/// <para>
/// The CSV is RFC 4180: fields containing a comma, a quote or a newline are quoted and inner quotes are
/// doubled. There is deliberately no <c>sep=</c> line and no BOM - both are Excel conveniences that make
/// the file wrong for everything else - and every value is already invariant-formatted text from
/// <see cref="SqlValueFormatter" />.
/// </para>
/// </remarks>
internal static class QueryExport
{
    /// <summary>The Marten mode's rows as a JSON array of <c>{ id, document }</c>.</summary>
    public static string ToJson(IReadOnlyList<MartenQueryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();

            foreach (MartenQueryRow row in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("id", row.Id);
                writer.WritePropertyName("document");

                // The document is already JSON, written by Marten's own serializer; re-serializing it
                // would be a second opinion about a document Marten has already had the last word on.
                WriteRawOrString(writer, row.Json);

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The Marten mode's rows as two CSV columns.</summary>
    public static string ToCsv(IReadOnlyList<MartenQueryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var text = new StringBuilder("id,document\r\n");

        foreach (MartenQueryRow row in rows)
        {
            text.Append(Escape(row.Id)).Append(',').Append(Escape(row.Json)).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>The SQL console's grid as a JSON array of objects keyed by column name.</summary>
    public static string ToJson(IReadOnlyList<SqlResultColumn> columns, IReadOnlyList<IReadOnlyList<SqlCell>> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();

            foreach (IReadOnlyList<SqlCell> row in rows)
            {
                writer.WriteStartObject();

                for (var i = 0; i < columns.Count; i++)
                {
                    writer.WritePropertyName(columns[i].Name);

                    if (i >= row.Count || row[i].Kind == SqlCellKind.Null)
                    {
                        // NULL is null, not "NULL": the whole point of the grid's distinction survives the
                        // export.
                        writer.WriteNullValue();
                        continue;
                    }

                    if (row[i].Kind == SqlCellKind.Json)
                    {
                        WriteRawOrString(writer, row[i].Text);
                        continue;
                    }

                    writer.WriteStringValue(row[i].Text);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The SQL console's grid as CSV, one header row and one row per result row.</summary>
    public static string ToCsv(IReadOnlyList<SqlResultColumn> columns, IReadOnlyList<IReadOnlyList<SqlCell>> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        var text = new StringBuilder();

        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            text.Append(Escape(columns[i].Name));
        }

        text.Append("\r\n");

        foreach (IReadOnlyList<SqlCell> row in rows)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }

                // An empty field is how CSV spells "nothing here"; a literal NULL would be indistinguishable
                // from a string that says NULL.
                if (i < row.Count && row[i].Kind != SqlCellKind.Null)
                {
                    text.Append(Escape(row[i].Text));
                }
            }

            text.Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// Writes already-serialized JSON straight through, or as a string when it will not parse - which is
    /// what a truncated cell is.
    /// </summary>
    private static void WriteRawOrString(Utf8JsonWriter writer, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });

            document.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(json);
        }
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
