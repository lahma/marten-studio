using System.Globalization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Live;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Aurora;

/// <summary>What a host's <see cref="IPostgresCapacitySource" /> said for one database and instance, for one interval.</summary>
/// <param name="Capacity">The reading, or <see langword="null" />: no source, the source does not know, it timed out or it threw.</param>
/// <param name="AskedAt">When the studio asked, by its own clock.</param>
/// <param name="Unknown">
/// Why <paramref name="Capacity" /> is <see langword="null" /> when a source is registered - "it took longer than 5 s", "it threw
/// InvalidOperationException" - or <see langword="null" /> when it answered, or when no source is registered.
/// </param>
/// <param name="SourceRegistered">Whether the host registered a capacity source at all.</param>
internal sealed record CapacityReading(PostgresComputeCapacity? Capacity, DateTimeOffset AskedAt, string? Unknown, bool SourceRegistered)
{
    /// <summary>The memo key of a sweep's one ask.</summary>
    public const string Key = "platform.capacity";

    /// <summary>Whether the reading is Aurora Serverless v2's: its unit is <c>ACU</c>, which C03 and A6 key on.</summary>
    public bool IsAcu => Capacity is { Unit: "ACU" };

    /// <summary>"4.5 ACU (min 0.5, max 16), measured 12:01 UTC", or <see langword="null" /> without a reading.</summary>
    public string? Text => Capacity is { } capacity ? Describe(capacity) : null;

    /// <summary>"4.5 ACU (min 0.5, max 16), measured 12:01 UTC".</summary>
    public static string Describe(PostgresComputeCapacity capacity)
    {
        ArgumentNullException.ThrowIfNull(capacity);

        return Amount(capacity.Current) + " " + capacity.Unit + " (min " + Amount(capacity.Minimum) + ", max " + Amount(capacity.Maximum)
            + "), measured " + capacity.MeasuredAt.UtcDateTime.ToString("HH:mm 'UTC'", CultureInfo.InvariantCulture);
    }

    /// <summary>"16", "4.5": a capacity as its source spells it, to a hundredth.</summary>
    public static string Amount(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The sweep's one ask, whichever check asks first (<see cref="HealthCheckContext.Shared{T}" />) - C03 and C11.</summary>
    /// <remarks>
    /// Asks the process's <see cref="CapacityReader" />, which a sweep's scope resolves from the root container; a container
    /// without one (a test's) reads as no source registered.
    /// </remarks>
    public static Task<CapacityReading> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Shared(Key, async token =>
        {
            PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);
            CapacityReader? reader = context.Services.GetService<CapacityReader>();

            return reader is null
                ? new CapacityReading(null, context.Now, null, false)
                : await reader.GetAsync(context.Source, server, token).ConfigureAwait(false);
        });
    }
}

/// <summary>
/// Asks the host's <see cref="IPostgresCapacitySource" /> for the compute capacity behind a database (issue #8 A8) - for any
/// platform: a source decides what it knows, and answers null for the rest.
/// </summary>
/// <remarks>
/// <para>
/// <b>As <see cref="IPostgresCapacitySource" /> promises.</b> The source is found in the container (<c>GetService</c>), lazily
/// and once. It is asked at most once per <c>HealthInterval</c> per database and instance, one call at a time - every sweep,
/// page and visitor within an interval shares the answer (<see cref="StudioSnapshotCache" />) - and given five seconds. An
/// answer of null, a timeout or an exception is "capacity unknown" for that interval, a value and never a failure of the
/// sweep. An exception is event 9500, at Warning once in ten minutes per database and kind of failure and at Debug in between
/// (<see cref="StudioLogThrottle" />): a source that is down is said, and a page that polls it does not flood the log.
/// </para>
/// <para>
/// <b>No cloud dependency.</b> The studio references no AWS package; host A's source reads CloudWatch's
/// <c>ServerlessDatabaseCapacity</c> with its own SDK and caches it (<c>docs/aurora.md</c>).
/// </para>
/// </remarks>
internal sealed class CapacityReader
{
    /// <summary>How long a source is given: five seconds, the interface's own promise.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider services;
    private readonly IStudioSettings settings;
    private readonly StudioLogThrottle throttle;
    private readonly ILogger<CapacityReader> logger;
    private readonly TimeProvider timeProvider;
    private readonly StudioSnapshotCache cache;
    private readonly Lazy<IPostgresCapacitySource?> source;
    private int asked;

    public CapacityReader(
        IServiceProvider services,
        IStudioSettings settings,
        StudioLogThrottle throttle,
        ILogger<CapacityReader> logger,
        TimeProvider? timeProvider = null)
    {
        this.services = services;
        this.settings = settings;
        this.throttle = throttle;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        cache = new StudioSnapshotCache(this.timeProvider, settings.EffectiveHealthInterval);
        source = new Lazy<IPostgresCapacitySource?>(Find, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>How long a source is given, when a test needs it shorter than <see cref="DefaultTimeout" />.</summary>
    internal TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>How many times a source was actually asked, for a test.</summary>
    internal int Asked => Volatile.Read(ref asked);

    /// <summary>Whether the host registered a source.</summary>
    public bool SourceRegistered => source.Value is not null;

    /// <summary>
    /// The capacity behind <paramref name="resolved" />'s database as <paramref name="server" /> answered: a reading from this
    /// interval, or the one ask shared with everybody who wants it while it runs. Never throws but for this caller's
    /// cancellation.
    /// </summary>
    /// <param name="resolved">The database, resolved with no tenant.</param>
    /// <param name="server">The server facts the instance comes from.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared ask.</param>
    public Task<CapacityReading> GetAsync(ResolvedSource resolved, PostgresServerFacts server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(server);

        if (source.Value is null)
        {
            return Task.FromResult(new CapacityReading(null, timeProvider.GetUtcNow(), null, false));
        }

        PostgresDatabaseRef database = new(resolved.Source.Key, resolved.Target.Identity);
        string key = database.Source + "|" + database.Database + "|" + (server.Instance ?? string.Empty);

        return cache.GetAsync(key, _ => AskAsync(database, server.ToServerInfo()), cancellationToken);
    }

    private async Task<CapacityReading> AskAsync(PostgresDatabaseRef database, PostgresServerInfo server)
    {
        IPostgresCapacitySource capacitySource = source.Value!;
        DateTimeOffset askedAt = timeProvider.GetUtcNow();
        Interlocked.Increment(ref asked);

        using CancellationTokenSource timeout = new(Timeout);

        try
        {
            // WaitAsync on the same token as well: a source that ignores its token still costs this read no more than the
            // timeout. Only the reader's own token firing is "took longer"; a source's own TimeoutException, or a cancellation
            // of its own, is a failure like any other (event 9500, W5-AUR-fix F4).
            PostgresComputeCapacity? capacity = await capacitySource
                .GetCapacityAsync(database, server, timeout.Token)
                .AsTask()
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);

            return new CapacityReading(capacity, askedAt, capacity is null ? "the capacity source does not know this database" : null, true);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("{Product}'s capacity source took longer than {Timeout} for {DatabaseId}", settings.Text.ProductName, Timeout, database.Database);
            }

            return new CapacityReading(null, askedAt, "the capacity source took longer than " + Seconds(Timeout), true);
        }
        catch (Exception exception)
        {
            LogLevel level = throttle.WarningOrDebug("Aurora.Capacity", database.Source, database.Database, StudioLogThrottle.KindOf(exception));

            if (settings.Text.Product == StudioProduct.PostgresStudio)
            {
                logger.PostgresStudioCapacitySourceFailed(level, exception, database.Source, database.Database);
            }
            else
            {
                logger.CapacitySourceFailed(level, exception, database.Source, database.Database);
            }

            return new CapacityReading(null, askedAt, "the capacity source failed (" + exception.GetType().Name + ")", true);
        }
    }

    private IPostgresCapacitySource? Find()
    {
        try
        {
            return services.GetService<IPostgresCapacitySource>();
        }
        catch (Exception exception)
        {
            // A source registered scoped is not resolvable from the root container, and one whose construction throws never
            // will be: said once - the Lazy keeps this answer for the process - and read as no source, so nothing that asks for
            // the capacity fails for it (W5-AUR-fix F4).
            LogLevel level = throttle.WarningOrDebug("Aurora.Capacity", null, null, StudioLogThrottle.KindOf(exception));

            if (settings.Text.Product == StudioProduct.PostgresStudio)
            {
                logger.PostgresStudioCapacitySourceFailed(level, exception, "(registration)", "(registration)");
            }
            else
            {
                logger.CapacitySourceFailed(level, exception, "(registration)", "(registration)");
            }

            return null;
        }
    }

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds >= 1 ? span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s" : span.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";
}
