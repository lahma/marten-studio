using System.Runtime.CompilerServices;

using Npgsql;

namespace MartenStudio.Integration.Tests;

/// <summary>
/// A fact that needs the Postgres container: it skips with a reason when Docker is not running, and never
/// skips on CI (see <see cref="DockerAvailability"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresFactAttribute : FactAttribute
{
    /// <summary>Wires up xunit's conditional skip against the Docker probe.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler; xunit uses it to locate the test.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler; xunit uses it to locate the test.</param>
    public PostgresFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = DockerAvailability.SkipReason;
        SkipType = typeof(DockerAvailability);
        SkipUnless = nameof(DockerAvailability.IsAvailable);
    }
}

/// <summary>A theory that needs the Postgres container. Same rule as <see cref="PostgresFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    /// <summary>Wires up xunit's conditional skip against the Docker probe.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler; xunit uses it to locate the test.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler; xunit uses it to locate the test.</param>
    public PostgresTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = DockerAvailability.SkipReason;
        SkipType = typeof(DockerAvailability);
        SkipUnless = nameof(DockerAvailability.IsAvailable);
    }
}

/// <summary>
/// A test class with a schema of its own on the shared container.
/// </summary>
/// <remarks>
/// The schema is named after the test class and dropped before it is created, so a reused container
/// carrying yesterday's tables still gives every run a clean one. When Docker is absent nothing is
/// created and every test in the class skips.
/// </remarks>
public abstract class PostgresTestBase(PostgresFixture fixture) : IAsyncLifetime
{
    /// <summary>The shared container.</summary>
    protected PostgresFixture Fixture { get; } = fixture;

    /// <summary>This class's schema.</summary>
    protected string Schema { get; private set; } = string.Empty;

    /// <summary>Creates the schema, then whatever <see cref="SeedAsync"/> puts in it.</summary>
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        Schema = await Fixture.CreateSchemaAsync(GetType().Name.ToLowerInvariant());

        await using var connection = await Fixture.OpenAsync();

        await SeedAsync(connection);
    }

    /// <summary>Nothing to clean up: the schema is dropped by the next run that uses this name.</summary>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Puts the tables this class needs into its schema.</summary>
    protected virtual Task SeedAsync(NpgsqlConnection connection) => Task.CompletedTask;

    /// <summary>An open connection.</summary>
    protected Task<NpgsqlConnection> OpenAsync() => Fixture.OpenAsync();

    /// <summary>Runs DDL or a statement that returns nothing.</summary>
    protected static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs a scalar query.</summary>
    protected static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);

        return await command.ExecuteScalarAsync();
    }
}
