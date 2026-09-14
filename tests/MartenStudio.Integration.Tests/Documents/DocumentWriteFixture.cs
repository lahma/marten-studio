using System.Security.Claims;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain;
using MartenStudio.Services;
using MartenStudio.Services.Documents;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// A real Marten store over the demo domain, in a schema of this test class's own, with the studio's
/// write service wired up over it exactly as the container would.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of these tests is that they go <em>through</em> the studio's services rather than
/// around them: the capability guard, the scope resolver, the authorization policy and the audit log are
/// the real ones, so a test that writes a document has also exercised the gating that was supposed to
/// stop it. Only the two things a circuit supplies — who is signed in, and what the host's policy says —
/// are doubles, because there is no browser here to supply them.
/// </para>
/// <para>
/// The schema is named after the test class and dropped before it is created, so a reused container
/// carrying yesterday's tables still gives every run a clean one, and two test classes running in
/// parallel never see each other's rows. <see cref="SampleStore.Configure" /> is used as the sample host
/// uses it and then re-pointed at that schema, which is also a small proof of D17: the demo domain knows
/// nothing about the studio.
/// </para>
/// </remarks>
public abstract class DocumentWriteFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The shared container.</summary>
    protected PostgresFixture Postgres { get; } = postgres;

    /// <summary>This class's document schema.</summary>
    protected string Schema { get; private set; } = string.Empty;

    /// <summary>The demo store, built over <see cref="Schema" />.</summary>
    protected IDocumentStore Store => store ?? throw new InvalidOperationException("The store is only there when Docker is.");

    /// <summary>
    /// The studio options the service reads. A test may change them before it calls
    /// <see cref="CreateService" />.
    /// </summary>
    /// <remarks>
    /// The tenants are listed rather than discovered, which is the first tier of
    /// <see cref="TenantDiscovery" /> and the only one that works for a demo store whose tenants exist
    /// in documents and not in event streams.
    /// </remarks>
    protected MartenStudioOptions StudioOptions { get; } = new()
    {
        StoreAuthorizationPolicy = StorePolicyName,
        WriteAuthorizationPolicy = WritePolicyName,
        Capabilities = MartenStudioCapabilities.All(),
        KnownTenantIds = { "acme", "globex" },
    };

    /// <summary>The host's per-store and per-write policy, as a test drives it.</summary>
    protected FakeStoreAuthorizationService Authorization { get; } = new();

    /// <summary>Who is signed in.</summary>
    protected FakeAuthenticationStateProvider Users { get; } = new();

    /// <summary>
    /// The process-wide audit ring the Activity page reads.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> rather than <c>protected</c>, here and on the two members below, because the
    /// studio's data seam is internal (D12) and a <c>protected</c> member of a public class may not be
    /// less accessible than the class. The derived test classes are in this assembly, which
    /// <c>InternalsVisibleTo</c> covers, so internal is exactly as reachable as protected would have been.
    /// </remarks>
    internal StudioActionLogService Ring { get; } = new();

    /// <summary>Everything the studio wrote to the application's logger, by event id.</summary>
    protected CapturingLogs Logs { get; } = new();

    /// <summary>The name the store policy is configured under.</summary>
    protected const string StorePolicyName = "studio-store";

    /// <summary>The name the write policy is configured under.</summary>
    protected const string WritePolicyName = "studio-write";

    private IDocumentStore? store;
    private ServiceProvider? provider;
    private MartenStoreRegistry? registry;
    private readonly ColumnCatalog columnCatalog = new();

    /// <summary>The scope every test writes through: the default store, its one database, one tenant.</summary>
    internal static StudioScope ScopeFor(string? tenantId = null) => new("default", string.Empty, tenantId);

    /// <summary>
    /// A write service over the current options. Built per call, because a test that changes a
    /// capability expects the next call to see it.
    /// </summary>
    internal IDocumentWriteService CreateService()
    {
        var wrapped = Options.Create(StudioOptions);
        var stores = registry ?? throw new InvalidOperationException("The registry is only there when Docker is.");

        var authorization = new StudioAuthorization(wrapped, Authorization, Users);
        var tenants = new TenantDiscovery(wrapped, NullLogger<TenantDiscovery>.Instance);

        // The registry scans the collection the store was registered in and resolves from the provider
        // that built it, so it hands back the one store this fixture seeded rather than a second one.
        var resolver = new StudioScopeResolver(wrapped, stores, authorization, tenants, RequireProvider());

        var catalog = new StudioScopeCatalog(
            wrapped,
            stores,
            authorization,
            tenants,
            RequireProvider(),
            NullLogger<StudioScopeCatalog>.Instance);

        var state = new StudioState(catalog, NullLogger<StudioState>.Instance, new HttpContextAccessor());
        var audit = new StudioActionLog(Ring, Logs.CreateLogger<StudioActionLog>(), Users, state);

        return new DocumentWriteService(
            new StudioCapabilityGuard(wrapped),
            resolver,
            audit,
            columnCatalog,
            wrapped,
            Logs.CreateLogger<DocumentWriteService>(),
            Users);
    }

    /// <summary>
    /// Extra configuration this test class needs on top of the demo domain: its own document types, its
    /// own serializer settings.
    /// </summary>
    /// <remarks>
    /// The shapes the write path has to get right — a subclass hierarchy, a numeric-revision type, a
    /// string id with a slash in it, a store whose serializer is not Marten's default — do not exist in
    /// <c>samples/MartenStudio.SampleDomain</c> and must not be added there: the demo domain is what a
    /// reader looks at to understand the product, and it is owned by another packet. They live in this
    /// test project instead, registered here on the same store and in the same schema, which costs
    /// nothing because a table nobody writes to is a table Marten never creates.
    /// </remarks>
    /// <param name="opts">The store being configured.</param>
    protected virtual void ConfigureExtras(StoreOptions opts)
    {
    }

    /// <summary>
    /// Whether the demo data is seeded. A class that only exercises its own document types turns it off
    /// and starts a second or two faster.
    /// </summary>
    protected virtual bool SeedSampleData => true;

    /// <summary>Creates the schema, builds the store and seeds the demo data.</summary>
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        Schema = GetType().Name.ToLowerInvariant();

        await Postgres.CreateSchemaAsync(Schema);
        await Postgres.CreateSchemaAsync(EventSchema);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMarten(ConfigureStore);

        registry = new MartenStoreRegistry(services);
        provider = services.BuildServiceProvider();
        store = provider.GetRequiredService<IDocumentStore>();

        if (SeedSampleData)
        {
            await new SampleDataSeeder().Populate(store, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Disposes the store. The schema is dropped by the next run that uses this name.</summary>
    public virtual async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (provider is not null)
        {
            await provider.DisposeAsync();
        }
    }

    private string EventSchema => Schema + "_ev";

    private ServiceProvider RequireProvider() =>
        provider ?? throw new InvalidOperationException("The container is only there when Docker is.");

    private void ConfigureStore(StoreOptions opts)
    {
        SampleStore.Configure(opts, Postgres.ConnectionString);

        // The demo domain names its own schemas, which every test class would then share. Re-pointing
        // them is the isolation: one schema per class, dropped and recreated by InitializeAsync.
        opts.DatabaseSchemaName = Schema;
        opts.Events.DatabaseSchemaName = EventSchema;

        ConfigureExtras(opts);
    }

    /// <summary>Stores one document of a test-local type through Marten itself.</summary>
    /// <remarks>
    /// Deliberately not through the studio: these are the <em>arrangements</em>, and an arrangement made
    /// with the code under test proves nothing.
    /// </remarks>
    protected async Task StoreAsync<T>(params T[] documents) where T : notnull
    {
        await using var session = Store.LightweightSession();
        session.Store(documents);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Runs a statement against this class's schema.</summary>
    protected async Task ExecuteAsync(string sql)
    {
        await using var connection = await Postgres.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    // -----------------------------------------------------------------------------------------------
    // Raw reads, so an assertion about what is in the database does not go through the code under test.
    // -----------------------------------------------------------------------------------------------

    /// <summary>Runs a scalar query against this class's schema.</summary>
    protected async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await Postgres.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };

        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is DBNull ? null : value;
    }

    /// <summary>The id of the demo customer with this e-mail, which the seeder makes deterministic.</summary>
    protected async Task<Guid> CustomerIdAsync(string email) =>
        (Guid) (await ScalarAsync($"select id from \"{Schema}\".mt_doc_customer where email = '{email}'")
            ?? throw new InvalidOperationException($"No demo customer with e-mail {email}."));

    /// <summary>The id of the demo order with this reference.</summary>
    protected async Task<Guid> OrderIdAsync(string reference) =>
        (Guid) (await ScalarAsync($"select id from \"{Schema}\".mt_doc_order where data ->> 'Reference' = '{reference}'")
            ?? throw new InvalidOperationException($"No demo order with reference {reference}."));

    /// <summary>The id of the demo invoice with this number, in this tenant.</summary>
    protected async Task<int> InvoiceIdAsync(string tenantId, string number) =>
        (int) (await ScalarAsync(
            $"select id from \"{Schema}\".mt_doc_invoice where tenant_id = '{tenantId}' and data ->> 'Number' = '{number}'")
            ?? throw new InvalidOperationException($"No demo invoice {number} for tenant {tenantId}."));

    /// <summary>The stored JSON of one row, or <see langword="null" /> when the row is not there.</summary>
    protected async Task<string?> StoredJsonAsync(string table, object id, string? tenantId = null) =>
        (string?) await ScalarAsync(
            $"select data::text from \"{Schema}\".{table} where id = {Literal(id)}{TenantPredicate(tenantId)}");

    /// <summary>The <c>mt_version</c> of one row.</summary>
    protected async Task<Guid?> VersionAsync(string table, object id, string? tenantId = null) =>
        (Guid?) await ScalarAsync(
            $"select mt_version from \"{Schema}\".{table} where id = {Literal(id)}{TenantPredicate(tenantId)}");

    /// <summary>
    /// The <c>mt_last_modified</c> of one row, as UTC.
    /// </summary>
    /// <remarks>
    /// Npgsql hands a <c>timestamptz</c> back as a <see cref="DateTime" /> with <c>Kind == Utc</c>, not as
    /// a <see cref="DateTimeOffset" />, so the cast is written for what actually arrives.
    /// </remarks>
    protected async Task<DateTime?> LastModifiedAsync(string table, object id, string? tenantId = null) =>
        await ScalarAsync(
                $"select mt_last_modified from \"{Schema}\".{table} where id = {Literal(id)}{TenantPredicate(tenantId)}")
            switch
        {
            DateTime value => value,
            DateTimeOffset value => value.UtcDateTime,
            _ => null,
        };

    /// <summary>Whether one row is marked soft-deleted.</summary>
    protected async Task<bool?> IsDeletedAsync(string table, object id) =>
        (bool?) await ScalarAsync($"select mt_deleted from \"{Schema}\".{table} where id = {Literal(id)}");

    /// <summary>Whether the row is there at all.</summary>
    protected async Task<bool> RowExistsAsync(string table, object id) =>
        await ScalarAsync($"select 1 from \"{Schema}\".{table} where id = {Literal(id)}") is not null;

    private static string TenantPredicate(string? tenantId) =>
        tenantId is null ? string.Empty : $" and tenant_id = '{tenantId}'";

    private static string Literal(object id) => id switch
    {
        Guid guid => $"'{guid}'::uuid",
        int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        long number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => $"'{id}'",
    };
}

/// <summary>
/// The host's authorization policies, as a test drives them.
/// </summary>
/// <remarks>
/// A double rather than a real policy engine because what these tests are about is <em>whether the
/// studio asks</em> and with what resource — the policy engine is ASP.NET Core's and is not ours to
/// test. <see cref="Calls" /> is what lets a test see that the capability travelled on the second ask.
/// </remarks>
public sealed class FakeStoreAuthorizationService : IAuthorizationService
{
    private Func<MartenStoreResource, bool> rule = static _ => true;

    /// <summary>Every policy and resource the studio asked about, in order.</summary>
    public List<(string Policy, MartenStoreResource Resource)> Calls { get; } = [];

    /// <summary>Allows everything again.</summary>
    public void AllowEverything() => rule = static _ => true;

    /// <summary>Refuses everything.</summary>
    public void DenyEverything() => rule = static _ => false;

    /// <summary>Allows reads and refuses writes, which is what a viewer looks like.</summary>
    public void DenyWrites() => rule = static resource => resource.Capability is null;

    /// <summary>Allows or refuses by a rule over the resource.</summary>
    public void Allow(Func<MartenStoreResource, bool> predicate) => rule = predicate;

    public Task<AuthorizationResult> AuthorizeAsync(
        ClaimsPrincipal user,
        object? resource,
        IEnumerable<IAuthorizationRequirement> requirements) =>
        Task.FromResult(resource is MartenStoreResource store && rule(store)
            ? AuthorizationResult.Success()
            : AuthorizationResult.Failed());

    public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
    {
        if (resource is MartenStoreResource store)
        {
            Calls.Add((policyName, store));
        }

        return Task.FromResult(resource is MartenStoreResource allowed && rule(allowed)
            ? AuthorizationResult.Success()
            : AuthorizationResult.Failed());
    }
}

/// <summary>The circuit's visitor, as a test names them.</summary>
public sealed class FakeAuthenticationStateProvider : AuthenticationStateProvider
{
    private AuthenticationState state = new(new ClaimsPrincipal(new ClaimsIdentity()));

    /// <summary>Signs in as <paramref name="name" />.</summary>
    public void SignIn(string name)
    {
        state = new AuthenticationState(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "test")));
        NotifyAuthenticationStateChanged(Task.FromResult(state));
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(state);
}

/// <summary>One line the studio wrote to the application's logger.</summary>
/// <param name="Level">How loud it was.</param>
/// <param name="EventId">The pinned event id, which is the contract an operator queries on.</param>
/// <param name="Message">The formatted message.</param>
public sealed record CapturedLine(LogLevel Level, EventId EventId, string Message);

/// <summary>
/// Captures what the studio logs, so a test can assert the event id rather than the wording.
/// </summary>
public sealed class CapturingLogs
{
    private readonly List<CapturedLine> lines = [];
    private readonly Lock gate = new();

    /// <summary>Everything logged so far, in order.</summary>
    public IReadOnlyList<CapturedLine> Lines
    {
        get
        {
            lock (gate)
            {
                return lines.ToArray();
            }
        }
    }

    /// <summary>The event ids logged so far.</summary>
    public IReadOnlyList<int> EventIds => [.. Lines.Select(static x => x.EventId.Id)];

    /// <summary>A logger that writes into this capture and nowhere else.</summary>
    public ILogger<T> CreateLogger<T>() => new Logger<T>(this);

    private void Add(CapturedLine line)
    {
        lock (gate)
        {
            lines.Add(line);
        }
    }

    private sealed class Logger<T>(CapturingLogs logs) : ILogger<T>
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
            logs.Add(new CapturedLine(logLevel, eventId, formatter(state, exception)));
        }
    }
}
