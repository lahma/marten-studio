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
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithReuse(!DockerAvailability.OnContinuousIntegration)
        .Build();

    private bool started;

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
}
