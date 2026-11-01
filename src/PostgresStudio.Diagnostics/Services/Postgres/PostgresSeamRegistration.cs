using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Statements;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// Registers the diagnostics engine's seams as answered for plain PostgreSQL - where the application's own data sources are
/// found, how they are listed, which locks are labelled and how a statement is grouped - for whichever of
/// <c>AddPostgresDiagnostics()</c> or <c>AddPostgresStudio()</c> calls it.
/// </summary>
internal static class PostgresSeamRegistration
{
    /// <summary>The schema browsed when nothing names one (<see cref="PostgresDiagnosticsOptions.BrowsableSchemas" />).</summary>
    internal const string DefaultBrowsableSchema = "public";

    /// <summary>
    /// The default schema is the answer to "nothing named any", added after every <c>Configure</c>, including configuration
    /// binding, which appends to a list rather than replacing it - never an entry a host has to remove.
    /// </summary>
    /// <param name="options">The options after every configuration.</param>
    public static void DefaultBrowsableSchemas(PostgresDiagnosticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.BrowsableSchemas.Count == 0)
        {
            options.BrowsableSchemas.Add(DefaultBrowsableSchema);
        }
    }

    /// <summary>Registers the seams, over the options a host configured on <typeparamref name="TOptions" />.</summary>
    /// <typeparam name="TOptions">The options type: the diagnostics' own, or a studio's, which derives from it.</typeparam>
    /// <param name="services">The application's service collection.</param>
    public static void Add<TOptions>(IServiceCollection services)
        where TOptions : PostgresDiagnosticsOptions
    {
        ArgumentNullException.ThrowIfNull(services);

        // The engine reads the diagnostics' options; where a studio's options derive from them, the same instance answers.
        if (typeof(TOptions) != typeof(PostgresDiagnosticsOptions))
        {
            services.TryAddSingleton<IOptions<PostgresDiagnosticsOptions>>(
                static provider => new ForwardedOptions<PostgresDiagnosticsOptions, TOptions>(provider.GetRequiredService<IOptions<TOptions>>()));
        }

        // The registry captures this collection and scans it lazily, on first use: the registration may be called
        // before AddNpgsqlDataSource(), and a scan now would find nothing. A singleton built by the container, so the
        // provider it is handed is the root one - what the default source and every factory resolve from.
        services.TryAddSingleton(provider => new PostgresSourceRegistry(services, provider, provider.GetService<TimeProvider>()));

        // The seams, answered for plain PostgreSQL. The source provider is scoped, as the seam asks: its refresh
        // generation is the circuit's.
        services.TryAddScoped<IStudioSourceProvider, PostgresStudioSourceProvider>();
        services.TryAddSingleton<ISessionLockLabeler, PostgresSessionLockLabeler>();
        services.TryAddScoped<IDatabaseDeclarationsSource, PostgresDatabaseDeclarationsSource>();
        services.TryAddSingleton<IHealthTableSource, PostgresHealthTableSource>();
        services.TryAddSingleton<IHealthTableRemedies, PostgresHealthTableRemedies>();

        // W5-Q1: the Statements groups - a statement's verb and its first table of the judged set.
        services.TryAddSingleton<IStatementClassifier>(PostgresStatementClassifier.Instance);
    }
}

/// <summary>
/// <typeparamref name="TBase" />'s options as the <typeparamref name="TDerived" /> options object a host configured: live, the
/// same instance, so a write onto it after the container is built is seen by what reads the base.
/// </summary>
/// <typeparam name="TBase">The options type the engine reads.</typeparam>
/// <typeparam name="TDerived">The options type the host configured.</typeparam>
internal sealed class ForwardedOptions<TBase, TDerived>(IOptions<TDerived> inner) : IOptions<TBase>
    where TBase : class
    where TDerived : class, TBase
{
    /// <inheritdoc />
    public TBase Value => inner.Value;
}
