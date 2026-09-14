using System.Security.Claims;

using JasperFx;

using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Query;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Integration.Tests.Query;

/// <summary>A Guid-id document with a duplicated field, so an index verdict and a filter have something real.</summary>
public class QueryPerson
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Age { get; set; }
}

/// <summary>A string-id document, because Marten ids are frequently not Guids and the page has to cope.</summary>
public class QueryTag
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}

/// <summary>An event, so the event tables exist and the <c>mt_events</c> example is a statement that runs.</summary>
/// <param name="Name">Who joined.</param>
public record QueryPersonJoined(string Name);

/// <summary>
/// One store, one schema, two document types and a few rows - driven <em>through</em> the studio's own
/// services so the gating is exercised rather than bypassed (plan §5.4).
/// </summary>
/// <remarks>
/// <para>
/// Everything is built from a real <see cref="IServiceCollection" /> with <c>AddMarten</c> and
/// <c>AddMartenStudio</c> on it: the registry discovers the store the way it does in a host, the resolver
/// authorizes the way it does in a host, and <see cref="IQueryService" /> is the registered
/// implementation. A harness that hand-built <see cref="QueryService" /> would test a different object
/// from the one an application gets.
/// </para>
/// <para>
/// The document types are declared here rather than taken from the sample domain: this suite is about the
/// query path, and a type with one duplicated field and a string-id sibling says everything it needs to
/// without tying these tests to whatever the demo domain becomes.
/// </para>
/// </remarks>
internal sealed class QueryHarness : IAsyncDisposable
{
    /// <summary>The policy that refuses every write, for the write-policy denial test.</summary>
    public const string DenyWritesPolicy = "deny-writes";

    private readonly ServiceProvider provider;
    private readonly IServiceScope scope;

    private QueryHarness(ServiceProvider provider, IServiceScope scope, string schema)
    {
        this.provider = provider;
        this.scope = scope;
        Schema = schema;
    }

    /// <summary>The schema this harness owns.</summary>
    public string Schema { get; }

    /// <summary>The scope every call is made for.</summary>
    public StudioScope Scope { get; } = new(MartenStoreRegistry.DefaultStoreKey, string.Empty, null);

    /// <summary>The registered query service - the same object a host would resolve.</summary>
    public IQueryService Queries => scope.ServiceProvider.GetRequiredService<IQueryService>();

    /// <summary>The audit ring, for asserting what was recorded.</summary>
    public StudioActionLog Audit => scope.ServiceProvider.GetRequiredService<StudioActionLog>();

    /// <summary>The store, for seeding and for checking the database afterwards.</summary>
    public IDocumentStore Store => provider.GetRequiredService<IDocumentStore>();

    /// <summary>
    /// Builds the container, creates the schema and seeds it.
    /// </summary>
    /// <param name="fixture">The shared Postgres.</param>
    /// <param name="schema">The schema to own, dropped and recreated.</param>
    /// <param name="configure">Studio options - by default <c>RunSql</c> is on, the way these tests need it.</param>
    public static async Task<QueryHarness> CreateAsync(
        PostgresFixture fixture,
        string schema,
        Action<MartenStudioOptions>? configure = null)
    {
        await fixture.CreateSchemaAsync(schema);

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddAuthorization(options =>
            options.AddPolicy(DenyWritesPolicy, policy => policy.RequireAssertion(static _ => false)));
        services.AddSingleton<AuthenticationStateProvider, HarnessAuthenticationStateProvider>();

        services.AddMarten(options =>
        {
            options.Connection(fixture.ConnectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = schema + "_events";
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<QueryPerson>().Duplicate(x => x.Name);
            options.RegisterDocumentType<QueryTag>();
        });

        services.AddMartenStudio(options =>
        {
            options.Capabilities.RunSql = true;
            options.QueryTimeout = TimeSpan.FromSeconds(30);
            options.MaxSqlConsoleRows = 500;
            configure?.Invoke(options);
        });

        ServiceProvider provider = services.BuildServiceProvider();
        IServiceScope serviceScope = provider.CreateScope();
        var harness = new QueryHarness(provider, serviceScope, schema);

        await harness.SeedAsync();

        return harness;
    }

    private async Task SeedAsync()
    {
        await using IDocumentSession session = Store.LightweightSession();

        session.Store(
            new QueryPerson { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "Alice", Age = 41 },
            new QueryPerson { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Name = "Bob", Age = 29 },
            new QueryPerson { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Name = "Carla", Age = 63 });

        session.Store(
            new QueryTag { Id = "red", Label = "Red" },
            new QueryTag { Id = "blue", Label = "Blue" });

        // One event, only so that the event tables exist: the idle panel offers an mt_events example and a
        // test asserts that every example it offers actually runs against this store.
        session.Events.StartStream(Guid.NewGuid(), new QueryPersonJoined("Alice"));

        await session.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        scope.Dispose();
        await provider.DisposeAsync();
    }

    /// <summary>
    /// Somebody is signed in, because an audit entry that said <c>anonymous</c> for every test would not
    /// prove the entry carries who did it.
    /// </summary>
    private sealed class HarnessAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "ops")], "test"))));
    }
}
