using System.Security.Claims;

using JasperFx;

using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Query;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

/// <summary>
/// A conjoined-tenancy, soft-deleted document type - the shape that proved Mode A was leaking.
/// </summary>
/// <remarks>
/// Both at once, and deliberately. Marten's string-query path composes neither a tenant filter nor a
/// soft-delete filter, so this is the one collection where "the studio composes its own statement" is the
/// difference between showing one tenant's live rows and showing everybody's, deleted included.
/// </remarks>
public class QueryTicket
{
    public Guid Id { get; set; }

    public string Subject { get; set; } = string.Empty;
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
    private readonly HarnessLogCollector logs;

    private QueryHarness(ServiceProvider provider, IServiceScope scope, string schema, HarnessLogCollector logs)
    {
        this.provider = provider;
        this.scope = scope;
        this.logs = logs;
        Schema = schema;
    }

    /// <summary>
    /// Everything the studio logged, so a test can assert the stable event id rather than only the ring.
    /// </summary>
    /// <remarks>
    /// The ring is in memory and gone at the next restart; the application's own log is the record that
    /// survives a deployment, and the event ids (9204, 9205) are what an operator's saved query matches on.
    /// A test that only read the ring would not notice the id moving.
    /// </remarks>
    public IReadOnlyList<HarnessLogEntry> Logs => logs.Entries;

    /// <summary>The schema this harness owns.</summary>
    public string Schema { get; }

    /// <summary>The scope every call is made for: no tenant, which is the cross-tenant view.</summary>
    public StudioScope Scope { get; } = new(MartenStoreRegistry.DefaultStoreKey, string.Empty, null);

    /// <summary>The same scope, narrowed to one tenant the way the scope selector narrows it.</summary>
    public static StudioScope ScopeFor(string tenantId) =>
        new(MartenStoreRegistry.DefaultStoreKey, string.Empty, tenantId);

    /// <summary>The tenant that owns most of the seeded tickets.</summary>
    public const string Acme = "acme";

    /// <summary>The other tenant, which nothing scoped to <see cref="Acme" /> may ever see.</summary>
    public const string Globex = "globex";

    /// <summary>The live ticket <see cref="Acme" /> owns.</summary>
    public static Guid AcmeLive { get; } = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    /// <summary>The soft-deleted ticket <see cref="Acme" /> owns.</summary>
    public static Guid AcmeDeleted { get; } = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    /// <summary>The live ticket <see cref="Globex" /> owns.</summary>
    public static Guid GlobexLive { get; } = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    /// <summary>The soft-deleted ticket <see cref="Globex" /> owns.</summary>
    public static Guid GlobexDeleted { get; } = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

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
        var logs = new HarnessLogCollector();

        services.AddLogging(builder => builder.AddProvider(logs));
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
            options.Schema.For<QueryTicket>().MultiTenanted().SoftDeleted();
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
        var harness = new QueryHarness(provider, serviceScope, schema, logs);

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

        await SeedTicketsAsync(Acme, AcmeLive, AcmeDeleted);
        await SeedTicketsAsync(Globex, GlobexLive, GlobexDeleted);
    }

    /// <summary>
    /// One live and one soft-deleted ticket per tenant, written through Marten so the tenancy and the
    /// delete are exactly what Marten's own write path produces.
    /// </summary>
    private async Task SeedTicketsAsync(string tenantId, Guid live, Guid deleted)
    {
        await using (IDocumentSession session = Store.LightweightSession(tenantId))
        {
            session.Store(
                new QueryTicket { Id = live, Subject = tenantId + " live" },
                new QueryTicket { Id = deleted, Subject = tenantId + " deleted" });

            await session.SaveChangesAsync();
        }

        await using IDocumentSession remover = Store.LightweightSession(tenantId);

        remover.Delete<QueryTicket>(deleted);

        await remover.SaveChangesAsync();
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

/// <summary>One line the studio wrote to the application's log.</summary>
/// <param name="EventId">The stable id from <c>StudioLog</c> - 9204, 9205, and so on.</param>
/// <param name="Level">How loud it was.</param>
/// <param name="Message">The formatted message.</param>
internal sealed record HarnessLogEntry(int EventId, LogLevel Level, string Message);

/// <summary>Collects everything logged in one harness, so a test can assert an event id.</summary>
internal sealed class HarnessLogCollector : ILoggerProvider
{
    private readonly List<HarnessLogEntry> entries = [];

    /// <summary>What has been logged so far, newest last.</summary>
    public IReadOnlyList<HarnessLogEntry> Entries
    {
        get
        {
            lock (entries)
            {
                return [.. entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Sink(this);

    public void Dispose() => GC.SuppressFinalize(this);

    private void Add(HarnessLogEntry entry)
    {
        lock (entries)
        {
            entries.Add(entry);
        }
    }

    private sealed class Sink(HarnessLogCollector owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            owner.Add(new HarnessLogEntry(eventId.Id, logLevel, formatter(state, exception)));
        }
    }
}
