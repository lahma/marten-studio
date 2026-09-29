using JasperFx;
using JasperFx.Events.Projections;

using Marten;
using Marten.Storage;

using MartenStudio.SampleDomain.Events;
using MartenStudio.Services;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// DB-0-fix-2, F1: a store whose event tables exist and hold no event has a high-water mark of zero, and
/// the projections page raises no "nothing is running these projections" alarm for it.
/// </summary>
/// <remarks>
/// <para>
/// Postgres reports a sequence nothing has drawn from as <c>last_value = 1</c>, <c>is_called = false</c>:
/// the value the next <c>nextval</c> will return, not one any event has. The studio read
/// <c>select last_value</c> as it stood, so a fresh event store had a mark of 1, every async projection
/// lagged it by one, and <c>ProjectionsView.NoDaemonAnywhere</c> - which DB-0-fix taught to need a mark
/// above zero precisely so that a fresh install raises nothing - raised it anyway. The statement is
/// <c>case when is_called then last_value else 0 end</c> now.
/// </para>
/// <para>
/// The event tables are created by Marten's own call, which a test may make and the studio never does
/// (hard rule 14); nothing is appended; no daemon is hosted; one async projection is registered, which
/// is the shape that raises the banner.
/// </para>
/// </remarks>
public class HighWaterMarkLiveTests(PostgresFixture fixture)
{
    private static readonly StudioScope Scope = EmptySchemaStudio.Scope;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_store_with_event_tables_and_no_events_has_a_high_water_mark_of_zero_and_no_banner()
    {
        await using EmptySchemaStudio host = await StartAsync(nameof(A_store_with_event_tables_and_no_events_has_a_high_water_mark_of_zero_and_no_banner));

        IMartenDatabase database = await DatabaseOf(host);
        await database.AllProjectionProgress(Token);

        // The shape the bug came from, read raw: the sequence exists, has handed nothing out, and reports 1.
        (long lastValue, bool isCalled) = await ReadSequenceAsync(host);
        lastValue.Should().Be(1);
        isCalled.Should().BeFalse("nothing has been appended");

        ProjectionsView view = await host.ProjectionsAsync(x => x.GetProjectionsAsync(Scope, Token));

        view.HasEventStore.Should().BeTrue("the tables are there; this is not the no-event-store case");
        view.HighWaterMark.Should().Be(0, "a sequence that has handed nothing out has no high-water mark");
        view.Daemon.IsHostedHere.Should().BeFalse();
        view.Projections.Should().Contain(x => x.IsAsync);
        view.NoDaemonAnywhere.Should().BeFalse("there has been nothing to advance over");

        long? highest = await host.EventsAsync(x => x.GetHighestSequenceAsync(Scope, Token));
        highest.Should().Be(0, "the event feed's follow tick reads the same statement");

        ProjectionSummary summary = await host.ProjectionsAsync(x => x.GetSummaryAsync(Scope, Token));
        summary.HighWaterMark.Should().Be(0);
        summary.MaxLag.Should().Be(0);
    }

    /// <summary>
    /// The anti-vacuity half: one appended event is a sequence that has handed out 1, which reads 1 - and
    /// with no daemon anywhere to advance over it, the banner is raised.
    /// </summary>
    [PostgresFact]
    public async Task The_first_event_moves_it_to_one_and_the_banner_is_raised()
    {
        await using EmptySchemaStudio host = await StartAsync(nameof(The_first_event_moves_it_to_one_and_the_banner_is_raised));

        IDocumentStore store = host.Services.GetRequiredService<IDocumentStore>();
        IMartenDatabase database = await DatabaseOf(host);
        await database.AllProjectionProgress(Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new OrderPlaced(Guid.NewGuid(), "acme", DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(Token);
        }

        (long lastValue, bool isCalled) = await ReadSequenceAsync(host);
        lastValue.Should().Be(1);
        isCalled.Should().BeTrue("1 has been handed out now");

        ProjectionsView view = await host.ProjectionsAsync(x => x.GetProjectionsAsync(Scope, Token));

        view.HighWaterMark.Should().Be(1);
        view.NoDaemonAnywhere.Should().BeTrue("there is an event and nothing has advanced over it");
    }

    private Task<EmptySchemaStudio> StartAsync(string name) =>
        EmptySchemaStudio.StartAsync(
            fixture,
            "hwm_",
            name,
            AutoCreate.CreateOrUpdate,
            static options => options.Projections.Add(new DailySalesProjection(), ProjectionLifecycle.Async));

    private static async Task<IMartenDatabase> DatabaseOf(EmptySchemaStudio host)
    {
        IDocumentStore store = host.Services.GetRequiredService<IDocumentStore>();
        IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases();

        return databases[0];
    }

    /// <summary>The event sequence's two columns, read on a connection of the test's own.</summary>
    private async Task<(long LastValue, bool IsCalled)> ReadSequenceAsync(EmptySchemaStudio host)
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync(Token);

        // The schema name is this class's own constant plus the test's name, never input; it is quoted
        // regardless, like every identifier this suite writes.
        await using var command = new NpgsqlCommand(
            $"select last_value, is_called from {MartenStudio.Internal.Sql.SqlIdentifier.Qualify(host.EventSchema, "mt_events_sequence")}",
            connection);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).Should().BeTrue();

        return (reader.GetInt64(0), reader.GetBoolean(1));
    }
}
