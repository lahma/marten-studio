using System.Buffers;
using System.Text;
using System.Text.Json;

namespace PostgresStudio.Services.Query;

/// <summary>One query somebody kept: the recent list and the named list hold the same shape.</summary>
/// <param name="Name">
/// What it is called. A recent entry is named by its own first line, a saved one by whatever was typed
/// into the save prompt.
/// </param>
/// <param name="Mode">Which editor it belongs in.</param>
/// <param name="Alias">
/// The document alias, for a Marten clause or a LINQ predicate (1.2: a LINQ entry for the event log carries
/// <c>(events)</c>, the target picker's own token). <see langword="null" /> for SQL.
/// </param>
/// <param name="Text">The clause, predicate or statement itself.</param>
/// <param name="OrderBy">
/// A LINQ entry's ordering (1.2): LINQ mode is two texts, the predicate and the ordering, and this is the second.
/// <see langword="null" /> for every other mode and for a LINQ query with no ordering.
/// </param>
/// <param name="Archived">A LINQ entry for the event log: its archived selector (<c>include</c> or <c>only</c>), or <see langword="null" />.</param>
internal sealed record SavedQuery(string Name, QueryMode Mode, string? Alias, string Text, string? OrderBy = null, string? Archived = null);

/// <summary>
/// Reads and writes the recent and saved query lists, as text, so the browser can keep them.
/// </summary>
/// <remarks>
/// <para>
/// The lists live in <c>martenStudio.prefs</c> (localStorage, with a cookie fallback) keyed by store and
/// mode, never on the server: a query somebody was writing is theirs, it is not worth a database table,
/// and a server-side list would be one more thing a shared studio leaks between colleagues.
/// </para>
/// <para>
/// The JSON is written and read by hand with <see cref="Utf8JsonWriter" /> and <see cref="JsonDocument" />
/// rather than through a serializer. It is a handful of fields, it has to survive a browser handing back
/// whatever was in storage - including something another version wrote, or nothing at all - and every
/// malformed shape has to come back as "no saved queries" instead of an exception on a circuit.
/// </para>
/// </remarks>
internal static class SavedQueryStore
{
    /// <summary>How many recent queries are kept per store and mode.</summary>
    public const int MaxRecent = 25;

    /// <summary>How many named queries are kept per store and mode.</summary>
    public const int MaxSaved = 100;

    /// <summary>The longest query text that is remembered. Longer ones still run; they are not stored.</summary>
    public const int MaxTextLength = 64 * 1024;

    /// <summary>The longest name that is remembered.</summary>
    public const int MaxNameLength = 120;

    /// <summary>The preference key the recent list for one store and mode is kept under.</summary>
    public static string RecentKey(string? storeKey, QueryMode mode) => Key("recent", storeKey, mode);

    /// <summary>The preference key the named list for one store and mode is kept under.</summary>
    public static string SavedKey(string? storeKey, QueryMode mode) => Key("saved", storeKey, mode);

    /// <summary>The whole list as JSON, ready for <c>martenStudio.prefs.set</c> or a download.</summary>
    public static string Serialize(IReadOnlyList<SavedQuery> queries)
    {
        ArgumentNullException.ThrowIfNull(queries);

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();

            foreach (var query in queries)
            {
                writer.WriteStartObject();
                writer.WriteString("name", query.Name);
                writer.WriteString("mode", query.Mode.ToString());
                writer.WriteString("alias", query.Alias);
                writer.WriteString("text", query.Text);

                // Written only when there is one, so a list with no LINQ entry is byte-for-byte what 1.1 wrote.
                if (query.OrderBy is not null)
                {
                    writer.WriteString("orderBy", query.OrderBy);
                }

                if (query.Archived is not null)
                {
                    writer.WriteString("archived", query.Archived);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// The list a string holds, or an empty list. Never throws: the input is whatever a browser had.
    /// </summary>
    public static IReadOnlyList<SavedQuery> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<SavedQuery> queries = [];

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var text = ReadString(element, "text");
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var name = ReadString(element, "name");
                var mode = Enum.TryParse(ReadString(element, "mode"), ignoreCase: true, out QueryMode parsed)
                    ? parsed
                    : QueryMode.Marten;

                queries.Add(Normalize(new SavedQuery(
                    string.IsNullOrWhiteSpace(name) ? DefaultName(text) : name,
                    mode,
                    ReadString(element, "alias"),
                    text,
                    ReadString(element, "orderBy"),
                    ReadString(element, "archived"))));

                if (queries.Count == MaxSaved)
                {
                    break;
                }
            }

            return queries;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// <paramref name="entry" /> at the head of the recent list, with any identical earlier run removed
    /// and the list cut to <see cref="MaxRecent" />.
    /// </summary>
    public static IReadOnlyList<SavedQuery> AddRecent(IReadOnlyList<SavedQuery> existing, SavedQuery entry)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(entry);

        var normalized = Normalize(entry);

        if (string.IsNullOrWhiteSpace(normalized.Text))
        {
            return existing;
        }

        List<SavedQuery> queries = [normalized];

        foreach (var candidate in existing)
        {
            if (string.Equals(candidate.Text, normalized.Text, StringComparison.Ordinal)
                && candidate.Mode == normalized.Mode
                && string.Equals(candidate.Alias, normalized.Alias, StringComparison.Ordinal)
                && string.Equals(candidate.OrderBy, normalized.OrderBy, StringComparison.Ordinal)
                && string.Equals(candidate.Archived, normalized.Archived, StringComparison.Ordinal))
            {
                continue;
            }

            queries.Add(candidate);

            if (queries.Count == MaxRecent)
            {
                break;
            }
        }

        return queries;
    }

    /// <summary>
    /// <paramref name="entry" /> added to the named list, replacing one of the same name in place.
    /// </summary>
    public static IReadOnlyList<SavedQuery> Save(IReadOnlyList<SavedQuery> existing, SavedQuery entry)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(entry);

        var normalized = Normalize(entry);
        List<SavedQuery> queries = [];
        var replaced = false;

        foreach (var candidate in existing)
        {
            if (!replaced && string.Equals(candidate.Name, normalized.Name, StringComparison.OrdinalIgnoreCase))
            {
                queries.Add(normalized);
                replaced = true;
                continue;
            }

            queries.Add(candidate);
        }

        if (!replaced)
        {
            queries.Insert(0, normalized);
        }

        if (queries.Count > MaxSaved)
        {
            queries.RemoveRange(MaxSaved, queries.Count - MaxSaved);
        }

        return queries;
    }

    /// <summary>The list without the entry of this name.</summary>
    public static IReadOnlyList<SavedQuery> Remove(IReadOnlyList<SavedQuery> existing, string name)
    {
        ArgumentNullException.ThrowIfNull(existing);

        List<SavedQuery> queries = [];

        foreach (var candidate in existing)
        {
            if (!string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                queries.Add(candidate);
            }
        }

        return queries;
    }

    /// <summary>The first line of a query, which is what an unnamed recent entry is called.</summary>
    public static string DefaultName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var line = text.Trim();
        var newLine = line.IndexOfAny(['\r', '\n']);

        if (newLine >= 0)
        {
            line = line[..newLine].TrimEnd() + " …";
        }

        return line.Length <= MaxNameLength ? line : line[..MaxNameLength] + "…";
    }

    private static SavedQuery Normalize(SavedQuery entry)
    {
        var text = entry.Text.Length > MaxTextLength ? entry.Text[..MaxTextLength] : entry.Text;
        var name = entry.Name.Trim();

        if (string.IsNullOrEmpty(name))
        {
            name = DefaultName(text);
        }
        else if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength];
        }

        var alias = string.IsNullOrWhiteSpace(entry.Alias) ? null : entry.Alias.Trim();
        var orderBy = string.IsNullOrWhiteSpace(entry.OrderBy) ? null : entry.OrderBy.Trim();
        var archived = string.IsNullOrWhiteSpace(entry.Archived) ? null : entry.Archived.Trim();

        if (orderBy is { Length: > MaxTextLength })
        {
            orderBy = orderBy[..MaxTextLength];
        }

        return entry with { Name = name, Text = text, Alias = alias, OrderBy = orderBy, Archived = archived };
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Key(string kind, string? storeKey, QueryMode mode) =>
        "ms_query_" + kind + "_" +
        (string.IsNullOrWhiteSpace(storeKey) ? "default" : storeKey.ToLowerInvariant()) + "_" +
        mode.ToString().ToLowerInvariant();
}
