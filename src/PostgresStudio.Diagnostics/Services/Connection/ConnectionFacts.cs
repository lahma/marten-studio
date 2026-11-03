using System.Globalization;

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
/// <param name="Pools">How many pools share the data source's identity, this one included.</param>
/// <param name="SumMaxPoolSize">The sum of their <c>Maximum Pool Size</c>, or <see langword="null" /> when one did not report it.</param>
/// <param name="UnparsedNames">How many pool names are not a connection string (a name the host gave its data source) and were left out.</param>
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

    /// <summary>Counts the pools of <paramref name="readings" /> that have <paramref name="identity" />.</summary>
    /// <param name="identity">The data source's <see cref="IdentityOf" />.</param>
    /// <param name="readings">Every pool the meter reports.</param>
    public static ConnectionPoolSiblings Count(string identity, IEnumerable<NpgsqlPoolReading> readings)
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
/// with <see cref="ObservedNotRead" /> saying why the rest is missing.
/// </para>
/// </remarks>
internal sealed record ConnectionFacts
{
    /// <summary>The redacted settings, with the hints evaluated against what the server saw.</summary>
    public required ConnectionSettings Settings { get; init; }

    /// <summary>What the server saw, or <see langword="null" /> when it was not read (see <see cref="ObservedNotRead" />).</summary>
    public ConnectionObserved? Observed { get; init; }

    /// <summary>Why <see cref="Observed" /> is <see langword="null" />, or <see langword="null" /> when it was read.</summary>
    public string? ObservedNotRead { get; init; }

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

        (ConnectionObserved? observed, string? notRead) = await ReadObservedAsync(context, optionNames, cancellationToken).ConfigureAwait(false);
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
            Observed = observed,
            ObservedNotRead = notRead,
            Platform = platform,
            Sessions = sessions,
            Siblings = identity is null ? null : ObserveSiblings(context, identity),
            SiblingsNotObserved = identity is null ? NoIdentityReason : null,
        };
    }

    private static async Task<(ConnectionObserved? Observed, string? NotRead)> ReadObservedAsync(
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
                return (null, "the server answered " + rows.Count.ToString(CultureInfo.InvariantCulture) + " rows to the connection read");
            }

            // The parameters the server reported at start-up are on the open connection already: no statement.
            IReadOnlyDictionary<string, string> reported = await context.Sql
                .ReportedParametersAsync(cancellationToken)
                .ConfigureAwait(false);

            return (rows[0] with { ReportedParameters = reported }, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (exception is HealthSweepRuleException)
            {
                throw;
            }

            PostgresException? postgres = exception as PostgresException ?? exception.InnerException as PostgresException;

            return (null, "the connection read failed (" + (postgres is null ? exception.GetType().Name : "SQLSTATE " + postgres.SqlState) + ")");
        }
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

    private static ConnectionPoolSiblings? ObserveSiblings(HealthCheckContext context, string identity)
    {
        if (context.Services.GetService<SessionPoolObserver>() is not { } observer)
        {
            return null;
        }

        try
        {
            using SessionPoolLease lease = observer.Lease();
            return ConnectionPoolSiblings.Count(identity, lease.Read());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
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
        new HashSet<string>(reader.IsDBNull(21) ? [] : reader.GetFieldValue<string[]>(21), StringComparer.OrdinalIgnoreCase));

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? Long(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
