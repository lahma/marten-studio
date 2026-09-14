using System.Collections.Concurrent;
using System.Security.Claims;

using JasperFx;

using Marten;
using Marten.Schema;

using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Schema;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Schema;

/// <summary>
/// A real Marten store in a schema of its own, reached the way a browser reaches it: through Marten
/// Studio's own DI registration and its own scope resolver.
/// </summary>
/// <remarks>
/// <para>
/// The point of driving the tests through <see cref="ISchemaDataService" /> rather than through
/// <c>IMartenDatabase</c> directly is that the capability gate, the write policy and the audit log are
/// then <em>exercised</em> rather than bypassed. A test that called
/// <c>ApplyAllConfiguredChangesToDatabaseAsync</c> itself would prove that Weasel works, which nobody
/// doubted.
/// </para>
/// <para>
/// Two hosts over one schema. The <see cref="Studio" /> host is the store as it was applied; the
/// <see cref="Drifted" /> host is the same store with one extra index in its configuration and
/// <c>AutoCreate.None</c>, which is exactly the shape a deployment has between a code change and its
/// migration. That is what makes "the database does not match the configuration" reproducible without
/// touching the database by hand.
/// </para>
/// </remarks>
internal sealed class SchemaFixture : IAsyncDisposable
{
    private readonly List<ServiceProvider> providers = [];

    private SchemaFixture(string connectionString, string schema)
    {
        ConnectionString = connectionString;
        Schema = schema;
    }

    /// <summary>The container's connection string.</summary>
    public string ConnectionString { get; }

    /// <summary>The document schema this fixture owns.</summary>
    public string Schema { get; }

    /// <summary>The event schema this fixture owns.</summary>
    public string EventSchema => Schema + "_events";

    /// <summary>The studio, over the store as it was applied.</summary>
    public StudioHost Studio { get; private set; } = null!;

    /// <summary>The scope every call is made for: this store, its one database, no tenant filter.</summary>
    public static StudioScope Scope { get; } = new("default", string.Empty, null);

    /// <summary>
    /// Creates the schemas, builds the studio over a store with four document types and an event store,
    /// and applies every configured change so the database starts in sync.
    /// </summary>
    public static async Task<SchemaFixture> StartAsync(
        PostgresFixture postgres,
        string schema,
        Action<MartenStudioOptions>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        var fixture = new SchemaFixture(postgres.ConnectionString, schema);

        await postgres.CreateSchemaAsync(schema, cancellationToken);
        await postgres.CreateSchemaAsync(schema + "_events", cancellationToken);

        fixture.Studio = fixture.BuildHost(extraIndex: false, configure);

        IDocumentStore store = fixture.Studio.Services.GetRequiredService<IDocumentStore>();
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        return fixture;
    }

    /// <summary>
    /// A second studio over the same schema whose configuration asks for one more index than the
    /// database has, and which will never create it on its own.
    /// </summary>
    public StudioHost Drifted(Action<MartenStudioOptions>? configure = null) =>
        BuildHost(extraIndex: true, configure);

    /// <summary>The name of the index the drifted configuration asks for and the database does not have.</summary>
    public const string DriftIndexName = "mt_doc_customer_idx_name";

    /// <summary>Every index currently on one table, by name.</summary>
    public async Task<IReadOnlyList<string>> IndexNamesAsync(string table, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "select indexname from pg_indexes where schemaname = @schema and tablename = @table order by indexname",
            connection);

        command.Parameters.AddWithValue("schema", Schema);
        command.Parameters.AddWithValue("table", table);

        List<string> names = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ServiceProvider provider in providers)
        {
            await provider.DisposeAsync();
        }

        providers.Clear();
    }

    private StudioHost BuildHost(bool extraIndex, Action<MartenStudioOptions>? configure)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new CapturingLoggerProvider());
        });

        services.AddAuthorization(options =>
        {
            // Two named policies, so a test can say "the host wired up a write policy that refuses" and
            // "the host wired up one that does not" without inventing an authorization handler.
            options.AddPolicy(DenyPolicy, policy => policy.RequireAssertion(static _ => false));
            options.AddPolicy(AllowPolicy, policy => policy.RequireAssertion(static _ => true));
        });

        services.AddSingleton<AuthenticationStateProvider, TestAuthenticationStateProvider>();

        services.AddMarten(options =>
        {
            options.Connection(ConnectionString);
            options.DatabaseSchemaName = Schema;
            options.Events.DatabaseSchemaName = EventSchema;

            // The studio must never be the thing that creates schema behind somebody's back: every host
            // here is None, and the only writes are the fixture's own explicit apply and the one the
            // ApplySchemaChanges capability performs.
            options.AutoCreateSchemaObjects = AutoCreate.None;

            MartenRegistry.DocumentMappingExpression<SchemaCustomer> customer = options.Schema.For<SchemaCustomer>()
                .Duplicate(x => x.Email, configure: static index => index.IsUnique = true);

            if (extraIndex)
            {
                customer.Index(x => x.Name);
            }

            options.Schema.For<SchemaOrder>().SoftDeleted();
            options.Schema.For<SchemaInvoice>().MultiTenanted();
            options.Schema.For<SchemaNote>();

            options.Events.AddEventType<SchemaThingHappened>();
        });

        services.AddMartenStudio(options =>
        {
            options.AuthorizationPolicy = null;
            configure?.Invoke(options);
        });

        ServiceProvider provider = services.BuildServiceProvider();
        providers.Add(provider);

        return new StudioHost(provider);
    }

    /// <summary>The policy name a test uses for a write policy that refuses.</summary>
    public const string DenyPolicy = "studio-deny";

    /// <summary>The policy name a test uses for a write policy that allows.</summary>
    public const string AllowPolicy = "studio-allow";

    /// <summary>One built studio, and a scope to reach its services through.</summary>
    internal sealed class StudioHost(ServiceProvider provider)
    {
        /// <summary>The container.</summary>
        public IServiceProvider Services => provider;

        /// <summary>The process-wide audit ring.</summary>
        public StudioActionLogService Audit => provider.GetRequiredService<StudioActionLogService>();

        /// <summary>Everything the log saw, from the provider registered in this host.</summary>
        public static IReadOnlyList<CapturedLogEntry> LogEntries => CapturingLoggerProvider.Entries;

        /// <summary>Runs one action against a fresh scope, the way one page render would.</summary>
        public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
        {
            ArgumentNullException.ThrowIfNull(action);

            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await action(scope.ServiceProvider);
        }

        /// <summary>The schema service, for one scope.</summary>
        public Task<T> SchemaAsync<T>(Func<ISchemaDataService, Task<T>> action)
        {
            ArgumentNullException.ThrowIfNull(action);

            return InScopeAsync(services => action(services.GetRequiredService<ISchemaDataService>()));
        }

        /// <summary>The configuration service, for one scope.</summary>
        public Task<T> ConfigurationAsync<T>(Func<IConfigurationService, Task<T>> action)
        {
            ArgumentNullException.ThrowIfNull(action);

            return InScopeAsync(services => action(services.GetRequiredService<IConfigurationService>()));
        }
    }

    /// <summary>Somebody is signed in; who they are is not what these tests are about.</summary>
    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "integration")], "test"))));
    }
}

/// <summary>One log entry, kept so a test can assert on the event id the audit trail promises.</summary>
/// <param name="EventId">The numeric event id - 9206 for an applied schema change.</param>
/// <param name="Message">The formatted message.</param>
internal sealed record CapturedLogEntry(int EventId, string Message);

/// <summary>
/// Collects <c>ILogger</c> events so a test can assert that event 9206 was written and what it said.
/// </summary>
/// <remarks>
/// Static storage, because the studio's own registration builds the logger factory and there is no seam
/// to hand an instance through. The tests that read it filter by event id and by content, so entries
/// from another test in the same process are ignored rather than confusing.
/// </remarks>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private static readonly ConcurrentQueue<CapturedLogEntry> Captured = new();

    /// <summary>Everything logged so far in this process.</summary>
    public static IReadOnlyList<CapturedLogEntry> Entries => [.. Captured];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger();

    public void Dispose() => GC.SuppressFinalize(this);

    private sealed class CapturingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            Captured.Enqueue(new CapturedLogEntry(eventId.Id, formatter(state, exception)));
        }
    }
}

/// <summary>A type with a duplicated unique field, and in the drifted configuration a second index.</summary>
[DocumentAlias("customer")]
public class SchemaCustomer
{
    public Guid Id { get; set; }

    public string Email { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

/// <summary>A soft-deleted type.</summary>
[DocumentAlias("order")]
public class SchemaOrder
{
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }

    public decimal Total { get; set; }
}

/// <summary>A conjoined-tenancy type.</summary>
[DocumentAlias("invoice")]
public class SchemaInvoice
{
    public Guid Id { get; set; }

    public decimal Amount { get; set; }
}

/// <summary>A type with nothing but its primary key, for the Indexes tab's advice.</summary>
[DocumentAlias("note")]
public class SchemaNote
{
    public Guid Id { get; set; }

    public string Text { get; set; } = string.Empty;
}

/// <summary>One event type, so the event store's tables are configured and created.</summary>
/// <param name="Id">The thing it happened to.</param>
/// <param name="What">What happened.</param>
public record SchemaThingHappened(Guid Id, string What);
