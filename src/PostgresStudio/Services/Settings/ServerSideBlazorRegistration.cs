using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PostgresStudio.Services.Settings;

/// <summary>
/// Registers the framework's server-side Blazor for the studio's circuit without undoing what a host that already
/// registered it, and tuned it, set (issue #38).
/// </summary>
/// <remarks>
/// <para>
/// <c>AddRazorComponents().AddInteractiveServerComponents()</c> is not idempotent for options. It calls
/// <c>AddSignalR().AddHubOptions&lt;ComponentHub&gt;(...)</c>, and SignalR's <c>AddHubOptions&lt;THub&gt;</c> registers its
/// <c>HubOptionsSetup&lt;THub&gt;</c> with <c>AddSingleton</c>, not <c>TryAddEnumerable</c>: a second one is a second
/// <c>IConfigureOptions&lt;HubOptions&lt;ComponentHub&gt;&gt;</c>, and options are configured in registration order. The
/// second setup runs after the host's own <c>AddServerSideBlazor().AddHubOptions(...)</c> and copies the global
/// <c>HubOptions</c> over everything the host set - <c>ClientTimeoutInterval</c>, <c>KeepAliveInterval</c>,
/// <c>HandshakeTimeout</c>, <c>MaximumReceiveMessageSize</c> and the rest. Nothing fails; the host's circuits simply run on
/// the defaults. The same call's plain <c>Configure&lt;WebSocketOptions&gt;(...)</c> (SignalR resets
/// <c>KeepAliveInterval</c> to zero) and the hub-options lambda that selects the <c>blazorpack</c> protocol repeat the way.
/// </para>
/// <para>
/// So after the call this removes each options setup it added that the container already held, and only those: the
/// descriptors the call appended are told from the host's by reference, a repeat is a descriptor with the same service
/// type and either the same implementation type (<c>HubOptionsSetup&lt;ComponentHub&gt;</c>) or the same configuration
/// delegate (a <c>ConfigureNamedOptions&lt;T&gt;</c> over the same lambda), and a descriptor that cannot be proved a repeat
/// - a factory, a lambda of the host's own - stays. What is left is the order the host registered in, so its hub options
/// stay last whichever of the two registered first. A host that registers the studio first needs none of this: its own
/// registration then comes after the studio's and wins by order.
/// </para>
/// </remarks>
internal static class ServerSideBlazorRegistration
{
    /// <summary>Registers the interactive server components the studio renders with, leaving a host's options as they were.</summary>
    /// <param name="services">The application's service collection.</param>
    public static void Add(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        ServiceDescriptor[] before = [.. services];

        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        RemoveRepeatedOptionsSetups(services, before);
    }

    /// <summary>
    /// Removes every options setup in <paramref name="services" /> that is not in <paramref name="before" /> and repeats
    /// one that is.
    /// </summary>
    /// <param name="services">The collection, after the registration that may have repeated a setup.</param>
    /// <param name="before">The collection's descriptors from before it.</param>
    internal static void RemoveRepeatedOptionsSetups(IServiceCollection services, ServiceDescriptor[] before)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(before);

        // By reference: a descriptor is a class that does not override equality, and "added by the call" is exactly
        // "not one the collection held before it".
        HashSet<ServiceDescriptor> held = new(before, ReferenceEqualityComparer.Instance);

        for (int i = services.Count - 1; i >= 0; i--)
        {
            ServiceDescriptor added = services[i];

            if (held.Contains(added) || !IsOptionsSetup(added.ServiceType))
            {
                continue;
            }

            if (before.Any(existing => Repeats(existing, added)))
            {
                services.RemoveAt(i);
            }
        }
    }

    private static bool IsOptionsSetup(Type serviceType)
    {
        if (!serviceType.IsGenericType)
        {
            return false;
        }

        Type definition = serviceType.GetGenericTypeDefinition();
        return definition == typeof(IConfigureOptions<>) || definition == typeof(IPostConfigureOptions<>);
    }

    /// <summary>Whether <paramref name="added" /> would configure the same options in the same way as <paramref name="existing" />.</summary>
    private static bool Repeats(ServiceDescriptor existing, ServiceDescriptor added)
    {
        // Reading ImplementationType of a keyed descriptor throws, and no options setup is keyed.
        if (existing.IsKeyedService || added.IsKeyedService
            || existing.ServiceType != added.ServiceType
            || existing.Lifetime != added.Lifetime)
        {
            return false;
        }

        if (added.ImplementationType is { } implementation)
        {
            return implementation == existing.ImplementationType;
        }

        if (added.ImplementationInstance is { } instance && existing.ImplementationInstance is { } other)
        {
            return ReferenceEquals(instance, other) || ConfiguresTheSame(instance, other);
        }

        // A factory cannot be compared. Leaving it is the safe error: a repeat that stays is what the framework did anyway.
        return false;
    }

    /// <summary>
    /// Whether two <c>Configure*Options</c> instances (<c>Configure&lt;T&gt;(Action&lt;T&gt;)</c> registers a
    /// <c>ConfigureNamedOptions&lt;T&gt;</c>, <c>PostConfigure</c> a <c>PostConfigureOptions&lt;T&gt;</c>) run the same delegate under the
    /// same name. A non-capturing lambda is one delegate however often its registration runs, which is what a framework
    /// registration repeated by a second call looks like; a host's own lambda is a different one.
    /// </summary>
    private static bool ConfiguresTheSame(object instance, object other)
    {
        Type type = instance.GetType();

        if (type != other.GetType() || type.GetProperty("Action", BindingFlags.Instance | BindingFlags.Public) is not { } action)
        {
            return false;
        }

        if (action.GetValue(instance) is not Delegate configure || !configure.Equals(action.GetValue(other)))
        {
            return false;
        }

        PropertyInfo? name = type.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public);
        return Equals(name?.GetValue(instance), name?.GetValue(other));
    }
}
