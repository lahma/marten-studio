using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using PostgresStudio.Internal;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;

namespace PostgresStudio;

/// <summary>
/// Registers Postgres Studio's services.
/// </summary>
public static class PostgresStudioServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything Postgres Studio renders with: its Blazor components, the source provider that finds this
    /// application's <c>NpgsqlDataSource</c>s, the scope, authorization, capability and audit services, the database
    /// browser, Sessions, the SQL console, Relationships, the health advisor, and the startup guard that refuses an
    /// unauthorized mapping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No option points the studio at a database by connection string. It renders the data sources <em>this
    /// application</em> registered - the unkeyed <c>NpgsqlDataSource</c> as <c>default</c>, and whatever
    /// <see cref="PostgresDiagnosticsOptions.AddDataSource" /> and <see cref="PostgresDiagnosticsOptions.AddDatabaseSource" />
    /// name. Call this before or after <c>AddNpgsqlDataSource()</c>; the scan that finds the default one runs lazily on
    /// first use, when the container is complete, and nothing connects until somebody opens a page.
    /// </para>
    /// <para>
    /// Nothing here changes how the host behaves. No global JSON or SignalR option is touched, no middleware ordering is
    /// required, and every service is registered with <c>TryAdd</c> so an application that registered its own first is
    /// the one that answers. Call <c>MapPostgresStudio()</c> on the built application to map the endpoints.
    /// </para>
    /// <para>
    /// <b>One studio per application.</b> Calling this in an application that already called <c>AddMartenStudio()</c>
    /// throws: the two share their services, and Marten Studio already browses its store's database.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the studio, including the path it is served under and its sources.</param>
    /// <returns>The same collection, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services" /> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Marten Studio is already registered in this container, or the application carries a newer
    /// PostgresStudio.Diagnostics than the one this package was released with, or another build of the same version. A
    /// missing or older PostgresStudio.Diagnostics fails earlier, in the loader, with a <see cref="FileNotFoundException" />
    /// that names it and the version to pin.
    /// </exception>
    public static IServiceCollection AddPostgresStudio(
        this IServiceCollection services,
        Action<PostgresStudioOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Before anything that names a PostgresStudio.Diagnostics type: this body names none, and everything else is in
        // AddPostgresStudioServices, which is NoInlining so that it is compiled only once the guard has passed - so a newer
        // engine whose members moved, or another build of the same version, gets the guard's message rather than a
        // MissingMethodException. (The parameter type below derives from an engine type, so a missing or older engine fails
        // in the loader before this line, with a FileNotFoundException that names it; see the guard's remarks.)
        // PostgresStudioDiagnosticsVersionGuardTests scans this method's IL to keep it that way.
        PostgresStudioDiagnosticsVersionGuard.Ensure();

        return AddPostgresStudioServices(services, configure);
    }

    /// <summary>The body of <see cref="AddPostgresStudio" />, once the engine is known to be the right one.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IServiceCollection AddPostgresStudioServices(
        IServiceCollection services,
        Action<PostgresStudioOptions>? configure)
    {
        // First, so that a second studio in this container is refused before anything of this one is registered.
        StudioCoreRegistration.Add(
            services,
            PostgresStudioDescriptor.Marker,
            PostgresStudioDescriptor.Instance,
            static provider => new PostgresStudioSettings(provider.GetRequiredService<IOptions<PostgresStudioOptions>>()));

        // The bounds and sentences MartenStudio has, from the one shared chain, in PostgresStudio's own names; then the
        // source names, which only this studio has.
        OptionsBuilder<PostgresStudioOptions> optionsBuilder = services
            .AddOptions<PostgresStudioOptions>()
            .ValidateSharedBounds(nameof(PostgresStudioOptions), nameof(PostgresStudioOptions.MaxInlineValueBytes), Shape)
            .ValidateSharedCapabilities(nameof(PostgresStudioOptions), nameof(PostgresStudioCapabilities), Shape)
            .ValidateSharedSchemas(nameof(PostgresStudioOptions), Shape)
            .ValidateSharedAcceptedChecks(nameof(PostgresStudioOptions), PostgresStudioDescriptor.Instance, static options => options.AcceptedCheckReasons)
            .ValidateSourceNames(nameof(PostgresStudioOptions), SourceNames);

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        // After every Configure, including configuration binding, which appends to a list rather than replacing it: the
        // default schema is the answer to "nothing named any", never an entry a host has to remove.
        optionsBuilder.PostConfigure(static options => PostgresSeamRegistration.DefaultBrowsableSchemas(options));

        // The engine's seams, answered for plain PostgreSQL - the source registry and provider, the lock labeler, the
        // declarations, the health table source and the statement classifier - over the options this studio configured.
        PostgresSeamRegistration.Add<PostgresStudioOptions>(services);

        // The shared seams only a UI has, answered for plain PostgreSQL.
        services.TryAddSingleton<IStudioLinks, PostgresStudioLinks>();

        // The Overview's server read: scoped, because what it remembers is the circuit's.
        services.TryAddScoped<IPostgresServerReader, PostgresServerReader>();

        return services;
    }

    private static IEnumerable<string> SourceNames(PostgresStudioOptions options) =>
        options.DataSources.Concat(options.DatabaseSources).Select(static x => x.Name);

    /// <summary>The options PostgresStudio shares with every studio, as the shared validation reads them.</summary>
    private static StudioOptionsShape Shape(PostgresStudioOptions options) => new(
        options.Path,
        options.DefaultPageSize,
        options.MaxPageSize,
        options.QueryTimeout,
        options.MaxInlineValueBytes,
        options.MaxSqlConsoleRows,
        options.SqlConsoleRole,
        options.ExactCountThreshold,
        options.RefreshInterval,
        options.HealthInterval,
        options.Capabilities is not null,
        options.BrowsableSchemas);
}
