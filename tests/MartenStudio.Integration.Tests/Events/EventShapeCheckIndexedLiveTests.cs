using Marten;

using MartenStudio.Services.Shapes;

using Npgsql;

using static MartenStudio.Integration.Tests.Events.EventShapeLiveSupport;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// 1.2-SHAPE-B: the events' shape check with <c>opts.Events.EnableEventTypeIndex = true</c>. Where the index leads with <c>type</c> each
/// probe between the oldest and newest events is one index descent, so the sample is the 25 oldest, the 25 newest and up to 150 spread
/// between them - and the result carries no remedy line about the index.
/// </summary>
public class EventShapeCheckIndexedLiveTests(EventShapeCheckIndexedLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<EventShapeCheckIndexedLiveTests.Fixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The store: 450 events of one type, the oldest and the newest corrupt, an index on <c>(type, seq_id)</c>.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <inheritdoc />
        protected override bool SeedSampleData => false;

        /// <inheritdoc />
        protected override void ConfigureStore(StoreOptions options)
        {
            options.Events.MapEventType<ShapeBulk>("shape_bulk");
            options.Events.EnableEventTypeIndex = true;
        }

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            Guid stream = Guid.NewGuid();

            await using (IDocumentSession session = Marten.Store.LightweightSession())
            {
                session.Events.StartStream(stream, new ShapeBulk { N = 0 });
                await session.SaveChangesAsync(token);
            }

            await using NpgsqlConnection connection = await Postgres.OpenAsync(token);
            string events = EventsTable(Schema);

            await ExecuteAsync(
                connection,
                $"insert into {events} (seq_id, id, stream_id, version, data, type, timestamp, mt_dotnet_type, is_archived) "
                + $"select nextval('\"{Schema}_events\".mt_events_sequence'), gen_random_uuid(), '{stream}', 1000 + n, ('{{\"N\":' || n || '}}')::jsonb, 'shape_bulk', now(), "
                + $"'{typeof(ShapeBulk).FullName}, {typeof(ShapeBulk).Assembly.GetName().Name}', false from generate_series(1, 450) n",
                token);

            List<long> bulk = await SequencesAsync(connection, Schema, "shape_bulk", token);

            await ExecuteAsync(connection, $"update {events} set data = '{{\"N\":\"x\"}}'::jsonb where seq_id in ({bulk[0]}, {bulk[^1]})", token);
            await ExecuteAsync(connection, $"analyze {events}", token);
        }
    }

    private async Task<ShapeCheckResult> CheckAsync(string eventType)
    {
        using MartenFixture.ScopedService<IEventShapeCheck> check = Marten.Resolve<IEventShapeCheck>();
        return await check.Service.CheckAsync(Scope, eventType, false, Token);
    }

    [PostgresFact]
    public async Task With_an_index_on_type_the_sample_is_the_edges_and_a_spread_between_them_and_no_remedy_is_offered()
    {
        ShapeCheckResult result = await CheckAsync("shape_bulk");

        result.Error.Should().BeNull();
        result.Coverage.Should().Be(ShapeCoverage.Sampled);
        result.LowestPart.Should().Be(ShapeCheckLimits.Edge);
        result.HighestPart.Should().BeGreaterThan(10, "a few of the newest are found by the spread first");
        result.SpreadPart.Should().BeGreaterThan(50);
        (result.LowestPart + result.HighestPart + result.SpreadPart).Should().Be(result.Examined);
        result.Examined.Should().BeLessThanOrEqualTo(ShapeCheckLimits.SampleSize);
        result.CoverageReason.Should().BeNull();
        result.Remedy.Should().NotContain(ShapeCheckText.TypeIndexRemedy);
        result.Statements.Select(static x => x.Sql).Should().Contain(x => x.Contains("unnest(@probes)", StringComparison.Ordinal));

        result.Types.Single().Failures.Should().ContainSingle().Which.Count.Should().Be(2, "the oldest and the newest event are the two corrupt rows");
    }
}
