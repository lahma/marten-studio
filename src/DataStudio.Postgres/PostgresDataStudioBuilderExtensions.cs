using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using DataStudio.Postgres;
using DataStudio.Postgres.Internal;
using DataStudio.Postgres.Services.Postgres;
using DataStudio.Postgres.Services.Settings;
using DataStudio.Services.Settings;

namespace DataStudio;

/// <summary>
/// Adds the Postgres module to Data Studio.
/// </summary>
public static class PostgresDataStudioBuilderExtensions
{
    /// <summary>
    /// Adds the Postgres module to the studio: its database pages, the source provider that finds this application's
    /// <c>NpgsqlDataSource</c>s, the scope, authorization, capability and audit services, the database browser, Sessions, the SQL
    /// console, Relationships and the health advisor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No option points the module at a database by connection string. It renders the data sources <em>this application</em> registered - the
    /// unkeyed <c>NpgsqlDataSource</c> as <c>default</c>, and whatever <see cref="PostgresDiagnosticsOptions.AddDataSource" /> and
    /// <see cref="PostgresDiagnosticsOptions.AddDatabaseSource" /> name. Call this before or after <c>AddNpgsqlDataSource()</c>; the scan
    /// that finds the default one runs lazily on first use, when the container is complete, and nothing connects until somebody opens a page.
    /// </para>
    /// <para>
    /// Nothing here changes how the host behaves. No global JSON or SignalR option is touched, no middleware ordering is required, and every
    /// service is registered with <c>TryAdd</c> so an application that registered its own first is the one that answers. Call
    /// <c>MapDataStudio()</c> on the built application to map the endpoints.
    /// </para>
    /// <para>
    /// <b>One database module per application.</b> Calling this in an application that already called <c>AddMarten()</c> throws: the two
    /// share their services, and the Marten module already browses its stores' databases. Calling it twice is safe.
    /// </para>
    /// </remarks>
    /// <param name="builder">The builder <c>AddDataStudio()</c> returned.</param>
    /// <param name="configure">Configures the module, including its sources.</param>
    /// <returns>The same builder, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The Marten module is already added to this builder's services, or the application carries a newer DataStudio.Postgres.Diagnostics or
    /// DataStudio than the one this package was released with, or another build of the same version. A missing or older one fails earlier, in the
    /// loader, with a <see cref="FileNotFoundException" /> that names it and the version to pin.
    /// </exception>
    public static DataStudioBuilder AddPostgres(
        this DataStudioBuilder builder,
        Action<PostgresOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Before anything that names a DataStudio.Postgres.Diagnostics type: this body names none, and everything else is in
        // AddPostgresServices, which is NoInlining so that it is compiled only once the guard has passed - so a newer
        // engine whose members moved, or another build of the same version, gets the guard's message rather than a
        // MissingMethodException. (The parameter types above derive from an engine type and name the shell's builder, so a missing or older
        // engine or shell fails in the loader before this line, with a FileNotFoundException that names it; see the guard's remarks.)
        // PostgresStudioDiagnosticsVersionGuardTests scans this method's IL to keep it that way.
        PostgresStudioDiagnosticsVersionGuard.Ensure();

        AddPostgresServices(builder, configure);

        return builder;
    }

    /// <summary>The body of <see cref="AddPostgres" />, once the engine and the shell are known to be the right ones.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddPostgresServices(
        DataStudioBuilder builder,
        Action<PostgresOptions>? configure)
    {
        IServiceCollection services = builder.Services;

        // First, so that a second database module in this container is refused before anything of this one is registered.
        StudioCoreRegistration.Add(
            services,
            PostgresStudioDescriptor.Marker,
            PostgresStudioDescriptor.Instance,
            static provider => new PostgresStudioSettings(
                provider.GetRequiredService<IOptions<PostgresOptions>>(),
                provider.GetRequiredService<IShellSettings>()));

        // The bounds and sentences the Marten module has, from the one shared chain, in this module's own names; then the
        // source names, which only this module has.
        OptionsBuilder<PostgresOptions> optionsBuilder = services
            .AddOptions<PostgresOptions>()
            .ValidateSharedBounds(nameof(PostgresOptions), nameof(PostgresOptions.MaxInlineValueBytes), Shape)
            .ValidateSharedCapabilities(nameof(PostgresOptions), nameof(PostgresCapabilities), Shape)
            .ValidateSharedSchemas(nameof(PostgresOptions), Shape)
            .ValidateSharedAcceptedChecks(nameof(PostgresOptions), PostgresStudioDescriptor.Instance, static options => options.AcceptedCheckReasons)
            .ValidateSourceNames(nameof(PostgresOptions), SourceNames);

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        // After every Configure, including configuration binding, which appends to a list rather than replacing it: the
        // default schema is the answer to "nothing named any", never an entry a host has to remove.
        optionsBuilder.PostConfigure(static options => PostgresSeamRegistration.DefaultBrowsableSchemas(options));

        // The engine's seams, answered for plain PostgreSQL - the source registry and provider, the lock labeler, the
        // declarations, the health table source and the statement classifier - over the options this module configured.
        PostgresSeamRegistration.Add<PostgresOptions>(services);

        // The shared seams only a UI has, answered for plain PostgreSQL.
        services.TryAddSingleton<IStudioLinks, PostgresStudioLinks>();

        // The Overview's server read: scoped, because what it remembers is the circuit's.
        services.TryAddScoped<IPostgresServerReader, PostgresServerReader>();
    }

    private static IEnumerable<string> SourceNames(PostgresOptions options) =>
        options.DataSources.Concat(options.DatabaseSources).Select(static x => x.Name);

    /// <summary>The options the Postgres and Marten modules share, as the shared validation reads them.</summary>
    private static StudioOptionsShape Shape(PostgresOptions options) => new(
        options.DefaultPageSize,
        options.MaxPageSize,
        options.QueryTimeout,
        options.MaxInlineValueBytes,
        options.MaxSqlConsoleRows,
        options.SqlConsoleRole,
        options.ExactCountThreshold,
        options.HealthInterval,
        options.Capabilities is not null,
        options.BrowsableSchemas,
        options.MaxStudioConnections,
        options.StudioApplicationName);
}
