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
/// <para>
/// <b>And it is not only RFC 4180</b>, because RFC 4180 is not what makes a CSV dangerous. A cell that
/// begins <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return is a <em>formula</em> to
/// Excel, LibreOffice and Google Sheets, and <c>=cmd|' /C calc'!A0</c> in a document somebody stored is
/// how a database row becomes code execution on the machine of whoever opened the export. Every cell and
/// every header therefore goes through <see cref="Neutralize" /> first - see it for the one deliberate
/// exception, which is that a plain negative number stays a number.
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

    /// <summary>
    /// One CSV field: made safe to open in a spreadsheet, then quoted the way RFC 4180 asks.
    /// </summary>
    /// <remarks>
    /// The order matters. Neutralising first and quoting second keeps the apostrophe <em>inside</em> the
    /// quoted field where the spreadsheet will see it; doing it the other way round would put it in front
    /// of the opening quote, where it is not part of the value at all.
    /// </remarks>
    private static string Escape(string value)
    {
        string safe = Neutralize(value);

        if (safe.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return safe;
        }

        return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// Stops a cell from being read as a formula, by putting an apostrophe in front of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>=</c>, <c>@</c>, a tab and a carriage return are always prefixed: none of them begins a value
    /// anybody means literally, and the last two are the documented way round a guard that only looks at
    /// the first visible character.
    /// </para>
    /// <para>
    /// <b><c>-</c> and <c>+</c> are the trade-off, and it is made in favour of the data.</b> Prefixing
    /// every one of them would turn <c>-1</c> into <c>'-1</c> in every export of every negative number,
    /// which is a spreadsheet full of text where numbers should be - a real, everyday cost paid against a
    /// theoretical one. So a cell that is <em>entirely</em> a plain number (optional sign, digits, an
    /// optional decimal part, an optional exponent) is left exactly as it is, and anything else beginning
    /// with a sign is prefixed: <c>-1</c> and <c>+3.5e10</c> survive, while <c>-2+3</c>, <c>-1-2+cmd</c>
    /// and <c>-cmd|' /C calc'!A0</c> - every shape that is a <em>formula</em> rather than a number - do
    /// not. The apostrophe is visible in the file and is what a spreadsheet strips on open.
    /// </para>
    /// </remarks>
    internal static string Neutralize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            return value;
        }

        char first = value[0];

        if (first is '=' or '@' or '\t' or '\r')
        {
            return "'" + value;
        }

        return first is '-' or '+' && !IsPlainNumber(value) ? "'" + value : value;
    }

    /// <summary>
    /// Whether the whole cell is a plain invariant number - the one thing a leading sign is allowed to be.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than <c>double.TryParse</c>, which accepts <c>-Infinity</c>, <c>NaN</c>, group
    /// separators and surrounding whitespace. Every one of those would be a formula-shaped string sailing
    /// through on a technicality.
    /// </remarks>
    private static bool IsPlainNumber(string value)
    {
        var i = 0;

        if (value[0] is '-' or '+')
        {
            i++;
        }

        var digits = 0;

        while (i < value.Length && char.IsAsciiDigit(value[i]))
        {
            i++;
            digits++;
        }

        if (i < value.Length && value[i] == '.')
        {
            i++;

            while (i < value.Length && char.IsAsciiDigit(value[i]))
            {
                i++;
                digits++;
            }
        }

        if (digits == 0)
        {
            return false;
        }

        if (i < value.Length && value[i] is 'e' or 'E')
        {
            i++;

            if (i < value.Length && value[i] is '-' or '+')
            {
                i++;
            }

            var exponent = 0;

            while (i < value.Length && char.IsAsciiDigit(value[i]))
            {
                i++;
                exponent++;
            }

            if (exponent == 0)
            {
                return false;
            }
        }

        return i == value.Length;
    }
}
