using System.Security.Claims;

using Marten;
using Marten.Schema;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Services.Live;
using MartenStudio.Services.Projections;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// The sidebar's badges and the dead-letter screen, against a database whose event store was never
/// created.
/// </summary>
/// <remarks>
/// <para>
/// <b>The test the wiring packet was missing.</b> <c>NavIndicatorService</c> asked
/// <c>IProjectionDataService.GetSummaryAsync</c> unconditionally, which reaches
/// <c>IMartenDatabase.FetchHighestEventSequenceNumber</c> and <c>AllProjectionProgress</c> - and both of
/// those open with <c>EnsureStorageExistsAsync(typeof(IEvent))</c>
/// (Marten 9.35, <c>Storage/MartenDatabase.EventStorage.cs</c>), which applies the event store's Weasel
/// migration under the database's own <c>AutoCreate</c> before reading a row. The sidebar is rendered on
/// every route, during prerender and again on the interactive render and then once per refresh interval,
/// so a host whose database had no event tables got them created by opening
/// <c>/marten/documents</c>. That is a studio writing DDL because somebody navigated, which is exactly
/// what hard rule 14 exists to stop.
/// </para>
/// <para>
/// The fast suite pins the branch with fakes; this pins the thing the fakes stand for. Every test below
/// reads <c>information_schema</c> on a connection of its own before and after, so the measurement
/// contains none of the studio's own code, and <see cref="Martens_own_high_water_read_really_does_create_the_event_store" />
/// is the anti-vacuity theory: without it, a Marten release that stopped migrating from these calls would
/// turn the rest of the class into assertions that nothing happens when nothing could have happened.
/// </para>
/// </remarks>
public class NavIndicatorNoDdlLiveTests(PostgresFixture fixture)
{
    /// <summary>
    /// The scope every call is made in. The database is left empty, which resolves to the store's only
    /// one - the same thing a freshly opened studio does.
    /// </summary>
    private static readonly StudioScope Scope = new("default", string.Empty, null);

    /// <summary>
    /// Acceptance 1: the sidebar's badges create nothing on a database with no event store.
    /// </summary>
    [PostgresFact]
    public async Task The_sidebar_badges_create_no_object_at_all()
    {
        await using Host host = await StartAsync(nameof(The_sidebar_badges_create_no_object_at_all));

        SchemaObjects before = await host.ReadObjectsAsync();
        before.Should().Be(SchemaObjects.Empty, "the fixture created two empty schemas and nothing else");

        // Exactly what a circuit does on any route: settle the scope the layout settles, then read the
        // badges - twice, because the sidebar is rendered once prerendered and once interactive.
        NavIndicators first = await host.BadgesAsync();
        NavIndicators second = await host.BadgesAsync();

        // Answered, rather than failing in a way that would make the counts trivially unchanged.
        first.DeadLetters.Should().Be(0, "no dead-letter table is a real zero, not a failure");
        first.ProjectionsNeedAttention.Should().BeFalse();
        first.ProjectionsExplanation.Should().BeNull();
        second.Should().Be(first);

        SchemaObjects after = await host.ReadObjectsAsync();

        after.Should().Be(before, "a badge must never be able to create an event store");
        after.Should().Be(SchemaObjects.Empty);
    }

    /// <summary>
    /// Acceptance 2: opening the dead-letter screen creates nothing either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page's load is three reads in order: <c>DescribeAsync</c> for the shape, the dead-letter list,
    /// and the projections its Rewind button needs. Only the first is unconditional - it is an
    /// <c>information_schema</c> question through the studio's own cached column catalog - and the other
    /// two happen only where it says there are event tables. That sequence is replayed here rather than
    /// the page being rendered, because the fast suite is where a component is rendered and this project
    /// is where a Postgres is; <c>DeadLettersTests</c> pins the branch, and this pins what the branch is
    /// worth.
    /// </para>
    /// <para>
    /// Both skipped reads have their own anti-vacuity theory below.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task Opening_the_dead_letter_screen_creates_no_object_at_all()
    {
        await using Host host = await StartAsync(nameof(Opening_the_dead_letter_screen_creates_no_object_at_all));

        SchemaObjects before = await host.ReadObjectsAsync();
        before.Should().Be(SchemaObjects.Empty);

        EventStoreShape shape = await host.EventsAsync(x => x.DescribeAsync(Scope));

        shape.Available.Should().BeTrue("the database is reachable - there is simply nothing in it");
        shape.EventTablesExist.Should().BeFalse("this is the branch the page takes, and it reads no further");

        // What the page does on that branch, said in full: an empty list, no projections read, and the
        // "this store has no event storage yet" empty state.
        DeadLetterPage page = shape.EventTablesExist
            ? await host.EventsAsync(x => x.ListDeadLettersAsync(Scope, new DeadLetterQuery()))
            : DeadLetterPage.Empty;

        ProjectionsView? projections = shape.EventTablesExist
            ? await host.ProjectionsAsync(x => x.GetProjectionsAsync(Scope))
            : null;

        page.Rows.Should().BeEmpty();
        projections.Should().BeNull();

        SchemaObjects after = await host.ReadObjectsAsync();

        after.Should().Be(before, "opening the dead-letter screen must not create anything");
        after.Should().Be(SchemaObjects.Empty);
    }

    /// <summary>
    /// The anti-vacuity theory for the dead-letter list: it really does migrate.
    /// </summary>
    /// <remarks>
    /// <b>Found by this class, on its first run.</b> <c>ListDeadLettersAsync</c> is a Marten session query
    /// over <c>DeadLetterEvent</c> (D6 - it has to be, because the tenant axis is a predicate over the
    /// document body), and every Marten query opens with <c>EnsureStorageExistsAsync</c> for its document
    /// type. On a database that has never run the daemon that creates <c>mt_doc_deadletterevent</c> and
    /// Marten's twenty helper functions, under the database's own <c>AutoCreate</c> - a studio writing DDL
    /// because somebody opened a tab, which is the same failure the sidebar had and the same rule it
    /// breaks.
    /// </remarks>
    [PostgresFact]
    public async Task Listing_the_dead_letters_really_does_create_the_document_table()
    {
        await using Host host = await StartAsync(nameof(Listing_the_dead_letters_really_does_create_the_document_table));

        (await host.ReadObjectsAsync()).Should().Be(SchemaObjects.Empty);

        DeadLetterPage page = await host.EventsAsync(x => x.ListDeadLettersAsync(Scope, new DeadLetterQuery()));

        page.Error.Should().BeNull("Marten answered - by making the table first");

        SchemaObjects after = await host.ReadObjectsAsync();

        after.Tables.Should().Contain("mt_doc_deadletterevent",
            "this is the DDL the page refuses to run - if Marten has stopped doing it, the test above no " +
            "longer proves anything and this one is where that is noticed");
        after.Routines.Should().NotBeEmpty("and Marten's whole helper-function set came with it");
    }

    /// <summary>
    /// The anti-vacuity theory: the danger the two tests above measure is real on this Marten.
    /// </summary>
    /// <remarks>
    /// This calls the method the sidebar refuses to call - <c>FetchHighestEventSequenceNumber</c>, which
    /// is what <c>ProjectionDataService.ReadStoredAsync</c> reaches - and asserts that on a schema that
    /// did not have an event store, it makes one. If a future Marten stops doing that, this test is where
    /// it is noticed, rather than the other two quietly becoming assertions about nothing.
    /// </remarks>
    [PostgresFact]
    public async Task Martens_own_high_water_read_really_does_create_the_event_store()
    {
        await using Host host = await StartAsync(nameof(Martens_own_high_water_read_really_does_create_the_event_store));

        (await host.ReadObjectsAsync()).Should().Be(SchemaObjects.Empty);

        IDocumentStore store = host.Services.GetRequiredService<IDocumentStore>();
        IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases();

        await databases[0].FetchHighestEventSequenceNumber(TestContext.Current.CancellationToken);

        SchemaObjects after = await host.ReadObjectsAsync();

        after.Tables.Should().Contain("mt_events",
            "this is the DDL the sidebar used to run - if Marten has stopped doing it, the two tests " +
            "above no longer prove anything and this one is where that is noticed");
        after.Tables.Should().Contain("mt_streams").And.Contain("mt_event_progression");
    }

    /// <summary>
    /// And the projections service is the way the sidebar reached it, so pin that route too.
    /// </summary>
    /// <remarks>
    /// One level up from the theory above: it is <c>IProjectionDataService.GetSummaryAsync</c> that the
    /// badge called, and what makes the fix necessary is that this call - through the studio's own
    /// service, with its scope resolved and its policy applied - still ends in Marten's migration.
    /// </remarks>
    [PostgresFact]
    public async Task The_projection_summary_really_does_create_the_event_store()
    {
        await using Host host = await StartAsync(nameof(The_projection_summary_really_does_create_the_event_store));

        (await host.ReadObjectsAsync()).Should().Be(SchemaObjects.Empty);

        ProjectionSummary summary = await host.ProjectionsAsync(x => x.GetSummaryAsync(Scope));

        summary.Error.Should().BeNull("the read succeeded, which is the point - it succeeded by migrating");

        (await host.ReadObjectsAsync()).Tables.Should().Contain("mt_events");
    }

    private async Task<Host> StartAsync(string name)
    {
        string schema = "navn_" + name.ToLowerInvariant()[..Math.Min(name.Length, 40)];

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

            options.Schema.For<Note>();
            options.Events.AddEventType<NoteWritten>();
        });

        services.AddMartenStudio(options =>
        {
            options.AuthorizationPolicy = null;

            // The tenant probe is a read of its own and is not what these tests are measuring.
            options.DiscoverTenantIds = false;
        });

        return new Host(services.BuildServiceProvider(), fixture.ConnectionString, schema);
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

        /// <summary>The container, for the tests that call Marten directly.</summary>
        public IServiceProvider Services => provider;

        /// <summary>
        /// One circuit's worth of the sidebar: settle the scope the layout settles, then read the badges.
        /// </summary>
        /// <remarks>
        /// A scope of its own per call, because <c>StudioState</c> and <c>NavIndicatorService</c> are both
        /// scoped and a second call is a second circuit - which is also what makes the repeat meaningful,
        /// since the snapshot cache is the process-wide singleton the two circuits share.
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

        internal async Task<T> EventsAsync<T>(Func<IEventDataService, Task<T>> action)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IEventDataService>());
        }

        internal async Task<T> ProjectionsAsync<T>(Func<IProjectionDataService, Task<T>> action)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IProjectionDataService>());
        }

        public async ValueTask DisposeAsync() => await provider.DisposeAsync();

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
    }

    /// <summary>Somebody is signed in; who they are is not what these tests are about.</summary>
    private sealed class SignedInProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "integration")], "test"))));
    }
}
