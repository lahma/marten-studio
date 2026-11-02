using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;

namespace PostgresStudio;

/// <summary>
/// Registers the Postgres diagnostics engine, with no UI.
/// </summary>
public static class PostgresDiagnosticsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the diagnostics engine alone: the health sweep and its checks, the facts they read, and
    /// <see cref="IPostgresDiagnostics" />, for the host's own code - a worker that mails a daily summary, a job that fails a
    /// deployment, a console tool - with no ASP.NET Core and no pages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It reads the data sources <em>this application</em> registered - the unkeyed <c>NpgsqlDataSource</c> as
    /// <c>default</c>, and whatever <see cref="PostgresDiagnosticsOptions.AddDataSource" /> and
    /// <see cref="PostgresDiagnosticsOptions.AddDatabaseSource" /> name - with no connection string of its own. Call this
    /// before or after <c>AddNpgsqlDataSource()</c>; the scan that finds the default one runs lazily on first use.
    /// </para>
    /// <para>
    /// <b>Nothing is mapped, nothing runs and nothing connects</b> until host code calls
    /// <see cref="IPostgresDiagnostics" />: no hosted service, no timer, no endpoint. No finding is muted, because muting is a
    /// visitor's; the checks that judge a studio's exposure are not run, because there is no studio. The
    /// <see cref="IPostgresDiagnostics.GetStudioPath" /> strings and each finding's links are relative to wherever a UI is
    /// mounted, and meaningless where none is: a host that mounts no UI does not render them.
    /// </para>
    /// <para>
    /// The options are validated when the host starts (<c>ValidateOnStart</c>): with no endpoint to map, nothing else would
    /// read them before the first call.
    /// </para>
    /// <para>
    /// <b>One registration.</b> <c>AddPostgresStudio()</c> and <c>AddMartenStudio()</c> register the engine themselves, with
    /// the same options as base members of their own: calling this in an application that registers either throws, in
    /// either order. Calling it twice behaves as calling <c>AddPostgresStudio()</c> twice does: every service is registered
    /// once and the options are configured twice.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the engine: its sources, the schemas it may name, the role it reads as.</param>
    /// <returns>The same collection, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services" /> is null.</exception>
    /// <exception cref="InvalidOperationException">A studio is already registered in this container, or this call precedes one.</exception>
    public static IServiceCollection AddPostgresDiagnostics(
        this IServiceCollection services,
        Action<PostgresDiagnosticsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        DiagnosticsCoreRegistration.Add(
            services,
            PostgresDiagnosticsDescriptor.Instance,
            static provider => new PostgresDiagnosticsSettings(provider.GetRequiredService<IOptions<PostgresDiagnosticsOptions>>()),
            "AddPostgresDiagnostics()",
            byStudio: false);

        const string optionsType = nameof(PostgresDiagnosticsOptions);

        OptionsBuilder<PostgresDiagnosticsOptions> optionsBuilder = services
            .AddOptions<PostgresDiagnosticsOptions>()
            .Validate(static options => DiagnosticsOptionsValidation.IsQueryTimeout(options.QueryTimeout), DiagnosticsOptionsValidation.QueryTimeoutMessage(optionsType))
            .Validate(static options => DiagnosticsOptionsValidation.IsSqlConsoleRole(options.SqlConsoleRole), DiagnosticsOptionsValidation.SqlConsoleRoleMessage(optionsType))
            .Validate(static options => DiagnosticsOptionsValidation.IsHealthInterval(options.HealthInterval), DiagnosticsOptionsValidation.HealthIntervalMessage(optionsType))
            .Validate(static options => DiagnosticsOptionsValidation.IsMaxStudioConnections(options.MaxStudioConnections), DiagnosticsOptionsValidation.MaxStudioConnectionsMessage(optionsType))
            .Validate(static options => DiagnosticsOptionsValidation.IsStudioApplicationName(options.StudioApplicationName), DiagnosticsOptionsValidation.StudioApplicationNameMessage(optionsType))
            .Validate(static options => DiagnosticsOptionsValidation.AreSchemas(options.BrowsableSchemas), DiagnosticsOptionsValidation.SchemasMessage(optionsType))
            .ValidateSharedAcceptedChecks(optionsType, PostgresDiagnosticsDescriptor.Instance, static options => options.AcceptedCheckReasons)
            .ValidateSourceNames(optionsType, static options => options.DataSources.Concat(options.DatabaseSources).Select(static x => x.Name));

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        optionsBuilder.PostConfigure(PostgresSeamRegistration.DefaultBrowsableSchemas);
        optionsBuilder.ValidateOnStart();

        PostgresSeamRegistration.Add<PostgresDiagnosticsOptions>(services);

        return services;
    }
}
