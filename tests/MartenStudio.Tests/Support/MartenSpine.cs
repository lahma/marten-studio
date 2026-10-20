using MartenStudio.Components;
using MartenStudio.Services;
using MartenStudio.Services.Settings;
using MartenStudio.Services.Sources;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MartenStudio.Tests.Support;

/// <summary>
/// The shared studio's services in MartenStudio's terms, for a test that builds them by hand rather than through
/// <c>AddMartenStudio()</c> - so that the shared services' constructors, which take the studio's settings and
/// descriptor rather than <see cref="MartenStudioOptions" />, are spelled once.
/// </summary>
internal static class MartenSpine
{
    /// <summary>MartenStudio's descriptor.</summary>
    public static StudioDescriptor Descriptor => MartenStudioDescriptor.Instance;

    /// <summary>MartenStudio's route table.</summary>
    public static StudioRouteTable Routes => Descriptor.Routes;

    /// <summary>The settings over <paramref name="options" />, or over the defaults.</summary>
    public static MartenStudioSettings Settings(MartenStudioOptions? options = null) => MartenStudioSettings.Of(options ?? new MartenStudioOptions());

    /// <summary>The settings over <paramref name="options" />.</summary>
    public static MartenStudioSettings Settings(IOptions<MartenStudioOptions> options) => MartenStudioSettings.Of(options);

    /// <summary>The scope and write policies, over <paramref name="options" />.</summary>
    public static StudioAuthorization Authorization(
        IOptions<MartenStudioOptions> options,
        IAuthorizationService policies,
        AuthenticationStateProvider visitor) =>
        new(Settings(options), Descriptor, policies, visitor);

    /// <summary>The capability guard, over <paramref name="options" />.</summary>
    public static StudioCapabilityGuard Guard(IOptions<MartenStudioOptions> options) => new(Settings(options));

    /// <summary>The capability guard, over <paramref name="options" />.</summary>
    public static StudioCapabilityGuard Guard(MartenStudioOptions options) => new(Settings(options));

    /// <summary>A capability refusal worded as MartenStudio words it.</summary>
    public static StudioCapabilityDeniedException Denied(StudioCapability capability, CapabilityDenialReason reason) =>
        new(capability, reason, Settings());

    /// <summary>
    /// MartenStudio's source provider and the shared catalog and resolvers over it, built the way the container builds
    /// them for one circuit.
    /// </summary>
    public static (MartenStudioSourceProvider Sources, StudioScopeCatalog Catalog, StudioScopeResolver Resolver) Scope(
        IOptions<MartenStudioOptions> options,
        MartenStoreRegistry registry,
        StudioAuthorization authorization,
        TenantDiscovery tenants,
        IServiceProvider provider,
        StudioLogThrottle? throttle = null,
        ILoggerFactory? loggers = null)
    {
        loggers ??= NullLoggerFactory.Instance;

        MartenStudioSourceProvider sources = new(
            options,
            registry,
            tenants,
            provider,
            loggers.CreateLogger<MartenStudioSourceProvider>(),
            throttle);

        StudioScopeCatalog catalog = new(sources, authorization, loggers.CreateLogger<StudioScopeCatalog>(), throttle);
        StudioScopeResolver resolver = new(new StudioSourceResolver(sources, authorization, Descriptor));

        return (sources, catalog, resolver);
    }
}
