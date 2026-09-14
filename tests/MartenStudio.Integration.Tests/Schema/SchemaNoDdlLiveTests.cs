using System.Security.Claims;

using Marten;
using Marten.Schema;

using MartenStudio.Services;
using MartenStudio.Services.Schema;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Schema;

/// <summary>
/// The Schema screen's navigation paths, against a schema that starts completely empty.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the test the P7 review's blocking finding exists for.</b> <c>AllSchemaNames()</c> is
/// <c>AllObjects().Select(x =&gt; x.Identifier.Schema)</c>; <c>AllObjects()</c> is
/// <c>BuildFeatureSchemas().SelectMany(x =&gt; x.Objects)</c>; Marten's <c>BuildFeatureSchemas()</c> is
/// <c>StorageFeatures.AllActiveFeatures(this)</c>, which yields <c>database.Sequences</c> whenever any
/// document type has a numeric or HiLo id; and <c>MartenDatabase.Sequences</c> is a
/// <c>Lazy&lt;SequenceFactory&gt;</c> whose factory calls <c>generateOrUpdateFeature(...)</c> and blocks
/// on it - running <c>Migrator.ApplyAllAsync</c> under the database's own <c>AutoCreate</c>. So a schema
/// screen that called either on navigation created <c>mt_hilo</c> and <c>mt_get_next_hi</c> because
/// somebody opened a tab.
/// </para>
/// <para>
/// The store below is deliberately shaped to make that happen if it can: a document type with a
/// <c>long</c> id (so the HiLo sequence feature is active), <c>AutoCreate.CreateOrUpdate</c> (Marten's
/// own default, and the mode under which the migration would run), and two schemas with nothing in them.
/// If any of Tables, Indexes or Functions reaches Weasel, the counts below move.
/// </para>
/// </remarks>
public class SchemaNoDdlLiveTests(PostgresFixture fixture)
{
    private static readonly StudioScope Scope = new("default", string.Empty, null);

    /// <summary>
    /// Acceptance, P7-fix B1: every tab's navigation path creates nothing.
    /// </summary>
    [PostgresFact]
    public async Task Opening_every_tab_creates_no_object_at_all()
    {
        await using Host host = await StartAsync(nameof(Opening_every_tab_creates_no_object_at_all));

        SchemaObjects before = await host.ReadObjectsAsync();
        before.Should().Be(SchemaObjects.Empty, "the fixture created two empty schemas and nothing else");

        // Exactly what a visitor's navigation does: land on Tables, then Indexes, then Functions.
        SchemaTables tables = await host.SchemaAsync(x => x.TablesAsync(Scope));
        SchemaIndexes indexes = await host.SchemaAsync(x => x.IndexesAsync(Scope));
        SchemaFunctions functions = await host.SchemaAsync(x => x.FunctionsAsync(Scope));

        // Each answered, rather than failing in a way that would make the counts trivially unchanged.
        tables.Reason.Should().BeNull();
        indexes.Reason.Should().BeNull();
        functions.Reason.Should().BeNull();

        tables.Schemas.Should().Contain(host.Schema).And.Contain(host.EventSchema,
            "the schema list comes from StoreOptions, and the event schema is part of it");

        SchemaObjects after = await host.ReadObjectsAsync();

        after.Should().Be(before,
            "navigating the Schema screen must never run DDL - not even Marten's own HiLo bookkeeping");
        after.Should().Be(SchemaObjects.Empty);
    }

    /// <summary>
    /// The other half of the same acceptance: the explicit actions are the only ones that may create
    /// anything, and what they may create is Marten's own bookkeeping - which is what their wording says.
    /// </summary>
    [PostgresFact]
    public async Task Only_the_explicit_DDL_action_may_create_Martens_own_bookkeeping_objects()
    {
        await using Host host = await StartAsync(nameof(Only_the_explicit_DDL_action_may_create_Martens_own_bookkeeping_objects));

        await host.SchemaAsync(x => x.TablesAsync(Scope));
        (await host.ReadObjectsAsync()).Should().Be(SchemaObjects.Empty);

        // The button-gated action. It is allowed to create the HiLo objects; nothing else may appear,
        // because the script is written to a string rather than executed.
        DdlScript script = await host.SchemaAsync(x => x.DdlAsync(Scope));
        script.Reason.Should().BeNull(script.Reason);

        SchemaObjects after = await host.ReadObjectsAsync();

        after.Tables.Should().Equal(["mt_hilo"],
            "building the script builds Marten's feature schemas, and the HiLo feature applies itself - " +
            "which is exactly what the DDL tab's button says it may do, and why it is a button");
        after.Routines.Should().Equal(["mt_get_next_hi"]);
        after.Sequences.Should().BeEmpty(
            "nothing in the script itself is executed - the tables and functions it describes are not there");
        script.Text.Should().Contain("mt_doc_numbered", "the script describes the table it did not create");
    }

    /// <summary>
    /// The anti-vacuity theory: the danger the two tests above measure is real on this Marten.
    /// </summary>
    /// <remarks>
    /// Without this, a Marten release that stopped applying migrations from <c>AllSchemaNames()</c>
    /// would turn both tests into assertions that nothing happens when nothing could have happened, and
    /// they would go on passing while quietly proving less and less. This calls the method the studio
    /// refuses to call and asserts that it really does create Marten's HiLo objects on a schema that did
    /// not have them - so a change in Marten shows up here as a failure, with a name that says what
    /// changed, rather than as silence.
    /// </remarks>
    [PostgresFact]
    public async Task Martens_own_AllSchemaNames_really_does_create_the_HiLo_objects()
    {
        await using Host host = await StartAsync(nameof(Martens_own_AllSchemaNames_really_does_create_the_HiLo_objects));

        (await host.ReadObjectsAsync()).Should().Be(SchemaObjects.Empty);

        IDocumentStore store = host.Services.GetRequiredService<IDocumentStore>();
        IReadOnlyList<Marten.Storage.IMartenDatabase> databases = await store.Storage.AllDatabases();

        // The exact call SchemaDataService.SchemaNames used to make on every navigation.
        databases[0].AllSchemaNames().Should().NotBeEmpty();

        SchemaObjects after = await host.ReadObjectsAsync();

        after.Tables.Should().Contain("mt_hilo",
            "this is the DDL a read path used to run - if Marten has stopped doing it, the two tests " +
            "above no longer prove anything and this one is where that is noticed");
        after.Routines.Should().Contain("mt_get_next_hi");
    }

    private async Task<Host> StartAsync(string name)
    {
        string schema = "p7n_" + name.ToLowerInvariant()[..Math.Min(name.Length, 40)];

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

            // Marten's own default, said out loud: this is the mode a read path would have run a
            // migration under, and leaving it at the default is what makes the test meaningful.
            options.AutoCreateSchemaObjects = JasperFx.AutoCreate.CreateOrUpdate;

            // A long id means StorageFeatures.SequenceIsRequired() is true, which is what puts the lazy
            // HiLo feature into AllActiveFeatures and made a read create schema.
            options.Schema.For<NumberedThing>();
        });

        services.AddMartenStudio(options => options.AuthorizationPolicy = null);

        return new Host(services.BuildServiceProvider(), fixture.ConnectionString, schema);
    }

    /// <summary>A document type with a numeric id, so Marten's HiLo sequence feature is active.</summary>
    [DocumentAlias("numbered")]
    public class NumberedThing
    {
        /// <summary>A <c>long</c> id, which Marten backs with the HiLo sequence.</summary>
        public long Id { get; set; }

        /// <summary>Something to index, if anything ever indexed it.</summary>
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>Every object in the two schemas, by name.</summary>
    private sealed record SchemaObjects(
        IReadOnlyList<string> Tables,
        IReadOnlyList<string> Routines,
        IReadOnlyList<string> Sequences)
    {
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

    /// <summary>One built studio over one pair of empty schemas.</summary>
    private sealed class Host(ServiceProvider provider, string connectionString, string schema) : IAsyncDisposable
    {
        public string Schema => schema;

        public string EventSchema => schema + "_events";

        /// <summary>The container, for the one test that calls Marten directly.</summary>
        public IServiceProvider Services => provider;

        public async Task<T> SchemaAsync<T>(Func<ISchemaDataService, Task<T>> action)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<ISchemaDataService>());
        }

        /// <summary>
        /// What <c>information_schema</c> says is in the two schemas.
        /// </summary>
        /// <remarks>
        /// Read straight from the catalog on a connection of the test's own, so nothing about the
        /// studio's own code is in the measurement.
        /// </remarks>
        public async Task<SchemaObjects> ReadObjectsAsync()
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

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
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }
    }

    /// <summary>Somebody is signed in; who they are is not what these tests are about.</summary>
    private sealed class SignedInProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "integration")], "test"))));
    }
}
