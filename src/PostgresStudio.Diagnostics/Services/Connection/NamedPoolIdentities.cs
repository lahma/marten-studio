using System.Reflection;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Connection;

/// <summary>
/// Which database and login the pools a host <em>named</em> connect to (C18, issue #56): the map from a data source's
/// <c>Name</c> - what Npgsql's meter reports its pool under, instead of the connection string - to the identity of its
/// connection string, built from the data sources the studio already resolves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which data sources.</b> The one the sweep is about (<see cref="IStudioDatabase.DataSource" />), then whatever the studio's
/// source provider holds (<see cref="IStudioSourceProvider.HeldDataSourcesAsync" />): PostgresStudio's registered and
/// <c>AddDataSource</c> sources, a host's database sources, and MartenStudio's stores' databases. No new scan of the
/// container: a data source the studio does not resolve stays unidentified, and C18 says so.
/// </para>
/// <para>
/// <b>Asked only when needed, and stops early.</b> The caller names the pool names it could not parse
/// (<c>wanted</c>); nothing is read when there are none, and the enumeration stops once every one is
/// identified or <see cref="MaxExamined" /> data sources have been looked at, so a host database source that lists thousands of
/// names is not opened for the sake of one.
/// </para>
/// <para>
/// <b>D33 holds.</b> The map is a local of the caller: names and connection strings go in, only identities are compared, and
/// the result is counted into <see cref="ConnectionPoolSiblings" /> and dropped. Nothing here logs, throws a name or keeps
/// one.
/// </para>
/// <para>
/// <b>The name is read by reflection.</b> <c>NpgsqlDataSource.Name</c> is <c>internal</c> in Npgsql 9.0.4 and 10.0.3
/// (<c>Name = name ?? ConnectionString</c>), and the public <c>NpgsqlDataSourceBuilder.Name</c> sets it. A property that is
/// gone, or not a string, reads as "no name", so every named pool stays unidentified - the safe answer, said by C18 - and
/// <c>NamedPoolIdentitiesTests</c> pins the property at the restored Npgsql so that a rename fails a build rather than
/// quietly decaying.
/// </para>
/// </remarks>
internal static class NamedPoolIdentities
{
    /// <summary>The most data sources one read looks at.</summary>
    internal const int MaxExamined = 256;

    private static readonly PropertyInfo? NameProperty = FindNameProperty();

    /// <summary>
    /// The name Npgsql reports <paramref name="dataSource" />'s pool under, or <see langword="null" /> when it cannot be read.
    /// </summary>
    /// <param name="dataSource">A data source.</param>
    internal static string? NameOf(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        try
        {
            return NameProperty?.GetValue(dataSource) as string;
        }
        catch (Exception exception) when (exception is TargetInvocationException or MethodAccessException or InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Whether the restored Npgsql has the property <see cref="NameOf" /> reads - for the test that pins it.</summary>
    internal static bool CanReadNames => NameProperty is not null;

    /// <summary>
    /// The identity (<see cref="ConnectionPoolSiblings.IdentityOf" />) of each data source whose name is in
    /// <paramref name="wanted" />, by that name. A name is read from the first data source that holds it, in the order the
    /// studio lists them: two data sources the host gave one name are one tally in Npgsql's meter, and the host chose that.
    /// </summary>
    /// <param name="context">The sweep's context: the database it is about, and its scope for the source provider.</param>
    /// <param name="wanted">The pool names that did not parse as connection strings.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal static async Task<IReadOnlyDictionary<string, string>> ReadAsync(
        HealthCheckContext context,
        IReadOnlySet<string> wanted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(wanted);

        Dictionary<string, string> found = new(StringComparer.Ordinal);

        void Take(NpgsqlDataSource? dataSource)
        {
            if (dataSource is not null
                && NameOf(dataSource) is { } name
                && wanted.Contains(name)
                && !found.ContainsKey(name)
                && ConnectionPoolSiblings.IdentityOf(dataSource.ConnectionString) is { } identity)
            {
                found[name] = identity;
            }
        }

        Take(context.Target.DataSource);

        if (found.Count < wanted.Count && context.Services.GetService<IStudioSourceProvider>() is { } provider)
        {
            try
            {
                int examined = 0;

                await foreach (NpgsqlDataSource dataSource in provider.HeldDataSourcesAsync(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    Take(dataSource);

                    if (++examined >= MaxExamined || found.Count >= wanted.Count)
                    {
                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A source that would not answer (the providers have logged why, throttled): what was identified stands, and the
                // rest stays unidentified, which C18 says. Nothing of the exception is kept - it may carry anything.
            }
        }

        return found;
    }

    private static PropertyInfo? FindNameProperty()
    {
        PropertyInfo? property = typeof(NpgsqlDataSource).GetProperty(
            "Name",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        return property is { PropertyType: var type, CanRead: true } && type == typeof(string) ? property : null;
    }
}
