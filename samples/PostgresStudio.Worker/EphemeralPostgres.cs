using Testcontainers.PostgreSql;

namespace PostgresStudio.Worker;

/// <summary>
/// The throwaway Postgres the worker starts when nothing configured a real one (AGENTS.md D18), as the Postgres Studio
/// sample host does: either a connection string was configured, or a container is started, or the host refuses to start and
/// says how to fix it. It never falls back to <c>localhost</c> silently.
/// </summary>
internal static class EphemeralPostgres
{
    /// <summary>The label that keeps a reused container of this sample's apart from every other one.</summary>
    private const string ReuseLabel = "marten-studio.sample";

    /// <summary>The environment variable that asks for a reused container, the one every sample and the integration suite read.</summary>
    internal const string ReuseEnvironmentVariable = "MARTENSTUDIO_PG_REUSE";

    /// <summary>The connection string of the container this process started, or <see langword="null" /> when it started none.</summary>
    private static string? Started { get; set; }

    /// <summary>Whether <paramref name="connectionString" /> is the throwaway container this process started.</summary>
    /// <param name="connectionString">The connection string the worker uses.</param>
    public static bool IsThrowaway(string? connectionString) =>
        Started is { } started && string.Equals(started, connectionString, StringComparison.Ordinal);

    /// <summary>Starts a <c>postgres:17-alpine</c> container and returns its connection string.</summary>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <exception cref="InvalidOperationException">Docker is not available.</exception>
    public static async Task<string> StartAsync(CancellationToken cancellationToken = default)
    {
        bool reuse = IsReuseEnabled(Environment.GetEnvironmentVariable(ReuseEnvironmentVariable));

        // The image goes to the constructor: Testcontainers 4.15 has obsoleted the parameterless one.
        PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("worker")
            .WithLabel(ReuseLabel, "postgres-studio-worker")
            .WithReuse(reuse)
            .Build();

        try
        {
            await container.StartAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                """
                The Postgres Studio diagnostics worker needs a PostgreSQL server and could not start one.

                It starts a throwaway `postgres:17-alpine` container through Testcontainers when no
                connection string is configured, and Docker did not answer. Either:

                  - start Docker (or Podman with the Docker-compatible socket) and run again, or
                  - point the worker at a database you already have. It only reads there:

                        dotnet run --project samples/PostgresStudio.Worker -- \
                          --ConnectionStrings:Postgres "Host=localhost;Database=app;Username=postgres;Password=postgres"

                The worker will not quietly fall back to localhost: connecting to a database nobody asked
                for is how a demo reports on something that mattered.
                """,
                exception);
        }

        string connectionString = container.GetConnectionString();
        Started = connectionString;

        Console.WriteLine();
        Console.WriteLine("  ┌───────────────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("  │  Postgres Studio worker: no ConnectionStrings:Postgres was configured,    │");
        Console.WriteLine("  │  so a throwaway PostgreSQL container was started for this run, with the   │");
        Console.WriteLine("  │  demo schemas applied to it.                                              │");
        Console.WriteLine($"  │  Container: {container.Id[..Math.Min(12, container.Id.Length)],-61} │");
        Console.WriteLine($"  │  Host port: {container.GetMappedPublicPort(5432),-61} │");
        Console.WriteLine($"  │  Reused:    {(reuse ? "asked for (" + ReuseEnvironmentVariable + ")" : "no - a fresh container"),-61} │");
        Console.WriteLine("  │  Nothing in it survives `docker rm`. Configure a connection string to     │");
        Console.WriteLine("  │  point the worker at a database of your own.                              │");
        Console.WriteLine("  └───────────────────────────────────────────────────────────────────────────┘");
        Console.WriteLine();

        return connectionString;
    }

    /// <summary>A fresh container is the default; only a value that reads as true asks for reuse.</summary>
    /// <param name="value">The variable's value, or <see langword="null" /> when it is not set.</param>
    internal static bool IsReuseEnabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();

        return trimmed is "1" or "yes" or "on" || string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase);
    }
}
