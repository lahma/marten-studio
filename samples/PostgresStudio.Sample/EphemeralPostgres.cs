using Testcontainers.PostgreSql;

namespace PostgresStudio.Sample;

/// <summary>
/// The throwaway Postgres the demo starts when nothing configured a real one (AGENTS.md D18).
/// </summary>
/// <remarks>
/// <para>
/// Zero-config <c>dotnet run</c> is what makes the sample a demo rather than a setup exercise. What it must never do is
/// fall back to <c>localhost</c> silently and appear to work against somebody's real database, so there is no
/// fallback: either a connection string was configured, or a container is started, or the host refuses to start and
/// says how to fix it.
/// </para>
/// <para>
/// <b>A fresh container every run, unless asked otherwise.</b> The Marten sample reuses its container by default, and a
/// reused container is how a month-old database ends up under a new build. This sample creates databases on its server
/// as well as schemas in one, so it starts clean unless <see cref="ReuseEnvironmentVariable" /> says
/// <c>true</c>. Even then it never shares the Marten sample's container, or the integration suite's: its container
/// carries a label of its own and a database named <see cref="AdminDatabase" />, and either one is enough to give it a
/// reuse identity of its own.
/// </para>
/// </remarks>
internal static class EphemeralPostgres
{
    /// <summary>The database the container is created with: the sample's admin database.</summary>
    public const string AdminDatabase = "admin";

    /// <summary>The label that keeps a reused container of this sample's apart from every other one.</summary>
    private const string ReuseLabel = "marten-studio.sample";

    /// <summary>
    /// Starts a <c>postgres:17-alpine</c> container and returns the admin database's connection string.
    /// </summary>
    /// <exception cref="InvalidOperationException">Docker is not available.</exception>
    public static async Task<string> StartAsync(CancellationToken cancellationToken = default)
    {
        bool reuse = ReuseRequested();

        // The image goes to the constructor: Testcontainers 4.15 has obsoleted the parameterless one.
        PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase(AdminDatabase)
            .WithLabel(ReuseLabel, "postgres-studio")
            // Honoured only when the machine has testcontainers.reuse.enable=true in ~/.testcontainers.properties as
            // well; without it this is simply a new container each run.
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
                The Postgres Studio sample needs a PostgreSQL server and could not start one.

                It starts a throwaway `postgres:17-alpine` container through Testcontainers when no
                connection string is configured, and Docker did not answer. Either:

                  - start Docker (or Podman with the Docker-compatible socket) and run again, or
                  - point the sample at a database you already have, on a server where its role may
                    create the three tenant databases (tenant_a, tenant_b and tenant_c) beside it:

                        dotnet run --project samples/PostgresStudio.Sample \
                          --ConnectionStrings:Postgres "Host=localhost;Database=admin;Username=postgres;Password=postgres"

                The sample will not quietly fall back to localhost: connecting to a database nobody asked
                for is how a demo writes into something that mattered.
                """,
                exception);
        }

        string connectionString = container.GetConnectionString();

        Console.WriteLine();
        Console.WriteLine("  ┌───────────────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("  │  Postgres Studio sample: no ConnectionStrings:Postgres was configured,    │");
        Console.WriteLine("  │  so a throwaway PostgreSQL container was started for this run, with an    │");
        Console.WriteLine("  │  admin database and three tenant databases beside it.                     │");
        Console.WriteLine($"  │  Container: {container.Id[..Math.Min(12, container.Id.Length)],-61} │");
        Console.WriteLine($"  │  Host port: {container.GetMappedPublicPort(5432),-61} │");
        Console.WriteLine($"  │  Reused:    {(reuse ? "asked for (" + ReuseEnvironmentVariable + ")" : "no - a fresh container"),-61} │");
        Console.WriteLine("  │  Nothing in it survives `docker rm`. Configure a connection string to     │");
        Console.WriteLine("  │  point the demo somewhere durable.                                        │");
        Console.WriteLine("  └───────────────────────────────────────────────────────────────────────────┘");
        Console.WriteLine();

        return connectionString;
    }

    /// <summary>The environment variable that asks for a reused container.</summary>
    /// <remarks>
    /// The same name the Marten sample and the integration suite read, so one variable governs every container this
    /// repository starts. <c>TESTCONTAINERS_REUSE_ENABLE</c> reads like the obvious name and does nothing: Testcontainers
    /// for .NET 4.x has no such variable.
    /// </remarks>
    internal const string ReuseEnvironmentVariable = "MARTENSTUDIO_PG_REUSE";

    private static bool ReuseRequested() => IsReuseEnabled(Environment.GetEnvironmentVariable(ReuseEnvironmentVariable));

    /// <summary>
    /// A fresh container is the default here; only a value that reads as true asks for reuse. <see langword="null" />
    /// means the variable is not set.
    /// </summary>
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
