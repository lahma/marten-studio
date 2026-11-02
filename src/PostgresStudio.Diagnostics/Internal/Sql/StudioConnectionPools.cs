using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

using Npgsql;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// Issue #44: the studio's share of an application's connection pool - at most <see cref="StudioConnectionPool.Max" /> connections at a time per
/// pool (an <see cref="NpgsqlDataSource" />), and the name its sessions carry in <c>pg_stat_activity</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per pool, set once per process.</b> A studio reads through the application's own data sources, so every connection it opens
/// is one the application's pool lends it. The cap is per data source instance - the pool the connection is lent from - and its
/// value, like the session name, is the one the studio in this process configured (D30: one studio per process), held to the data
/// source's own <c>Maximum Pool Size</c> (issue #49); it is set when a
/// studio's source provider is first built (<see cref="Configure" />). A data source no studio has configured is never capped and
/// never named, so a connection a test builds by hand behaves as it always did.
/// </para>
/// <para>
/// <b>Never a deadlock.</b> A slot is taken when a studio connection opens (<see cref="StudioConnections.OpenStudioAsync" />) and
/// given back when it closes. An operation that already holds a slot of a pool - in the same asynchronous flow, a nested read or a
/// fan-out of one - takes the next connection of that pool without waiting, so no read waits for a slot its own caller holds. A
/// wait is bounded by the studio's <c>QueryTimeout</c> and by the caller's token, and a wait that runs out says so
/// (<see cref="StudioConnectionsBusyException" />).
/// </para>
/// </remarks>
internal static class StudioConnectionPools
{
    /// <summary>The cap when a studio sets none: two, the issue's default - the studio is there to watch, not to add load.</summary>
    public const int DefaultMax = 2;

    /// <summary>The most a host may allow the studio, per pool.</summary>
    public const int MaxAllowed = 100;

    /// <summary>PostgreSQL keeps fewer than this many bytes of an <c>application_name</c> (<c>NAMEDATALEN</c> less one).</summary>
    public const int MaxApplicationNameBytes = 63;

    private static readonly ConditionalWeakTable<NpgsqlDataSource, StudioConnectionPool> Pools = new();
    private static readonly Lock Gate = new();
    private static Configuration? current;

    /// <summary>What the studio in this process configured.</summary>
    /// <param name="Max">The cap per pool.</param>
    /// <param name="ApplicationName">The name the host set, <c>""</c> for none, or <see langword="null" /> for the derived one.</param>
    /// <param name="Product">The product the derived name ends with: <c>Marten Studio</c>.</param>
    /// <param name="Timeout">How long an open waits for a slot before it gives up.</param>
    private sealed record Configuration(int Max, string? ApplicationName, string Product, TimeSpan Timeout);

    /// <summary>
    /// Sets what every pool this process's studio reads through is held to - called by a studio's source provider when it is built.
    /// A pool already created keeps its cap; its name follows.
    /// </summary>
    public static void Configure(int max, string? applicationName, string product, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrEmpty(product);

        lock (Gate)
        {
            current = new Configuration(Math.Clamp(max, 1, MaxAllowed), applicationName, product, timeout);
        }
    }

    /// <summary>
    /// The studio's share of <paramref name="dataSource" />, held to what its own studio configured: a source provider that knows
    /// its settings registers each data source it hands out, so the first registration of a pool wins whatever another container
    /// in the process configured later (a test host's, say).
    /// </summary>
    public static StudioConnectionPool Register(NpgsqlDataSource dataSource, int max, string? applicationName, string product, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrEmpty(product);

        return GetOrCreate(dataSource, source => CreatePool(source, Math.Clamp(max, 1, MaxAllowed), applicationName, product, timeout));
    }

    /// <summary>
    /// The pool <paramref name="dataSource" /> lends the studio its connections from - the one registered for it, or one held to what
    /// this process's studio configured - or <see langword="null" /> when no studio is configured.
    /// </summary>
    public static StudioConnectionPool? Of(NpgsqlDataSource? dataSource)
    {
        if (dataSource is null)
        {
            return null;
        }

        if (Pools.TryGetValue(dataSource, out StudioConnectionPool? registered))
        {
            return registered;
        }

        Configuration? configuration;

        lock (Gate)
        {
            configuration = current;
        }

        return configuration is null
            ? null
            : GetOrCreate(dataSource, source => CreatePool(source, configuration.Max, configuration.ApplicationName, configuration.Product, configuration.Timeout));
    }

    /// <summary>A pool for <paramref name="source" />, held to the smaller of <paramref name="configured" /> and its own <c>Maximum Pool Size</c> (issue #49).</summary>
    private static StudioConnectionPool CreatePool(NpgsqlDataSource source, int configured, string? applicationName, string product, TimeSpan timeout)
    {
        int effective = EffectiveMax(source.ConnectionString, configured);

        return new StudioConnectionPool(
            effective,
            ApplicationNameFor(applicationName, source.ConnectionString, product),
            timeout,
            configured,
            poolSize: effective < configured ? effective : null);
    }

    /// <summary>
    /// Issue #49: the cap for a data source - <paramref name="configured" /> (<c>MaxStudioConnections</c>), never above the data
    /// source's own <c>Maximum Pool Size</c> (Npgsql's 100 when it sets none), and never below one. A data source with <c>Pooling</c> off
    /// has no pool to be held to, and a connection string Npgsql cannot read is held to the option alone. A multi-host connection
    /// string's size applies to each host, so the same rule holds.
    /// </summary>
    /// <param name="connectionString">The data source's connection string.</param>
    /// <param name="configured">The option's value.</param>
    internal static int EffectiveMax(string? connectionString, int configured)
    {
        int max = Math.Max(configured, 1);

        try
        {
            NpgsqlConnectionStringBuilder builder = new(connectionString ?? string.Empty);

            return builder.Pooling ? Math.Max(Math.Min(max, builder.MaxPoolSize), 1) : max;
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException or FormatException)
        {
            // A connection string Npgsql cannot read (a keyword it does not know, a value it cannot parse) has no pool size to be held to.
            return max;
        }
    }

    /// <summary>The pool registered for <paramref name="dataSource" />, created by <paramref name="create" /> the first time.</summary>
    private static StudioConnectionPool GetOrCreate(NpgsqlDataSource dataSource, Func<NpgsqlDataSource, StudioConnectionPool> create)
    {
        lock (Gate)
        {
            if (!Pools.TryGetValue(dataSource, out StudioConnectionPool? pool))
            {
                pool = create(dataSource);
                Pools.Add(dataSource, pool);
            }

            return pool;
        }
    }

    /// <summary>
    /// The name a studio session carries: the host's own (validated at startup), none for <c>""</c>, or - by default - the
    /// application's own <c>Application Name</c> with the product after it in parentheses (<c>orders-api (Marten Studio)</c>), or the
    /// product alone when the application names none. Printable ASCII only, and at most <see cref="MaxApplicationNameBytes" /> bytes,
    /// cut from the application's part so the product is always said.
    /// </summary>
    internal static string? ApplicationNameFor(string? configured, string? connectionString, string product)
    {
        if (configured is not null)
        {
            return configured.Length == 0 ? null : configured;
        }

        string? application = null;

        try
        {
            application = new NpgsqlConnectionStringBuilder(connectionString ?? string.Empty).ApplicationName;
        }
        catch (ArgumentException)
        {
            // A connection string Npgsql cannot read names no application.
        }

        string suffix = Ascii(product);
        string own = Ascii(application ?? string.Empty).Trim();

        if (own.Length == 0)
        {
            return suffix.Length > MaxApplicationNameBytes ? suffix[..MaxApplicationNameBytes] : suffix;
        }

        string tail = " (" + suffix + ")";
        int room = MaxApplicationNameBytes - tail.Length;

        return room <= 0 ? suffix[..Math.Min(suffix.Length, MaxApplicationNameBytes)] : (own.Length > room ? own[..room] : own) + tail;
    }

    /// <summary>Whether <paramref name="name" /> is one PostgreSQL keeps as written: printable ASCII, at most <see cref="MaxApplicationNameBytes" /> bytes.</summary>
    public static bool IsValidApplicationName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length <= MaxApplicationNameBytes && name.All(static c => c is >= ' ' and <= '~');
    }

    private static string Ascii(string text)
    {
        StringBuilder ascii = new(text.Length);

        foreach (char c in text)
        {
            ascii.Append(c is >= ' ' and <= '~' ? c : '?');
        }

        return ascii.ToString();
    }

    /// <summary>For tests: forgets the configuration, so a data source seen next is not capped or named.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            current = null;
        }
    }
}

/// <summary>One pool's share for the studio: its slots, and the name its sessions carry.</summary>
/// <remarks>
/// It lives as long as its data source (a weak table keyed on it) and is never disposed: a <see cref="SemaphoreSlim" /> whose
/// wait handle is never asked for holds nothing a finalizer must release.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Lives as long as its data source; the semaphore's wait handle is never created.")]
internal sealed class StudioConnectionPool
{
    private readonly SemaphoreSlim slots;
    private int waiting;

    /// <param name="max">The most connections the studio holds of this pool at a time: the effective cap.</param>
    /// <param name="applicationName">The name its sessions carry, or <see langword="null" /> for none.</param>
    /// <param name="timeout">How long an open waits for a slot.</param>
    /// <param name="configured">The option's value (<c>MaxStudioConnections</c>) when it differs from <paramref name="max" />; <paramref name="max" /> otherwise.</param>
    /// <param name="poolSize">The data source's <c>Maximum Pool Size</c> when it was what held the cap below <paramref name="configured" />.</param>
    public StudioConnectionPool(int max, string? applicationName, TimeSpan timeout, int? configured = null, int? poolSize = null)
    {
        Max = max;
        ApplicationName = applicationName;
        Timeout = timeout;
        Configured = configured ?? max;
        PoolSize = poolSize;
        slots = new SemaphoreSlim(max, max);
    }

    /// <summary>The most connections the studio holds of this pool at a time: <c>MaxStudioConnections</c>, never above the data source's <c>Maximum Pool Size</c> (issue #49).</summary>
    public int Max { get; }

    /// <summary>What <c>MaxStudioConnections</c> was set to.</summary>
    public int Configured { get; }

    /// <summary>The data source's <c>Maximum Pool Size</c>, when it held <see cref="Max" /> below <see cref="Configured" />; <see langword="null" /> when the option was the bound.</summary>
    public int? PoolSize { get; }

    /// <summary>The <c>application_name</c> the studio's transactions on this pool set, or <see langword="null" /> for none.</summary>
    public string? ApplicationName { get; }

    /// <summary>How long an open waits for a slot.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>How many connections of this pool the studio holds now.</summary>
    public int InUse => Max - slots.CurrentCount;

    /// <summary>How many of the studio's opens are waiting for a slot now.</summary>
    public int Waiting => Volatile.Read(ref waiting);

    /// <summary>Takes a slot, waiting at most <see cref="Timeout" />.</summary>
    internal async Task<StudioConnectionLease> AcquireAsync(StudioConnectionLease lease, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref waiting);

        try
        {
            if (!await slots.WaitAsync(Timeout, cancellationToken).ConfigureAwait(false))
            {
                lease.Abandon();
                throw new StudioConnectionsBusyException(Max, Timeout, PoolSize);
            }
        }
        catch
        {
            lease.Abandon();
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref waiting);
        }

        lease.Activate();
        return lease;
    }

    internal void Release() => slots.Release();
}

/// <summary>One slot, held from a studio connection's open to its close.</summary>
internal sealed class StudioConnectionLease(StudioConnectionPool pool, bool reentrant)
{
    private int state; // 0 pending, 1 active, 2 done

    /// <summary>The pool it is a slot of.</summary>
    public StudioConnectionPool Pool { get; } = pool;

    /// <summary>Whether it rides on a slot its flow already holds, and so took none of its own.</summary>
    public bool Reentrant { get; } = reentrant;

    /// <summary>Whether it holds its slot now.</summary>
    public bool IsActive => Volatile.Read(ref state) == 1;

    internal void Activate() => Interlocked.CompareExchange(ref state, 1, 0);

    internal void Abandon() => Interlocked.Exchange(ref state, 2);

    /// <summary>Gives the slot back, once.</summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref state, 2) == 1 && !Reentrant)
        {
            Pool.Release();
        }
    }
}

/// <summary>
/// An open of a studio connection that found no free slot within the studio's <c>QueryTimeout</c>. An <see cref="NpgsqlException" />
/// on purpose, with the timeout inside it: every studio service already answers "the connection could not be had" - audited where
/// the operation is, said on the page, logged - so this reaches all of them by the paths a database that is down does.
/// </summary>
internal sealed class StudioConnectionsBusyException : NpgsqlException
{
    public StudioConnectionsBusyException(int max, TimeSpan timeout, int? poolSize = null)
        : base(
            "The studio's connections to this database are all in use (" + max.ToString(CultureInfo.InvariantCulture) + " at most, "
            + (poolSize is { } size
                ? "MaxStudioConnections, held to the data source's Maximum Pool Size of " + size.ToString(CultureInfo.InvariantCulture)
                : "MaxStudioConnections")
            + "), and none came free within " + timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)
            + " s. Try again in a moment.",
            new TimeoutException())
    {
    }
}

/// <summary>How a studio connection is opened: through its pool's slot, re-entrant within one asynchronous flow.</summary>
internal static class StudioConnections
{
    private static readonly AsyncLocal<ImmutableDictionary<StudioConnectionPool, StudioConnectionLease>?> Held = new();

    /// <summary>
    /// Opens <paramref name="connection" /> - a connection the studio created (<see cref="StudioConnectionMeter.Attach" />) - after
    /// taking a slot of its pool, unless the calling flow already holds one; the slot goes back when the connection closes. A
    /// connection the studio does not meter, or whose pool no studio configured, is simply opened.
    /// </summary>
    /// <remarks>
    /// Not <see langword="async" /> on purpose: the slot is marked in the caller's own execution context before anything awaits, so
    /// what the caller then awaits - a nested read, a fan-out - sees it and takes no second slot.
    /// </remarks>
    public static Task OpenStudioAsync(this NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (StudioConnectionMeter.PoolOf(connection) is not { } pool)
        {
            return connection.OpenAsync(cancellationToken);
        }

        ImmutableDictionary<StudioConnectionPool, StudioConnectionLease> held = Held.Value ?? ImmutableDictionary<StudioConnectionPool, StudioConnectionLease>.Empty;
        bool reentrant = held.TryGetValue(pool, out StudioConnectionLease? outer) && outer.IsActive;
        StudioConnectionLease lease = new(pool, reentrant);

        if (!reentrant)
        {
            Held.Value = held.SetItem(pool, lease);
        }

        return OpenCoreAsync(connection, lease, cancellationToken);
    }

    private static async Task OpenCoreAsync(NpgsqlConnection connection, StudioConnectionLease lease, CancellationToken cancellationToken)
    {
        if (lease.Reentrant)
        {
            lease.Activate();
        }
        else
        {
            await lease.Pool.AcquireAsync(lease, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            StudioConnectionMeter.Hold(connection, lease);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lease.Release();
            throw;
        }
    }
}
