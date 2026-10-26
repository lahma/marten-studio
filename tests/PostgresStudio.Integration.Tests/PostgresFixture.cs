using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

using Npgsql;

using PostgresStudio.Internal.Sql;

using Testcontainers.PostgreSql;

namespace PostgresStudio.Integration.Tests;

/// <summary>
/// The one Postgres this suite runs against, started once for the whole assembly - a copy of
/// <c>MartenStudio.Integration.Tests</c>' fixture with a container reuse identity of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own reuse identity</b> (design §7.3). Reuse is on locally, and Testcontainers decides which running container a
/// reusing run attaches to by a hash of the container's configuration - image, environment, ports, labels. Two suites
/// built from the same <c>postgres:17-alpine</c> defaults hash alike and attach to one container, and schema names derive
/// from test class names, so they would drop each other's tables. <see cref="ReuseLabel" /> puts this suite's name into
/// the hash; <c>PostgresFixtureTests</c> proves the hash differs from the sample's, and the Marten suite's fixture carries
/// no such label.
/// </para>
/// <para>
/// <b><see cref="ReuseEnvironmentVariable"/> turns reuse off for one run</b>, read exactly as the Marten suite reads it,
/// and never on CI. <c>TESTCONTAINERS_REUSE_ENABLE</c> is not read by Testcontainers for .NET 4.x at all.
/// </para>
/// <para>
/// <b>Isolation is per schema or per database.</b> Each class works in a schema or a database of its own, dropped before
/// it is created, so a reused container carrying an earlier run's objects still gives every run a clean one.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>The variable that turns container reuse off for one run; the same name every container here reads.</summary>
    public const string ReuseEnvironmentVariable = "MARTENSTUDIO_PG_REUSE";

    /// <summary>The image, the one every other live suite here runs against.</summary>
    public const string Image = "postgres:17-alpine";

    /// <summary>The label that gives this suite's container a reuse identity of its own: its key.</summary>
    public const string ReuseLabel = "marten-studio.tests";

    /// <summary>The label's value: this suite's name.</summary>
    public const string ReuseLabelValue = "PostgresStudio.Integration.Tests";

    private PostgreSqlContainer? container;
    private bool started;

    /// <summary>
    /// Whether this run shares a container with the ones around it: never on CI, and not when
    /// <see cref="ReuseEnvironmentVariable"/> is <c>false</c> or <c>0</c>.
    /// </summary>
    public static bool ShouldReuse =>
        !DockerAvailability.OnContinuousIntegration && !IsReuseDisabled(Environment.GetEnvironmentVariable(ReuseEnvironmentVariable));

    /// <summary>The superuser's connection string to the container's <c>postgres</c> database, once it is running.</summary>
    public string ConnectionString => started
        ? container!.GetConnectionString()
        : throw new InvalidOperationException(
            "The Postgres container is not running. A test that needs it must be a [PostgresFact] so that it skips " +
            "instead of failing when Docker is absent.");

    /// <summary>
    /// The container this fixture starts, built but not started - for <c>PostgresFixtureTests</c>, which reads its reuse
    /// identity.
    /// </summary>
    /// <param name="reuse">Whether to ask for reuse; the reuse hash does not depend on it.</param>
    internal static PostgreSqlContainer CreateContainer(bool reuse) =>
        new PostgreSqlBuilder(Image)
            .WithLabel(ReuseLabel, ReuseLabelValue)
            .WithReuse(reuse)
            .Build();

    /// <summary>Starts the container, unless there is no Docker to start it on.</summary>
    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        container = CreateContainer(ShouldReuse);
        await container.StartAsync();
        started = true;
    }

    /// <summary>Stops the container, or leaves it running when reuse is on.</summary>
    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    /// <summary>An open connection to the container's <c>postgres</c> database.</summary>
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
    /// An empty schema of this name in the <c>postgres</c> database, dropping whatever a previous run left behind. Quoted
    /// through the studio's own <see cref="SqlIdentifier"/>.
    /// </summary>
    public async Task<string> CreateSchemaAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        var quoted = SqlIdentifier.Quote(name);

        await using var command = new NpgsqlCommand($"drop schema if exists {quoted} cascade; create schema {quoted};", connection);

        await command.ExecuteNonQueryAsync(cancellationToken);

        return name;
    }

    /// <summary>
    /// An empty database of this name on the container, dropping whatever a previous run left first (ending its sessions),
    /// and its connection string. Outside a transaction, because Postgres will not create a database inside one.
    /// </summary>
    /// <param name="name">A literal of the calling test's, never anything typed.</param>
    /// <param name="cancellationToken">Cancels the statements.</param>
    public async Task<string> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default)
    {
        await DropDatabaseAsync(name, cancellationToken);

        await using (NpgsqlConnection connection = await OpenAsync(cancellationToken))
        {
            await using var create = new NpgsqlCommand($"create database {SqlIdentifier.Quote(name)}", connection);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        return ConnectionStringFor(name);
    }

    /// <summary>Drops the database if it is there, ending any session on it first.</summary>
    public async Task DropDatabaseAsync(string name, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenAsync(cancellationToken);

        // `with (force)` ends whatever is still attached - a pool a test forgot, or a reused container's leftover.
        await using var drop = new NpgsqlCommand($"drop database if exists {SqlIdentifier.Quote(name)} with (force)", connection);
        await drop.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The container's connection string, pointed at another database on it.</summary>
    public string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString;

    /// <summary>
    /// Whether a <see cref="ReuseEnvironmentVariable"/> value turns reuse off: only <c>false</c> and <c>0</c> do, so an
    /// unset or misspelt value leaves the default alone.
    /// </summary>
    internal static bool IsReuseDisabled(string? value) =>
        value?.Trim() is { } trimmed &&
        (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "0", StringComparison.Ordinal));
}

/// <summary>A built container's reuse identity, as Testcontainers computes it.</summary>
internal static class ContainerReuse
{
    /// <summary>
    /// The configuration Testcontainers hashes to decide which running container a reusing start attaches to. It is
    /// held in a private field of <see cref="DockerContainer" />, so a rename there fails here, by name.
    /// </summary>
    public static IContainerConfiguration ConfigurationOf(IContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        System.Reflection.FieldInfo field = typeof(DockerContainer).GetField(
                "_configuration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Testcontainers' DockerContainer no longer has a private _configuration field; find where its "
                + "IContainerConfiguration (whose GetReuseHash() decides reuse) went.");

        return (IContainerConfiguration) field.GetValue(container)!;
    }

    /// <summary>The reuse hash: Testcontainers' own, from the container's configuration.</summary>
    public static string HashOf(IContainer container) => ConfigurationOf(container).GetReuseHash();
}
