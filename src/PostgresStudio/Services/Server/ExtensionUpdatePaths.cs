using System.Collections.Concurrent;

namespace PostgresStudio.Services.Server;

/// <summary>
/// S07's answers from <c>pg_extension_update_paths</c>, remembered by database, extension, installed version and the server's
/// default version: the question is asked of the server once for each such four, not once a sweep.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is worth remembering.</b> <c>pg_extension_update_paths</c> reads every update script of the extension from the
/// server's disk and builds the whole graph of paths between its versions, for one row the check wants. The answer is the
/// extension's scripts' and nothing else's, and an upgrade that brings new scripts brings a new default version with them in
/// practice - which is a new key, so it is asked again - while an <c>ALTER EXTENSION … UPDATE</c> moves the installed version,
/// a new key too. What the key cannot see, new scripts on the server with the default version unchanged, is read again when
/// the process restarts or the memo fills.
/// </para>
/// <para>
/// <b>Bounded.</b> At most <see cref="Capacity" /> answers; past it the memo is emptied and starts again, which costs one
/// read per database per extension and nothing else. A database is named by its source's key and its identity, as the sweep
/// cache names it, so two sources never share an answer.
/// </para>
/// </remarks>
internal sealed class ExtensionUpdatePaths
{
    /// <summary>The most answers remembered before the memo starts again.</summary>
    public const int Capacity = 4096;

    private readonly ConcurrentDictionary<Key, bool> known = new();
    private int reads;

    /// <summary>How many times the server was asked, for a test.</summary>
    internal int ReadCount => Volatile.Read(ref reads);

    /// <summary>
    /// Whether the server has an update path for each of <paramref name="candidates" /> - remembered answers, and one read,
    /// through <paramref name="read" />, for those it does not know yet.
    /// </summary>
    /// <param name="source">The source's registration key.</param>
    /// <param name="database">The database's identity.</param>
    /// <param name="candidates">The extensions whose installed version differs from the default, with both versions.</param>
    /// <param name="read">Asks the server about the given candidates: a row for each it could answer.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Each candidate's answer, by name; one the read did not answer is absent.</returns>
    public async Task<IReadOnlyDictionary<string, bool>> GetAsync(
        string source,
        string database,
        IReadOnlyList<(string Name, string Installed, string Target)> candidates,
        Func<IReadOnlyList<(string Name, string Installed, string Target)>, CancellationToken, Task<IReadOnlyList<(string Name, bool Updatable)>>> read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(read);

        Dictionary<string, bool> answers = new(StringComparer.Ordinal);
        List<(string Name, string Installed, string Target)> misses = [];

        foreach ((string Name, string Installed, string Target) candidate in candidates)
        {
            if (known.TryGetValue(new Key(source, database, candidate.Name, candidate.Installed, candidate.Target), out bool updatable))
            {
                answers[candidate.Name] = updatable;
            }
            else
            {
                misses.Add(candidate);
            }
        }

        if (misses.Count == 0)
        {
            return answers;
        }

        Interlocked.Increment(ref reads);
        IReadOnlyList<(string Name, bool Updatable)> rows = await read(misses, cancellationToken).ConfigureAwait(false);

        if (known.Count + misses.Count > Capacity)
        {
            known.Clear();
        }

        foreach ((string Name, bool Updatable) row in rows)
        {
            int asked = misses.FindIndex(x => string.Equals(x.Name, row.Name, StringComparison.Ordinal));
            if (asked >= 0)
            {
                (string Name, string Installed, string Target) candidate = misses[asked];
                known[new Key(source, database, candidate.Name, candidate.Installed, candidate.Target)] = row.Updatable;
                answers[row.Name] = row.Updatable;
            }
        }

        return answers;
    }

    private readonly record struct Key(string Source, string Database, string Extension, string Installed, string Default);
}
