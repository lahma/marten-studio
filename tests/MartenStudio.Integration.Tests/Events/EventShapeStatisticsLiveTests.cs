using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Services.Shapes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;

using PostgresStudio.Internal.Sql;

using static MartenStudio.Integration.Tests.Events.EventShapeLiveSupport;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// 1.2-UX-FIX: Check shapes finds an alias only the log holds from the planner's statistics on <c>mt_events.type</c> - E05's read - and
/// never from a <c>group by type</c> of its own. <c>ANALYZE</c> is what makes the alias visible to the statistics, and one written
/// afterwards is not: the sample can miss a rare alias, and the panel says so. No test in this class loads the counted listing, so
/// "none is in hand" holds throughout and is itself the proof that the only <c>group by type</c> (reached through that listing) never ran.
/// </summary>
public class EventShapeStatisticsLiveTests(EventShapeStatisticsLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<EventShapeStatisticsLiveTests.Fixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The store: a registered type, an alias nothing maps that was analysed, and one written after the analyse.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <inheritdoc />
        protected override bool SeedSampleData => false;

        /// <inheritdoc />
        protected override void ConfigureStore(StoreOptions options)
        {
            options.Events.MapEventType<ShapeOrderPlaced>("shape_order");
            options.Events.EnableEventTypeIndex = true;
        }

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            Guid stream = Guid.NewGuid();

            await using (IDocumentSession session = Marten.Store.LightweightSession())
            {
                session.Events.StartStream(stream, new ShapeOrderPlaced { Total = 1 }, new ShapeOrderPlaced { Total = 2 });
                await session.SaveChangesAsync(token);
            }

            await using NpgsqlConnection connection = await Postgres.OpenAsync(token);

            for (int i = 0; i < 3; i++)
            {
                await InsertAsync(connection, Schema, stream, 100 + i, "ghost_event", "Gone.Ghost, Gone", $"{{\"Total\":{i}}}", null, token);
            }

            await ExecuteAsync(connection, $"analyze {EventsTable(Schema)}", token);

            // After the analyse: the statistics do not know of it.
            await InsertAsync(connection, Schema, stream, 200, "late_ghost", "Gone.Late, Gone", "{\"Total\":9}", null, token);
        }
    }

    private async Task<UnregisteredAliases> ListAsync()
    {
        using MartenFixture.ScopedService<IEventShapeCheck> check = Marten.Resolve<IEventShapeCheck>();
        return await check.Service.ListUnregisteredAsync(Scope, Token);
    }

    private async Task<EventTypeList?> CountedListingInHandAsync()
    {
        using MartenFixture.ScopedService<IEventDataService> events = Marten.Resolve<IEventDataService>();
        return await events.Service.SharedCountedListingAsync(Scope, Token);
    }

    [PostgresFact]
    public async Task An_analysed_alias_nothing_maps_is_found_from_the_statistics_and_one_written_since_is_not()
    {
        UnregisteredAliases found = await ListAsync();

        found.Source.Should().Be(UnregisteredAliasSource.Statistics);
        found.ListedAt.Should().BeNull();
        found.Names.Should().Contain("ghost_event").And.NotContain("shape_order", "a registered type is not unregistered");
        found.Names.Should().NotContain("late_ghost", "the sample is the last ANALYZE's: a rare or newer alias can be missing, and the panel says so");

        (await CountedListingInHandAsync()).Should().BeNull("no group by type ran: it is only ever reached through the counted listing");
    }

    [PostgresFact]
    public async Task Such_an_alias_is_checked_as_an_unknown_event_type_with_no_group_by_and_nothing_registered()
    {
        string[] before = [.. Store.Options.Events.AllKnownEventTypes().Select(static x => x.EventTypeName).Order(StringComparer.Ordinal)];

        using MartenFixture.ScopedService<IEventShapeCheck> check = Marten.Resolve<IEventShapeCheck>();
        ShapeCheckResult result = await check.Service.CheckAsync(Scope, "ghost_event", false, Token);

        result.Error.Should().BeNull();
        result.Examined.Should().Be(3);
        result.Types.Single().Failures.Should().ContainSingle().Which.Kind.Should().Be(ShapeFailureKind.UnknownEventType);

        // The SQL the run recorded, and the one the name's validation ran: neither is a group by, and the counted listing was never read.
        result.Statements.Select(static x => x.Sql).Should().NotContain(static x => x.Contains("group by", StringComparison.OrdinalIgnoreCase));
        (await CountedListingInHandAsync()).Should().BeNull();

        Store.Options.Events.AllKnownEventTypes().Select(static x => x.EventTypeName).Order(StringComparer.Ordinal).Should().Equal(before);
    }

    [PostgresFact]
    public async Task A_name_the_statistics_do_not_hold_is_not_confirmed_and_costs_no_scan()
    {
        using MartenFixture.ScopedService<IEventShapeCheck> check = Marten.Resolve<IEventShapeCheck>();

        ShapeCheckResult result = await check.Service.CheckAsync(Scope, "late_ghost", false, Token);

        result.Coverage.Should().Be(ShapeCoverage.NotChecked);
        result.CoverageReason.Should().Be(ShapeCheckText.CouldNotConfirm, "the statistics are a sample: not holding a name is not \"no such type\"");
        result.Statements.Should().BeEmpty();
        (await CountedListingInHandAsync()).Should().BeNull();
    }

    // 1.2-UX-FIX-FU3 (F2): a name the page lists is confirmed against the listing the click holds - the object, read for this scope, within
    // five minutes - however long ago the shared half minute ran out, and never refused for want of an answer.

    private sealed class ClockAt(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 5, 14, 0, 0, TimeSpan.Zero);

    private async Task<ShapeCheckResult> CheckHoldingAsync(string name, Func<string, EventTypeList?> listingFor)
    {
        using IServiceScope scope = Services.CreateScope();
        IServiceProvider provider = scope.ServiceProvider;
        StudioScopeResolver resolver = provider.GetRequiredService<StudioScopeResolver>();
        ResolvedScope resolved = await resolver.ResolveAsync(Scope, null, Token);

        ClockAt clock = new(Now);
        EventShapeCheckService service = new(
            provider.GetRequiredService<IOptions<MartenStudioOptions>>(),
            resolver,
            provider.GetRequiredService<ColumnCatalog>(),
            provider.GetRequiredService<IndexCatalog>(),
            provider.GetRequiredService<IEventDataService>(),
            new ShapeCheckCache(clock),
            NullLogger<EventShapeCheckService>.Instance,
            clock);

        return await service.CheckAsync(Scope, name, false, listingFor(EventTypeListingCache.KeyOf(resolved)), Token);
    }

    private static EventTypeList Listing(string listedFor, TimeSpan age, params string[] names) =>
        new([.. names.Select(static x => new EventTypeInfo(x, "Gone.Type, Gone", false, 1))], CountsLoaded: true)
        {
            ListedAt = Now - age,
            ListedFor = listedFor,
        };

    [PostgresFact]
    public async Task A_name_the_page_listed_is_confirmed_against_its_own_listing_after_the_shared_half_minute_and_the_statistics_do_not_know_it()
    {
        ShapeCheckResult result = await CheckHoldingAsync("late_ghost", key => Listing(key, TimeSpan.FromMinutes(2), "late_ghost"));

        result.Error.Should().BeNull();
        result.Examined.Should().Be(1);
        result.Types.Single().Failures.Should().ContainSingle().Which.Kind.Should().Be(ShapeFailureKind.UnknownEventType);
        (await CountedListingInHandAsync()).Should().BeNull("nothing read the log's counts");
    }

    [PostgresFact]
    public async Task A_listing_older_than_five_minutes_or_read_for_another_scope_confirms_nothing_and_the_answer_is_could_not_confirm()
    {
        ShapeCheckResult old = await CheckHoldingAsync("late_ghost", key => Listing(key, TimeSpan.FromMinutes(6), "late_ghost"));
        ShapeCheckResult other = await CheckHoldingAsync("late_ghost", _ => Listing("otherdb", TimeSpan.FromMinutes(1), "late_ghost"));
        ShapeCheckResult none = await CheckHoldingAsync("late_ghost", _ => null);

        foreach (ShapeCheckResult result in new[] { old, other, none })
        {
            result.Coverage.Should().Be(ShapeCoverage.NotChecked);
            result.CoverageReason.Should().Be(ShapeCheckText.CouldNotConfirm, "never \"no such event type\" for want of an answer");
            result.Statements.Should().BeEmpty();
        }
    }

    [PostgresFact]
    public async Task A_fresh_listing_of_this_scope_that_does_not_hold_the_name_is_no_such_type()
    {
        ShapeCheckResult result = await CheckHoldingAsync("late_ghost", key => Listing(key, TimeSpan.FromMinutes(1), "something_else"));

        result.CoverageReason.Should().Be(ShapeCheckText.NoSuchEventType("late_ghost"));
    }
}
