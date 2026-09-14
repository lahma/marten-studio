using Testcontainers.PostgreSql;

namespace MartenStudio.Sample;

/// <summary>
/// The throwaway Postgres the demo starts when nothing configured a real one (AGENTS.md D18).
/// </summary>
/// <remarks>
/// Zero-config <c>dotnet run</c> is what makes the sample a demo rather than a setup exercise. What it
/// must never do is fall back to <c>localhost</c> silently and appear to work against somebody's real
/// database, so there is no fallback: either a connection string was configured, or a container is
/// started, or the host refuses to start and says how to fix it.
/// </remarks>
internal static class EphemeralPostgres
{
    /// <summary>
    /// Starts a <c>postgres:17-alpine</c> container and returns its connection string.
    /// </summary>
    /// <exception cref="InvalidOperationException">Docker is not available.</exception>
    public static async Task<string> StartAsync(CancellationToken cancellationToken = default)
    {
        // The image goes to the constructor: Testcontainers 4.15 has obsoleted the parameterless one.
        PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
            // Reuse so that stopping and starting the sample does not cost a fresh container and a fresh
            // seed every time. Honoured only when the machine has testcontainers.reuse.enable=true in
            // ~/.testcontainers.properties; without it this is simply a new container each run.
            .WithReuse(true)
            .Build();

        try
        {
            await container.StartAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                """
                The Marten Studio sample needs a PostgreSQL database and could not start one.

                It starts a throwaway `postgres:17-alpine` container through Testcontainers when no
                connection string is configured, and Docker did not answer. Either:

                  - start Docker (or Podman with the Docker-compatible socket) and run again, or
                  - point the sample at a database you already have:

                        dotnet run --project samples/MartenStudio.Sample \
                          --ConnectionStrings:Marten "Host=localhost;Database=marten_studio;Username=postgres;Password=postgres"

                The sample will not quietly fall back to localhost: connecting to a database nobody asked
                for is how a demo writes into something that mattered.
                """,
                exception);
        }

        string connectionString = container.GetConnectionString();

        Console.WriteLine();
        Console.WriteLine("  ┌───────────────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("  │  Marten Studio sample: no ConnectionStrings:Marten was configured, so     │");
        Console.WriteLine("  │  a throwaway PostgreSQL container was started for this run.               │");
        Console.WriteLine($"  │  Container: {container.Id[..Math.Min(12, container.Id.Length)],-61} │");
        Console.WriteLine($"  │  Host port: {container.GetMappedPublicPort(5432),-61} │");
        Console.WriteLine("  │  Nothing in it survives `docker rm`. Configure a connection string to     │");
        Console.WriteLine("  │  point the demo somewhere durable.                                        │");
        Console.WriteLine("  └───────────────────────────────────────────────────────────────────────────┘");
        Console.WriteLine();

        return connectionString;
    }
}
