using System.Security.Claims;

using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Storage;

using MartenStudio.Integration.Tests.Documents;
using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain.Events;
using MartenStudio.Services;
using MartenStudio.Services.Projections;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using MartenCoordinator = Marten.Events.Daemon.Coordination.IProjectionCoordinator;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// A store with two real databases and one daemon coordinator, which is the shape the studio used to get
/// wrong for every host that has it.
/// </summary>
/// <remarks>
/// <para>
/// The coordinator keys its daemons on <c>IDatabase.Identifier</c> - the name the tenancy gave the
/// database, here <c>primary</c> and <c>secondary</c> - and <c>DaemonForDatabase(id)</c> puts that string
/// through <c>Tenancy.FindOrCreateDatabase</c>. The studio addresses databases by
/// <c>DatabaseId.Identity</c> instead, which Weasel derives from the connection string
/// (<c>server.name</c>). Handing one to the other threw <c>UnknownTenantIdException</c>, the accessor
/// caught it, and every multi-database host was told its daemon was "not hosted in this process" while it
/// was running in the very same process. <see cref="The_two_identities_really_are_different" /> is that
/// bug, pinned.
/// </para>
/// <para>
/// Two databases means a second one has to exist: it is created with <c>CREATE DATABASE</c> on the
/// fixture's server, and each test class owns its own schema pair inside both of them.
/// </para>
/// </remarks>
public class MultiDatabaseDaemonLiveTests(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The second database on the fixture's server. Created once; schemas are what isolate runs.</summary>
    private const string SecondDatabaseName = "martenstudio_second_database";

    /// <summary>What the tenancy calls the two databases. Deliberately not "server.name".</summary>
    private const string PrimaryIdentifier = "primary";

    private const string SecondaryIdentifier = "secondary";

    private const string PrimaryTenant = "tenant-primary";

    private const string SecondaryTenant = "tenant-secondary";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost? host;

    private IDocumentStore Store => host?.Services.GetRequiredService<IDocumentStore>()
        ?? throw new InvalidOperationException(
            "The multi-database host was not started. A test that needs it must be a [PostgresFact] so that it "
            + "skips instead of failing when Docker is absent.");

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        string schema = GetType().Name.ToLowerInvariant();
        string eventSchema = schema + "_events";

        string first = postgres.ConnectionString;
        string second = await CreateSecondDatabaseAsync(first, Token);

        await CreateSchemasAsync(first, schema, eventSchema, Token);
        await CreateSchemasAsync(second, schema, eventSchema, Token);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddMarten(options =>
            {
                options.DatabaseSchemaName = schema;
                options.Events.DatabaseSchemaName = eventSchema;
                options.AutoCreateSchemaObjects = AutoCreate.All;

                // One async projection is all this class is about: whether the daemon that runs it can be
                // found for the database the studio is pointed at.
                options.Projections.Add(new DailySalesProjection(), ProjectionLifecycle.Async);

                options.MultiTenantedDatabases(tenancy =>
                {
                    tenancy.AddMultipleTenantDatabase(first, PrimaryIdentifier).ForTenants(PrimaryTenant);
                    tenancy.AddMultipleTenantDatabase(second, SecondaryIdentifier).ForTenants(SecondaryTenant);
                });
            })
            .UseLightweightSessions()
            .AddAsyncDaemon(DaemonMode.Solo);

        // What a Blazor circuit supplies and a generic host does not.
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<AuthenticationStateProvider, AnonymousAuthenticationStateProvider>();
        builder.Services.AddMartenStudio(options => options.Capabilities = MartenStudioCapabilities.All());

        host = builder.Build();

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        // Different numbers of events per database, so "progress for the selected one" is a claim that can
        // actually fail: the two high-water marks are not the same number.
        await SeedAsync(store, PrimaryTenant, streams: 3, Token);
        await SeedAsync(store, SecondaryTenant, streams: 1, Token);

        await host.StartAsync(Token);

        var coordinator = host.Services.GetRequiredService<MartenCoordinator>();

        await ProjectionsFixture.WaitForAsync(
            async () => (await coordinator.AllDaemonsAsync()).Count == 2
                && (await coordinator.AllDaemonsAsync()).All(x => x.IsRunning),
            "both databases' daemons to start",
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

    /// <summary>
    /// The two identities the fix is about, side by side: what the studio addresses a database by, and
    /// what the coordinator keys its daemons on.
    /// </summary>
    [PostgresFact]
    public async Task The_two_identities_really_are_different()
    {
        IReadOnlyList<IMartenDatabase> databases = await Store.Storage.AllDatabases();
        databases.Should().HaveCount(2);

        var coordinator = host!.Services.GetRequiredService<MartenCoordinator>();

        foreach (IMartenDatabase database in databases)
        {
#pragma warning disable CS0618 // the obsolete Identifier is exactly what the coordinator is keyed on
            string identifier = database.Identifier;
#pragma warning restore CS0618

            identifier.Should().BeOneOf(PrimaryIdentifier, SecondaryIdentifier);

            database.Id.Identity.Should().NotBe(
                identifier,
                "DatabaseId.Identity is server.name from the connection string, not the tenancy's own name");

            // The old call, with the identity the studio addresses databases by. This is the throw that
            // the accessor turned into "not hosted in this process".
            Func<Task> byIdentity = async () => await coordinator.DaemonForDatabase(database.Id.Identity);
            await byIdentity.Should().ThrowAsync<UnknownTenantIdException>();

            // And the tracker's stamp, which is what the fix matches on.
            IProjectionDaemon daemon = await coordinator.DaemonForDatabase(identifier);
            daemon.Tracker.DatabaseIdentifier.Should().Be(identifier);
            daemon.Tracker.Should().BeSameAs(database.Tracker);
        }
    }

    /// <summary>
    /// B2: both databases answer <c>Hosted</c>, and each gets <em>its own</em> daemon rather than the main
    /// database's.
    /// </summary>
    [PostgresFact]
    public async Task Every_database_in_the_store_finds_its_own_running_daemon()
    {
        IReadOnlyList<IMartenDatabase> databases = await Store.Storage.AllDatabases();

        List<IProjectionDaemon> found = [];

        foreach (IMartenDatabase database in databases)
        {
            using IServiceScope scope = host!.Services.CreateScope();

            var resolver = scope.ServiceProvider.GetRequiredService<StudioScopeResolver>();
            var accessor = scope.ServiceProvider.GetRequiredService<DaemonAccessor>();

            ResolvedScope resolved = await resolver.ResolveAsync(ScopeFor(database), null, Token);
            DaemonHosting hosting = await accessor.ForScopeAsync(resolved, Token);

            hosting.State.Should().Be(
                DaemonHostingState.Hosted,
                "the daemon for {0} runs in this very process: {1}",
                database.Id.Identity,
                hosting.Explanation);

            hosting.TryGetDaemon(out IProjectionDaemon daemon).Should().BeTrue();
            daemon.Tracker.Should().BeSameAs(database.Tracker, "each database gets the daemon built against it");

            found.Add(daemon);
        }

        found.Should().HaveCount(2);
        found[0].Should().NotBeSameAs(found[1], "two databases are two daemons, not one used twice");
    }

    /// <summary>
    /// And the page reads through it: each scope's card says hosted, and its numbers are that database's
    /// and not the other one's.
    /// </summary>
    [PostgresFact]
    public async Task The_projections_page_shows_the_selected_databases_own_progress()
    {
        IReadOnlyList<IMartenDatabase> databases = await Store.Storage.AllDatabases();

        Dictionary<string, ProjectionsView> views = [];

        foreach (IMartenDatabase database in databases)
        {
            StudioScope scope = ScopeFor(database);

            await ProjectionsFixture.WaitForAsync(
                async () =>
                {
                    ProjectionsView current = await ReadAsync(scope);
                    return current.Progress.Any(x => x is { HasProgressRow: true, Lag: 0, Sequence: > 0 });
                },
                $"the daemon of {database.Id.Identity} to catch up",
                TimeSpan.FromMinutes(2),
                Token);

#pragma warning disable CS0618 // the tenancy's own name, for the assertion below
            views[database.Identifier] = await ReadAsync(scope);
#pragma warning restore CS0618
        }

        views.Should().HaveCount(2);
        views.Values.Should().AllSatisfy(view =>
        {
            view.Daemon.Hosting.Should().Be(DaemonHostingState.Hosted);
            view.Daemon.IsRunning.Should().BeTrue();
            view.NoDaemonAnywhere.Should().BeFalse();
        });

        // Three streams against one database and one against the other: the high-water marks differ, which
        // is how "it read the selected database" is told apart from "it read the main one twice".
        views[PrimaryIdentifier].HighWaterMark.Should().BeGreaterThan(views[SecondaryIdentifier].HighWaterMark);
        views[SecondaryIdentifier].HighWaterMark.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// A pause reaches every database of the store, so a visitor who may not reach one of them may not
    /// pause at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IProjectionCoordinator.PauseAsync()</c> stops the leadership runner and then
    /// <c>StopAllAsync()</c>s every daemon that coordinator has resolved - which is both of this store's
    /// databases, not the one the scope selector names. <c>StudioScopeResolver</c> authorizes the scope in
    /// the URL and nothing else, so on a host whose <c>StoreAuthorizationPolicy</c> scopes by database -
    /// which the scope selector honours, so it is a shape hosts are expected to have - a visitor allowed
    /// only the primary could stop the secondary's projections, and the confirm dialog would have named
    /// the secondary while doing it.
    /// </para>
    /// <para>
    /// Every database is therefore asked for before the coordinator is touched, and the first refusal is
    /// a scope denial: the audit ring records the attempt as a failed <c>PauseDaemon</c>, the log gets
    /// 9203, and both daemons are still running afterwards - which is the assertion that would fail if
    /// the check were removed, because this studio is wired to the coordinator that is really running.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task A_pause_is_refused_when_the_visitor_may_not_reach_every_database_it_would_stop()
    {
        IReadOnlyList<IMartenDatabase> databases = await Store.Storage.AllDatabases();
        databases.Should().HaveCount(2);

        string visible = databases[0].Id.Identity;
        string hidden = databases[1].Id.Identity;

        var policy = new FakeStoreAuthorizationService();
        policy.Allow(resource => !string.Equals(resource.DatabaseIdentifier, hidden, StringComparison.Ordinal));

        var logs = new CapturingLogs();

        await using ServiceProvider restricted = BuildRestrictedStudio(policy, logs);

        using IServiceScope scope = restricted.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProjectionDataService>();

        // The database this visitor IS allowed - so the scope itself resolves, and the only thing that can
        // refuse is the check over the other one.
        var asked = new StudioScope(MartenStoreRegistry.DefaultStoreKey, visible, null);

        Func<Task> pausing = () => service.PauseDaemonAsync(asked, Token);

        await pausing.Should().ThrowAsync<StudioNotAuthorizedException>(
            "a pause of this store would stop a database this visitor may not address");

        var ring = restricted.GetRequiredService<StudioActionLogService>();

        StudioActionLogEntry refusal = ring.GetLatest()
            .Should().ContainSingle(x => x.Action == "PauseDaemon" && !x.Succeeded).Subject;

        // The database the visitor could not reach, not the one they asked for. The check that refuses
        // throws with the refused scope on it, and that is only worth anything because the catch records
        // `denied.Scope`; recording the requested scope - which is what it used to do - threw the one new
        // fact away and left an entry naming a database nothing was wrong with.
        refusal.DatabaseId.Should().Be(
            hidden,
            "an audit entry for a refusal has to say which database was refused");
        refusal.DatabaseId.Should().NotBe(visible);

        logs.EventIds.Should().Contain(9203, "a scope denial is logged as one");

        // Nothing was paused: this studio is wired to the coordinator that is actually running.
        var coordinator = host!.Services.GetRequiredService<MartenCoordinator>();
        IReadOnlyList<IProjectionDaemon> daemons = await coordinator.AllDaemonsAsync();

        daemons.Should().HaveCount(2);
        daemons.Should().AllSatisfy(daemon => daemon.IsRunning.Should().BeTrue());

        // And the card never names the database the visitor may not see.
        ProjectionsView view = await service.GetProjectionsAsync(asked, Token);

        view.Daemon.Databases.Should().Equal(visible);
    }

    /// <summary>
    /// A second studio over the <em>same</em> store and the same running coordinator, with a store policy
    /// that refuses one of the two databases.
    /// </summary>
    /// <remarks>
    /// Its own container rather than the class's, because the policy is what the test is about and the
    /// other tests here need none. The store and the coordinator are the running host's own instances, so
    /// a refusal that did not happen would really pause the daemons the other tests depend on - which is
    /// what makes "both are still running" worth asserting.
    /// </remarks>
    private ServiceProvider BuildRestrictedStudio(FakeStoreAuthorizationService policy, CapturingLogs logs)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddProvider(new CapturingLoggerProvider(logs)));

        services.AddSingleton(Store);
        services.AddSingleton(host!.Services.GetRequiredService<MartenCoordinator>());

        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, AnonymousAuthenticationStateProvider>();

        // Registered after AddAuthorization so that this is the one resolved: the fake is the host's
        // policy, expressed as the answer rather than as a requirement handler.
        services.AddSingleton<IAuthorizationService>(policy);

        services.AddMartenStudio(options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.StoreAuthorizationPolicy = "studio-store";
            options.WriteAuthorizationPolicy = "studio-write";
            options.DiscoverTenantIds = false;
        });

        return services.BuildServiceProvider();
    }

    private async Task<ProjectionsView> ReadAsync(StudioScope scope)
    {
        using IServiceScope serviceScope = host!.Services.CreateScope();
        var service = serviceScope.ServiceProvider.GetRequiredService<IProjectionDataService>();

        return await service.GetProjectionsAsync(scope, Token);
    }

    private static StudioScope ScopeFor(IMartenDatabase database) =>
        new(MartenStoreRegistry.DefaultStoreKey, database.Id.Identity, null);

    private static async Task SeedAsync(IDocumentStore store, string tenantId, int streams, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession(tenantId);

        for (int index = 0; index < streams; index++)
        {
            Guid streamId = Guid.NewGuid();
            DateTimeOffset placedAt = new(2026, 9, 14, 9, index, 0, TimeSpan.Zero);

            session.Events.StartStream(
                streamId,
                new OrderPlaced(streamId, $"Customer {index}", placedAt),
                new ItemAdded($"SKU-{index:000}", 1, 19.90m, placedAt.AddMinutes(1)));
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Creates the second database if the server does not already have it, and returns its connection
    /// string.
    /// </summary>
    /// <remarks>
    /// <c>CREATE DATABASE</c> cannot run inside a transaction and takes no parameters, so the name is a
    /// constant quoted through the studio's own <see cref="SqlIdentifier" /> - the same rule the rest of
    /// this suite follows. It is never dropped: with container reuse on it is reused too, and the schema
    /// pair inside it is what isolates one run from the next.
    /// </remarks>
    private static async Task<string> CreateSecondDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using (var maintenance = new NpgsqlConnection(connectionString))
        {
            await maintenance.OpenAsync(cancellationToken);

            await using var exists = new NpgsqlCommand("select 1 from pg_database where datname = @name", maintenance);
            exists.Parameters.AddWithValue("name", SecondDatabaseName);

            if (await exists.ExecuteScalarAsync(cancellationToken) is null)
            {
                await using var create = new NpgsqlCommand(
                    $"create database {SqlIdentifier.Quote(SecondDatabaseName)}", maintenance);

                await create.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { Database = SecondDatabaseName }.ConnectionString;
    }

    private static async Task CreateSchemasAsync(
        string connectionString,
        string schema,
        string eventSchema,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        string first = SqlIdentifier.Quote(schema);
        string second = SqlIdentifier.Quote(eventSchema);

        await using var command = new NpgsqlCommand(
            $"drop schema if exists {first} cascade; create schema {first};"
            + $"drop schema if exists {second} cascade; create schema {second};",
            connection);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Who a generic host's circuit belongs to: nobody.</summary>
    private sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    /// <summary>
    /// Puts everything the studio logs into a <see cref="CapturingLogs" />, so a test can assert on the
    /// event id rather than on prose.
    /// </summary>
    private sealed class CapturingLoggerProvider(CapturingLogs logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logs.CreateLogger<CapturingLoggerProvider>();

        public void Dispose()
        {
        }
    }
}
