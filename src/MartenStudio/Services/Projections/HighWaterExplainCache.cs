using System.Collections.Concurrent;

using MartenStudio.Services.Database;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

namespace MartenStudio.Services.Projections;

/// <summary>
/// What the D01 Explain read from the database, before any visitor's gate is applied: the reading, every session and
/// prepared transaction it found, and the statements it ran.
/// </summary>
/// <param name="Unavailable">Why nothing was asked, or <see langword="null" /> when the questions were put.</param>
/// <param name="Reading">The high-water reading the questions were judged against.</param>
/// <param name="Sessions">Every session listed, with its statement text as read (unmasked).</param>
/// <param name="Prepared">Every prepared transaction listed.</param>
/// <param name="HiddenWriters">Write transactions whose start the reading role cannot see.</param>
/// <param name="Statements">What was run, for "Show SQL".</param>
internal sealed record HighWaterExplainRead(
    string? Unavailable,
    HighWaterReading? Reading,
    IReadOnlyList<HighWaterSuspect> Sessions,
    IReadOnlyList<PreparedTransactionInfo> Prepared,
    long HiddenWriters,
    IReadOnlyList<HealthStatement> Statements);

/// <summary>
/// The Explain's database read, shared: one read per store and database at a time, kept for
/// <see cref="TimeToLive" />, whichever circuit asked (plan §8).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is cached at all.</b> The questions are a <c>pg_locks</c> scan and a <c>pg_stat_activity</c> read. A
/// pinned mark is exactly when several people open the projections screen and press Explain, and N presses must cost
/// the database one read a minute, not N - the same rule D10's snapshot cache keeps for the polls, over a longer life.
/// </para>
/// <para>
/// <b>The raw read is shared; the visitor's view of it is not.</b> What is cached is the unmasked read. Each caller's
/// gate - whether sessions are named, which schemas are masked in a statement - is applied to it afresh, so a cached
/// read never carries one visitor's permissions to another. The shared work runs on no caller's token: one tab
/// navigating away does not cancel the read four others wait on, and every statement is bounded by
/// <c>statement_timeout</c> anyway. A read that fails is dropped at once, so the next press asks again.
/// </para>
/// </remarks>
internal sealed class HighWaterExplainCache(TimeProvider timeProvider)
{
    /// <summary>How long one read answers every press, from every circuit.</summary>
    public static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(60);

    /// <summary>Above this many keys, expired ones are swept on the next new read.</summary>
    private const int PruneThreshold = 64;

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <summary>A cache on the system clock.</summary>
    public HighWaterExplainCache() : this(TimeProvider.System)
    {
    }

    /// <summary>How many reads are held, for a test.</summary>
    internal int Count => entries.Count;

    /// <summary>The read for <paramref name="key" /> - in hand, in flight, or started now.</summary>
    /// <param name="key">The store and database.</param>
    /// <param name="read">The read; runs at most once per key per <see cref="TimeToLive" />.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared read.</param>
    public async Task<HighWaterExplainRead> GetAsync(
        string key,
        Func<CancellationToken, Task<HighWaterExplainRead>> read,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(read);

        DateTimeOffset now = timeProvider.GetUtcNow();
        Entry fresh = new(now, new Lazy<Task<HighWaterExplainRead>>(() => read(CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication));

        Entry entry = entries.AddOrUpdate(key, fresh, (_, existing) => now - existing.CreatedAt < TimeToLive ? existing : fresh);

        if (ReferenceEquals(entry, fresh) && entries.Count > PruneThreshold)
        {
            foreach (KeyValuePair<string, Entry> old in entries)
            {
                if (now - old.Value.CreatedAt >= TimeToLive)
                {
                    entries.TryRemove(old);
                }
            }
        }

        try
        {
            return await entry.Work.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            throw;
        }
    }

    private sealed record Entry(DateTimeOffset CreatedAt, Lazy<Task<HighWaterExplainRead>> Work);
}

/// <summary>
/// A session's statement as the Explain shows it to one visitor: masked for their withheld schemas (D27), never ending
/// in half a name, or withheld outright when those schemas cannot be read.
/// </summary>
/// <remarks>
/// The same rule Database › Sessions applies (H3's <c>SessionQueryText</c>): <c>pg_stat_activity.query</c> is any
/// application's source text, so every withheld schema is masked in it by <see cref="PostgresErrorText.Redact" /> - as
/// a qualifier and as every whole token that spells it - and a text the read cut first loses the identifier, quoted
/// name or literal the cut left open, because <c>from hr_payr</c> is the start of a withheld <c>hr_payroll</c> that no
/// mask recognises. When H3 lands, this can become a call to its helper.
/// </remarks>
internal static class ExplainQueryText
{
    /// <summary>What is shown instead of a statement whose schemas could not be checked.</summary>
    public const string Withheld = "(statement withheld: the schemas it may name could not be checked against the ones you may see)";

    /// <summary>What a cut text ends with.</summary>
    public const string Cut = " …";

    /// <summary>The statement as this visitor is shown it.</summary>
    /// <param name="query">The first characters of the statement, as read.</param>
    /// <param name="truncated">Whether the read cut it.</param>
    /// <param name="withheld">The visitor's withheld schemas, or <see langword="null" /> when they could not be read.</param>
    public static string? Present(string? query, bool truncated, IReadOnlySet<string>? withheld)
    {
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        if (withheld is null)
        {
            return Withheld;
        }

        string text = truncated ? DropOpenTail(query) : query;
        string masked = PostgresErrorText.Redact(text, withheld) ?? text;

        return truncated ? masked.TrimEnd() + Cut : masked;
    }

    /// <summary><paramref name="text" /> without an open quoted name, an open literal, or the identifier it ends in.</summary>
    internal static string DropOpenTail(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string result = CutAtOpenQuote(CutAtOpenQuote(text, '"'), '\'');

        int end = result.Length;
        while (end > 0 && IsNameCharacter(result[end - 1]))
        {
            end--;
        }

        return result[..end];
    }

    private static string CutAtOpenQuote(string text, char quote)
    {
        int count = 0;
        int last = -1;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == quote)
            {
                count++;
                last = i;
            }
        }

        return count % 2 == 1 ? text[..last] : text;
    }

    private static bool IsNameCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' || c >= '\u0080';
}
