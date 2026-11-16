using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Postgres.Services.Settings;

/// <summary>
/// That the diagnostics engine is registered in this container, and by whom: <c>AddPostgresDiagnostics()</c> alone, or a
/// database module's <c>AddPostgres()</c> or <c>AddMarten()</c>, which registers it too.
/// </summary>
/// <remarks>
/// One registration of the engine per container: its services are single registrations (the sweep cache, the history,
/// the source registry), so a database module added beside <c>AddPostgresDiagnostics()</c> would share them with a configuration
/// that is not its own. The same caller twice is allowed, as <c>AddPostgres()</c> twice always has been; the other
/// kind is refused with the reason.
/// </remarks>
/// <param name="RegisteredBy">The method that registered it, for the refusal: <c>AddPostgresDiagnostics()</c>.</param>
/// <param name="ByStudio">Whether a database module registered it.</param>
internal sealed record DiagnosticsRegistrationMarker(string RegisteredBy, bool ByStudio)
{
    /// <summary>
    /// Registers the marker, or does nothing when the same kind of registration is already there.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="registeredBy">The method registering, as the refusal names it.</param>
    /// <param name="byStudio">Whether it is a database module's.</param>
    /// <exception cref="InvalidOperationException">The other kind of registration is already in this container.</exception>
    public static void Claim(IServiceCollection services, string registeredBy, bool byStudio)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(registeredBy);

        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType == typeof(DiagnosticsRegistrationMarker)
                && !descriptor.IsKeyedService
                && descriptor.ImplementationInstance is DiagnosticsRegistrationMarker existing)
            {
                if (existing.ByStudio == byStudio)
                {
                    return;
                }

                string module = byStudio ? registeredBy : existing.RegisteredBy;
                string diagnostics = byStudio ? existing.RegisteredBy : registeredBy;

                throw new InvalidOperationException(
                    $"{registeredBy} was called in an application that already called {existing.RegisteredBy}. "
                    + $"The database module ({module}) already registers the diagnostics engine, so {diagnostics} would register it twice "
                    + "with a configuration of its own. Remove AddPostgresDiagnostics() and configure the engine's options on the module's.");
            }
        }

        services.AddSingleton(new DiagnosticsRegistrationMarker(registeredBy, byStudio));
    }
}
