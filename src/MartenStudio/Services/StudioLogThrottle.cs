using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Services;

/// <summary>
/// Decides whether a real anomaly on a polled or repeated path is worth a Warning <em>this</em> time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The Overview, the projections screen and the navigation badges each re-read
/// their data every <c>RefreshInterval</c> (five seconds by default), in every open circuit. A failure on
/// one of those paths - a database that stopped answering, a coordinator that throws - is a real thing to
/// tell an operator about, once. Told every five seconds per open tab it is a log that nobody can read
/// and an alert that fires all night, which is how a production host ends up muting the whole category
/// and missing the next warning that mattered.
/// </para>
/// <para>
/// <b>The rule.</b> <see cref="ShouldLog" /> answers <see langword="true" /> once per key per
/// <see cref="Window" />, where the key is the log site, the store, the database and the kind of failure.
/// A caller logs at Warning when it says yes and at Debug when it says no, so the detail of every
/// occurrence is still there for whoever turns Debug on - nothing is dropped, only demoted. A different
/// store, database or kind is a different key and is told about in its own right.
/// </para>
/// <para>
/// <b>The kind is finer than the exception type</b>, and every caller spells it with
/// <see cref="KindOf" />. Keyed on the type alone, every Postgres error is one
/// <c>Npgsql.PostgresException</c>, so a role that lost a grant (42501) silenced the pool running out of
/// connections (53300) and a deadlock (40P01) for ten minutes - three different problems, told about as
/// one. The SQLSTATE is part of the kind, and so is what an <c>NpgsqlException</c> wraps, because a
/// timeout, a refused socket and a TLS failure all arrive as that one type too.
/// </para>
/// <para>
/// <b>Bounded.</b> A singleton that grew one entry per distinct key for the life of the process would be
/// a slow leak in the one component that exists because something is going wrong. Expired entries are
/// pruned whenever the map is full, and if it is still full the oldest entry makes room: forgetting the
/// oldest key costs at most one extra Warning, which is the cheap direction to be wrong in.
/// </para>
/// <para>
/// Expected, configuration-driven states - no daemon, an externally managed daemon, event tables that do
/// not exist yet, a speculative count running out of its budget - never come here at all. They are
/// values the UI renders, and they log at Debug unconditionally.
/// </para>
/// </remarks>
internal sealed class StudioLogThrottle
{
    /// <summary>How long a key stays quiet after it has logged a Warning.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>The most keys the throttle remembers at once.</summary>
    public const int MaxKeys = 1_024;

    /// <summary>The unit separator, which no store key, database identity or type name contains.</summary>
    private const char Separator = '\u001f';

    private readonly TimeProvider timeProvider;
    private readonly Dictionary<string, DateTimeOffset> lastWarned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();

    /// <summary>A throttle on the system clock.</summary>
    public StudioLogThrottle() : this(TimeProvider.System)
    {
    }

    /// <summary>A throttle on <paramref name="timeProvider" />, which a test can move by hand.</summary>
    public StudioLogThrottle(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.timeProvider = timeProvider;
    }

    /// <summary>How many keys are remembered right now, for a test and for nothing else.</summary>
    internal int Count
    {
        get
        {
            lock (gate)
            {
                return lastWarned.Count;
            }
        }
    }

    /// <summary>
    /// The kind of failure <paramref name="exception" /> is, as the throttle keys it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exception type's full name; then, for a <see cref="PostgresException" />, a colon and its
    /// SQLSTATE (<c>Npgsql.PostgresException:42501</c>); for any other <see cref="NpgsqlException" />
    /// that wraps something, the wrapped type in brackets
    /// (<c>Npgsql.NpgsqlException(System.TimeoutException)</c>); and for anything else that wraps a
    /// <see cref="PostgresException" /> somewhere in its inner chain, that SQLSTATE after a colon. Nothing
    /// else is added: a message carries identifiers and values, and a key built from one would never
    /// repeat, which is a throttle that throttles nothing.
    /// </para>
    /// <para>
    /// <see cref="PostgresException" /> is tested first because it <em>is</em> an
    /// <see cref="NpgsqlException" />; its SQLSTATE is the fact, and its inner exception is normally empty.
    /// </para>
    /// </remarks>
    /// <param name="exception">What went wrong.</param>
    public static string KindOf(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string kind = NameOf(exception.GetType());

        return exception switch
        {
            PostgresException postgres => kind + ":" + postgres.SqlState,
            NpgsqlException { InnerException: { } inner } => kind + "(" + NameOf(inner.GetType()) + ")",
            _ when WrappedPostgres(exception.InnerException) is { } wrapped => kind + ":" + wrapped.SqlState,
            _ => kind,
        };
    }

    /// <summary>
    /// The first <see cref="PostgresException" /> in an exception's inner chain, for a caller that wraps
    /// one - Marten's <c>MartenCommandException</c> is the usual case - so that the SQLSTATE still keys
    /// the throttle.
    /// </summary>
    private static PostgresException? WrappedPostgres(Exception? exception)
    {
        for (int depth = 0; exception is not null && depth < 8; depth++, exception = exception.InnerException)
        {
            if (exception is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    /// <summary>
    /// The level for a site whose owner may have been built without a throttle: the throttle's answer,
    /// or Warning when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one place a throttled event may be written at a fixed Warning, and <c>LogLevelsTests</c> names
    /// it as the only helper a throttled call site may take its level from besides
    /// <see cref="WarningOrDebug" />. Three owners have a constructor a test calls by hand with no
    /// throttle - <c>StudioScopeCatalog</c>, <c>MartenStoreRegistry</c> (which asks the provider for one)
    /// and <c>StudioLiveUpdates</c> - and the container always supplies one, so in a running studio this
    /// is exactly <see cref="WarningOrDebug" />.
    /// </para>
    /// <para>
    /// Spelled out here rather than as <c>throttle?.WarningOrDebug(…) ?? LogLevel.Warning</c> at each site
    /// so that a literal Warning in front of a throttled event is always a mistake a scanner can name.
    /// </para>
    /// </remarks>
    public static LogLevel LevelOrWarning(
        StudioLogThrottle? throttle,
        string site,
        string? storeKey,
        string? databaseId,
        string? kind) =>
        throttle is null ? LogLevel.Warning : throttle.WarningOrDebug(site, storeKey, databaseId, kind);

    /// <summary>
    /// Whether this occurrence should be logged at Warning: the first for its key in <see cref="Window" />.
    /// </summary>
    /// <param name="site">A stable name for the log site, such as <c>DaemonAccessor.ForScope</c>.</param>
    /// <param name="storeKey">The store it is about, when it is about one.</param>
    /// <param name="databaseId">The database it is about, when it is about one.</param>
    /// <param name="kind">
    /// What went wrong, as <see cref="KindOf" /> spells an exception, or <see langword="null" /> for an
    /// anomaly that is not an exception (a row Marten would never have written, say).
    /// </param>
    /// <returns>
    /// <see langword="true" /> to log at Warning; <see langword="false" /> to log the same event at Debug.
    /// </returns>
    public bool ShouldLog(string site, string? storeKey, string? databaseId, string? kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(site);

        string key = $"{site}{Separator}{storeKey}{Separator}{databaseId}{Separator}{kind}";

        DateTimeOffset now = timeProvider.GetUtcNow();

        lock (gate)
        {
            if (lastWarned.TryGetValue(key, out DateTimeOffset warnedAt) && now - warnedAt < Window)
            {
                return false;
            }

            if (!lastWarned.ContainsKey(key) && lastWarned.Count >= MaxKeys)
            {
                MakeRoom(now);
            }

            lastWarned[key] = now;
            return true;
        }
    }

    /// <summary>
    /// <see cref="ShouldLog" />, answered as the level to log at: Warning the first time, Debug after.
    /// </summary>
    public LogLevel WarningOrDebug(string site, string? storeKey, string? databaseId, string? kind) =>
        ShouldLog(site, storeKey, databaseId, kind) ? LogLevel.Warning : LogLevel.Debug;

    private static string NameOf(Type type) => type.FullName ?? type.Name;

    /// <summary>
    /// Drops every expired key, and the oldest one if that freed nothing. Called under the lock.
    /// </summary>
    private void MakeRoom(DateTimeOffset now)
    {
        List<string>? expired = null;
        string? oldest = null;
        DateTimeOffset oldestAt = DateTimeOffset.MaxValue;

        foreach (KeyValuePair<string, DateTimeOffset> entry in lastWarned)
        {
            if (now - entry.Value >= Window)
            {
                (expired ??= []).Add(entry.Key);
            }

            if (entry.Value < oldestAt)
            {
                oldestAt = entry.Value;
                oldest = entry.Key;
            }
        }

        if (expired is not null)
        {
            foreach (string key in expired)
            {
                lastWarned.Remove(key);
            }

            return;
        }

        if (oldest is not null)
        {
            lastWarned.Remove(oldest);
        }
    }
}
