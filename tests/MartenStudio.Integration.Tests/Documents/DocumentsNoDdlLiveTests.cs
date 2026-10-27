using System.Security.Claims;

using Marten;
using Marten.Schema;

using MartenStudio.Services.Documents;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;
using PostgresStudio.Services;

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
    /// M5: the newest-first orders are the studio's own list read with another <c>order by</c> - no Marten call, so nothing
    /// to ensure - on empty schemas and on a deployed one alike.
    /// </summary>
    [PostgresFact]
    public async Task Ordering_newest_first_creates_nothing()
    {
        await using Host host = await StartAsync(nameof(Ordering_newest_first_creates_nothing));

        foreach (string key in NewestFirstKeys)
        {
            await host.DocumentsAsync(x => x.ListAsync(Scope, "numbered", Newest(key)));
        }

        (await host.ReadObjectsAsync()).Should().Be(SchemaObjects.Empty, "an ordered read of a table that is not there creates it no more than an unordered one");

        await host.ApplyAsync();
        await host.WriteAsync();
        SchemaObjects before = await host.ReadObjectsAsync();

        DocumentPage page = await host.DocumentsAsync(x => x.ListAsync(Scope, "numbered", Newest("meta:LastModified")));

        page.SortKey.Should().Be("meta:LastModified");
        page.Direction.Should().Be(PostgresStudio.Services.Query.SortDirection.Descending);
        page.Sql.Should().Contain("desc nulls last", "the same order Marten 9.52's metadata ordering asks for");

        (await host.ReadObjectsAsync()).Should().Be(before, "ordering a deployed table newest first creates nothing");
    }

    /// <summary>
    /// M5: Marten's own preview of a save (9.53+) opens no session and no connection - here on schemas with nothing in them,
    /// where a preview that ensured the type's storage would leave its table behind, and then through the studio's edit
    /// preview on a deployed table. Below 9.53 the answer is the line naming the Marten it needs, and nothing is created
    /// either.
    /// </summary>
    [PostgresFact]
    public async Task Martens_own_edit_preview_creates_nothing()
    {
        await using Host host = await StartAsync(
            nameof(Martens_own_edit_preview_creates_nothing),
            static options => options.Capabilities.EditDocuments = true);

        IDocumentStore store = host.Services.GetRequiredService<IDocumentStore>();
        bool available = MartenStudio.Services.Configuration.RuntimeFeature.DocumentEditPreview.IsAvailable(
            MartenStudio.Services.Configuration.MartenRuntime.Current);

        const string json = """{"Id":1,"Name":"one"}""";
        string roundTrip = store.Options.Serializer().ToJson(new NumberedThing { Id = 1, Name = "one" });
        MartenPreviewCheck direct = await MartenEditPreview.CheckAsync(
            store, typeof(NumberedThing), "1", json, roundTrip, TestContext.Current.CancellationToken);

        direct.Verdict.Should().Be(available ? MartenPreviewVerdict.Agrees : MartenPreviewVerdict.Unavailable, direct.Reason);
        (await host.ReadObjectsAsync()).Should().Be(SchemaObjects.Empty, "Marten's preview resolves the type in memory and reads nothing");

        await host.ApplyAsync();
        await host.WriteAsync();
        SchemaObjects before = await host.ReadObjectsAsync();

        DocumentPage page = await host.DocumentsAsync(x => x.ListAsync(Scope, "numbered", new DocumentListRequest()));
        string id = page.Rows[0].Id;

        WritePreview preview = await host.WritesAsync(x => x.PreviewAsync(
            Scope, "numbered", id, "{\"Id\":" + id + ",\"Name\":\"renamed\"}", TestContext.Current.CancellationToken));

        preview.Status.Should().Be(WritePreviewStatus.Ready, preview.Reason);
        preview.MartenCheck.Should().NotBeNull();
        preview.MartenCheck!.Verdict.Should().Be(
            available ? MartenPreviewVerdict.Agrees : MartenPreviewVerdict.Unavailable,
            preview.MartenCheck.Reason);

        if (!available)
        {
            preview.MartenCheck.Reason.Should().Contain("needs Marten 9.53+", "below 9.53 the line says which Marten it needs");
        }

        (await host.ReadObjectsAsync()).Should().Be(before, "previewing an edit, Marten's half included, creates nothing");
    }

    private static readonly string[] NewestFirstKeys = ["meta:LastModified", "meta:CreatedAt", "meta:Revision"];

    private static DocumentListRequest Newest(string key) => new()
    {
        SortKey = key,
        Direction = PostgresStudio.Services.Query.SortDirection.Descending,
    };

    /// <summary>
    /// The anti-vacuity theory: the danger the two tests above measure is real on this Marten.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without it, a Marten release that stopped applying migrations from <c>DocumentTables()</c> would
    /// turn both tests into assertions that nothing happens when nothing could have happened. This calls
    /// the two methods the documents browser refuses to call and asserts that, below Marten 9.50.3, they really
    /// do create Marten's HiLo objects on a schema that did not have them.
    /// </para>
    /// <para>
    /// <b>Version-aware (L5).</b> One release did stop: from 9.50.3 (#5665) <c>AllObjects()</c> describes the HiLo
    /// feature and applies nothing, so <c>DocumentTables()</c> and <c>Functions()</c> create nothing - asserted, so the
    /// change is recorded rather than silent, and the second test above proves its point at the floor, which still has
    /// the hazard. From 9.50.3 the proof that the first test is not vacuous moves to the read the documents list and
    /// detail replaced: Marten's own <c>LoadAsync</c>, on the same empty schemas, still ensures - and so creates - the
    /// document's storage at every version.
    /// </para>
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

        if (MartenStudio.Services.Configuration.RuntimeFeature.OfflineSchemaEnumeration.Probe() != true)
        {
            after.Tables.Should().Contain("mt_hilo",
                "this is the DDL a read path used to run - if Marten has stopped doing it, the two tests " +
                "above no longer prove anything and this one is where that is noticed");
            after.Routines.Should().Contain("mt_get_next_hi");
            return;
        }

        after.Should().Be(SchemaObjects.Empty,
            "from Marten 9.50.3 DocumentTables() and Functions() describe the HiLo feature and apply nothing");

        // The read the documents list and detail replaced: Marten's own load ensures the document's storage first, which
        // on these empty schemas creates it - at every version, and so from 9.50.3 too.
        await using (IQuerySession session = store.QuerySession())
        {
            (await session.LoadAsync<NumberedThing>(1L)).Should().BeNull();
        }

        (await host.ReadObjectsAsync()).Tables.Should().Contain("mt_doc_numbered",
            "Marten's own read still writes DDL on this setup - if it no longer does, the first test above proves nothing");
    }

    private async Task<Host> StartAsync(string name, Action<MartenStudioOptions>? studio = null)
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

        services.AddMartenStudio(options =>
        {
            options.AuthorizationPolicy = null;
            studio?.Invoke(options);
        });

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

        internal async Task<T> WritesAsync<T>(Func<IDocumentWriteService, Task<T>> action)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IDocumentWriteService>());
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
