using System.Security.Claims;

using JasperFx;

using Marten;
using Marten.Schema;

using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Services.Live;
using MartenStudio.Services.Projections;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests;

/// <summary>
/// One built studio over a pair of schemas that are empty, and a reading of everything in them.
/// </summary>
/// <remarks>
/// <para>
/// The harness the <c>NoDdl</c> suites share. Two schemas are created and nothing is put in them, the
/// store is configured with a document type and an event type so that there is an event store to fail to
/// create, and every test then reads <c>information_schema</c> on a connection of its own before and
/// after whatever it is measuring - so the measurement contains none of the studio's own code.
/// </para>
/// <para>
/// <c>AutoCreateSchemaObjects</c> is a parameter and Marten's own default is the usual value, because
/// that is the mode a read path would have run a migration under and leaving it at the default is what
/// makes the measurement mean anything. <c>AutoCreate.None</c> is the other case worth testing: Marten's
/// schema-building calls <em>throw</em> under it rather than migrating, and a studio that answered a
/// stack trace there would have swapped one failure for another.
/// </para>
/// </remarks>
internal sealed class EmptySchemaStudio : IAsyncDisposable
{
    private readonly ServiceProvider provider;
    private readonly string connectionString;

    private EmptySchemaStudio(ServiceProvider provider, string connectionString, string schema)
    {
        this.provider = provider;
        this.connectionString = connectionString;
        Schema = schema;
    }

    /// <summary>The document schema.</summary>
    public string Schema { get; }

    /// <summary>The event store's schema, which is where all the event objects would be created.</summary>
    public string EventSchema => Schema + "_events";

    /// <summary>The container, for the tests that call Marten directly to prove the danger is real.</summary>
    public IServiceProvider Services => provider;

    /// <summary>
    /// The scope every call is made in. The database is left empty, which resolves to the store's only
    /// one - the same thing a freshly opened studio does.
    /// </summary>
    public static StudioScope Scope { get; } = new("default", string.Empty, null);

    /// <summary>Builds a studio over two freshly created, empty schemas.</summary>
    /// <param name="fixture">The shared Postgres.</param>
    /// <param name="prefix">A short prefix that keeps one test class's schemas out of another's.</param>
    /// <param name="name">The test's own name, which the schema is named after.</param>
    /// <param name="autoCreate">
    /// The store's <c>AutoCreateSchemaObjects</c>. Marten's own default unless a test is about the other
    /// mode.
    /// </param>
    /// <param name="configure">
    /// Anything else the store needs - a projection registration, say. Run last, so a test can override
    /// what is set here.
    /// </param>
    public static async Task<EmptySchemaStudio> StartAsync(
        PostgresFixture fixture,
        string prefix,
        string name,
        AutoCreate autoCreate = AutoCreate.CreateOrUpdate,
        Action<StoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        string schema = prefix + name.ToLowerInvariant()[..Math.Min(name.Length, 40)];

        await fixture.CreateSchemaAsync(schema);
        await fixture.CreateSchemaAsync(schema + "_events");

        var services = new ServiceCollection();

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, SignedInProvider>();

        services.AddMarten(options =>
        {
            options.Connection(fixture.ConnectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = schema + "_events";

            options.AutoCreateSchemaObjects = autoCreate;

            options.Schema.For<Note>();
            options.Events.AddEventType<NoteWritten>();

            configure?.Invoke(options);
        });

        services.AddMartenStudio(options =>
        {
            options.AuthorizationPolicy = null;

            // The tenant probe is a read of its own and is not what these tests are measuring.
            options.DiscoverTenantIds = false;
        });

        return new EmptySchemaStudio(services.BuildServiceProvider(), fixture.ConnectionString, schema);
    }

    /// <summary>
    /// One circuit's worth of the sidebar: settle the scope the layout settles, then read the badges.
    /// </summary>
    /// <remarks>
    /// A scope of its own per call, because <c>StudioState</c> and <c>NavIndicatorService</c> are both
    /// scoped and a second call is a second circuit - which is also what makes a repeat meaningful, since
    /// the snapshot cache is the process-wide singleton the two circuits share.
    /// </remarks>
    public async Task<NavIndicators> BadgesAsync()
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        StudioState state = scope.ServiceProvider.GetRequiredService<StudioState>();
        await state.EnsureInitializedAsync(TestContext.Current.CancellationToken);

        state.ActiveScope.Should().NotBeNull("the layout settles a scope before anything renders");

        INavIndicatorService badges = scope.ServiceProvider.GetRequiredService<INavIndicatorService>();

        return await badges.ReadAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>One call against the events service, in a scope of its own.</summary>
    public async Task<T> EventsAsync<T>(Func<IEventDataService, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IEventDataService>());
    }

    /// <summary>One call against the projections service, in a scope of its own.</summary>
    public async Task<T> ProjectionsAsync<T>(Func<IProjectionDataService, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IProjectionDataService>());
    }

    /// <summary>
    /// What <c>information_schema</c> says is in the two schemas, read straight from the catalog on a
    /// connection of the test's own, so nothing about the studio's own code is in the measurement.
    /// </summary>
    public async Task<SchemaObjects> ReadObjectsAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        return new SchemaObjects(
            await ReadAsync(connection, "select table_name from information_schema.tables where table_schema = any(@schemas) order by table_name"),
            await ReadAsync(connection, "select routine_name from information_schema.routines where routine_schema = any(@schemas) order by routine_name"),
            await ReadAsync(connection, "select sequence_name from information_schema.sequences where sequence_schema = any(@schemas) order by sequence_name"));
    }

    public async ValueTask DisposeAsync() => await provider.DisposeAsync();

    private async Task<IReadOnlyList<string>> ReadAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schemas", new[] { Schema, EventSchema });

        List<string> names = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>A document type with a Guid id, so nothing here is about Marten's HiLo feature.</summary>
    [DocumentAlias("note")]
    public class Note
    {
        /// <summary>The identity.</summary>
        public Guid Id { get; set; }

        /// <summary>Something to read back.</summary>
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>An event type, so the store has an event store to fail to create.</summary>
    /// <param name="Text">What was written.</param>
    public record NoteWritten(string Text);

    /// <summary>Somebody is signed in; who they are is not what these tests are about.</summary>
    private sealed class SignedInProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "integration")], "test"))));
    }
}

/// <summary>Every object in the two schemas, by name.</summary>
/// <param name="Tables">Tables, including partitioned ones.</param>
/// <param name="Routines">Functions and procedures - Marten installs a great many.</param>
/// <param name="Sequences">Sequences, which is where <c>mt_events_sequence</c> would appear.</param>
internal sealed record SchemaObjects(
    IReadOnlyList<string> Tables,
    IReadOnlyList<string> Routines,
    IReadOnlyList<string> Sequences)
{
    /// <summary>Nothing at all, which is what an empty schema pair holds.</summary>
    public static SchemaObjects Empty { get; } = new([], [], []);

    public bool Equals(SchemaObjects? other) =>
        other is not null
        && Tables.SequenceEqual(other.Tables)
        && Routines.SequenceEqual(other.Routines)
        && Sequences.SequenceEqual(other.Sequences);

    public override int GetHashCode() => HashCode.Combine(Tables.Count, Routines.Count, Sequences.Count);

    public override string ToString() =>
        $"tables [{string.Join(", ", Tables)}], routines [{string.Join(", ", Routines)}], " +
        $"sequences [{string.Join(", ", Sequences)}]";
}
