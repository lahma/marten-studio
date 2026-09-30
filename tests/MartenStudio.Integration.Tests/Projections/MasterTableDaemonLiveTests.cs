using System.Security.Claims;

using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;
using Marten.Storage;

using MartenStudio.SampleDomain.Events;
using MartenStudio.Services;
using MartenStudio.Services.Projections;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MartenCoordinator = Marten.Events.Daemon.Coordination.IProjectionCoordinator;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// DB-0-fix-2, F2, live: a daemon hosted in this process for a master-table tenancy with one tenant
/// database is "hosted here", and not "run by an external system".
/// </summary>
/// <remarks>
/// <para>
/// <c>MasterTableTenancy.Default</c> throws <c>NotSupportedException</c> - there is no default tenant -
/// and Marten's own <c>ProjectionCoordinator.DaemonForMainDatabase()</c> reads it. DB-0-fix asked that call
/// for every store whose <c>AllDatabases()</c> answered one database, and read the
/// <c>NotSupportedException</c> as the Wolverine shape: the card said "External", every daemon control
/// was hidden and refused, and the daemon was running in the very same process. The accessor now matches
/// Marten's coordinator's set by tracker, and nothing Marten's coordinator throws is read as "external".
/// </para>
/// <para>
/// The master table and the one tenant database are both in the fixture's database, in schemas this
/// class owns: what is under test is the tenancy's shape, not the number of physical databases.
/// </para>
/// </remarks>
public class MasterTableDaemonLiveTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Tenant = "tenant-a";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost? host;

    private IServiceProvider Services => host?.Services
        ?? throw new InvalidOperationException(
            "The master-table host was not started. A test that needs it must be a [PostgresFact] so that it "
            + "skips instead of failing when Docker is absent.");

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        string schema = GetType().Name.ToLowerInvariant();

        await postgres.CreateSchemaAsync(schema, Token);
        await postgres.CreateSchemaAsync(schema + "_events", Token);
        await postgres.CreateSchemaAsync(schema + "_tenants", Token);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddMarten(options =>
            {
                options.MultiTenantedDatabasesWithMasterDatabaseTable(tenancy =>
                {
                    tenancy.ConnectionString = postgres.ConnectionString;
                    tenancy.SchemaName = schema + "_tenants";
                    tenancy.AutoCreate = AutoCreate.All;
                    tenancy.RegisterDatabase(Tenant, postgres.ConnectionString);
                });

                options.DatabaseSchemaName = schema;
                options.Events.DatabaseSchemaName = schema + "_events";
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.Projections.LeadershipPollingTime = 1_000;

                options.Projections.Add(new DailySalesProjection(), ProjectionLifecycle.Async);
            })
            .UseLightweightSessions()
            .AddAsyncDaemon(DaemonMode.Solo);

        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<AuthenticationStateProvider, AnonymousVisitor>();
        builder.Services.AddMartenStudio(static options => options.Capabilities = MartenStudioCapabilities.All());

        host = builder.Build();

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        await using (IDocumentSession session = store.LightweightSession(Tenant))
        {
            Guid streamId = Guid.NewGuid();
            session.Events.StartStream(streamId, new OrderPlaced(streamId, "Customer", DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(Token);
        }

        await host.StartAsync(Token);

        var coordinator = host.Services.GetRequiredService<MartenCoordinator>();

        await ProjectionsFixture.WaitForAsync(
            async () => (await coordinator.AllDaemonsAsync()) is [{ IsRunning: true }],
            "the tenant database's daemon to start",
            TimeSpan.FromSeconds(90),
            Token);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (host is null)
        {
            return;
        }

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

    [PostgresFact]
    public async Task The_daemon_of_a_one_database_master_table_tenancy_is_hosted_here_and_not_External()
    {
        var store = Services.GetRequiredService<IDocumentStore>();
        IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases();

        IMartenDatabase database = databases.Should().ContainSingle("one tenant database, and the master table is not one").Subject;
        store.Options.Tenancy.Cardinality.Should().Be(DatabaseCardinality.DynamicMultiple);

        // The shape DB-0-fix read as "external": Marten's own coordinator, answering NotSupported.
        var coordinator = Services.GetRequiredService<MartenCoordinator>();
        FluentActions.Invoking(() => coordinator.DaemonForMainDatabase()).Should().Throw<NotSupportedException>();

        using IServiceScope scope = Services.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<StudioScopeResolver>();
        var accessor = scope.ServiceProvider.GetRequiredService<DaemonAccessor>();
        var projections = scope.ServiceProvider.GetRequiredService<IProjectionDataService>();

        var asked = new StudioScope(MartenStoreRegistry.DefaultStoreKey, database.Id.Identity, null);

        ResolvedScope resolved = await resolver.ResolveAsync(asked, null, Token);
        DaemonHosting hosting = await accessor.ForScopeAsync(resolved, Token);

        hosting.State.Should().Be(DaemonHostingState.Hosted, hosting.Explanation);
        hosting.TryGetDaemon(out IProjectionDaemon daemon).Should().BeTrue();
        daemon.Tracker.Should().BeSameAs(database.Tracker);
        daemon.IsRunning.Should().BeTrue();

        ProjectionsView view = await projections.GetProjectionsAsync(asked, Token);

        view.Daemon.Hosting.Should().Be(DaemonHostingState.Hosted);
        view.Daemon.IsExternallyManaged.Should().BeFalse("nothing external runs these projections; this process does");
    }

    /// <summary>Who a generic host's circuit belongs to: nobody.</summary>
    private sealed class AnonymousVisitor : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
