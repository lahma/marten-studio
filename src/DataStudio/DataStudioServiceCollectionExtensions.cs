using DataStudio.Internal;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio;

/// <summary>
/// Registers the Data Studio shell.
/// </summary>
public static class DataStudioServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shell: its Blazor components, the mount, the layout, the Activity page and the startup guard that refuses an
    /// unauthorized mapping. Returns the builder every installed module adds itself to: <c>AddPostgres()</c>, <c>AddMarten()</c>, <c>AddKafka()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing here changes how the host behaves. No global JSON or SignalR option is touched, no middleware ordering is required, and
    /// every service is registered with <c>TryAdd</c> so an application that registered its own first is the one that answers. Nothing
    /// connects to anything: a module reads its database or its brokers when somebody opens one of its pages. Call <c>MapDataStudio()</c> on
    /// the built application to map the endpoints; an application that never maps it is the application it was.
    /// </para>
    /// <para>
    /// <b>One shell per application.</b> Calling this twice configures the options twice and returns a builder over the same collection.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the shell: the path it is served under, its title, who gets in, whether it is read-only.</param>
    /// <returns>The builder every module's <c>Add…()</c> call extends.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services" /> is null.</exception>
    public static DataStudioBuilder AddDataStudio(
        this IServiceCollection services,
        Action<DataStudioOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        ShellRegistration.Add(services);

        if (configure is not null)
        {
            services.AddOptions<DataStudioOptions>().Configure(configure);
        }
        else
        {
            services.AddOptions<DataStudioOptions>();
        }

        return new DataStudioBuilder(services);
    }
}
