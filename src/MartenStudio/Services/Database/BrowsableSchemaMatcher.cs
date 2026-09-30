using System.Text;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Database;

/// <summary>
/// Turns <see cref="MartenStudioOptions.BrowsableSchemas" /> and the database's live schema list into the
/// set of schemas the database browser may read beyond the store's own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Live, because <c>"*"</c> is a statement about the database, not the configuration.</b> It means
/// "every schema the reading role has <c>USAGE</c> on", so it cannot be settled without asking - and the
/// asking is done inside the read-only session with <c>SET LOCAL ROLE</c>, so <c>USAGE</c> is the
/// <see cref="MartenStudioOptions.SqlConsoleRole" />'s when one is set.
/// </para>
/// <para>
/// <b>Never a system schema.</b> <c>pg_catalog</c>, <c>pg_toast</c>, the temporary schemas and
/// <c>information_schema</c> are refused by name, even when an entry names one exactly: the browser is for
/// the host's objects, and a <c>pg_catalog</c> row browser is a catalog dump by another route. <c>"*"</c>
/// also skips the schemas an extension created (TimescaleDB's <c>_timescaledb_*</c>, pg_cron's
/// <c>cron</c>), which are the extension's rather than the host's; an exact entry naming one is honoured,
/// because that is a host saying it wants it.
/// </para>
/// <para>
/// Exact entries compare case-sensitively (<see cref="StringComparison.Ordinal" />), as Postgres stores
/// the names: <c>"Quartz"</c> is not <c>quartz</c>. And a schema the reading role has no <c>USAGE</c> on is
/// never matched, whichever way it is named - nothing in it could be read.
/// </para>
/// </remarks>
internal static class BrowsableSchemaMatcher
{
    /// <summary>The entry that means every schema the role can use.</summary>
    public const string Everything = "*";

    /// <summary>Postgres' own limit on an identifier, in bytes.</summary>
    public const int MaxIdentifierBytes = 63;

    /// <summary>
    /// Whether <paramref name="entry" /> is one <see cref="MartenStudioOptions.BrowsableSchemas" /> accepts:
    /// <c>"*"</c>, or a schema name of at most 63 bytes with no double quote and no NUL.
    /// </summary>
    /// <remarks>
    /// The same two characters <see cref="SqlIdentifier.Quote" /> refuses, for the same reason: a name the
    /// studio could never quote is a name no schema it can read has.
    /// </remarks>
    public static bool IsValidEntry(string? entry)
    {
        if (entry is null)
        {
            return false;
        }

        if (entry == Everything)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(entry)
            && entry.IndexOf('"', StringComparison.Ordinal) < 0
            && entry.IndexOf('\0', StringComparison.Ordinal) < 0
            && Encoding.UTF8.GetByteCount(entry) <= MaxIdentifierBytes;
    }

    /// <summary>Whether <paramref name="schema" /> is one of Postgres' own, which is never browsable.</summary>
    public static bool IsSystemSchema(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        // pg_catalog, pg_toast, pg_temp_N and pg_toast_temp_N all start with it, and Postgres reserves the
        // prefix: CREATE SCHEMA pg_anything is refused with 42939.
        return schema.StartsWith("pg_", StringComparison.Ordinal)
            || string.Equals(schema, "information_schema", StringComparison.Ordinal);
    }

    /// <summary>Whether the entries include <c>"*"</c>.</summary>
    public static bool IncludesEverything(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Any(static x => x == Everything);
    }

    /// <summary>Whether <paramref name="schema" /> is named exactly by an entry.</summary>
    public static bool IsNamed(IEnumerable<string> entries, string schema)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Any(x => x != Everything && string.Equals(x, schema, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether an entry could admit <paramref name="schema" /> at all - by name, or through <c>"*"</c> -
    /// without asking the database. A <see langword="false" /> is final; a <see langword="true" /> still
    /// needs <see cref="Match" /> to settle <c>USAGE</c> and extension ownership.
    /// </summary>
    public static bool MightMatch(IEnumerable<string> entries, string schema)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(schema);

        return !IsSystemSchema(schema) && (IncludesEverything(entries) || IsNamed(entries, schema));
    }

    /// <summary>The live schemas the entries admit.</summary>
    /// <param name="entries">The configured entries.</param>
    /// <param name="live">The database's schemas, as the reading role sees them.</param>
    public static IReadOnlySet<string> Match(IEnumerable<string> entries, IReadOnlyList<CatalogSchema> live)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(live);

        string[] configured = [.. entries];
        bool everything = IncludesEverything(configured);
        HashSet<string> named = new(configured.Where(static x => x != Everything), StringComparer.Ordinal);

        HashSet<string> matched = new(StringComparer.Ordinal);

        foreach (CatalogSchema schema in live)
        {
            if (IsSystemSchema(schema.Name) || !schema.HasUsage)
            {
                continue;
            }

            if (named.Contains(schema.Name) || (everything && !schema.OwnedByExtension))
            {
                matched.Add(schema.Name);
            }
        }

        return matched;
    }
}
