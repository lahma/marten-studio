using Testcontainers.PostgreSql;

namespace KStudio.Sample;

/// <summary>
/// The throwaway Postgres the sample starts when <c>ConnectionStrings:Postgres</c> names none (AGENTS.md D18): the Marten sample's behaviour, in
/// the sample's own namespace and without that sample's own banner, because the one banner this host prints names both containers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Copied, not linked</b> (<c>samples/MartenStudio.Sample/EphemeralPostgres.cs</c>): the original prints its own boxed banner and
/// names <c>ConnectionStrings:Marten</c>, and its reuse identity must not be shared - a reused container of that sample would otherwise
/// be this one's database.
/// </para>
/// <para>
/// <b>No fallback.</b> Either a connection string was configured, or a container is started, or the host stops saying how to fix it.
/// </para>
/// <para>
/// <b>Reuse is opt-in</b> and follows the Kafka container: <c>KSTUDIO_SAMPLE_REUSE=true</c>. The banner promises both containers are deleted
/// when the process stops, and a reused one is not.
/// </para>
/// </remarks>
internal static class EphemeralPostgres
{
    /// <summary>The label every container the sample starts carries.</summary>
    internal const string SampleLabel = "kstudio.sample";

    /// <summary>The label that gives a reused Postgres its identity.</summary>
    internal const string ReuseLabel = "kstudio.sample.reuse";

    /// <summary>The label's value.</summary>
    internal const string ReuseLabelValue = "kstudio-sample-postgres";

    /// <summary>The connection string of the container this process started, or <see langword="null" /> when it started none.</summary>
    private static string? Started { get; set; }

    /// <summary>Starts a <c>postgres:17-alpine</c> container.</summary>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The connection string, and the container's <c>host:port</c> for the banner.</returns>
    /// <exception cref="InvalidOperationException">Docker is not available.</exception>
    public static async Task<(string ConnectionString, string HostAndPort)> StartAsync(CancellationToken cancellationToken = default)
    {
        PostgreSqlContainer container = CreateContainer(
            Kafka.SampleKafka.IsReuseRequested(Environment.GetEnvironmentVariable(Kafka.SampleKafka.ReuseEnvironmentVariable)),
            Environment.GetEnvironmentVariable(Kafka.SampleKafka.PacketEnvironmentVariable));

        try
        {
            await container.StartAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "KStudio sample: no ConnectionStrings:Postgres is configured and Docker is not available to start a throwaway Postgres. "
                + "Start Docker, or set ConnectionStrings:Postgres to a database you own.",
                exception);
        }

        string connectionString = container.GetConnectionString();
        Started = connectionString;
        return (connectionString, $"{container.Hostname}:{container.GetMappedPublicPort(5432)}");
    }

    /// <summary>The container <see cref="StartAsync" /> starts, built and not started.</summary>
    /// <param name="reuse">Whether to ask for reuse; the reuse hash does not depend on it.</param>
    /// <param name="packet">The agent packet for the <c>kstudio.packet</c> label, or <see langword="null" /> for none.</param>
    internal static PostgreSqlContainer CreateContainer(bool reuse, string? packet)
    {
        // The image goes to the constructor: Testcontainers 4.15 has obsoleted the parameterless one.
        PostgreSqlBuilder builder = new PostgreSqlBuilder("postgres:17-alpine").WithLabel(SampleLabel, "true");

        if (reuse)
        {
            builder = builder.WithLabel(ReuseLabel, ReuseLabelValue);
        }

        if (!string.IsNullOrWhiteSpace(packet))
        {
            builder = builder.WithLabel("kstudio.packet", packet);
        }

        return builder.WithReuse(reuse).Build();
    }

    /// <summary>Whether <paramref name="connectionString" /> is the throwaway container this process started, as opposed to a database somebody configured.</summary>
    /// <param name="connectionString">The store's connection string.</param>
    public static bool IsThrowaway(string? connectionString) =>
        Started is { } started && string.Equals(started, connectionString, StringComparison.Ordinal);
}
