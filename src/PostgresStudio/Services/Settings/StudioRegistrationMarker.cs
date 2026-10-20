using Microsoft.Extensions.DependencyInjection;

namespace PostgresStudio.Services.Settings;

/// <summary>
/// Which studio this process registered: one per process (design PS8, option a).
/// </summary>
/// <remarks>
/// <para>
/// Every shared service is a single registration - one settings object, one descriptor, one source provider, one
/// sweep cache - so a second studio in the same container would silently share them with the first and answer for
/// the wrong one. A studio's <c>Add*Studio()</c> calls <see cref="Claim" /> first: the same studio twice is allowed,
/// because <c>AddMartenStudio()</c> has always been safe to call twice (every registration is a <c>TryAdd</c> and the
/// options configure twice), and a different one throws with the reason.
/// </para>
/// <para>
/// The seams that would let two studios live side by side - settings, descriptor and source provider from DI, cache
/// keys qualified by source - exist, so lifting this is one packet's work, not a redesign.
/// </para>
/// </remarks>
/// <param name="Key">The studio's descriptor key: <c>marten</c>.</param>
/// <param name="ProductName">The product's name, for the refusal.</param>
/// <param name="RegisteredBy">The method that registered it, for the refusal: <c>AddMartenStudio()</c>.</param>
internal sealed record StudioRegistrationMarker(string Key, string ProductName, string RegisteredBy)
{
    /// <summary>
    /// Registers <paramref name="marker" />, or does nothing when the same studio is already registered.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="marker">The studio being registered.</param>
    /// <exception cref="InvalidOperationException">Another studio is already registered in this container.</exception>
    public static void Claim(IServiceCollection services, StudioRegistrationMarker marker)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(marker);

        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType == typeof(StudioRegistrationMarker)
                && !descriptor.IsKeyedService
                && descriptor.ImplementationInstance is StudioRegistrationMarker existing)
            {
                if (string.Equals(existing.Key, marker.Key, StringComparison.Ordinal))
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"{marker.RegisteredBy} was called in an application that already registered {existing.ProductName} with "
                    + $"{existing.RegisteredBy}. One studio per application: {existing.ProductName} and {marker.ProductName} share "
                    + "their services, so a second one would answer for the first. Register one of them.");
            }
        }

        services.AddSingleton(marker);
    }
}
