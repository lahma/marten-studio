using DataStudio.Postgres.Services.Health;

namespace DataStudio.Postgres.Services.Aurora;

/// <summary>Where Aurora's own functions are called on the server a read is about (issue #81).</summary>
internal static class AuroraFunctions
{
    /// <summary>
    /// The schema every Aurora read qualifies its calls with: where the platform read found the <c>aurora_version()</c> a superuser owns, in a
    /// schema only a superuser can create in (<c>HealthServerQueries.Platform</c>). The sweep's one read of the server facts, so no statement is
    /// run for it here.
    /// </summary>
    /// <param name="context">The read's context.</param>
    /// <exception cref="HealthCheckUnavailableException">
    /// There is none - Aurora's settings alone mark the server, or its <c>aurora_version()</c> is in a schema a database owner could also have made a function in
    /// - so the read says that in a sentence (<see cref="PostgresServerFacts.AuroraFunctionsNotFound" />) and calls nothing.
    /// </exception>
    public static async Task<string> SchemaAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        return server.AuroraFunctionSchema ?? throw new HealthCheckUnavailableException(PostgresServerFacts.AuroraFunctionsNotFound);
    }
}
