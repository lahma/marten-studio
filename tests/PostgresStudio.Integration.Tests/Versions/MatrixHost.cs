using System.Security.Claims;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Versions;

/// <summary>
/// A PostgresStudio host over one matrix server, as one role, in one database: the studio's own services, resolved the way
/// a visitor's would be, so a matrix read runs exactly the code a page runs.
/// </summary>
/// <remarks>
/// No Marten anywhere: the host registers the role's <see cref="NpgsqlDataSource" /> unkeyed, which PostgresStudio
/// discovers on its own, and admits <see cref="MatrixReads.Schemas" /> with every capability on - so the folder can move to
/// <c>tests/PostgresStudio.Integration.Tests</c> as it is (PS-5).
/// </remarks>
internal sealed class MatrixHost : IAsyncDisposable
{
    private readonly ServiceProvider services;

    private MatrixHost(MatrixServer server, MatrixRole role, string database, ServiceProvider services, ResolvedSource resolved)
    {
        Server = server;
        Role = role;
        Database = database;
        this.services = services;
        Resolved = resolved;
    }

    /// <summary>The server.</summary>
    public MatrixServer Server { get; }

    /// <summary>The role the studio connects as.</summary>
    public MatrixRole Role { get; }

    /// <summary>The database it reads.</summary>
    public string Database { get; }

    /// <summary>The host's container.</summary>
    public IServiceProvider Services => services;

    /// <summary>The source, resolved with no tenant.</summary>
    public ResolvedSource Resolved { get; }

    /// <summary>The host's sweeper.</summary>
    public HealthSweeper Sweeper => services.GetRequiredService<HealthSweeper>();

    /// <summary>Builds the host and resolves its one source.</summary>
    /// <param name="server">The server.</param>
    /// <param name="role">The role the studio connects as.</param>
    /// <param name="database">The database it reads.</param>
    /// <param name="services">
    /// Registrations made before <c>AddPostgresStudio()</c>, whose <c>TryAdd</c> then keeps them - a
    /// <c>HealthCheckRegistry.ForTypes(...)</c> of a test's own checks, say.
    /// </param>
    /// <param name="options">
    /// Changes to the studio's options after the matrix's own (<see cref="MatrixReads.Schemas" />, every capability) - a
    /// <c>SqlConsoleRole</c>, say.
    /// </param>
    /// <remarks>
    /// <b>A pool of the host's own.</b> The data source's connection string carries an <c>Application Name</c> no other pool
    /// in the process has, and <see cref="RunAsync" /> reads through that same data source: C01 matches the store's pool by
    /// its connection string, and Npgsql reports every pool of one connection string under one name, so a second pool of
    /// the same string - another class's host of the same role, or a connection opened beside the data source - made C01
    /// "not checked" on every leg.
    /// </remarks>
    public static async Task<MatrixHost> CreateAsync(
        MatrixServer server,
        MatrixRole role,
        string database = MatrixServer.Database,
        Action<IServiceCollection>? services = null,
        Action<PostgresStudioOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(server);

        NpgsqlConnectionStringBuilder own = new(server.ConnectionString(role, database))
        {
            ApplicationName = "ms-matrix-host-" + Guid.NewGuid().ToString("N")[..12],
        };

        ServiceCollection collection = new();
        services?.Invoke(collection);
        collection.AddLogging();
        collection.AddAuthorization();
        collection.AddSingleton<AuthenticationStateProvider, Anonymous>();
        collection.AddSingleton(NpgsqlDataSource.Create(own.ConnectionString));
        collection.AddPostgresStudio(studio =>
        {
            studio.BrowsableSchemas.Clear();

            foreach (string schema in MatrixReads.Schemas)
            {
                studio.BrowsableSchemas.Add(schema);
            }

            studio.Capabilities = PostgresStudioCapabilities.All();
            options?.Invoke(studio);
        });

        ServiceProvider provider = collection.BuildServiceProvider();

        try
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            ResolvedSource resolved = await scope.ServiceProvider.GetRequiredService<StudioSourceResolver>()
                .ResolveAsync(new StudioScope("default", "default", null), null, TestContext.Current.CancellationToken);

            // As a running host's would, the pool exists before the first sweep: C01 runs before any catalog check opens it.
            NpgsqlConnection warm = await provider.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(TestContext.Current.CancellationToken);
            await warm.DisposeAsync();

            return new MatrixHost(server, role, database, provider, resolved);
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    /// <summary>One sweep of <paramref name="kind" />, under <paramref name="lockTimeout" /> when it is given.</summary>
    public Task<HealthSweep> SweepAsync(HealthSweepKind kind, TimeSpan? lockTimeout = null)
    {
        HealthSweeper sweeper = Sweeper;
        sweeper.LockTimeout = lockTimeout;
        return sweeper.SweepAsync(Resolved, kind, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A check context over this host's source, for a test that calls a gatherer's shared read itself
    /// (<c>PostgresServerFacts.ReadAsync</c>, <c>PostgresGrants.ReadAsync</c>); dispose the connection after.
    /// </summary>
    public (HealthCheckContext Context, HealthConnection Connection) Context()
    {
        HealthConnection connection = new(Resolved.Target);
        HealthCheckContext context = new(
            Resolved,
            services.GetRequiredService<IStudioSettings>(),
            NoStudioRuntime.Instance,
            services,
            new HealthSqlRunner(connection, new ReadOnlySqlOptions(), observer: null),
            DateTimeOffset.UtcNow,
            new HealthSweepMemo(),
            TestContext.Current.CancellationToken);

        return (context, connection);
    }

    /// <summary>
    /// Runs <paramref name="read" /> as this host's role in a read-only transaction with <c>search_path</c> pinned, under
    /// <paramref name="lockTimeout" />, and returns what it threw - or <see langword="null" />.
    /// </summary>
    public async Task<Exception?> RunAsync(MatrixRead read, TimeSpan lockTimeout)
    {
        ArgumentNullException.ThrowIfNull(read);

        const int Timeout = 30;
        CancellationToken token = TestContext.Current.CancellationToken;

        await using NpgsqlConnection connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(token);
        ReadOnlySqlSession session = new(new ReadOnlySqlOptions { StatementTimeout = TimeSpan.FromSeconds(Timeout), LockTimeout = lockTimeout });

        try
        {
            await session.InTransactionAsync(
                connection,
                async (transaction, cancellation) =>
                {
                    await DatabaseCatalogQueries.PinSearchPathAsync(connection, transaction, Timeout, cancellation);
                    await read.RunAsync(new MatrixReadContext(connection, transaction, MatrixReads.Schemas, Timeout, services, Resolved), cancellation);
                    return 0;
                },
                token);

            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            return exception;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (NpgsqlDataSource dataSource in services.GetServices<NpgsqlDataSource>())
        {
            await dataSource.DisposeAsync();
        }

        await services.DisposeAsync();
    }

    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
