using System.Globalization;
using System.Text.Json;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Shapes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;

using PostgresStudio.Internal.Sql;

using static MartenStudio.Integration.Tests.Events.EventShapeLiveSupport;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// 1.2-SHAPE-B: the events' shape check against a real Postgres, single tenancy and no index on <c>type</c>. Events are altered by raw
/// SQL behind Marten's back - a string where a decimal is declared, a member the type no longer declares, an alias no type answers to,
/// rows an upcaster maps, an upcaster that throws - and the check is asked what a rebuild would meet. It is a plain read: nothing is
/// written, no DDL runs, and no value is in what comes back.
/// </summary>
public class EventShapeCheckLiveTests(EventShapeCheckLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<EventShapeCheckLiveTests.Fixture>
{
    private readonly Fixture data = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The store: one event type of each shape the check meets.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <summary>The sequences of the live <c>shape_order</c> events, oldest first.</summary>
        public List<long> OrderSequences { get; private set; } = [];

        /// <summary>The oldest and the newest <c>shape_bulk</c> sequence: the two corrupt rows.</summary>
        public (long Oldest, long Newest) BulkEdges { get; private set; }

        /// <inheritdoc />
        protected override bool SeedSampleData => false;

        /// <inheritdoc />
        protected override void ConfigureStore(StoreOptions options)
        {
            options.Events.MapEventType<ShapeOrderPlaced>("shape_order");
            options.Events.MapEventType<ShapeBulk>("shape_bulk");
            options.Events.Upcast<ShapeOrderOld, ShapeOrderUpcast>("shape_order_old", static old => new ShapeOrderUpcast { Amount = old.Total });
            options.Events.Upcast<ShapeThrowOld, ShapeThrowUpcast>("shape_throw_old", static _ => throw new InvalidOperationException(Sentinel));
        }

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            Guid stream = Guid.NewGuid();

            await using (IDocumentSession session = Marten.Store.LightweightSession())
            {
                session.Events.StartStream(
                    stream,
                    Enumerable.Range(0, 8).Select(static i => (object) new ShapeOrderPlaced { Total = i, Note = "n" + i }).ToArray());
                await session.SaveChangesAsync(token);
            }

            await using NpgsqlConnection connection = await Postgres.OpenAsync(token);
            string events = EventsTable(Schema);

            OrderSequences = await SequencesAsync(connection, Schema, "shape_order", token);

            // Six live events: the second no longer fits, the fourth carries a member the type dropped; the last two are archived.
            await ExecuteAsync(connection, $"update {events} set data = jsonb_set(data, '{{Total}}', '\"x\"') where seq_id = {OrderSequences[1]}", token);
            await ExecuteAsync(connection, $"update {events} set data = jsonb_set(data, '{{Legacy}}', '\"{Sentinel}\"') where seq_id = {OrderSequences[3]}", token);
            await ExecuteAsync(
                connection,
                $"update {events} set is_archived = true, data = jsonb_set(data, '{{Total}}', '\"x\"') where seq_id in ({OrderSequences[6]}, {OrderSequences[7]})",
                token);

            for (int i = 0; i < 4; i++)
            {
                await InsertAsync(connection, Schema, stream, 100 + i, "order_placed_v1", "Shop.Gone, Gone", $"{{\"Total\":{i}}}", null, token);
            }

            for (int i = 0; i < 3; i++)
            {
                await InsertAsync(
                    connection, Schema, stream, 110 + i, "shape_order_old", typeof(ShapeOrderOld).FullName + ", " + typeof(ShapeOrderOld).Assembly.GetName().Name,
                    $"{{\"Total\":{i + 1},\"Extra\":\"{Sentinel}\"}}", null, token);
            }

            // An alias no mapping answers to, whose stored .NET type this process has loaded: Marten would register it on first read.
            for (int i = 0; i < 3; i++)
            {
                await InsertAsync(
                    connection, Schema, stream, 130 + i, "loadable_v1", typeof(ShapeLoadable).FullName + ", " + typeof(ShapeLoadable).Assembly.GetName().Name,
                    $"{{\"Total\":{i},\"Extra\":\"{Sentinel}\"}}", null, token);
            }

            for (int i = 0; i < 2; i++)
            {
                await InsertAsync(
                    connection, Schema, stream, 120 + i, "shape_throw_old", typeof(ShapeThrowOld).FullName + ", " + typeof(ShapeThrowOld).Assembly.GetName().Name,
                    $"{{\"Total\":{i}}}", null, token);
            }

            await ExecuteAsync(
                connection,
                $"insert into {events} (seq_id, id, stream_id, version, data, type, timestamp, mt_dotnet_type, is_archived) "
                + $"select nextval('\"{Schema}_events\".mt_events_sequence'), gen_random_uuid(), '{stream}', 1000 + n, ('{{\"N\":' || n || '}}')::jsonb, 'shape_bulk', now(), "
                + $"'{typeof(ShapeBulk).FullName}, {typeof(ShapeBulk).Assembly.GetName().Name}', false from generate_series(1, 450) n",
                token);

            List<long> bulk = await SequencesAsync(connection, Schema, "shape_bulk", token);
            BulkEdges = (bulk[0], bulk[^1]);

            await ExecuteAsync(connection, $"update {events} set data = '{{\"N\":\"x\"}}'::jsonb where seq_id in ({BulkEdges.Oldest}, {BulkEdges.Newest})", token);
        }
    }

    private async Task<ShapeCheckResult> CheckAsync(string eventType, bool force = false)
    {
        using MartenFixture.ScopedService<IEventShapeCheck> check = Marten.Resolve<IEventShapeCheck>();
        return await check.Service.CheckAsync(Scope, eventType, force, Token);
    }

    [PostgresFact]
    public async Task A_corrupted_event_is_unreadable_with_its_sequence_and_what_a_rebuild_would_do_and_archived_ones_are_left_out()
    {
        ShapeCheckResult result = await CheckAsync("shape_order");

        result.Error.Should().BeNull();
        result.Coverage.Should().Be(ShapeCoverage.Complete);
        result.Examined.Should().Be(6, "the two archived events are not read: a rebuild skips them");
        result.LeftOut.Should().Contain(ShapeCheckText.ArchivedLeftOut);

        ShapeTypeResult type = result.Types.Should().ContainSingle().Subject;
        ShapeFailureGroup failure = type.Failures.Should().ContainSingle().Subject;

        failure.Kind.Should().Be(ShapeFailureKind.Unreadable);
        failure.Path.Should().Be("$.Total");
        failure.StoredKind.Should().Be(JsonValueKind.String);
        failure.Example.Sequence.Should().Be(data.OrderSequences[1]);
        failure.Example.Version.Should().Be(2);

        type.Members.Should().ContainSingle(x => x.Kind == ShapeMemberKind.Dropped && x.Path == "$.Legacy")
            .Which.Example.Sequence.Should().Be(data.OrderSequences[3]);

        result.RebuildSentence.Should().Contain("stops at sequence " + data.OrderSequences[1].ToString("N0", CultureInfo.InvariantCulture))
            .And.Contain("RebuildErrors.SkipSerializationErrors");
        result.Remedy.Should().Contain("opts.Events.Upcast");

        JsonSerializer.Serialize(result).Should().NotContain(Sentinel);
    }

    [PostgresFact]
    public async Task An_alias_no_type_answers_to_is_an_unknown_event_type_naming_the_alias_and_the_stored_dotnet_type()
    {
        ShapeCheckResult result = await CheckAsync("order_placed_v1");

        result.Error.Should().BeNull();
        result.Examined.Should().Be(4);

        ShapeFailureGroup failure = result.Types.Single().Failures.Should().ContainSingle().Subject;

        failure.Kind.Should().Be(ShapeFailureKind.UnknownEventType);
        failure.Count.Should().Be(4);
        failure.Detail.Should().Be("order_placed_v1 / Shop.Gone, Gone");
        result.RebuildSentence.Should().Contain("RebuildErrors.SkipUnknownEvents");
    }

    [PostgresFact]
    public async Task Events_an_upcaster_maps_are_counted_as_read_as_another_type_and_report_no_dropped_member()
    {
        ShapeCheckResult result = await CheckAsync("shape_order_old");

        result.Error.Should().BeNull();
        result.Examined.Should().Be(3);

        ShapeTypeResult type = result.Types.Should().ContainSingle().Subject;

        type.ReadAsOtherType.Should().Be(3);
        type.Members.Should().BeEmpty("the stored JSON was written for the old shape, so a member diff would call every transformation a loss");
        type.Failures.Should().BeEmpty();
        JsonSerializer.Serialize(result).Should().NotContain(Sentinel);
    }

    [PostgresFact]
    public async Task An_upcaster_that_throws_with_the_sentinel_in_its_message_is_unreadable_and_the_message_is_nowhere()
    {
        ShapeCheckResult result = await CheckAsync("shape_throw_old");

        result.Error.Should().BeNull();
        result.Types.Single().Failures.Should().ContainSingle().Which.Kind.Should().Be(ShapeFailureKind.Unreadable);
        JsonSerializer.Serialize(result).Should().NotContain(Sentinel);
        result.RebuildSentence.Should().NotContain(Sentinel);
    }

    [PostgresFact]
    public async Task Without_an_index_on_type_the_oldest_and_newest_hundred_are_read_and_the_result_says_so_with_the_remedy()
    {
        ShapeCheckResult result = await CheckAsync("shape_bulk");

        result.Error.Should().BeNull();
        result.Coverage.Should().Be(ShapeCoverage.Sampled);
        result.Examined.Should().Be(2 * ShapeCheckLimits.EdgeWithoutTypeIndex);
        result.LowestPart.Should().Be(ShapeCheckLimits.EdgeWithoutTypeIndex);
        result.HighestPart.Should().Be(ShapeCheckLimits.EdgeWithoutTypeIndex);
        result.SpreadPart.Should().Be(0);
        result.CoverageReason.Should().Be(ShapeCheckText.NoTypeIndex);
        result.Remedy.Should().Contain(ShapeCheckText.TypeIndexRemedy);

        ShapeFailureGroup failure = result.Types.Single().Failures.Should().ContainSingle().Subject;

        failure.Count.Should().Be(2, "the oldest and the newest event are the two corrupt rows");
        failure.Example.Sequence.Should().Be(data.BulkEdges.Oldest);
        result.RebuildSentence.Should().Contain("stops at sequence " + data.BulkEdges.Oldest.ToString("N0", CultureInfo.InvariantCulture));
    }

    [PostgresFact]
    public async Task A_name_the_store_does_not_know_an_over_long_name_and_an_empty_one_get_one_sentence_and_no_read()
    {
        string tooLong = new('x', 300);

        foreach (string name in new[] { "nothing_like_it", tooLong, string.Empty })
        {
            ShapeCheckResult result = await CheckAsync(name);

            result.Coverage.Should().Be(ShapeCoverage.NotChecked);
            result.Statements.Should().BeEmpty("the refusal is before any statement for the name");
            result.CoverageReason.Should().Be(ShapeCheckText.NoSuchEventType(result.Subject));
            result.Subject.Length.Should().BeLessThanOrEqualTo(ShapeCheckLimits.MaxSubjectLength);
        }
    }

    [PostgresFact]
    public async Task A_check_creates_and_changes_no_object_in_the_database()
    {
        await using NpgsqlConnection connection = await Postgres.OpenAsync(Token);
        string before = await ObjectsAsync(connection, Schema, Token);

        await CheckAsync("shape_order");
        await CheckAsync("order_placed_v1", force: true);

        (await ObjectsAsync(connection, Schema, Token)).Should().Be(before);
    }

    [PostgresFact]
    public async Task An_unmapped_alias_whose_dotnet_type_is_loaded_is_classified_and_never_registered_on_the_hosts_store()
    {
        var events = Store.Options.Events;
        string[] before = [.. events.AllKnownEventTypes().Select(static x => x.EventTypeName).Order(StringComparer.Ordinal)];
        Func<string, bool> known = EventHealth.KnownAliases(events)!;

        known("loadable_v1").Should().BeFalse("the arrangement: nothing maps this alias");

        ShapeCheckResult result = await CheckAsync("loadable_v1");

        result.Error.Should().BeNull();
        result.Examined.Should().Be(3);

        ShapeTypeResult type = result.Types.Should().ContainSingle().Subject;
        ShapeFailureGroup failure = type.Failures.Should().ContainSingle().Subject;

        failure.Kind.Should().Be(ShapeFailureKind.UnregisteredEventType);
        failure.Count.Should().Be(3);
        failure.Detail.Should().StartWith("loadable_v1 / " + typeof(ShapeLoadable).FullName);
        type.Members.Should().BeEmpty("their members are not compared");
        result.RebuildSentence.Should().BeNull("Marten would read them as the loaded type: a rebuild does not stop");

        // The point: a press registered nothing. E05's own predicate still calls the alias unmapped, and the registered types are as they were.
        events.AllKnownEventTypes().Select(static x => x.EventTypeName).Order(StringComparer.Ordinal).Should().Equal(before);
        EventHealth.KnownAliases(events)!("loadable_v1").Should().BeFalse();
        JsonSerializer.Serialize(result).Should().NotContain(Sentinel);
    }

    /// <summary>
    /// 1.2-UX-FIX (the sweep's "ghost_event"): an alias in <c>mt_events</c> that nothing maps and whose <c>mt_dotnet_type</c> does not load
    /// is not in the listing the Event types page draws until its counts are loaded - the uncounted listing is the registered types
    /// alone - so the page's Check shapes, which takes its names from that list, never asked about it. The counted listing has it,
    /// says when it was read, and the check reports it as an unknown event type without resolving the alias.
    /// </summary>
    [PostgresFact]
    public async Task An_alias_nothing_maps_is_absent_from_the_uncounted_listing_present_in_the_counted_one_and_checked_as_unknown()
    {
        string[] before = [.. Store.Options.Events.AllKnownEventTypes().Select(static x => x.EventTypeName).Order(StringComparer.Ordinal)];

        using MartenFixture.ScopedService<IEventDataService> events = Marten.Resolve<IEventDataService>();

        EventTypeList uncounted = await events.Service.ListEventTypesAsync(Scope, withCounts: false, Token);
        EventTypeList counted = await events.Service.ListEventTypesAsync(Scope, withCounts: true, Token);

        uncounted.Types.Select(static x => x.Name).Should().NotContain("order_placed_v1", "the uncounted list is what the store registers");
        uncounted.ListedAt.Should().BeNull();

        EventTypeInfo ghost = counted.Types.Should().ContainSingle(static x => x.Name == "order_placed_v1").Subject;
        ghost.IsRegistered.Should().BeFalse();
        ghost.Count.Should().Be(4);
        counted.ListedAt.Should().NotBeNull().And.BeOnOrBefore(DateTimeOffset.UtcNow);

        ShapeCheckResult result = await CheckAsync("order_placed_v1");

        result.Types.Single().Failures.Should().ContainSingle().Which.Kind.Should().Be(ShapeFailureKind.UnknownEventType);
        Store.Options.Events.AllKnownEventTypes().Select(static x => x.EventTypeName).Order(StringComparer.Ordinal).Should().Equal(before,
            "listing and checking an unmapped alias resolved nothing and registered nothing (hard rule 14)");
    }

    [PostgresFact]
    public async Task The_counted_listing_is_one_read_shared_by_every_circuit_and_by_the_check()
    {
        using MartenFixture.ScopedService<IEventDataService> circuitA = Marten.Resolve<IEventDataService>();
        EventTypeList first = await circuitA.Service.ListEventTypesAsync(Scope, withCounts: true, Token);

        // Another circuit's own scoped service, and the check's name validation, are handed the listing the first one read.
        using MartenFixture.ScopedService<IEventDataService> circuitB = Marten.Resolve<IEventDataService>();
        EventTypeList second = await circuitB.Service.ListEventTypesAsync(Scope, withCounts: true, Token);

        first.Error.Should().BeNull();
        second.Should().BeSameAs(first);

        // The uncounted listing is the registered types alone and is never shared.
        (await circuitB.Service.ListEventTypesAsync(Scope, withCounts: false, Token)).CountsLoaded.Should().BeFalse();
    }

    [PostgresFact]
    public async Task An_unmapped_alias_whose_dotnet_type_cannot_be_loaded_never_reaches_the_resolver_either()
    {
        var events = Store.Options.Events;

        await CheckAsync("order_placed_v1");

        EventHealth.KnownAliases(events)!("order_placed_v1").Should().BeFalse();
        events.AllKnownEventTypes().Select(static x => x.EventTypeName).Should().NotContain("order_placed_v1");
    }

    [PostgresFact]
    public async Task A_statement_that_runs_past_its_timeout_without_the_index_keeps_the_reason_and_the_remedy()
    {
        using IServiceScope scope = Services.CreateScope();
        IServiceProvider provider = scope.ServiceProvider;

        EventShapeCheckService service = new(
            provider.GetRequiredService<IOptions<MartenStudioOptions>>(),
            provider.GetRequiredService<StudioScopeResolver>(),
            provider.GetRequiredService<ColumnCatalog>(),
            provider.GetRequiredService<IndexCatalog>(),
            provider.GetRequiredService<IEventDataService>(),
            new ShapeCheckCache(),
            NullLogger<EventShapeCheckService>.Instance)
        {
            StatementTimeoutOverride = TimeSpan.FromMilliseconds(300),
        };

        // The catalogs are cached from a first, unlocked check: only the check's own statements are then left to wait on the lock.
        (await service.CheckAsync(Scope, "shape_order", false, Token)).Error.Should().BeNull();

        // The events table is locked by someone else, so the first statement waits until PostgreSQL's own statement_timeout ends it.
        await using NpgsqlConnection locker = await Postgres.OpenAsync(Token);
        await using NpgsqlTransaction transaction = await locker.BeginTransactionAsync(Token);
        await using (NpgsqlCommand lockCommand = new($"lock table {EventsTable(Schema)} in access exclusive mode", locker, transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(Token);
        }

        ShapeCheckResult result = await service.CheckAsync(Scope, "shape_bulk", false, Token);

        await transaction.RollbackAsync(Token);

        result.Error.Should().BeNull();
        result.Coverage.Should().Be(ShapeCoverage.Partial);
        result.CoverageReason.Should().Contain("statement timeout").And.Contain(ShapeCheckText.NoTypeIndex);
        result.Remedy.Should().Contain(ShapeCheckText.TypeIndexRemedy);
    }

    [PostgresFact]
    public async Task A_budget_that_has_run_out_gives_a_partial_result_and_not_a_throw()
    {
        using IServiceScope scope = Services.CreateScope();
        IServiceProvider provider = scope.ServiceProvider;

        EventShapeCheckService service = new(
            provider.GetRequiredService<IOptions<MartenStudioOptions>>(),
            provider.GetRequiredService<StudioScopeResolver>(),
            provider.GetRequiredService<ColumnCatalog>(),
            provider.GetRequiredService<IndexCatalog>(),
            provider.GetRequiredService<IEventDataService>(),
            new ShapeCheckCache(),
            NullLogger<EventShapeCheckService>.Instance)
        {
            Budget = TimeSpan.Zero,
        };

        ShapeCheckResult result = await service.CheckAsync(Scope, "shape_order", false, Token);

        result.Coverage.Should().Be(ShapeCoverage.Partial);
        result.CoverageReason.Should().Contain("budget ran out");
        result.Error.Should().BeNull();
    }

    [PostgresFact]
    public async Task A_second_check_inside_five_minutes_is_the_same_result()
    {
        ShapeCheckResult first = await CheckAsync("shape_order_old");
        ShapeCheckResult second = await CheckAsync("shape_order_old");
        ShapeCheckResult again = await CheckAsync("shape_order_old", force: true);

        second.Should().BeSameAs(first);
        again.Should().BeSameAs(first, "Check again inside thirty seconds is refused");
    }
}

/// <summary>An event type that fits unless a test alters its rows.</summary>
public sealed class ShapeOrderPlaced
{
    public decimal Total { get; set; }

    public string Note { get; set; } = string.Empty;
}

/// <summary>A loadable event type no store registers: what an unmapped alias's stored .NET type names.</summary>
public sealed class ShapeLoadable
{
    public decimal Total { get; set; }
}

/// <summary>The old shape an upcaster maps.</summary>
public sealed class ShapeOrderOld
{
    public decimal Total { get; set; }
}

/// <summary>The shape the upcaster makes.</summary>
public sealed class ShapeOrderUpcast
{
    public decimal Amount { get; set; }
}

/// <summary>An old shape whose upcaster throws with the sentinel in its message.</summary>
public sealed class ShapeThrowOld
{
    public decimal Total { get; set; }
}

/// <summary>What the throwing upcaster would make.</summary>
public sealed class ShapeThrowUpcast
{
    public decimal Amount { get; set; }
}

/// <summary>An event type with more events than a check reads.</summary>
public sealed class ShapeBulk
{
    public int N { get; set; }
}
