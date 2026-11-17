using DataStudio.Components;
using DataStudio.Marten.Services;
using DataStudio.Services;
using DataStudio.Services.Settings;
using DataStudio.Postgres.Services.Settings;
using DataStudio.Postgres.Services.Sources;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using DataStudio.Postgres.Services;

namespace DataStudio.Marten.Tests.Support;

/// <summary>
/// The shared studio's services in MartenStudio's terms, for a test that builds them by hand rather than through
/// <c>AddMarten()</c> - so that the shared services' constructors, which take the studio's settings and
/// descriptor rather than <see cref="MartenOptions" />, are spelled once.
/// </summary>
internal static class MartenSpine
{
    /// <summary>MartenStudio's descriptor.</summary>
    public static StudioDescriptor Descriptor => MartenDescriptor.Instance;

    /// <summary>MartenStudio's route table.</summary>
    public static StudioRouteTable Routes => RouteTable.Value;

    private static readonly Lazy<StudioRouteTable> RouteTable = new(static () => new StudioRouteTable([.. Descriptor.PageAssemblies, StudioShellDescriptor.ShellPages]));

    /// <summary>The settings over <paramref name="options" />, or over the defaults.</summary>
    public static MartenSettings Settings(MartenOptions? options = null) => MartenSettings.Of(options ?? new MartenOptions());

    /// <summary>The settings over <paramref name="options" />.</summary>
    public static MartenSettings Settings(IOptions<MartenOptions> options) => MartenSettings.Of(options);

    /// <summary>The scope and write policies, over <paramref name="options" />.</summary>
    public static StudioAuthorization Authorization(
        IOptions<MartenOptions> options,
        IAuthorizationService policies,
        AuthenticationStateProvider visitor) =>
        new(Settings(options), Descriptor, policies, visitor);

    /// <summary>The capability guard, over <paramref name="options" />.</summary>
    public static StudioCapabilityGuard Guard(IOptions<MartenOptions> options) => new(Settings(options));

    /// <summary>The capability guard, over <paramref name="options" />.</summary>
    public static StudioCapabilityGuard Guard(MartenOptions options) => new(Settings(options));

    /// <summary>A capability refusal worded as MartenStudio words it.</summary>
    public static StudioCapabilityDeniedException Denied(StudioCapability capability, CapabilityDenialReason reason) =>
        new(capability, reason, Settings());

    /// <summary>
    /// MartenStudio's source provider and the shared catalog and resolvers over it, built the way the container builds
    /// them for one circuit.
    /// </summary>
    public static (MartenSourceProvider Sources, StudioScopeCatalog Catalog, StudioScopeResolver Resolver) Scope(
        IOptions<MartenOptions> options,
        MartenStoreRegistry registry,
        StudioAuthorization authorization,
        TenantDiscovery tenants,
        IServiceProvider provider,
        StudioLogThrottle? throttle = null,
        ILoggerFactory? loggers = null)
    {
        loggers ??= NullLoggerFactory.Instance;

        MartenSourceProvider sources = new(
            options,
            registry,
            tenants,
            provider,
            loggers.CreateLogger<MartenSourceProvider>(),
            throttle);

        StudioScopeCatalog catalog = new(sources, authorization, loggers.CreateLogger<StudioScopeCatalog>(), throttle);
        StudioScopeResolver resolver = new(new StudioSourceResolver(sources, authorization, Descriptor));

        return (sources, catalog, resolver);
    }
}
