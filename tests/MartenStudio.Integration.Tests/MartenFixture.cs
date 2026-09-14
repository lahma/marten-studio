using System.Security.Claims;

using JasperFx;

using Marten;

using MartenStudio.SampleDomain;
using MartenStudio.Services;
using MartenStudio.Services.Documents;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Integration.Tests;

/// <summary>
/// A real Marten store over the assembly's Postgres, wired into a real Marten Studio container.
/// </summary>
/// <remarks>
/// <para>
/// <b>What makes this worth having.</b> Everything here goes <em>through</em> the studio's own services:
/// the container is built with <c>AddMartenStudio()</c> beside <c>AddMarten()</c>, and a data service is
/// resolved from a scope, so every call passes through <see cref="StudioScopeResolver" /> and the
/// authorization it runs first. A test that reached for an <c>IDocumentStore</c> directly would prove that
/// Postgres works, which nobody doubts, rather than that the studio works.
/// </para>
/// <para>
/// <b>Isolation is per schema.</b> Each test class gets a schema of its own, named after the class and
/// dropped before it is created, so a reused container carrying an earlier run's tables still gives every
/// run a clean one. The sample domain is configured with <see cref="SampleStore.Configure" /> and then
/// re-pointed at that schema, which is why the demo's own <c>studio_sample</c> is never touched.
/// </para>
/// <para>
/// <b>Reusing it from a later packet.</b> Declare a nested <c>public sealed class Fixture(PostgresFixture
/// postgres) : MartenClassFixture(postgres)</c> in the test class, override
/// <see cref="MartenClassFixture.ConfigureStore" /> to add document or event types, or
/// <see cref="MartenClassFixture.ConfigureStudio" /> to turn capabilities on, and
/// <see cref="MartenClassFixture.SeedAsync" /> for data this class alone needs - the sample seeder has
/// already run by then. Derive the test class from <see cref="MartenTestBase" /> and add
/// <c>IClassFixture&lt;Fixture&gt;</c>; everything a test needs is on the base class:
/// <see cref="MartenTestBase.Store" />, <see cref="MartenTestBase.Scope" /> and
/// <see cref="MartenTestBase.Services" />.
/// </para>
/// </remarks>
public sealed class MartenFixture : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    private MartenFixture(ServiceProvider provider, IDocumentStore store, string schema)
    {
        this.provider = provider;
        Store = store;
        Schema = schema;
    }

    /// <summary>The store the studio reads, exactly as the sample host configures it.</summary>
    public IDocumentStore Store { get; }

    /// <summary>The schema this fixture's tables live in.</summary>
    public string Schema { get; }

    /// <summary>The studio's container, for resolving a scoped service.</summary>
    public IServiceProvider Services => provider;

    /// <summary>The scope every data call is made with: the default store, its only database, all tenants.</summary>
    internal StudioScope Scope { get; } = new("default", string.Empty, null);

    /// <summary>The same scope narrowed to one tenant.</summary>
    internal static StudioScope ScopeFor(string? tenantId) => new("default", string.Empty, tenantId);

    /// <summary>
    /// Builds the store and the studio container, applies the schema and runs the sample seeder.
    /// </summary>
    /// <param name="connectionString">The container's connection string.</param>
    /// <param name="schema">The schema to isolate this test class in; dropped and recreated.</param>
    /// <param name="configureStore">Extra Marten configuration, applied after the sample domain's.</param>
    /// <param name="configureStudio">Studio options, applied after the defaults.</param>
    public static async Task<MartenFixture> CreateAsync(
        string connectionString,
        string schema,
        Action<StoreOptions>? configureStore = null,
        Action<MartenStudioOptions>? configureStudio = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        var services = new ServiceCollection();

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));

        // StudioAuthorization takes both of these even when no policy is configured, because the decision
        // "no policy means everything passes" is made inside it rather than by leaving it unbuilt.
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, AnonymousAuthenticationStateProvider>();

        services.AddMarten(options =>
        {
            SampleStore.Configure(options, connectionString);

            // The sample pins its own schemas; a test class needs its own so that two of them running at
            // once do not share tables.
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = schema + "_events";
            options.AutoCreateSchemaObjects = AutoCreate.All;

            configureStore?.Invoke(options);
        });

        services.AddMartenStudio(options =>
        {
            options.ReadOnly = false;
            configureStudio?.Invoke(options);
        });

        ServiceProvider provider = services.BuildServiceProvider();
        IDocumentStore store = provider.GetRequiredService<IDocumentStore>();

        // Applied up front rather than left to AutoCreate: the studio's reads are raw SQL against tables
        // it did not create, so a test that only ever read would never trigger Marten's own DDL and would
        // fail on a missing table rather than on the thing it is about.
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        return new MartenFixture(provider, store, schema);
    }

    /// <summary>Runs the sample domain's own seeder, which is idempotent.</summary>
    public Task SeedSampleDataAsync(CancellationToken cancellationToken = default) =>
        new SampleDataSeeder().Populate(Store, cancellationToken);

    /// <summary>
    /// A scoped <see cref="IDocumentDataService" />, resolved the way a Blazor circuit resolves one.
    /// </summary>
    /// <remarks>
    /// The scope is disposed with the returned handle, so a test writes
    /// <c>using var documents = fixture.Documents();</c> and then <c>documents.Service</c>.
    /// </remarks>
    internal ScopedService<IDocumentDataService> Documents() => Resolve<IDocumentDataService>();

    /// <summary>One scoped service, with the scope that owns it.</summary>
    internal ScopedService<T> Resolve<T>() where T : notnull
    {
        IServiceScope scope = provider.CreateScope();

        return new ScopedService<T>(scope, scope.ServiceProvider.GetRequiredService<T>());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        await provider.DisposeAsync();
    }

    /// <summary>A service and the DI scope it belongs to.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    internal sealed class ScopedService<T> : IDisposable
    {
        private readonly IServiceScope scope;

        internal ScopedService(IServiceScope scope, T service)
        {
            this.scope = scope;
            Service = service;
        }

        /// <summary>The service.</summary>
        public T Service { get; }

        /// <inheritdoc />
        public void Dispose() => scope.Dispose();
    }

    /// <summary>
    /// Nobody in particular. The studio asks for the visitor only when a policy is configured, and no test
    /// in this assembly configures one - but the service has to be there for the container to build.
    /// </summary>
    private sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}

/// <summary>
/// One Marten store, one studio container and one seeded schema, built <b>once for a whole test class</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a class fixture and not <c>IAsyncLifetime</c> on the test class.</b> xunit constructs a
/// test class per test <em>method</em>, so an <c>IAsyncLifetime</c> that built the store rebuilt it — and
/// re-ran <c>ApplyAllConfiguredChangesToDatabaseAsync</c> and the seeder — once per test. That is not a
/// tidiness point: <c>DocumentPagingLiveTests</c> seeds ten thousand customers, and it was seeding them
/// five times per run. A class fixture is constructed once, before the first test of the class, and
/// disposed after the last.
/// </para>
/// <para>
/// <b>Isolation is still per class.</b> The schema is named after the fixture's <em>declaring</em> type,
/// so the conventional shape — a nested <c>public sealed class Fixture : MartenClassFixture</c> inside the
/// test class — gives every class its own schema, exactly as the old base class did, while letting several
/// classes run in parallel.
/// </para>
/// <para>
/// <b>Reusing it.</b> Derive a nested fixture, override <see cref="ConfigureStore" /> to add document or
/// event types, <see cref="ConfigureStudio" /> to turn capabilities on, <see cref="SeedSampleData" /> to
/// turn the demo data off, and <see cref="SeedAsync" /> for data this class alone needs. Then derive the
/// test class from <see cref="MartenTestBase" /> and add <c>IClassFixture&lt;…&gt;</c>.
/// </para>
/// <para>
/// When Docker is absent nothing is created and every test in the class skips — use
/// <see cref="PostgresFactAttribute" /> so that they do.
/// </para>
/// </remarks>
/// <param name="postgres">The assembly's shared container.</param>
public abstract class MartenClassFixture(PostgresFixture postgres) : IAsyncLifetime
{
    private MartenFixture? fixture;

    /// <summary>The store and container, once <see cref="InitializeAsync" /> has run.</summary>
    public MartenFixture Marten => fixture
        ?? throw new InvalidOperationException(
            "The Marten fixture is not running. A test that needs it must be a [PostgresFact] so that it " +
            "skips instead of failing when Docker is absent.");

    /// <summary>
    /// The shared container, for the rare test that has to reach the database without the studio -
    /// creating a table no mapping claims, or writing a column behind Marten's back. Marten's own
    /// <c>CreateConnection()</c> is not a substitute: Npgsql strips the password out of
    /// <c>ConnectionString</c>, so a connection string taken from it cannot be reopened.
    /// </summary>
    public PostgresFixture Postgres { get; } = postgres;

    /// <summary>The schema this fixture's tables live in.</summary>
    public string Schema { get; private set; } = string.Empty;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        // The declaring type, so a nested `Fixture` is named after the test class that owns it rather
        // than colliding with every other nested `Fixture` in the assembly.
        Schema = (GetType().DeclaringType ?? GetType()).Name.ToLowerInvariant();

        // Dropped before it is created, because the container is reused between local runs and would
        // otherwise be carrying this class's tables from an earlier one.
        await Postgres.CreateSchemaAsync(Schema);
        await Postgres.CreateSchemaAsync(Schema + "_events");

        fixture = await MartenFixture.CreateAsync(
            Postgres.ConnectionString,
            Schema,
            ConfigureStore,
            ConfigureStudio);

        if (SeedSampleData)
        {
            await fixture.SeedSampleDataAsync();
        }

        await SeedAsync();
    }

    /// <inheritdoc />
    public virtual async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (fixture is not null)
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>Whether the sample domain's own data is written. Turn it off for a class that seeds its own.</summary>
    protected virtual bool SeedSampleData => true;

    /// <summary>Extra Marten configuration for this class.</summary>
    protected virtual void ConfigureStore(StoreOptions options)
    {
    }

    /// <summary>Studio options for this class.</summary>
    protected virtual void ConfigureStudio(MartenStudioOptions options)
    {
    }

    /// <summary>Data this class alone needs, written after the sample seeder has run.</summary>
    protected virtual Task SeedAsync() => Task.CompletedTask;
}

/// <summary>
/// A test class reading one <see cref="MartenClassFixture" />: everything it needs, without the lifecycle.
/// </summary>
/// <remarks>
/// The test class still has to declare <c>IClassFixture&lt;TFixture&gt;</c> — that is what makes xunit
/// build the fixture once and hand it to the constructor.
/// </remarks>
/// <param name="fixture">The class's fixture.</param>
public abstract class MartenTestBase(MartenClassFixture fixture)
{
    /// <summary>The store and container for this class.</summary>
    protected MartenFixture Marten => fixture.Marten;

    /// <summary>The store, as the sample host configures it.</summary>
    protected IDocumentStore Store => Marten.Store;

    /// <summary>The studio's container.</summary>
    protected IServiceProvider Services => Marten.Services;

    /// <summary>The scope every data call is made with.</summary>
    internal StudioScope Scope => Marten.Scope;

    /// <summary>This class's schema.</summary>
    protected string Schema => Marten.Schema;

    /// <summary>The shared container. See <see cref="MartenClassFixture.Postgres" />.</summary>
    protected PostgresFixture Postgres => fixture.Postgres;

    /// <summary>A scoped documents data service.</summary>
    internal MartenFixture.ScopedService<IDocumentDataService> Documents() => Marten.Documents();
}
