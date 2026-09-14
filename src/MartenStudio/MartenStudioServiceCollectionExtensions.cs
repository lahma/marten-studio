using System.Text.RegularExpressions;

using MartenStudio.Internal;
using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Events;
using MartenStudio.Services.Live;
using MartenStudio.Services.Projections;
using MartenStudio.Services.Schema;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MartenStudio;

/// <summary>
/// Registers Marten Studio's services.
/// </summary>
public static partial class MartenStudioServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything Marten Studio renders with: its Blazor components, the store registry that
    /// discovers this application's Marten stores, the scope, authorization, capability and audit
    /// services, and the startup guard that refuses an unauthorized mapping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No option points the studio at a store. It renders the stores <em>this application</em> registered
    /// - the default <c>IDocumentStore</c> and every ancillary store added with
    /// <c>AddMartenStore&lt;T&gt;()</c> - which is why there is no connection string anywhere in the
    /// public API. Call this before or after <c>AddMarten()</c>; the scan that finds the stores runs
    /// lazily on first use, when the container is complete.
    /// </para>
    /// <para>
    /// Nothing here changes how the host behaves. No global JSON or SignalR option is touched, no
    /// middleware ordering is required, and every service is registered with <c>TryAdd</c> so an
    /// application that registered its own first is the one that answers. Call
    /// <c>MapMartenStudio()</c> on the built application to map the endpoints.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the studio, including the path it is served under.</param>
    /// <returns>The same collection, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services" /> is null.</exception>
    public static IServiceCollection AddMartenStudio(
        this IServiceCollection services,
        Action<MartenStudioOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        OptionsBuilder<MartenStudioOptions> optionsBuilder = services
            .AddOptions<MartenStudioOptions>()
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.Path) && options.Path.StartsWith('/'),
                "MartenStudioOptions.Path must start with '/'")
            .Validate(
                static options => IsRoutableDashboardPath(options.Path),
                "MartenStudioOptions.Path must be a simple URL path: it cannot contain '{', '}', '?', '#', '.' or '..' segments, or empty segments ('//')")
            .Validate(
                static options => options.DefaultPageSize >= 1 && options.DefaultPageSize <= options.MaxPageSize,
                "MartenStudioOptions.DefaultPageSize must be between 1 and MaxPageSize")
            .Validate(
                static options => options.MaxPageSize >= 1 && options.MaxPageSize <= 5000,
                "MartenStudioOptions.MaxPageSize must be between 1 and 5000: a larger page is a sequential scan rendered into a browser")
            .Validate(
                static options => options.QueryTimeout >= TimeSpan.FromSeconds(1) && options.QueryTimeout <= TimeSpan.FromMinutes(10),
                "MartenStudioOptions.QueryTimeout must be between one second and ten minutes")
            .Validate(
                static options => options.MaxInlineDocumentBytes > 0 && options.MaxInlineDocumentBytes <= 64 * 1024 * 1024,
                "MartenStudioOptions.MaxInlineDocumentBytes must be between 1 and 64 MiB")
            .Validate(
                static options => options.MaxSqlConsoleRows >= 1 && options.MaxSqlConsoleRows <= 10_000,
                "MartenStudioOptions.MaxSqlConsoleRows must be between 1 and 10000")
            .Validate(
                static options => options.SqlConsoleRole is null || PostgresRoleName().IsMatch(options.SqlConsoleRole),
                "MartenStudioOptions.SqlConsoleRole must be a plain Postgres identifier: it is written into SET LOCAL ROLE, where it cannot be a parameter")
            .Validate(
                static options => options.ExactCountThreshold >= 0,
                "MartenStudioOptions.ExactCountThreshold cannot be negative")
            .Validate(
                static options => options.RefreshInterval >= TimeSpan.FromSeconds(1) && options.RefreshInterval <= TimeSpan.FromMinutes(5),
                "MartenStudioOptions.RefreshInterval must be between one second and five minutes")
            .Validate(
                static options => options.Capabilities is not null,
                "MartenStudioOptions.Capabilities cannot be null: use new MartenStudioCapabilities() for none, or MartenStudioCapabilities.All()")
            .Validate(
                static options => options.KnownTenantIds.All(static tenantId => !string.IsNullOrWhiteSpace(tenantId)),
                "MartenStudioOptions.KnownTenantIds cannot contain an empty or whitespace tenant id");

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        // Refuses to start an application whose mapped studio nothing authorizes. Registered here rather
        // than at the map site, because a hosted service added to a built application is too late.
        services.TryAddSingleton<MartenStudioMappedEndpoints>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, MartenStudioEndpointAuthorizationGuard>());

        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Deliberately no AddSignalR().AddJsonProtocol(...) and no JsonStringEnumConverter: those options
        // belong to the whole application's SignalR and its own hubs must keep their own format
        // (there is no studio hub in v1 - plan D10).
        services.AddHttpContextAccessor();

        // The registry captures this collection and scans it lazily, on first use: AddMartenStudio() is
        // routinely called before AddMarten(), and a scan now would find no stores at all.
        services.TryAddSingleton(new MartenStoreRegistry(services));

        services.TryAddSingleton<StudioCapabilityGuard>();
        services.TryAddSingleton<StudioActionLogService>();

        // The information_schema reader, singleton because what it caches - a table's physical columns -
        // changes only with a schema migration and is the same answer for every circuit.
        services.TryAddSingleton<ColumnCatalog>();

        // Scoped, because all five are about one circuit: what it is pointed at, who is driving it, and
        // what it has been told.
        services.TryAddScoped<StudioState>();
        services.TryAddScoped<IStudioScopeCatalog, StudioScopeCatalog>();
        services.TryAddScoped<StudioAuthorization>();
        services.TryAddScoped<StudioScopeResolver>();
        services.TryAddScoped<TenantDiscovery>();
        services.TryAddScoped<StudioActionLog>();
        services.TryAddScoped<ToastService>();
        services.TryAddScoped<IStoreInfoService, StoreInfoService>();
        services.TryAddScoped<IDocumentWriteService, DocumentWriteService>();
        services.TryAddScoped<IEventDataService, EventDataService>();
        services.TryAddScoped<ISchemaDataService, SchemaDataService>();
        services.TryAddScoped<IConfigurationService, ConfigurationService>();

        // Projections and the async daemon. The three singletons are process-wide on purpose: one
        // snapshot per interval however many circuits are watching, one tracker subscription per
        // database however many pages are open, and a rebuild that outlives the circuit that started it.
        services.TryAddSingleton<StudioSnapshotCache>();
        services.TryAddSingleton<StudioLiveState>();
        services.TryAddSingleton<StudioOperationTracker>();
        services.TryAddScoped<DaemonAccessor>();
        services.TryAddScoped<IProjectionDataService, ProjectionDataService>();

        // Transient: a polling loop belongs to one page, and two pages on one circuit each need their
        // own timer and their own .NET object reference.
        services.TryAddTransient<StudioLiveUpdates>();

        return services;
    }

    private static readonly char[] InvalidDashboardPathChars = ['{', '}', '?', '#'];

    /// <summary>
    /// A Postgres role name as <c>SET LOCAL ROLE</c> will accept it. The console's role cannot be a
    /// parameter - it is an identifier - so it is held to the shape of an unquoted identifier here, at
    /// startup, rather than escaped later.
    /// </summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_$]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex PostgresRoleName();

    /// <summary>
    /// Validates that <see cref="MartenStudioOptions.Path"/> is a plain URL path: the value is
    /// concatenated into route templates (where <c>{</c>/<c>}</c> would be parsed as route parameters)
    /// and percent-encoded for client-side comparisons (where <c>?</c>/<c>#</c> and <c>.</c>/<c>..</c>
    /// segments would be truncated or collapsed, diverging from the server route).
    /// </summary>
    internal static bool IsRoutableDashboardPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            // the empty/whitespace case is reported by the "must start with '/'" validation
            return true;
        }

        string trimmed = path.Trim().Trim('/');
        if (trimmed.Length == 0)
        {
            // normalizes to the default "/marten"
            return true;
        }

        foreach (string segment in trimmed.Split('/'))
        {
            if (segment.Length == 0
                || segment == "."
                || segment == ".."
                || segment.IndexOfAny(InvalidDashboardPathChars) >= 0)
            {
                return false;
            }
        }

        return true;
    }
}
