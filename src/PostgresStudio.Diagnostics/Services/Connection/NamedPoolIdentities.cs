using System.Reflection;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Connection;

/// <summary>
/// What <see cref="NamedPoolIdentities.ReadAsync" /> found: the identity of each named pool it could identify, by the pool's name,
/// and whether it stopped before it had looked everywhere. Local to the reader that asked: the names are never kept past it (D33).
/// </summary>
/// <param name="Identities">The identity of each data source whose name was asked for, by that name.</param>
/// <param name="CutOff">Whether the walk ended on its time bound or its attempt cap with names still unidentified.</param>
internal sealed record NamedPoolIdentification(IReadOnlyDictionary<string, string> Identities, bool CutOff);

/// <summary>
/// Which database and login the pools a host <em>named</em> connect to (C18, issue #56): the map from a data source's
/// <c>Name</c> - what Npgsql's meter reports its pool under, instead of the connection string - to the identity of its
/// connection string, built from the data sources the studio already resolves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which data sources.</b> The one the sweep is about (<see cref="IStudioDatabase.DataSource" />), then whatever the studio's
/// source provider holds (<see cref="IStudioSourceProvider.HeldDataSourcesAsync" />): PostgresStudio's registered and
/// <c>AddDataSource</c> sources, and MartenStudio's stores' databases. <b>Never a host's database source</b>
/// (<c>IPostgresDatabaseSource</c>): its contract is that the studio asks it for a data source only for a database somebody opens
/// or a sweep reaches, and a host that builds lazily would have every tenant's built by the first sweep. No new scan of the
/// container: a data source the studio does not resolve stays unidentified, and C18 says so.
/// </para>
/// <para>
/// <b>Asked only when needed, bounded, and never able to take the connection checks down.</b> The caller names the pool names it
/// could not parse (<c>wanted</c>); nothing is read when there are none. The walk stops once every one is identified, after
/// <see cref="MaxExamined" /> data sources, or when <c>QueryTimeout</c> has passed - then the rest are reported as cut off, not
/// judged. It runs in a scope of its own, because the provider is scoped and the Connection page's reader is a singleton over the
/// root provider. Whatever a source throws is contained here: this read feeds C16, C17, C18, S05, S11 to S13 and the page.
/// </para>
/// <para>
/// <b>D33 holds.</b> The map is a local of the caller: names and connection strings go in, only identities are compared, and
/// the result is counted into <see cref="ConnectionPoolSiblings" /> and dropped. Nothing here logs, throws a name or keeps
/// one.
/// </para>
/// <para>
/// <b>The name is read by reflection.</b> <c>NpgsqlDataSource.Name</c> is <c>internal</c> in Npgsql 9.0.4 and 10.0.3
/// (<c>Name = name ?? ConnectionString</c>), and the public <c>NpgsqlDataSourceBuilder.Name</c> sets it. A property that is
/// gone, ambiguous or not a string reads as "no name", so every named pool stays unidentified - the safe answer, said by C18 - and
/// <c>NamedPoolSiblingsTests</c> pins the property at the restored Npgsql, so that a rename fails a test rather than quietly
/// decaying.
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
    /// The identity (<see cref="ConnectionPoolSiblings.IdentityOf(string?)" />) of each data source whose name is in
    /// <paramref name="wanted" />, by that name. A name is read from the first data source that holds it, in the order the
    /// studio lists them. Never throws but for the caller's own cancellation.
    /// </summary>
    /// <param name="context">The sweep's context: the database it is about, and its services for the source provider.</param>
    /// <param name="wanted">The pool names that did not parse as connection strings.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal static async Task<NamedPoolIdentification> ReadAsync(
        HealthCheckContext context,
        IReadOnlySet<string> wanted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(wanted);

        Dictionary<string, string> found = new(StringComparer.Ordinal);
        bool cutOff = false;

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

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            budget.CancelAfter(context.Settings.QueryTimeout);

            Take(context.Target.DataSource);

            // The provider is scoped, and this read may be over the root provider (the Connection page's is): a scope of its own.
            if (found.Count < wanted.Count && context.Services.GetService<IServiceScopeFactory>() is { } factory)
            {
                await using AsyncServiceScope scope = factory.CreateAsyncScope();

                if (scope.ServiceProvider.GetService<IStudioSourceProvider>() is { } provider)
                {
                    int attempts = 0;

                    await foreach (NpgsqlDataSource dataSource in provider.HeldDataSourcesAsync(budget.Token).WithCancellation(budget.Token).ConfigureAwait(false))
                    {
                        Take(dataSource);

                        if (found.Count >= wanted.Count)
                        {
                            break;
                        }

                        if (++attempts >= MaxExamined)
                        {
                            cutOff = true;
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The time bound ran out, or a source would not answer (the providers have logged why, throttled): what was identified
            // stands, and the rest is not judged. Nothing of the exception is kept - it may carry anything.
            cutOff = found.Count < wanted.Count && budget.IsCancellationRequested;
        }

        return new NamedPoolIdentification(found, cutOff && found.Count < wanted.Count);
    }

    private static PropertyInfo? FindNameProperty()
    {
        try
        {
            PropertyInfo? property = typeof(NpgsqlDataSource).GetProperty(
                "Name",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            return property is { PropertyType: var type, CanRead: true } && type == typeof(string) ? property : null;
        }
        catch (AmbiguousMatchException)
        {
            return null;
        }
    }
}
