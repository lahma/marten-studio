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
/// <b>Reusing it from a later packet.</b> Derive a test class from <see cref="MartenTestBase" />, and
/// override <see cref="MartenTestBase.ConfigureStore" /> to add document or event types, or
/// <see cref="MartenTestBase.ConfigureStudio" /> to turn capabilities on. Override
/// <see cref="MartenTestBase.SeedAsync" /> for data this class alone needs - the sample seeder has already
/// run by then. Everything a test needs is on the base class: <see cref="MartenTestBase.Store" />,
/// <see cref="MartenTestBase.Scope" /> and <see cref="MartenTestBase.Services" />.
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
/// A test class with its own Marten schema, its own studio container and the sample data already in it.
/// </summary>
/// <remarks>
/// When Docker is absent nothing is created and every test in the class skips - use
/// <see cref="PostgresFactAttribute" /> so that they do.
/// </remarks>
public abstract class MartenTestBase(PostgresFixture postgres) : IAsyncLifetime
{
    private MartenFixture? fixture;

    /// <summary>The fixture, once <see cref="InitializeAsync" /> has run.</summary>
    protected MartenFixture Marten => fixture
        ?? throw new InvalidOperationException(
            "The Marten fixture is not running. A test that needs it must be a [PostgresFact] so that it " +
            "skips instead of failing when Docker is absent.");

    /// <summary>The store, as the sample host configures it.</summary>
    protected IDocumentStore Store => Marten.Store;

    /// <summary>The studio's container.</summary>
    protected IServiceProvider Services => Marten.Services;

    /// <summary>The scope every data call is made with.</summary>
    internal StudioScope Scope => Marten.Scope;

    /// <summary>This class's schema.</summary>
    protected string Schema => Marten.Schema;

    /// <summary>
    /// The shared container, for the rare test that has to reach the database without the studio -
    /// creating a table no mapping claims, or writing a column behind Marten's back. Marten's own
    /// <c>CreateConnection()</c> is not a substitute: Npgsql strips the password out of
    /// <c>ConnectionString</c>, so a connection string taken from it cannot be reopened.
    /// </summary>
    protected PostgresFixture Postgres { get; } = postgres;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        var schema = GetType().Name.ToLowerInvariant();

        // Dropped before it is created, because the container is reused between local runs and would
        // otherwise be carrying this class's tables from an earlier one.
        await Postgres.CreateSchemaAsync(schema);
        await Postgres.CreateSchemaAsync(schema + "_events");

        fixture = await MartenFixture.CreateAsync(
            Postgres.ConnectionString,
            schema,
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

    /// <summary>A scoped documents data service.</summary>
    internal MartenFixture.ScopedService<IDocumentDataService> Documents() => Marten.Documents();
}
