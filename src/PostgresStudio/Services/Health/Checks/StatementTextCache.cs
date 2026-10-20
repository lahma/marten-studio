using PostgresStudio.Internal.Sql;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// The texts of the statements the statement checks have read, per store and database, by <c>queryid</c> - so that a
/// sweep whose kept statements are all known makes no text call at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> A text call costs the extension its whole query-text file, however few <c>queryid</c>s it names:
/// PostgreSQL 17's <c>pg_stat_statements_internal</c> with <c>showtext := true</c> loads the file and fetches every
/// entry's text before the <c>queryid</c> filter sees a row. A statement's text does not change while its entry lives
/// - the <c>queryid</c> is a hash of the statement's shape - so a text read once is the text the next sweep would read.
/// </para>
/// <para>
/// <b>Bounded, and only what was read.</b> At most <see cref="MaxPerDatabase" /> texts a database, each at most
/// <see cref="HealthStatementQueries.MaxTextCharacters" /> characters; a database that would hold more starts again
/// empty, which costs one text call. The bound is per database and not across them: a process holds up to
/// 200 × 4,096 characters (about 1.6 MB as UTF-16) for every database whose statement checks have run, for its whole
/// life - which grows with the deployment exactly as <see cref="HealthHistory" />'s rings do, and is said beside them. It holds only texts the checks already read for a visitor through the activity
/// gate, in memory, for the life of the process, and every one is still masked for whoever it is shown to - holding a
/// text changes nobody's view of it.
/// </para>
/// <para>
/// Singleton, like <see cref="HealthHistory" />: the sweep is the database's, so its memory is too.
/// </para>
/// </remarks>
internal sealed class StatementTextCache
{
    /// <summary>The most texts one database's cache holds.</summary>
    public const int MaxPerDatabase = 200;

    private readonly Dictionary<string, Dictionary<long, StatementTextRow>> byDatabase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();

    /// <summary>The text of <paramref name="queryId" /> in this database, when it has been read.</summary>
    public bool TryGet(string storeKey, string databaseId, long queryId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out StatementTextRow? text)
    {
        lock (gate)
        {
            if (byDatabase.TryGetValue(KeyOf(storeKey, databaseId), out Dictionary<long, StatementTextRow>? texts)
                && texts.TryGetValue(queryId, out StatementTextRow? found))
            {
                text = found;
                return true;
            }
        }

        text = null;
        return false;
    }

    /// <summary>Remembers texts just read for this database.</summary>
    public void Remember(string storeKey, string databaseId, IEnumerable<StatementTextRow> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);

        lock (gate)
        {
            string key = KeyOf(storeKey, databaseId);

            if (!byDatabase.TryGetValue(key, out Dictionary<long, StatementTextRow>? held))
            {
                held = [];
                byDatabase[key] = held;
            }

            foreach (StatementTextRow text in texts)
            {
                if (held.Count >= MaxPerDatabase && !held.ContainsKey(text.QueryId))
                {
                    held.Clear();
                }

                held[text.QueryId] = text;
            }
        }
    }

    /// <summary>The <c>queryid</c>s whose text a database's cache holds, for a test and for nothing else.</summary>
    internal IReadOnlySet<long> Known(string storeKey, string databaseId)
    {
        lock (gate)
        {
            return byDatabase.TryGetValue(KeyOf(storeKey, databaseId), out Dictionary<long, StatementTextRow>? held)
                ? new HashSet<long>(held.Keys)
                : new HashSet<long>();
        }
    }

    private static string KeyOf(string storeKey, string databaseId) => storeKey + "|" + databaseId;
}
