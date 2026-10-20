using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Postgres;

/// <summary>What the Overview shows of a database's server: its version and platform, or why there is none.</summary>
/// <param name="Facts">The server's version and platform, when they were read.</param>
/// <param name="Failure">Why they could not be, in a sentence for the page.</param>
internal sealed record PostgresServerRead(PostgresServerFacts? Facts, string? Failure);

/// <summary>Reads the server behind a scope for PostgresStudio's Overview.</summary>
internal interface IPostgresServerReader
{
    /// <summary>
    /// The server's version and platform for <paramref name="scope" />'s database - asked of the scope policy alone, as
    /// S01's facts are (D29's <c>None</c> gate) - read once per database per circuit. Never throws but for cancellation
    /// and a refused scope.
    /// </summary>
    /// <param name="scope">The visitor's scope.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="StudioNotAuthorizedException">The visitor may not have the scope.</exception>
    Task<PostgresServerRead> ReadAsync(StudioScope scope, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IPostgresServerReader" />: S01's own statement (<see cref="HealthServerQueries.Platform" />, a catalog read
/// that locks nothing it lists) in a read-only transaction under the studio's timeout and role, mapped by the same
/// detector the health checks use.
/// </summary>
/// <remarks>
/// Scoped: what it remembers is this circuit's, the way MartenStudio's store overview remembers a store's version. A
/// failure is remembered too, so a page that polls does not ask an unreachable server again on every tick; event 9214,
/// throttled across circuits.
/// </remarks>
internal sealed class PostgresServerReader : IPostgresServerReader
{
    private readonly StudioSourceResolver resolver;
    private readonly IStudioSettings settings;
    private readonly ILogger<PostgresServerReader> logger;
    private readonly StudioLogThrottle? throttle;
    private readonly ConcurrentDictionary<string, PostgresServerRead> read = new(StringComparer.OrdinalIgnoreCase);

    public PostgresServerReader(
        StudioSourceResolver resolver,
        IStudioSettings settings,
        ILogger<PostgresServerReader> logger,
        StudioLogThrottle? throttle = null)
    {
        this.resolver = resolver;
        this.settings = settings;
        this.logger = logger;
        this.throttle = throttle;
    }

    /// <inheritdoc />
    public async Task<PostgresServerRead> ReadAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        ResolvedSource resolved = await resolver.ResolveAsync(scope with { TenantId = null }, null, cancellationToken).ConfigureAwait(false);
        string key = resolved.Source.Key + "|" + resolved.Target.Identity;

        if (read.TryGetValue(key, out PostgresServerRead? known))
        {
            return known;
        }

        PostgresServerRead answer;

        try
        {
            answer = new PostgresServerRead(await ReadAsync(resolved.Target, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogLevel level = StudioLogThrottle.LevelOrWarning(
                throttle, "Store.PostgresVersion", resolved.Source.Key, resolved.Target.Identity, StudioLogThrottle.KindOf(exception));
            logger.PostgresVersionUnreadable(level, exception, resolved.Source.Key);

            answer = new PostgresServerRead(null, "The server's version could not be read: " + exception.Message);
        }

        read[key] = answer;
        return answer;
    }

    private async Task<PostgresServerFacts> ReadAsync(IStudioDatabase database, CancellationToken cancellationToken)
    {
        ReadOnlySqlSession session = new(new ReadOnlySqlOptions
        {
            StatementTimeout = settings.QueryTimeout,
            Role = settings.SqlConsoleRole,
        });

        // A little past the server-side statement_timeout, which is what should fire: it produces 57014 with a message,
        // where a client-side timeout only breaks the connection.
        int commandTimeout = (int) Math.Ceiling(settings.QueryTimeout.TotalSeconds) + 5;

        await using NpgsqlConnection connection = database.CreateConnection(StudioConnectionUsage.Read);
        await PostgresFailure.OpenAsync(connection, cancellationToken).ConfigureAwait(false);

        ServerPlatformRow row = await session
            .InTransactionAsync(
                connection,
                (transaction, token) => OverviewServerQueries.ReadPlatformAsync(connection, transaction, commandTimeout, token),
                cancellationToken)
            .ConfigureAwait(false);

        PostgresPlatformMarkers markers = new(row.RdsSettings, row.AuroraVersionFunction, row.AzureExtensions, row.CloudSqlSettings, row.NeonSuperuserRole);
        PostgresPlatform platform = PostgresPlatform.Detect(markers, DataSourceEndpoints.Of(database)?.Hosts ?? []);
        return new PostgresServerFacts(DateTimeOffset.UtcNow, row.VersionNumber, row.Version, platform);
    }
}
