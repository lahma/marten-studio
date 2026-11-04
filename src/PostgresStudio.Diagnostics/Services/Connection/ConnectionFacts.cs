using System.Globalization;
using System.Runtime.ExceptionServices;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;

using Npgsql;

namespace PostgresStudio.Services.Connection;

/// <summary>
/// This database's session statistics, for C17: how long a session lasts on average when pooling is off.
/// </summary>
/// <param name="Sessions"><c>pg_stat_database.sessions</c>: sessions established since the statistics were reset (PostgreSQL 14 and later).</param>
/// <param name="SessionTimeMs"><c>session_time</c>: the time spent in those sessions, in milliseconds.</param>
/// <param name="StatsReset"><c>stats_reset</c>, or <see langword="null" /> when they never were.</param>
internal sealed record ConnectionSessionStatistics(long? Sessions, double? SessionTimeMs, DateTimeOffset? StatsReset)
{
    /// <summary>The average session length in milliseconds, or <see langword="null" /> when the server counts none or is older than 14.</summary>
    public double? AverageSessionMs => Sessions is > 0 && SessionTimeMs is { } time ? time / Sessions.Value : null;
}

/// <summary>
/// How many Npgsql pools in this process point at this database's host, port, database and login (C18), counted from the
/// pool names Npgsql reports to its meter. Counts and sums only: a pool name is a connection string and is parsed and dropped,
/// never kept or shown.
/// </summary>
/// <remarks>
/// A pool whose name is not a connection string - a data source the host gave a <c>Name</c> - is identified through the data
/// sources the studio resolves (<see cref="NamedPoolIdentities" />) and counted like an unnamed one; one the studio cannot
/// identify is counted in <paramref name="UnparsedNames" /> and judged by nobody.
/// </remarks>
/// <param name="Pools">How many pools share the data source's identity, this one included.</param>
/// <param name="SumMaxPoolSize">The sum of their <c>Maximum Pool Size</c>, or <see langword="null" /> when one did not report it.</param>
/// <param name="UnparsedNames">How many pools stay unidentified: their name is not a connection string, and no data source the studio resolves has it (a data source named with <c>NpgsqlDataSourceBuilder.Name</c> that the studio does not know). Not judged.</param>
internal sealed record ConnectionPoolSiblings(int Pools, int? SumMaxPoolSize, int UnparsedNames)
{
    /// <summary>
    /// The identity two pools must share to be one database's pools: the sorted, lower-cased <c>host:port</c> list, the database
    /// and the login of a connection string, or <see langword="null" /> when <paramref name="connectionString" /> does not parse.
    /// </summary>
    /// <param name="connectionString">A data source's connection string or a pool's name.</param>
    public static string? IdentityOf(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            NpgsqlConnectionStringBuilder builder = new(connectionString);

            if (string.IsNullOrEmpty(builder.Host))
            {
                return null;
            }

            string endpoints = string.Join(',', DataSourceEndpoints.Parse(builder.Host, builder.Port)
                .Select(static x => x.Host.ToLowerInvariant() + ":" + x.Port.ToString(CultureInfo.InvariantCulture))
                .Order(StringComparer.Ordinal));

            return endpoints + "|" + (builder.Database ?? string.Empty) + "|" + (builder.Username ?? string.Empty);
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The pool names among <paramref name="readings" /> that are not a connection string - the pools a host named, which only a
    /// data source can identify.
    /// </summary>
    /// <param name="readings">Every pool the meter reports.</param>
    public static HashSet<string> NamedPools(IEnumerable<NpgsqlPoolReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        return [.. readings.Where(static x => IdentityOf(x.PoolName) is null).Select(static x => x.PoolName)];
    }

    /// <summary>Counts the pools of <paramref name="readings" /> that have <paramref name="identity" />.</summary>
    /// <param name="identity">The data source's <see cref="IdentityOf" />.</param>
    /// <param name="readings">Every pool the meter reports.</param>
    /// <param name="named">
    /// The identity of each named pool the studio could identify, by the pool's name (<see cref="NamedPoolIdentities" />), or
    /// <see langword="null" /> when none was asked for. A pool name in it counts like an unnamed pool with that identity.
    /// </param>
    public static ConnectionPoolSiblings Count(string identity, IEnumerable<NpgsqlPoolReading> readings, IReadOnlyDictionary<string, string>? named = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(identity);
        ArgumentNullException.ThrowIfNull(readings);

        int pools = 0;
        int unparsed = 0;
        int sum = 0;
        bool sumKnown = true;

        foreach (NpgsqlPoolReading reading in readings)
        {
            string? other = IdentityOf(reading.PoolName);

            if (other is null && named is not null && named.TryGetValue(reading.PoolName, out string? identified))
            {
                other = identified;
            }

            if (other is null)
            {
                unparsed++;
            }
            else if (string.Equals(other, identity, StringComparison.Ordinal))
            {
                // More than one data source reporting under one name is that many pools with one connection string.
                pools += reading.Reporters;

                if (reading.Max is { } max)
                {
                    sum += max * reading.Reporters;
                }
                else
                {
                    sumKnown = false;
                }
            }
        }

        return new ConnectionPoolSiblings(pools, pools > 0 && sumKnown ? sum : null, unparsed);
    }
}

/// <summary>
/// What the connection checks and the Connection page read about one database, once per sweep (NP-DESIGN §1, §4): the
/// redacted settings, what the server saw of the studio's connection, the platform, and the two facts only the process or
/// the statistics know.
/// </summary>
/// <remarks>
/// <para>
/// <b>About the database, never about a visitor</b>, like every health fact: no gate is applied here. Each check and the page
/// apply D29's <see cref="HealthGate.Database" /> to what they show (NP-DESIGN §7, §8).
/// </para>
/// <para>
/// <b>The connection string is read and dropped inside <see cref="ReadAsync" />.</b> Only
/// <see cref="ConnectionSettings" /> - redacted rows and non-secret values - and the observed values leave it.
/// </para>
/// <para>
/// <b>A failed read is a value.</b> The settings need no connection, so a server that cannot be reached still yields them,
/// with <see cref="ObservedNotRead" /> saying why the rest is missing. The page shows them; the checks do not judge them
/// when the server never answered (<see cref="ObservedFailure" />), because a sweep that reached nothing must not find a
/// database in good order.
/// </para>
/// </remarks>
internal sealed record ConnectionFacts
{
    /// <summary>The redacted settings, with the hints evaluated against what the server saw.</summary>
    public required ConnectionSettings Settings { get; init; }

    /// <summary>
    /// When the facts were read (the sweep's or the page's own read), by the clock the context was given - not when somebody looked
    /// at them, which can be up to a <c>HealthInterval</c> later.
    /// </summary>
    public DateTimeOffset ReadAt { get; init; }

    /// <summary>What the server saw, or <see langword="null" /> when it was not read (see <see cref="ObservedNotRead" />).</summary>
    public ConnectionObserved? Observed { get; init; }

    /// <summary>Why <see cref="Observed" /> is <see langword="null" />, or <see langword="null" /> when it was read.</summary>
    public string? ObservedNotRead { get; init; }

    /// <summary>
    /// The failure behind <see cref="ObservedNotRead" /> when the server never answered the read - a connection that could not be
    /// opened (a login refused included: Npgsql raises those as a <see cref="PostgresException" />, which
    /// <c>PostgresFailure.OpenAsync</c> marks as a connection failure), a data source that was disposed, a timeout, the studio's own
    /// share of the pool in use - or <see langword="null" /> when it was read or Postgres answered a statement with an error of its
    /// own (a missing grant). Captured once; the connection checks throw it again (<c>ConnectionCheckSupport.ReadAsync</c>), so the
    /// sweep classifies it as it does every other check's and one trace is not extended check after check. It is never shown or
    /// logged by the facts themselves.
    /// </summary>
    public ExceptionDispatchInfo? ObservedFailure { get; init; }

    /// <summary>Who runs the server; <see cref="PostgresPlatform.SelfHosted" /> when it could not be told.</summary>
    public PostgresPlatform Platform { get; init; } = PostgresPlatform.SelfHosted;

    /// <summary>The database's session statistics, read only when pooling is off (C17).</summary>
    public ConnectionSessionStatistics? Sessions { get; init; }

    /// <summary>The pools in this process that share this data source's identity (C18), or <see langword="null" /> when the process's pools cannot be observed.</summary>
    public ConnectionPoolSiblings? Siblings { get; init; }

    /// <summary>
    /// Why <see cref="Siblings" /> is <see langword="null" /> when the cause is the data source and not the meter - its own host, database
    /// and login could not be read from its connection string, so there is nothing to compare pool names with - or <see langword="null" />
    /// when the cause is the meter or there is none.
    /// </summary>
    public string? SiblingsNotObserved { get; init; }

    internal const string NoIdentityReason =
        "The data source's own host, database and login could not be read from its connection string (it names no Host, or does not parse), "
        + "so there is no identity to compare the process's Npgsql pool names with and its pools are not counted.";

    /// <summary>Whether the data source names a host that is neither a socket nor loopback.</summary>
    public bool HasRemoteHost => Settings.RemoteEndpoints.Count > 0;

    /// <summary>
    /// Google's Cloud SQL Auth Proxy: a Cloud SQL server reached through a socket or a loopback host. The proxy wraps its leg to
    /// the instance in TLS itself, so the server sees no SSL from the application, and S05 and S11 stand down
    /// (<i>Cloud SQL Auth Proxy</i>, Google Cloud docs: the proxy "wraps the connection in a SSL/TLS layer that's authorized for a
    /// Cloud SQL instance"; note that the leg from the application to the proxy is not encrypted).
    /// </summary>
    public bool IsCloudSqlProxyCase =>
        Platform.Kind == PostgresPlatformKind.CloudSql && Settings.Endpoints.Count > 0 && !HasRemoteHost;

    /// <summary>The sweep's one read of it, shared by every check and the page that ask first (<see cref="HealthCheckContext.Shared{T}" />).</summary>
    /// <param name="context">The sweep's context.</param>
    public static Task<ConnectionFacts> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(ConnectionObservedQueries.FactsKey, token => ReadAsync(context, token));
    }

    /// <summary>Reads the database's settings and what the server saw.</summary>
    /// <param name="context">The sweep's context.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<ConnectionFacts> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IProcessEnvironment environment = context.Services.GetService<IProcessEnvironment>() ?? ProcessEnvironment.Instance;

        // The connection string lives in these locals and nowhere else.
        string? connectionString = context.Target.DataSource?.ConnectionString;
        IReadOnlyList<string> optionNames = NpgsqlSettingsReader.OptionNames(connectionString, environment);
        string? identity = ConnectionPoolSiblings.IdentityOf(connectionString);

        (ConnectionObserved? observed, string? notRead, ExceptionDispatchInfo? failure) = await ReadObservedAsync(context, optionNames, cancellationToken).ConfigureAwait(false);
        PostgresPlatform platform = await ReadPlatformAsync(context).ConfigureAwait(false);

        ConnectionReadContext read = new()
        {
            Environment = environment,
            ServerSettingNames = observed?.KnownOptionNames,
            Platform = platform.Kind,
            Observed = observed,
        };

        ConnectionSettings settings = NpgsqlSettingsReader.Read(connectionString, read);
        connectionString = null;

        ConnectionSessionStatistics? sessions = settings.Readable && !settings.Values.Pooling
            ? await ReadSessionsAsync(context, cancellationToken).ConfigureAwait(false)
            : null;

        return new ConnectionFacts
        {
            Settings = settings,
            ReadAt = context.Now,
            Observed = observed,
            ObservedNotRead = notRead,
            ObservedFailure = failure,
            Platform = platform,
            Sessions = sessions,
            Siblings = identity is null ? null : await ObserveSiblingsAsync(context, identity, cancellationToken).ConfigureAwait(false),
            SiblingsNotObserved = identity is null ? NoIdentityReason : null,
        };
    }

    private static async Task<(ConnectionObserved? Observed, string? NotRead, ExceptionDispatchInfo? Failure)> ReadObservedAsync(
        HealthCheckContext context,
        IReadOnlyList<string> optionNames,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ConnectionObserved> rows = await context.Sql
                .QueryAsync(ConnectionObservedQueries.Observed(optionNames), MapObserved, cancellationToken)
                .ConfigureAwait(false);

            if (rows.Count != 1)
            {
                return (null, "the server answered " + rows.Count.ToString(CultureInfo.InvariantCulture) + " rows to the connection read", null);
            }

            // The parameters the server reported at start-up are on the open connection already: no statement.
            IReadOnlyDictionary<string, string> reported = await context.Sql
                .ReportedParametersAsync(cancellationToken)
                .ConfigureAwait(false);

            return (rows[0] with { ReportedParameters = reported }, null, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (exception is HealthSweepRuleException)
            {
                throw;
            }

            // Postgres answered a statement with an error of its own: the database was reached, and the settings stand on their own.
            // Anything else - no connection, a disposed data source, a timeout, a login the server refused (a PostgresException too,
            // raised while the connection opened and marked so) - is the server not answering, which the checks must not read as a
            // database in good order.
            return (
                null,
                "the connection read failed (" + (PostgresOf(exception) is { } postgres ? "SQLSTATE " + postgres.SqlState : exception.GetType().Name) + ")",
                ServerDidNotAnswer(exception) ? ExceptionDispatchInfo.Capture(exception) : null);
        }
    }

    private static PostgresException? PostgresOf(Exception exception) => exception as PostgresException ?? exception.InnerException as PostgresException;

    /// <summary>
    /// Whether the server never answered the read: anything but a Postgres error to a statement. A login the server refused
    /// (28P01, 28000, 3D000, 53300, 57P03) is a <see cref="PostgresException" /> too - Npgsql raises it from the connection's
    /// open - and is marked as a connection failure by <c>PostgresFailure.OpenAsync</c>, so it is told from a statement's error
    /// (42501, a missing grant) by that mark.
    /// </summary>
    /// <param name="exception">What the connection read threw.</param>
    internal static bool ServerDidNotAnswer(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return PostgresOf(exception) is null || PostgresFailure.IsConnectionFailure(exception);
    }

    private static async Task<PostgresPlatform> ReadPlatformAsync(HealthCheckContext context)
    {
        try
        {
            return (await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false)).Platform;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PostgresPlatform.SelfHosted;
        }
    }

    private static async Task<ConnectionSessionStatistics?> ReadSessionsAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ConnectionSessionStatistics> rows = await context.Sql
                .QueryAsync(
                    ConnectionObservedQueries.Sessions(),
                    static reader => new ConnectionSessionStatistics(
                        reader.IsDBNull(0) ? null : reader.GetInt64(0),
                        reader.IsDBNull(1) ? null : reader.GetDouble(1),
                        reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)),
                    cancellationToken)
                .ConfigureAwait(false);

            return rows.Count == 1 ? rows[0] : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException && exception is not HealthSweepRuleException)
        {
            return null;
        }
    }

    private static async Task<ConnectionPoolSiblings?> ObserveSiblingsAsync(HealthCheckContext context, string identity, CancellationToken cancellationToken)
    {
        if (context.Services.GetService<SessionPoolObserver>() is not { } observer)
        {
            return null;
        }

        IReadOnlyList<NpgsqlPoolReading> readings;

        try
        {
            using SessionPoolLease lease = observer.Lease();
            readings = lease.Read();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        // Only the pools whose name is not a connection string need a data source to be told apart (issue #56); the lease is
        // given back before the studio's sources are asked, which may wait on a host.
        HashSet<string> named = ConnectionPoolSiblings.NamedPools(readings);
        IReadOnlyDictionary<string, string>? identified = null;

        if (named.Count > 0)
        {
            identified = await NamedPoolIdentities.ReadAsync(context, named, cancellationToken).ConfigureAwait(false);
        }

        return ConnectionPoolSiblings.Count(identity, readings, identified);
    }

    private static ConnectionObserved MapObserved(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        Text(reader, 3),
        reader.IsDBNull(4) ? null : reader.GetInt32(4),
        reader.GetBoolean(5),
        Text(reader, 6),
        Text(reader, 7),
        Text(reader, 8),
        Text(reader, 9),
        Long(reader, 10),
        Long(reader, 11),
        Long(reader, 12),
        Long(reader, 13),
        Text(reader, 14),
        reader.IsDBNull(15) ? null : reader.GetBoolean(15),
        Text(reader, 16),
        Text(reader, 17),
        reader.IsDBNull(18) ? null : reader.GetInt32(18),
        reader.IsDBNull(19) ? null : reader.GetBoolean(19),
        reader.IsDBNull(20) ? null : reader.GetBoolean(20),
        new HashSet<string>(reader.IsDBNull(21) ? [] : reader.GetFieldValue<string[]>(21), StringComparer.OrdinalIgnoreCase))
    {
        ClientAddress = Text(reader, 22),
    };

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? Long(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
