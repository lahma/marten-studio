using System.Security.Claims;

using Marten;
using Marten.Schema;

using MartenStudio.Services;
using MartenStudio.Services.Documents;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// The documents browser's navigation paths, against a schema that starts completely empty.
/// </summary>
/// <remarks>
/// <para>
/// <b>P2-fix B4, and the documents half of what <c>SchemaNoDdlLiveTests</c> proves for the Schema screen.</b>
/// The collections rail called <c>IMartenDatabase.DocumentTables()</c> and the detail pane called
/// <c>IMartenDatabase.Functions()</c> — once per document opened — to find out whether an
/// <c>mt_upsert_*</c> function exists. Both are <c>AllObjects()</c>: <c>AllObjects()</c> is
/// <c>BuildFeatureSchemas().SelectMany(x =&gt; x.Objects)</c>, Marten's feature set includes
/// <c>database.Sequences</c> for any store with a numeric or HiLo id, and that is a
/// <c>Lazy&lt;SequenceFactory&gt;</c> whose factory blocks on <c>Migrator.ApplyAllAsync</c> under the
/// database's own <c>AutoCreate</c>. So opening the documents tab, or a document, created <c>mt_hilo</c>
/// and <c>mt_get_next_hi</c> — on a connection of Marten's own, with no command timeout and no
/// cancellation token.
/// </para>
/// <para>
/// The store below is shaped to make that happen if it still can: a document type with a <c>long</c> id
/// (so the HiLo feature is active), <c>AutoCreate.CreateOrUpdate</c> (Marten's own default, and the mode
/// the migration would run under), and two schemas with nothing in them.
/// </para>
/// </remarks>
public class DocumentsNoDdlLiveTests(PostgresFixture fixture)
{
    private static readonly StudioScope Scope = new("default", string.Empty, null);

    /// <summary>
    /// Acceptance, P2-fix B4: the rail and a document detail create nothing.
    /// </summary>
    [PostgresFact]
    public async Task Opening_the_rail_and_a_document_creates_no_object_at_all()
    {
        await using Host host = await StartAsync(nameof(Opening_the_rail_and_a_document_creates_no_object_at_all));

        SchemaObjects before = await host.ReadObjectsAsync();
        before.Should().Be(SchemaObjects.Empty, "the fixture created two empty schemas and nothing else");

        // Exactly what a visitor's navigation does: land on the rail, open a collection, open a document.
        CollectionRail rail = await host.DocumentsAsync(x => x.GetCollectionsAsync(Scope));
        DocumentPage page = await host.DocumentsAsync(x => x.ListAsync(Scope, "numbered", new DocumentListRequest()));
        DocumentDetailResult detail = await host.DocumentsAsync(x => x.GetDocumentAsync(Scope, "numbered", "1"));
        RecentDocuments recent = await host.DocumentsAsync(x => x.ListRecentAsync(Scope, 20));

        // Each answered, rather than failing in a way that would make the counts trivially unchanged.
        rail.Error.Should().BeNull();
        rail.Groups.SelectMany(x => x.Collections).Select(x => x.Alias).Should().Contain("numbered");

        // The table itself was never created either, so the reads that touch it say so as values.
        page.State.Should().Be(DocumentListState.Failed);
        detail.Found.Should().BeFalse();
        recent.Rows.Should().BeEmpty();

        SchemaObjects after = await host.ReadObjectsAsync();

        after.Should().Be(before,
            "browsing documents must never run DDL - not even Marten's own HiLo bookkeeping");
        after.Should().Be(SchemaObjects.Empty);
    }

    /// <summary>
    /// The same, with the document tables actually there: the detail pane used to ask for the function
    /// list on every load, which is the call that applied the migration.
    /// </summary>
    [PostgresFact]
    public async Task Reading_a_real_document_creates_nothing_beyond_what_the_migration_made()
    {
        await using Host host = await StartAsync(nameof(Reading_a_real_document_creates_nothing_beyond_what_the_migration_made));

        // The host's own deployment applied the schema. Everything after this line is the studio reading.
        await host.ApplyAsync();
        await host.WriteAsync();

        SchemaObjects before = await host.ReadObjectsAsync();
        before.Tables.Should().Contain("mt_doc_numbered");

        DocumentPage page = await host.DocumentsAsync(x => x.ListAsync(Scope, "numbered", new DocumentListRequest()));

        page.State.Should().Be(DocumentListState.Loaded, page.Error);

        DocumentDetailResult detail = await host.DocumentsAsync(
            x => x.GetDocumentAsync(Scope, "numbered", page.Rows[0].Id));

        detail.Found.Should().BeTrue(detail.NotFound?.Message);
        detail.Detail!.UpsertFunction.Should().BeNull("Marten 9 writes with inline SQL and makes no such function");

        (await host.ReadObjectsAsync()).Should().Be(before, "reading a document creates nothing");
    }

    /// <summary>
    /// The anti-vacuity theory: the danger the two tests above measure is real on this Marten.
    /// </summary>
    /// <remarks>
    /// Without it, a Marten release that stopped applying migrations from <c>DocumentTables()</c> would
    /// turn both tests into assertions that nothing happens when nothing could have happened. This calls
    /// the two methods the documents browser refuses to call and asserts that they really do create
    /// Marten's HiLo objects on a schema that did not have them.
    /// </remarks>
    [PostgresFact]
    public async Task Martens_own_DocumentTables_and_Functions_really_do_create_the_HiLo_objects()
    {
        await using Host host = await StartAsync(nameof(Martens_own_DocumentTables_and_Functions_really_do_create_the_HiLo_objects));

        (await host.ReadObjectsAsync()).Should().Be(SchemaObjects.Empty);

        IDocumentStore store = host.Services.GetRequiredService<IDocumentStore>();
        IReadOnlyList<Marten.Storage.IMartenDatabase> databases = await store.Storage.AllDatabases();

        // The exact calls DiscoverAsync, LoadContextAsync and the detail pane used to make.
        await databases[0].DocumentTables();
        await databases[0].Functions();

        SchemaObjects after = await host.ReadObjectsAsync();

        after.Tables.Should().Contain("mt_hilo",
            "this is the DDL a read path used to run - if Marten has stopped doing it, the two tests " +
            "above no longer prove anything and this one is where that is noticed");
        after.Routines.Should().Contain("mt_get_next_hi");
    }

    private async Task<Host> StartAsync(string name)
    {
        string schema = "p2n_" + name.ToLowerInvariant()[..Math.Min(name.Length, 40)];

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

        /// <summary>Something to read back.</summary>
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

        internal async Task<T> DocumentsAsync<T>(Func<IDocumentDataService, Task<T>> action)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IDocumentDataService>());
        }

        /// <summary>What the host's own deployment would have done, which is not the studio's business.</summary>
        public async Task ApplyAsync()
        {
            IDocumentStore store = provider.GetRequiredService<IDocumentStore>();

            await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        }

        public async Task WriteAsync()
        {
            IDocumentStore store = provider.GetRequiredService<IDocumentStore>();

            await using IDocumentSession session = store.LightweightSession();

            session.Store(new NumberedThing { Name = "one" });

            await session.SaveChangesAsync();
        }

        /// <summary>
        /// What <c>information_schema</c> says is in the two schemas, read straight from the catalog on a
        /// connection of the test's own, so nothing about the studio's own code is in the measurement.
        /// </summary>
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
