using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace MartenStudio.Services.Connection;

/// <summary>Where Marten's data source for a database came from.</summary>
internal enum MartenDataSourceOriginKind
{
    /// <summary>It is an <see cref="NpgsqlDataSource" /> the application registered in its container (<c>UseNpgsqlDataSource()</c>, or <c>Connection(dataSource)</c>).</summary>
    Application,

    /// <summary>Marten built it itself, from the connection string it was given (<c>Connection(string)</c>), so the pool is Marten's own.</summary>
    Marten,

    /// <summary>Marten's database exposes no data source the studio can compare.</summary>
    Unknown,
}

/// <summary>
/// Whether Marten's data source for a database is one of the application's registered <see cref="NpgsqlDataSource" />s, and what
/// else the container holds.
/// </summary>
/// <param name="Kind">The answer.</param>
/// <param name="ServiceKey">The service key the shared data source is registered under, bounded; <see langword="null" /> for an unkeyed one.</param>
/// <param name="Registered">How many singleton data sources the container registers (unkeyed counts one, and each service key one).</param>
/// <param name="MultiHost">Whether Marten's data source is an <see cref="NpgsqlMultiHostDataSource" />.</param>
internal sealed record MartenDataSourceOrigin(MartenDataSourceOriginKind Kind, string? ServiceKey, int Registered, bool MultiHost)
{
    /// <summary>
    /// Compares <paramref name="martenDataSource" /> by reference with the container's registrations.
    /// </summary>
    /// <param name="martenDataSource">Marten's data source for the database, or <see langword="null" />.</param>
    /// <param name="registered">The container's singleton data sources, each with its service key.</param>
    public static MartenDataSourceOrigin Of(NpgsqlDataSource? martenDataSource, IReadOnlyList<(object? Key, NpgsqlDataSource DataSource)> registered)
    {
        ArgumentNullException.ThrowIfNull(registered);

        if (martenDataSource is null)
        {
            return new MartenDataSourceOrigin(MartenDataSourceOriginKind.Unknown, null, registered.Count, false);
        }

        foreach ((object? key, NpgsqlDataSource dataSource) in registered)
        {
            if (ReferenceEquals(dataSource, martenDataSource))
            {
                return new MartenDataSourceOrigin(MartenDataSourceOriginKind.Application, KeyText(key), registered.Count, martenDataSource is NpgsqlMultiHostDataSource);
            }
        }

        return new MartenDataSourceOrigin(MartenDataSourceOriginKind.Marten, null, registered.Count, martenDataSource is NpgsqlMultiHostDataSource);
    }

    private static string? KeyText(object? key)
    {
        if (key is null)
        {
            return null;
        }

        string text = Convert.ToString(key, CultureInfo.InvariantCulture) ?? key.GetType().Name;

        // A service key is the host's own word, but it is also arbitrary: bounded, and with control characters made harmless.
        text = new string([.. text.Select(static c => char.IsControl(c) ? ' ' : c)]);
        return text.Length > 64 ? text[..64] + "..." : text;
    }
}

/// <summary>
/// The application's <see cref="NpgsqlDataSource" /> registrations, found in the service collection the way PostgresStudio finds
/// its default one and resolved from the container - never built (<c>MartenStudio</c> builds no data source, and opens no
/// connection to compare).
/// </summary>
/// <remarks>
/// <para>
/// <b>Like <c>MartenStoreRegistry</c>, lazy.</b> It captures the <see cref="IServiceCollection" /> at <c>AddMartenStudio</c> time and
/// reads it when asked, because <c>AddMartenStudio()</c> is routinely called before <c>AddNpgsqlDataSource()</c>.
/// </para>
/// <para>
/// <b>Singletons only, and the last registration of each key.</b> The container resolves the last descriptor of a service type and
/// key; and only a singleton is the instance Marten can have been handed - a scoped or transient registration would be built anew
/// for this circuit, which could never be Marten's. Keyed registrations are included, because <c>UseNpgsqlDataSource(serviceKey)</c>
/// is how a host with several databases points Marten at one of them.
/// </para>
/// </remarks>
internal sealed class MartenDataSourceRegistry
{
    private readonly IServiceCollection services;

    public MartenDataSourceRegistry(IServiceCollection services)
    {
        this.services = services;
    }

    /// <summary>The container's singleton data sources, resolved from <paramref name="provider" />.</summary>
    /// <param name="provider">The container, or a scope of it: a singleton is the same instance from either.</param>
    public IReadOnlyList<(object? Key, NpgsqlDataSource DataSource)> Registered(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // The last descriptor of each (type, key) is the one the container resolves.
        List<ServiceDescriptor> last = [];

        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType != typeof(NpgsqlDataSource) && descriptor.ServiceType != typeof(NpgsqlMultiHostDataSource))
            {
                continue;
            }

            last.RemoveAll(x => x.ServiceType == descriptor.ServiceType && x.IsKeyedService == descriptor.IsKeyedService && Equals(x.ServiceKey, descriptor.ServiceKey));
            last.Add(descriptor);
        }

        List<(object? Key, NpgsqlDataSource DataSource)> found = [];

        foreach (ServiceDescriptor descriptor in last)
        {
            if (descriptor.Lifetime != ServiceLifetime.Singleton)
            {
                continue;
            }

            try
            {
                object? instance = descriptor.IsKeyedService
                    ? (provider as IKeyedServiceProvider)?.GetKeyedService(descriptor.ServiceType, descriptor.ServiceKey)
                    : provider.GetService(descriptor.ServiceType);

                if (instance is NpgsqlDataSource dataSource && !found.Any(x => ReferenceEquals(x.DataSource, dataSource)))
                {
                    found.Add((descriptor.IsKeyedService ? descriptor.ServiceKey : null, dataSource));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A registration that will not build is the host's problem; it is not the origin of anything Marten holds.
            }
        }

        return found;
    }
}
