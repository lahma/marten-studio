using MartenStudio.Internal.Sql;

using Npgsql;

using Testcontainers.PostgreSql;

namespace MartenStudio.Integration.Tests;

/// <summary>
/// The one Postgres the integration suite runs against, started once for the whole assembly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reuse is on locally and off on CI.</b> Locally the container outlives the run, so the second
/// <c>dotnet test</c> of an afternoon starts in milliseconds instead of seconds; this is the difference
/// between a suite that gets run while writing code and one that gets run before a commit. On CI the
/// container is disposed with the run, because a CI agent is thrown away anyway and a leaked container is
/// somebody's leaked quota. Reuse also means the server can be carrying schemas from a previous run, which
/// is why <see cref="CreateSchemaAsync"/> drops before it creates.
/// </para>
/// <para>
/// <b><see cref="ReuseEnvironmentVariable"/> turns reuse off for one run</b>, and it is this project's own
/// variable rather than a Testcontainers one. <c>TESTCONTAINERS_REUSE_ENABLE</c> is <em>not</em> read by
/// Testcontainers for .NET 4.x: the string does not occur anywhere in <c>Testcontainers.dll</c>, and reuse
/// is decided solely by the <c>WithReuse(bool)</c> below. Setting it therefore does nothing at all, and a
/// run that believed otherwise was still sharing one reused container with every other run on the
/// machine. That is a real collision on a box where several agent sessions test at once: the schema names
/// here are derived from test class names, so two runs land on the same schema and drop each other's
/// tables, and Testcontainers can restart the shared container under a run that is mid-query. Set
/// <c>MARTENSTUDIO_PG_REUSE=false</c> (or <c>0</c>) to get a container of this run's own; anything else,
/// including leaving it unset, keeps the local-reuse default.
/// </para>
/// <para>
/// <b>Isolation is per schema, not per server.</b> Each test class gets its own schema and never sees
/// another's tables, which is all the isolation these tests need and costs one round trip instead of one
/// container start.
/// </para>
/// <para>
/// When Docker is not there, this fixture starts nothing and every Docker-backed test skips with a reason
/// (see <see cref="DockerAvailability"/>) — except on CI, where it throws first.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>The variable that turns container reuse off for one run.</summary>
    public const string ReuseEnvironmentVariable = "MARTENSTUDIO_PG_REUSE";

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithReuse(ShouldReuse)
        .Build();

    private bool started;

    /// <summary>
    /// Whether this run shares a container with the ones around it: never on CI, and not when
    /// <see cref="ReuseEnvironmentVariable"/> is <c>false</c> or <c>0</c>.
    /// </summary>
    public static bool ShouldReuse =>
        !DockerAvailability.OnContinuousIntegration && !IsReuseDisabledByEnvironment();

    /// <summary>The connection string for the container, once it is running.</summary>
    public string ConnectionString => started
        ? container.GetConnectionString()
        : throw new InvalidOperationException(
            "The Postgres container is not running. A test that needs it must be a [PostgresFact] so that " +
            "it skips instead of failing when Docker is absent.");

    /// <summary>Starts the container, unless there is no Docker to start it on.</summary>
    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await container.StartAsync();
        started = true;
    }

    /// <summary>Stops the container, or leaves it running when reuse is on.</summary>
    public async ValueTask DisposeAsync() => await container.DisposeAsync();

    /// <summary>An open connection to the container's database.</summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// An empty schema of this name, dropping whatever a previous run left behind. The name is quoted
    /// through the studio's own <see cref="SqlIdentifier"/>, which is the only way identifiers reach SQL
    /// anywhere in this repository.
    /// </summary>
    public async Task<string> CreateSchemaAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        var quoted = SqlIdentifier.Quote(name);

        await using var command = new NpgsqlCommand(
            $"drop schema if exists {quoted} cascade; create schema {quoted};", connection);

        await command.ExecuteNonQueryAsync(cancellationToken);

        return name;
    }

    private static bool IsReuseDisabledByEnvironment() =>
        IsReuseDisabled(Environment.GetEnvironmentVariable(ReuseEnvironmentVariable));

    /// <summary>
    /// Whether a <see cref="ReuseEnvironmentVariable"/> value turns reuse off. Only <c>false</c> and
    /// <c>0</c> do, so an unset variable — or a misspelt value — leaves the default alone rather than
    /// silently changing it.
    /// </summary>
    /// <remarks>
    /// Split out from the environment read so that it can be tested without a test mutating process-wide
    /// state that every other test in the run shares.
    /// </remarks>
    internal static bool IsReuseDisabled(string? value) =>
        value?.Trim() is { } trimmed &&
        (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "0", StringComparison.Ordinal));
}
