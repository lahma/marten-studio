using System.Security.Claims;

using JasperFx;
using JasperFx.Events.Daemon;

using Marten;
using Marten.Storage;

using MartenStudio.SampleDomain;
using MartenStudio.Services;
using MartenStudio.Services.Live;
using MartenStudio.Services.Projections;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// A real host with a real daemon, for one test class.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of this fixture is that nothing is faked on the way to the daemon. The coordinator is
/// registered the way an application registers it - <c>AddMarten(...).AddAsyncDaemon(DaemonMode.Solo)</c>
/// on a real host - and <see cref="DaemonAccessor" /> then finds it exactly as it will in production. A
/// hand-built daemon would be the one thing the studio must never do (AGENTS.md hard rule 11), so a test
/// that built one would be testing something the product cannot do.
/// </para>
/// <para>
/// Everything the tests read goes <em>through</em> <see cref="IProjectionDataService" />, so the scope
/// resolution, the capability gating and the audit trail are exercised rather than bypassed.
/// </para>
/// <para>
/// One schema pair per test class, dropped and recreated on the way in, so a reused container carrying
/// yesterday's tables still gives every run a clean one.
/// </para>
/// </remarks>
internal sealed class ProjectionsFixture : IAsyncDisposable
{
    private readonly IHost host;

    private ProjectionsFixture(IHost host, string schema, IDocumentStore store, IMartenDatabase database)
    {
        this.host = host;
        Schema = schema;
        Store = store;
        Database = database;
        Scope = new StudioScope(MartenStoreRegistry.DefaultStoreKey, database.Id.Identity, null);
    }

    /// <summary>The document schema this class owns.</summary>
    public string Schema { get; }

    /// <summary>The store the host configured.</summary>
    public IDocumentStore Store { get; }

    /// <summary>Its one database.</summary>
    public IMartenDatabase Database { get; }

    /// <summary>The scope every call is made for.</summary>
    public StudioScope Scope { get; }

    /// <summary>The in-process shard tracker's latest states, as the studio holds them.</summary>
    public StudioLiveState LiveState => host.Services.GetRequiredService<StudioLiveState>();

    /// <summary>The detached operations the studio started.</summary>
    public StudioOperationTracker Operations => host.Services.GetRequiredService<StudioOperationTracker>();

    /// <summary>The process's audit ring.</summary>
    public StudioActionLogService ActionLog => host.Services.GetRequiredService<StudioActionLogService>();

    /// <summary>
    /// Builds the host, creates the schemas, seeds the demo's event streams and starts the daemon.
    /// </summary>
    /// <param name="postgres">The shared container.</param>
    /// <param name="schema">The schema prefix, normally the test class's name.</param>
    /// <param name="withDaemon">
    /// Whether to call <c>AddAsyncDaemon</c>. <see langword="false" /> is the deployment where the daemon
    /// runs somewhere else, which the studio has to render honestly rather than treat as a fault.
    /// </param>
    /// <param name="capabilities">The capabilities the host granted. Defaults to all of them.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    public static async Task<ProjectionsFixture> StartAsync(
        PostgresFixture postgres,
        string schema,
        bool withDaemon = true,
        MartenStudioCapabilities? capabilities = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        string eventSchema = schema + "_events";

        await postgres.CreateSchemaAsync(schema, cancellationToken);
        await postgres.CreateSchemaAsync(eventSchema, cancellationToken);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        MartenServiceCollectionExtensions.MartenConfigurationExpression marten = builder.Services
            .AddMarten(options =>
            {
                // The sample domain's own configuration, which is where the projections and their
                // lifecycles come from - then re-schemad so that each test class owns its tables.
                SampleStore.Configure(options, postgres.ConnectionString);
                options.DatabaseSchemaName = schema;
                options.Events.DatabaseSchemaName = eventSchema;
                options.AutoCreateSchemaObjects = AutoCreate.All;
            })
            .UseLightweightSessions();

        if (withDaemon)
        {
            marten.AddAsyncDaemon(DaemonMode.Solo);
        }

        // What a Blazor circuit supplies and a generic host does not: the studio asks both of these on
        // every data call.
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<AuthenticationStateProvider, AnonymousAuthenticationStateProvider>();

        builder.Services.AddMartenStudio(options => options.Capabilities = capabilities ?? MartenStudioCapabilities.All());

        IHost host = builder.Build();

        var store = host.Services.GetRequiredService<IDocumentStore>();

        // Everything before the daemon starts, which is the order a real host does it in: IInitialData
        // runs first, so the projections screen opens on a daemon with a backlog.
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await SampleDataSeeder.SeedEventsAsync(store, cancellationToken);

        await host.StartAsync(cancellationToken);

        if (withDaemon)
        {
            // Starting the host only *asks* the coordinator to start: it takes its advisory lock and
            // brings its agents up on its own thread. Every test here is about a daemon that is running,
            // so the fixture waits for that rather than making each test wait for it.
            var coordinator = host.Services.GetRequiredService<Marten.Events.Daemon.Coordination.IProjectionCoordinator>();

            await WaitForAsync(
                () => Task.FromResult(coordinator.DaemonForMainDatabase().IsRunning),
                "the async daemon to start",
                TimeSpan.FromSeconds(60),
                cancellationToken);
        }

        IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases();

        return new ProjectionsFixture(host, schema, store, databases[0]);
    }

    /// <summary>
    /// A dependency-injection scope, because the data service is scoped exactly as it is on a circuit.
    /// </summary>
    public IServiceScope CreateScope() => host.Services.CreateScope();

    /// <summary>The data service, in a scope the caller disposes.</summary>
    public static IProjectionDataService ServiceIn(IServiceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.ServiceProvider.GetRequiredService<IProjectionDataService>();
    }

    /// <summary>Runs one call against a fresh scope, the way one page load would.</summary>
    public async Task<T> UseAsync<T>(Func<IProjectionDataService, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        using IServiceScope scope = CreateScope();
        return await action(ServiceIn(scope));
    }

    /// <summary>Runs one call against a fresh scope.</summary>
    public async Task UseAsync(Func<IProjectionDataService, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        using IServiceScope scope = CreateScope();
        await action(ServiceIn(scope));
    }

    /// <summary>
    /// Waits until <paramref name="condition" /> holds, or fails with what it was waiting for.
    /// </summary>
    /// <remarks>
    /// Wait on the condition, never on a fixed delay (AGENTS.md's testing section). The poll interval is a
    /// <see cref="PeriodicTimer" /> rather than a <c>Task.Delay</c> chain so the wait costs one timer
    /// rather than a task per tick, and so that a machine running several suites at once is not spun.
    /// </remarks>
    public static async Task WaitForAsync(
        Func<Task<bool>> condition,
        string what,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);

        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(60);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + limit;

        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(50));

        while (true)
        {
            if (await condition())
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Waited {limit.TotalSeconds:0} s for {what} and it did not happen.");
            }

            await timer.WaitForNextTickAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await host.StopAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A daemon that will not stop must not fail the test that already passed.
        }

        host.Dispose();
    }

    /// <summary>
    /// Who a generic host's circuit belongs to: nobody.
    /// </summary>
    /// <remarks>
    /// The studio reads the visitor from <see cref="AuthenticationStateProvider" /> rather than from an
    /// <c>HttpContext</c>, so something has to answer even when there is no browser. With no policy
    /// configured nothing is asked of it beyond the name that goes into the audit entry.
    /// </remarks>
    private sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}

/// <summary>
/// A test class with its own host, its own schema pair and its own daemon.
/// </summary>
public abstract class ProjectionsTestBase : IAsyncLifetime
{
    private readonly PostgresFixture postgres;
    private ProjectionsFixture? fixture;

    protected ProjectionsTestBase(PostgresFixture postgres)
    {
        this.postgres = postgres;
    }

    /// <summary>The host under test. Only touched by tests that run, which is only when Docker is there.</summary>
    internal ProjectionsFixture Fixture => fixture
        ?? throw new InvalidOperationException(
            "The projections host was not started. A test that needs it must be a [PostgresFact] so that it " +
            "skips instead of failing when Docker is absent.");

    /// <summary>Whether this class's host registers <c>AddAsyncDaemon</c>.</summary>
    protected virtual bool WithDaemon => true;

    /// <summary>
    /// What the host granted. <see langword="null" /> means every capability, which is what the other
    /// classes need; a class about a refusal states something smaller.
    /// </summary>
    private protected virtual MartenStudioCapabilities? Capabilities => null;

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        fixture = await ProjectionsFixture.StartAsync(
            postgres,
            GetType().Name.ToLowerInvariant(),
            WithDaemon,
            Capabilities,
            TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (fixture is not null)
        {
            await fixture.DisposeAsync();
        }
    }
}
