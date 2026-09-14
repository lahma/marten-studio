using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Services.Live;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// The sidebar's badges and the dead-letter screen, against a database whose event store was never
/// created.
/// </summary>
/// <remarks>
/// <para>
/// <b>The test the wiring packet was missing.</b> <c>NavIndicatorService</c> asked
/// <c>IProjectionDataService.GetSummaryAsync</c> unconditionally, which reached
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
/// The fast suite pins the branches with fakes; this pins the thing the fakes stand for. Every test below
/// reads <c>information_schema</c> on a connection of its own before and after, so the measurement
/// contains none of the studio's own code, and
/// <see cref="Martens_own_high_water_read_really_does_create_the_event_store" /> and
/// <see cref="Martens_own_document_query_really_does_create_the_document_table" /> are the anti-vacuity
/// theories: without them, a Marten release that stopped migrating from these calls would turn the rest
/// of the class into assertions that nothing happens when nothing could have happened.
/// </para>
/// <para>
/// <b>What changed with P5-fix-3.</b> The services underneath are no longer gated, they are <em>fixed</em>:
/// the projection summary reads <c>mt_event_progression</c> and the event sequence with the studio's own
/// SQL and the dead-letter list probes its table before opening a Marten session, so neither migrates
/// whether or not a caller checked first. The two tests that used to assert "the studio's own service
/// creates the event store" therefore assert the opposite now, and the anti-vacuity theories moved down
/// onto Marten's own calls, which is where the danger actually lives.
/// </para>
/// </remarks>
public class NavIndicatorNoDdlLiveTests(PostgresFixture fixture)
{
    /// <summary>The scope every call is made in.</summary>
    private static StudioScope Scope => EmptySchemaStudio.Scope;

    /// <summary>
    /// Acceptance 1: the sidebar's badges create nothing on a database with no event store.
    /// </summary>
    [PostgresFact]
    public async Task The_sidebar_badges_create_no_object_at_all()
    {
        await using EmptySchemaStudio host = await StartAsync(nameof(The_sidebar_badges_create_no_object_at_all));

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
    /// and the projections its Rewind button needs. The page runs the last two only where the first says
    /// there are event tables - but all three are run here regardless, because since P5-fix-3 each of
    /// them is safe on its own and that is the property worth pinning: the page's branch is an
    /// optimisation, not the thing standing between the studio and a migration.
    /// </para>
    /// <para>
    /// Both of the reads have their own anti-vacuity theory below.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task Opening_the_dead_letter_screen_creates_no_object_at_all()
    {
        await using EmptySchemaStudio host = await StartAsync(nameof(Opening_the_dead_letter_screen_creates_no_object_at_all));

        SchemaObjects before = await host.ReadObjectsAsync();
        before.Should().Be(SchemaObjects.Empty);

        EventStoreShape shape = await host.EventsAsync(x => x.DescribeAsync(Scope));

        shape.Available.Should().BeTrue("the database is reachable - there is simply nothing in it");
        shape.EventTablesExist.Should().BeFalse("this is the branch the page takes");

        DeadLetterPage page = await host.EventsAsync(x => x.ListDeadLettersAsync(Scope, new DeadLetterQuery()));
        ProjectionsView projections = await host.ProjectionsAsync(x => x.GetProjectionsAsync(Scope));

        page.Error.Should().BeNull("an absent table is an empty page, not a failure");
        page.Rows.Should().BeEmpty();

        projections.HasEventStore.Should().BeFalse("there is no event store in this database to report on");
        projections.HighWaterMark.Should().Be(0);
        projections.Progress.Should().BeEmpty();

        SchemaObjects after = await host.ReadObjectsAsync();

        after.Should().Be(before, "opening the dead-letter screen must not create anything");
        after.Should().Be(SchemaObjects.Empty);
    }

    /// <summary>
    /// The dead-letter list is a Marten session query, and it creates nothing because it looks first.
    /// </summary>
    /// <remarks>
    /// <b>Found by this class, on its first run.</b> <c>ListDeadLettersAsync</c> has to be a Marten query
    /// (D6 - the tenant axis is a predicate over the document body), and every Marten query opens with
    /// <c>EnsureStorageExistsAsync</c> for its document type: on a database that has never run the daemon
    /// that made <c>mt_doc_deadletterevent</c>, that is a studio writing DDL because somebody opened a
    /// tab. P5-fix-3 probes the table through the shared column catalog before the session is opened,
    /// which is the same probe <c>CountDeadLettersAsync</c> already made.
    /// </remarks>
    [PostgresFact]
    public async Task Listing_the_dead_letters_does_not_create_the_document_table()
    {
        await using EmptySchemaStudio host = await StartAsync(nameof(Listing_the_dead_letters_does_not_create_the_document_table));

        (await host.ReadObjectsAsync()).Should().Be(SchemaObjects.Empty);

        DeadLetterPage page = await host.EventsAsync(x => x.ListDeadLettersAsync(Scope, new DeadLetterQuery()));

        page.Error.Should().BeNull();
        page.Rows.Should().BeEmpty();
        page.HasMore.Should().BeFalse();

        (await host.ReadObjectsAsync()).Should().Be(SchemaObjects.Empty);
    }

    /// <summary>
    /// The anti-vacuity theory for the dead-letter list: Marten's own query really does migrate.
    /// </summary>
    /// <remarks>
    /// The same query the service would have run without its probe. If a future Marten stops creating the
    /// document table here, the test above no longer proves anything and this one is where that is
    /// noticed.
    /// </remarks>
    [PostgresFact]
    public async Task Martens_own_document_query_really_does_create_the_document_table()
    {
        await using EmptySchemaStudio host = await StartAsync(nameof(Martens_own_document_query_really_does_create_the_document_table));

        (await host.ReadObjectsAsync()).Should().Be(SchemaObjects.Empty);

        IDocumentStore store = host.Services.GetRequiredService<IDocumentStore>();

        await using (IQuerySession session = store.QuerySession())
        {
            _ = await session.Query<JasperFx.Events.Daemon.DeadLetterEvent>()
                .Take(1)
                .ToListAsync(TestContext.Current.CancellationToken);
        }

        SchemaObjects after = await host.ReadObjectsAsync();

        after.Tables.Should().Contain("mt_doc_deadletterevent",
            "this is the DDL the list refuses to run - if Marten has stopped doing it, the test above no " +
            "longer proves anything and this one is where that is noticed");
        after.Routines.Should().NotBeEmpty("and Marten's whole helper-function set came with it");
    }

    /// <summary>
    /// The anti-vacuity theory: the danger the tests above measure is real on this Marten.
    /// </summary>
    /// <remarks>
    /// This calls the method the studio refuses to call - <c>FetchHighestEventSequenceNumber</c>, which
    /// is what <c>ProjectionDataService.ReadStoredAsync</c> used to reach - and asserts that on a schema
    /// that did not have an event store, it makes one. If a future Marten stops doing that, this test is
    /// where it is noticed, rather than the others quietly becoming assertions about nothing.
    /// </remarks>
    [PostgresFact]
    public async Task Martens_own_high_water_read_really_does_create_the_event_store()
    {
        await using EmptySchemaStudio host = await StartAsync(nameof(Martens_own_high_water_read_really_does_create_the_event_store));

        (await host.ReadObjectsAsync()).Should().Be(SchemaObjects.Empty);

        IDocumentStore store = host.Services.GetRequiredService<IDocumentStore>();
        IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases();

        await databases[0].FetchHighestEventSequenceNumber(TestContext.Current.CancellationToken);

        SchemaObjects after = await host.ReadObjectsAsync();

        after.Tables.Should().Contain("mt_events",
            "this is the DDL the sidebar used to run - if Marten has stopped doing it, the tests above " +
            "no longer prove anything and this one is where that is noticed");
        after.Tables.Should().Contain("mt_streams").And.Contain("mt_event_progression");
        after.Sequences.Should().Contain("mt_events_sequence", "which is what the studio reads instead");
    }

    /// <summary>
    /// And the projection summary is the way the sidebar reached it, so pin that route too.
    /// </summary>
    /// <remarks>
    /// One level up from the theory above: it is <c>IProjectionDataService.GetSummaryAsync</c> that the
    /// badge called, and what made the fix necessary is that this call - through the studio's own
    /// service, with its scope resolved and its policy applied - used to end in Marten's migration. It is
    /// now the studio's own SQL, so it answers "cannot report" as a value and creates nothing.
    /// </remarks>
    [PostgresFact]
    public async Task The_projection_summary_no_longer_creates_the_event_store()
    {
        await using EmptySchemaStudio host = await StartAsync(nameof(The_projection_summary_no_longer_creates_the_event_store));

        (await host.ReadObjectsAsync()).Should().Be(SchemaObjects.Empty);

        ProjectionSummary summary = await host.ProjectionsAsync(x => x.GetSummaryAsync(Scope));

        summary.Error.Should().BeNull("the read succeeded - by reading nothing rather than by migrating");
        summary.HighWaterMark.Should().Be(0);
        summary.Progress.Should().BeEmpty();
        summary.NeedsAttention.Should().BeFalse();

        (await host.ReadObjectsAsync()).Should().Be(SchemaObjects.Empty);
    }

    private Task<EmptySchemaStudio> StartAsync(string name) =>
        EmptySchemaStudio.StartAsync(fixture, "navn_", name);
}
