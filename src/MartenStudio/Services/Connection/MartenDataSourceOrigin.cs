using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace MartenStudio.Services.Connection;

/// <summary>Where Marten's data source for a database came from.</summary>
internal enum MartenDataSourceOriginKind
{
    /// <summary>It is one of the <see cref="NpgsqlDataSource" /> instances the application put in its container, found by reference.</summary>
    Application,

    /// <summary>
    /// Marten did not build it: the application handed it over (<c>UseNpgsqlDataSource()</c> or <c>Connection(NpgsqlDataSource)</c>), but
    /// it is not an instance the studio can find in the container without building one, so it is not named further.
    /// </summary>
    HandedIn,

    /// <summary>Marten built it itself, from the connection string it was given (<c>Connection(string)</c>), so the pool is Marten's own.</summary>
    Marten,

    /// <summary>Marten's database exposes no data source, or no ownership, that the studio can judge by.</summary>
    Unknown,
}

/// <summary>
/// Whether Marten's data source for a database is the application's, and what else the container registers.
/// </summary>
/// <param name="Kind">The answer.</param>
/// <param name="ServiceKey">The service key the shared data source is registered under, bounded; <see langword="null" /> for an unkeyed one or one not found.</param>
/// <param name="Registered">How many singleton data sources the container registers (unkeyed counts one, and each service key one).</param>
/// <param name="MultiHost">Whether Marten's data source is an <see cref="NpgsqlMultiHostDataSource" />.</param>
/// <param name="UseCall">The call that points Marten at the registered data source: <c>UseNpgsqlDataSource()</c>, with the service key when the only registration is keyed.</param>
internal sealed record MartenDataSourceOrigin(MartenDataSourceOriginKind Kind, string? ServiceKey, int Registered, bool MultiHost, string UseCall = "UseNpgsqlDataSource()")
{
    /// <summary>
    /// Works out where <paramref name="martenDataSource" /> came from, without building anything.
    /// </summary>
    /// <param name="martenDataSource">Marten's data source for the database, or <see langword="null" />.</param>
    /// <param name="ownedByMarten">
    /// <c>PostgresqlDatabase.OwnsDataSource</c> (public in Weasel): <see langword="true" /> when Marten built the data source from a
    /// connection string, <see langword="false" /> when it was handed to Marten, <see langword="null" /> when the database does not say.
    /// </param>
    /// <param name="registrations">What the container's service descriptors say, read without resolving any service.</param>
    /// <remarks>
    /// <c>OwnsDataSource</c> is Marten's store-wide flag for the primary data source. A store that was handed one and also builds
    /// data sources for further databases from connection strings reads <see langword="false" /> for those too.
    /// </remarks>
    public static MartenDataSourceOrigin Of(NpgsqlDataSource? martenDataSource, bool? ownedByMarten, MartenDataSourceRegistrations registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        string useCall = UseCallFor(registrations);

        if (martenDataSource is null)
        {
            return new MartenDataSourceOrigin(MartenDataSourceOriginKind.Unknown, null, registrations.Count, false, useCall);
        }

        bool multiHost = martenDataSource is NpgsqlMultiHostDataSource;

        // Marten built it, so it is not any instance of the application's, and nothing needs comparing.
        if (ownedByMarten == true)
        {
            return new MartenDataSourceOrigin(MartenDataSourceOriginKind.Marten, null, registrations.Count, multiHost, useCall);
        }

        foreach ((object? key, NpgsqlDataSource instance) in registrations.Instances)
        {
            if (ReferenceEquals(instance, martenDataSource))
            {
                return new MartenDataSourceOrigin(MartenDataSourceOriginKind.Application, KeyText(key), registrations.Count, multiHost, useCall);
            }
        }

        return new MartenDataSourceOrigin(
            ownedByMarten == false ? MartenDataSourceOriginKind.HandedIn : MartenDataSourceOriginKind.Unknown,
            null,
            registrations.Count,
            multiHost,
            useCall);
    }

    /// <summary>
    /// The call that shares the registered data source: <c>UseNpgsqlDataSource()</c> when there is an unkeyed registration, with the
    /// key when the one registration is keyed by a short string, and with a placeholder when it is keyed otherwise or there are several.
    /// </summary>
    private static string UseCallFor(MartenDataSourceRegistrations registrations)
    {
        if (registrations.Keys.Count == 0 || registrations.Keys.Any(static x => !x.Keyed))
        {
            return "UseNpgsqlDataSource()";
        }

        if (registrations.Keys.Count == 1 && registrations.Keys[0].Key is string { Length: <= 64 } text && !text.Any(char.IsControl))
        {
            return "UseNpgsqlDataSource(\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\")";
        }

        return "UseNpgsqlDataSource(serviceKey)";
    }

    /// <summary>A service key as text: bounded, with control characters made harmless.</summary>
    internal static string? KeyText(object? key)
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

/// <summary>What the container's service descriptors say about <see cref="NpgsqlDataSource" />, read without building anything.</summary>
/// <param name="Instances">The registrations that carry an instance that already exists, each with its service key.</param>
/// <param name="Keys">The singleton registrations there are: the unkeyed one, if any, and each service key.</param>
internal sealed record MartenDataSourceRegistrations(IReadOnlyList<(object? Key, NpgsqlDataSource Instance)> Instances, IReadOnlyList<(bool Keyed, object? Key)> Keys)
{
    /// <summary>A container that registers none.</summary>
    public static MartenDataSourceRegistrations None { get; } = new([], []);

    /// <summary>How many singleton registrations there are (unkeyed counts one, and each service key one).</summary>
    public int Count => Keys.Count;
}

/// <summary>
/// The application's <see cref="NpgsqlDataSource" /> registrations, read from the service collection's descriptors and never from the
/// container.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is resolved, so nothing is built.</b> Resolving a registered data source that nothing has asked for yet builds one: a
/// pool the studio created, which registers a metrics reporter and which check C18 would then count. So the registry only reads
/// <see cref="ServiceDescriptor" />s: how many singletons are registered, and the instances registered as instances
/// (<c>AddSingleton(dataSource)</c>), which exist already. A data source registered by a factory (<c>AddNpgsqlDataSource()</c>) is
/// counted and never compared; the answer for it is Marten's own <c>OwnsDataSource</c>.
/// </para>
/// <para>
/// <b>Like <c>MartenStoreRegistry</c>, lazy.</b> It captures the <see cref="IServiceCollection" /> at <c>AddMartenStudio</c> time and
/// reads it when asked, because <c>AddMartenStudio()</c> is routinely called before <c>AddNpgsqlDataSource()</c>.
/// </para>
/// <para>
/// <b>Singletons only, and the last registration of each key.</b> The container resolves the last descriptor of a service type and
/// key; and only a singleton is the instance Marten can have been handed - a scoped or transient registration would be built anew,
/// which could never be Marten's.
/// </para>
/// </remarks>
internal sealed class MartenDataSourceRegistry
{
    private readonly IServiceCollection services;

    public MartenDataSourceRegistry(IServiceCollection services)
    {
        this.services = services;
    }

    /// <summary>Reads the descriptors.</summary>
    public MartenDataSourceRegistrations Read()
    {
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

        List<(object? Key, NpgsqlDataSource Instance)> instances = [];
        List<(bool Keyed, object? Key)> keys = [];

        foreach (ServiceDescriptor descriptor in last)
        {
            if (descriptor.Lifetime != ServiceLifetime.Singleton)
            {
                continue;
            }

            // NpgsqlDataSource and NpgsqlMultiHostDataSource under one key are one registration, as AddNpgsqlDataSource makes them.
            if (!keys.Any(x => x.Keyed == descriptor.IsKeyedService && Equals(x.Key, descriptor.ServiceKey)))
            {
                keys.Add((descriptor.IsKeyedService, descriptor.ServiceKey));
            }

            // ImplementationInstance throws on a keyed descriptor; both are an object that already exists, and neither is a factory.
            object? existing = descriptor.IsKeyedService ? descriptor.KeyedImplementationInstance : descriptor.ImplementationInstance;

            if (existing is NpgsqlDataSource dataSource && !instances.Any(x => ReferenceEquals(x.Instance, dataSource)))
            {
                instances.Add((descriptor.IsKeyedService ? descriptor.ServiceKey : null, dataSource));
            }
        }

        return new MartenDataSourceRegistrations(instances, keys);
    }
}
