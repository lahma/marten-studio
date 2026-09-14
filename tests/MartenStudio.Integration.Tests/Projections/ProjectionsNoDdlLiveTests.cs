using JasperFx;
using JasperFx.Events.Projections;

using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Live;
using MartenStudio.Services.Projections;

using MartenStudio.SampleDomain.Events;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// The projections screen, the feed's follow tick and the rebuild dialog, against a database whose event
/// store was never created - and against one where it was.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is about.</b> <c>IMartenDatabase.AllProjectionProgress</c> and
/// <c>FetchHighestEventSequenceNumber</c> each open with
/// <c>await EnsureStorageExistsAsync(typeof(IEvent), token)</c> (Marten 9.35,
/// <c>Storage/MartenDatabase.EventStorage.cs</c>), which applies the event store's Weasel migration under
/// the database's own <c>AutoCreate</c> before a row is read. Those were the studio's progress reads, and
/// they are on a timer: the projections page polls, the feed's follow mode polls, the Overview polls and
/// the navigation badge is on every route. So the studio created <c>mt_events</c>, <c>mt_streams</c>,
/// <c>mt_events_sequence</c> and <c>mt_event_progression</c> on a database that had none, and would have
/// applied any pending event-store change to one that did (AGENTS.md hard rule 14). They are now the
/// studio's own parameterised SQL - <c>MartenStudio.Internal.Sql.ProjectionProgressQueries</c>.
/// </para>
/// <para>
/// <b>Four things have to be true at once</b>, and each has a test here: nothing is created on an empty
/// schema; a store that refuses migrations outright (<c>AutoCreate.None</c>, where those Marten calls
/// <em>throw</em> rather than migrate) still answers a value; the replacement reads the same numbers
/// Marten's own calls read, so "creates nothing" was not bought by reporting nothing; and Marten's calls
/// really do still migrate, so the first test is not an assertion about something that could not have
/// happened.
/// </para>
/// </remarks>
public class ProjectionsNoDdlLiveTests(PostgresFixture fixture)
{
    /// <summary>The scope every call is made in.</summary>
    private static StudioScope Scope => EmptySchemaStudio.Scope;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Acceptance: every read the projections area makes on a timer creates nothing at all.
    /// </summary>
    /// <remarks>
    /// The page's load, twice (it is rendered prerendered and then interactive, and polls after that),
    /// the sidebar's badges, the feed's follow tick, and the rebuild dialog's description - which needs
    /// the high-water mark and was therefore the fourth caller of the same Marten read.
    /// </remarks>
    [PostgresFact]
    public async Task The_projections_area_creates_no_object_at_all()
    {
        await using EmptySchemaStudio host = await StartAsync(nameof(The_projections_area_creates_no_object_at_all));

        SchemaObjects before = await host.ReadObjectsAsync();
        before.Should().Be(SchemaObjects.Empty, "the fixture created two empty schemas and nothing else");

        ProjectionsView first = await host.ProjectionsAsync(x => x.GetProjectionsAsync(Scope));
        ProjectionsView second = await host.ProjectionsAsync(x => x.GetProjectionsAsync(Scope));

        // Answered rather than failed: a read that threw would leave the counts trivially unchanged.
        first.HasEventStore.Should().BeFalse("there is no event store in this database");
        first.HighWaterMark.Should().Be(0);
        first.Projections.Should().ContainSingle(x => x.Name == "DailySales",
            "the static model comes from StoreOptions and does not need a database at all");

        // One row per registered async shard, as always - and every one of them saying "never started"
        // rather than "processed nothing", which are different facts and are drawn differently.
        first.Progress.Should().ContainSingle().Which.HasProgressRow.Should().BeFalse();
        first.UnregisteredShards.Should().BeEmpty("there are no progression rows at all to be left over");

        second.HasEventStore.Should().BeFalse();

        NavIndicators badges = await host.BadgesAsync();
        badges.ProjectionsNeedAttention.Should().BeFalse();

        long? highest = await host.EventsAsync(x => x.GetHighestSequenceAsync(Scope));
        highest.Should().BeNull("the feed's follow tick has nothing to follow, and says so as a value");

        RebuildScope rebuild = await host.ProjectionsAsync(x => x.DescribeRebuildAsync(Scope, "DailySales"));
        rebuild.EventsToReplay.Should().Be(0, "there are no events to replay, which is honestly zero");
        rebuild.ShardNames.Should().NotBeEmpty("the shards come from the projection, not from the database");

        SchemaObjects after = await host.ReadObjectsAsync();

        after.Should().Be(before, "not one of those reads may create an event store");
        after.Should().Be(SchemaObjects.Empty);
    }

    /// <summary>
    /// A store configured to refuse migrations answers a value rather than throwing.
    /// </summary>
    /// <remarks>
    /// <c>EnsureStorageExistsAsync</c> under <c>AutoCreate.None</c> does not migrate - it throws - so a
    /// studio that had merely been lucky about <c>CreateOrUpdate</c> would have swapped a migration for a
    /// stack trace on the circuit here. The page must render, and the numbers must be zero for the
    /// reason the page can say out loud.
    /// </remarks>
    [PostgresFact]
    public async Task A_store_that_refuses_migrations_reports_no_event_store_rather_than_failing()
    {
        await using EmptySchemaStudio host = await StartAsync(
            nameof(A_store_that_refuses_migrations_reports_no_event_store_rather_than_failing),
            AutoCreate.None);

        SchemaObjects before = await host.ReadObjectsAsync();
        before.Should().Be(SchemaObjects.Empty);

        ProjectionsView view = await host.ProjectionsAsync(x => x.GetProjectionsAsync(Scope));
        ProjectionSummary summary = await host.ProjectionsAsync(x => x.GetSummaryAsync(Scope));
        long? highest = await host.EventsAsync(x => x.GetHighestSequenceAsync(Scope));

        view.HasEventStore.Should().BeFalse();
        view.Progress.Should().AllSatisfy(x => x.HasProgressRow.Should().BeFalse());
        summary.Error.Should().BeNull("'no event store here' is a value, not a failure to report");
        summary.HighWaterMark.Should().Be(0);
        highest.Should().BeNull();

        (await host.ReadObjectsAsync()).Should().Be(before);
    }

    /// <summary>
    /// The anti-vacuity theory: Marten's own progress read really does create the event store.
    /// </summary>
    /// <remarks>
    /// The call <c>ProjectionDataService.ReadStoredAsync</c> used to make. If a future Marten stops
    /// migrating here, the two tests above stop proving anything and this one is where that is noticed -
    /// which is also the moment the studio could go back to the supported call.
    /// </remarks>
    [PostgresFact]
    public async Task Martens_own_progress_read_really_does_create_the_event_store()
    {
        await using EmptySchemaStudio host = await StartAsync(nameof(Martens_own_progress_read_really_does_create_the_event_store));

        (await host.ReadObjectsAsync()).Should().Be(SchemaObjects.Empty);

        IMartenDatabase database = await DatabaseOf(host);

        await database.AllProjectionProgress(Token);

        SchemaObjects after = await host.ReadObjectsAsync();

        after.Tables.Should().Contain("mt_event_progression",
            "this is the DDL the projections page refuses to run - if Marten has stopped doing it, the " +
            "tests above no longer prove anything and this one is where that is noticed");
        after.Tables.Should().Contain("mt_events").And.Contain("mt_streams");
        after.Sequences.Should().Contain("mt_events_sequence");
    }

    /// <summary>
    /// And on a database that <em>has</em> an event store, the studio's own reads answer exactly what
    /// Marten's answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of the rule, and the one that stops "creates nothing" being bought by reporting
    /// nothing. The event store is created here deliberately - by Marten's own call, which is allowed in
    /// a test and is the only place in this class that is - then events are appended and a progression
    /// row is written by hand, because a real one needs a running daemon and this test is not about the
    /// daemon.
    /// </para>
    /// <para>
    /// Both numbers are then read twice, once through Marten and once through the studio, and compared.
    /// The high-water mark is a sequence's <c>last_value</c> and the progression row is a row, so both
    /// comparisons are exact rather than approximate.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task The_studios_own_reads_answer_what_Martens_do_on_a_database_that_has_an_event_store()
    {
        await using EmptySchemaStudio host = await StartAsync(nameof(The_studios_own_reads_answer_what_Martens_do_on_a_database_that_has_an_event_store));

        IDocumentStore store = host.Services.GetRequiredService<IDocumentStore>();
        IMartenDatabase database = await DatabaseOf(host);

        // Marten makes the event store, which is what this test needs to exist before it starts.
        await database.AllProjectionProgress(Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new OrderPlaced(Guid.NewGuid(), "acme", DateTimeOffset.UtcNow));
            session.Events.StartStream(Guid.NewGuid(), new OrderPlaced(Guid.NewGuid(), "beta", DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(Token);
        }

        await WriteProgressionRowAsync(host, "DailySales:All", 1);

        // Marten's answers, which are the ones the studio used to get by asking it.
        long expectedHighWater = await database.FetchHighestEventSequenceNumber(Token);
        IReadOnlyList<ShardState> expectedProgress = await database.AllProjectionProgress(Token);

        expectedHighWater.Should().BeGreaterThan(0, "two streams were appended");
        expectedProgress.Should().Contain(x => x.ShardName == "DailySales:All");

        ProjectionsView view = await host.ProjectionsAsync(x => x.GetProjectionsAsync(Scope));
        long? highest = await host.EventsAsync(x => x.GetHighestSequenceAsync(Scope));

        view.HasEventStore.Should().BeTrue();
        view.HighWaterMark.Should().Be(expectedHighWater, "the same statement against the same sequence");
        highest.Should().Be(expectedHighWater);

        ShardProgress shard = view.Progress.Should().ContainSingle(x => x.ShardName == "DailySales:All").Subject;

        shard.Sequence.Should().Be(1);
        shard.HasProgressRow.Should().BeTrue();
        shard.HighWater.Should().Be(expectedHighWater);
        shard.IsRegistered.Should().BeTrue("DailySales is registered on this store");

        // And the rebuild dialog's number is the same one.
        RebuildScope rebuild = await host.ProjectionsAsync(x => x.DescribeRebuildAsync(Scope, "DailySales"));
        rebuild.EventsToReplay.Should().Be(expectedHighWater);
    }

    /// <summary>
    /// Marten's own high-water bookkeeping rows are not rendered as shards, and are excluded in SQL the
    /// way Marten excludes them.
    /// </summary>
    /// <remarks>
    /// <c>HighWaterMark</c>, <c>HighWaterAllocationFence</c> and <c>HighWaterStuckGap</c> are written into
    /// the same table as the shards. The gap row trails the mark by the detection gap <em>by design</em>,
    /// so a page sorted by lag descending put a projection that does not exist at the top of an
    /// operations screen. P5-fix-2 settled the predicate; this proves it against rows that are really in
    /// a real table.
    /// </remarks>
    [PostgresFact]
    public async Task Martens_high_water_bookkeeping_rows_are_never_rendered_as_shards()
    {
        await using EmptySchemaStudio host = await StartAsync(nameof(Martens_high_water_bookkeeping_rows_are_never_rendered_as_shards));

        IMartenDatabase database = await DatabaseOf(host);
        await database.AllProjectionProgress(Token);

        await WriteProgressionRowAsync(host, "HighWaterMark", 900);
        await WriteProgressionRowAsync(host, "HighWaterMark:acme", 800);
        await WriteProgressionRowAsync(host, "HighWaterAllocationFence", 700);
        await WriteProgressionRowAsync(host, "HighWaterStuckGap", 600);
        await WriteProgressionRowAsync(host, "Orphan:All", 500);

        ProjectionsView view = await host.ProjectionsAsync(x => x.GetProjectionsAsync(Scope));

        view.Progress.Should().NotContain(x => x.ShardName.StartsWith("HighWater", StringComparison.Ordinal));
        view.UnregisteredShards.Should().ContainSingle(x => x.ShardName == "Orphan:All");
    }

    private Task<EmptySchemaStudio> StartAsync(string name, AutoCreate autoCreate = AutoCreate.CreateOrUpdate) =>
        EmptySchemaStudio.StartAsync(
            fixture,
            "projn_",
            name,
            autoCreate,
            // One registered async projection, so the static model has a row and the rebuild dialog has
            // something to describe. Registering it creates nothing by itself.
            static options => options.Projections.Add(new DailySalesProjection(), ProjectionLifecycle.Async));

    private static async Task<IMartenDatabase> DatabaseOf(EmptySchemaStudio host)
    {
        IDocumentStore store = host.Services.GetRequiredService<IDocumentStore>();
        IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases();

        return databases[0];
    }

    /// <summary>
    /// Writes one progression row the way the daemon would, without a daemon.
    /// </summary>
    /// <remarks>
    /// A real row needs a running daemon and a projection with work to do, which is
    /// <c>ProjectionsLiveTests</c>'s job. What this class needs is a row with a known name and a known
    /// sequence, so it writes one - and the studio's read and Marten's read are then compared against the
    /// same row rather than against each other's timing.
    /// </remarks>
    private async Task WriteProgressionRowAsync(EmptySchemaStudio host, string name, long sequence)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = new NpgsqlCommand(
            $"""
             insert into "{host.EventSchema}"."mt_event_progression" (name, last_seq_id, last_updated)
             values (@name, @sequence, now())
             on conflict (name) do update set last_seq_id = excluded.last_seq_id
             """,
            connection);

        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("sequence", sequence);

        await command.ExecuteNonQueryAsync(Token);
    }
}
