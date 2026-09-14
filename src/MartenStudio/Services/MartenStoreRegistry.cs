using System.Diagnostics.CodeAnalysis;
using System.Text;

using Marten;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MartenStudio.Services;

/// <summary>
/// One Marten store the application registered, as the studio names it.
/// </summary>
/// <param name="Key">
/// <c>default</c> for the host's <c>IDocumentStore</c>, or the marker interface's type name for a store
/// registered with <c>AddMartenStore</c>. This is what travels in the URL and in
/// <see cref="MartenStoreResource.StoreName" />.
/// </param>
/// <param name="DisplayName">What a person reads in the picker.</param>
/// <param name="ServiceType">The service type to resolve the store through.</param>
internal sealed record MartenStoreRegistration(string Key, string DisplayName, Type ServiceType);

/// <summary>
/// Whether a registered store could be built, and if not, why.
/// </summary>
/// <remarks>
/// A value rather than an exception, because "cannot report" is a state the UI has to draw differently
/// from "nothing there" (plan section 4.8): a store whose connection string is wrong is still a store the
/// application registered, and leaving it out of the picker makes it look as though it never was.
/// </remarks>
internal sealed record StoreAvailability(bool IsAvailable, string? Message, DateTimeOffset? At)
{
    /// <summary>The store resolved.</summary>
    public static StoreAvailability Available { get; } = new(true, null, null);

    /// <summary>The store did not resolve, and this is what it said.</summary>
    public static StoreAvailability Unavailable(string message, DateTimeOffset at) => new(false, message, at);
}

/// <summary>
/// The Marten stores this application registered, discovered from the service collection.
/// </summary>
/// <remarks>
/// <para>
/// Constructed with the <see cref="IServiceCollection" /> at <c>AddMartenStudio</c> time and scanned
/// <em>lazily</em> on first use, because <c>AddMartenStudio()</c> is routinely called before
/// <c>AddMarten()</c> and a scan at registration time would find nothing. By the time anything renders,
/// the collection is complete - and enumerating it after <c>MakeReadOnly()</c> is allowed, which is what
/// makes reading it from a built application safe.
/// </para>
/// <para>
/// The scan keys on descriptors whose <c>ServiceType</c> is assignable to <see cref="IDocumentStore" />.
/// That finds the default store (registered as <c>IDocumentStore</c>) and every ancillary one (registered
/// as its own marker interface, never as <c>IDocumentStore</c>) in one pass. Open generics are skipped:
/// they are Marten's own coordinator registrations, not stores.
/// </para>
/// <para>
/// Stores are resolved on demand rather than at scan time, and a failure is cached for ten seconds so a
/// page that lists five broken stores does not try to build each of them on every render.
/// </para>
/// </remarks>
internal sealed class MartenStoreRegistry
{
    /// <summary>The key of the host's own <c>IDocumentStore</c>.</summary>
    public const string DefaultStoreKey = "default";

    private static readonly TimeSpan FailureCacheWindow = TimeSpan.FromSeconds(10);

    private readonly IServiceCollection services;
    private readonly TimeProvider timeProvider;
    private readonly Lock gate = new();
    private readonly Dictionary<string, StoreAvailability> failures = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<MartenStoreRegistration>? scanned;

    public MartenStoreRegistry(IServiceCollection services) : this(services, TimeProvider.System)
    {
    }

    public MartenStoreRegistry(IServiceCollection services, TimeProvider timeProvider)
    {
        this.services = services;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Every registered store, the default one first. Scanned once, on first use.
    /// </summary>
    /// <param name="includeAncillaryStores">
    /// Whether stores registered with <c>AddMartenStore</c> are included - the resolved value of
    /// <see cref="MartenStudioOptions.IncludeAncillaryStores" />.
    /// </param>
    public IReadOnlyList<MartenStoreRegistration> Registrations(bool includeAncillaryStores)
    {
        IReadOnlyList<MartenStoreRegistration> all = scanned ?? Scan();
        if (includeAncillaryStores)
        {
            return all;
        }

        List<MartenStoreRegistration> defaults = [];
        foreach (MartenStoreRegistration registration in all)
        {
            if (registration.Key == DefaultStoreKey)
            {
                defaults.Add(registration);
            }
        }

        return defaults;
    }

    /// <summary>
    /// The registration with this key, or <see langword="null" /> when nothing was registered under it.
    /// </summary>
    public MartenStoreRegistration? Find(string? key, bool includeAncillaryStores)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        foreach (MartenStoreRegistration registration in Registrations(includeAncillaryStores))
        {
            if (string.Equals(registration.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return registration;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the store behind <paramref name="registration" />, or says why it could not be built.
    /// </summary>
    /// <remarks>
    /// A construction failure is cached for ten seconds and logged once per failure rather than once per
    /// render, so a misconfigured store costs a connection attempt every ten seconds rather than one per
    /// page load per circuit.
    /// </remarks>
    public StoreAvailability TryResolve(
        MartenStoreRegistration registration,
        IServiceProvider provider,
        [NotNullWhen(true)] out IDocumentStore? store)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(provider);

        store = null;

        lock (gate)
        {
            if (failures.TryGetValue(registration.Key, out StoreAvailability? cached)
                && cached.At is { } failedAt
                && timeProvider.GetUtcNow() - failedAt < FailureCacheWindow)
            {
                return cached;
            }
        }

        try
        {
            store = (IDocumentStore) provider.GetRequiredService(registration.ServiceType);

            lock (gate)
            {
                failures.Remove(registration.Key);
            }

            return StoreAvailability.Available;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StoreAvailability unavailable = StoreAvailability.Unavailable(exception.Message, timeProvider.GetUtcNow());

            lock (gate)
            {
                failures[registration.Key] = unavailable;
            }

            provider.GetService<ILoggerFactory>()?
                .CreateLogger<MartenStoreRegistry>()
                .StoreUnavailable(registration.Key, registration.ServiceType.FullName ?? registration.ServiceType.Name, exception.Message);

            return unavailable;
        }
    }

    private IReadOnlyList<MartenStoreRegistration> Scan()
    {
        lock (gate)
        {
            if (scanned is not null)
            {
                return scanned;
            }

            List<MartenStoreRegistration> found = [];
            HashSet<Type> seen = [];

            foreach (ServiceDescriptor descriptor in services)
            {
                Type serviceType = descriptor.ServiceType;

                // An open generic is never a store: Marten registers IProjectionCoordinator of T that way,
                // and IsAssignableFrom answers nonsense for one.
                if (serviceType.IsGenericTypeDefinition || serviceType.ContainsGenericParameters)
                {
                    continue;
                }

                if (!typeof(IDocumentStore).IsAssignableFrom(serviceType) || !seen.Add(serviceType))
                {
                    continue;
                }

                found.Add(serviceType == typeof(IDocumentStore)
                    ? new MartenStoreRegistration(DefaultStoreKey, "Default", serviceType)
                    : new MartenStoreRegistration(serviceType.Name, DisplayNameFor(serviceType), serviceType));
            }

            // The default store first, then the ancillary ones by display name, so the picker's order does
            // not depend on the order the application happened to register them in.
            found.Sort(static (left, right) =>
            {
                if (left.Key == DefaultStoreKey)
                {
                    return right.Key == DefaultStoreKey ? 0 : -1;
                }

                return right.Key == DefaultStoreKey
                    ? 1
                    : string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            scanned = found;
            return scanned;
        }
    }

    /// <summary>
    /// <c>IInvoicingStore</c> reads as "Invoicing Store": the interface's leading <c>I</c> goes and the
    /// PascalCase is split, because the type name is a C# convention and the picker is for a person.
    /// </summary>
    internal static string DisplayNameFor(Type serviceType)
    {
        string name = serviceType.Name;
        if (name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]))
        {
            name = name[1..];
        }

        StringBuilder display = new(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char current = name[i];
            bool boundary = i > 0
                && char.IsUpper(current)
                && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])));

            if (boundary)
            {
                display.Append(' ');
            }

            display.Append(current);
        }

        return display.ToString();
    }
}

/// <summary>
/// Reads <see cref="MartenStudioOptions.IncludeAncillaryStores" /> so callers do not each have to.
/// </summary>
internal static class MartenStoreRegistryExtensions
{
    /// <inheritdoc cref="MartenStoreRegistry.Registrations" />
    public static IReadOnlyList<MartenStoreRegistration> Registrations(
        this MartenStoreRegistry registry,
        IOptions<MartenStudioOptions> options) =>
        registry.Registrations(options.Value.IncludeAncillaryStores);

    /// <inheritdoc cref="MartenStoreRegistry.Find" />
    public static MartenStoreRegistration? Find(
        this MartenStoreRegistry registry,
        string? key,
        IOptions<MartenStudioOptions> options) =>
        registry.Find(key, options.Value.IncludeAncillaryStores);
}
