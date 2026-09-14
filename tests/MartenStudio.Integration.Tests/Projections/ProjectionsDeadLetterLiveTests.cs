using JasperFx.Events.Daemon;

using Marten;

using MartenStudio.Services.Projections;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// The one dead letter the demo's poisoned stream produces, and the shard carrying on past it.
/// </summary>
/// <remarks>
/// <para>
/// Its own class, and therefore its own schema pair, host and daemon: a rebuild replays the poison event
/// and writes the dead letter again, so "exactly one" is only a true statement in a store nothing else
/// has rebuilt. The rebuild tests live next door in <see cref="ProjectionsLiveTests" /> for that reason.
/// </para>
/// <para>
/// Read here with a direct query - the dead-letter screen itself belongs to the events area.
/// </para>
/// </remarks>
public class ProjectionsDeadLetterLiveTests(PostgresFixture postgres) : ProjectionsTestBase(postgres)
{
    private const string ShipmentTrackerName = "ShipmentTracker";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Exactly one poisoned event is seeded, <c>SkipApplyErrors</c> is on, and nothing in this class
    /// rebuilds: so the daemon writes exactly one dead letter for <c>ShipmentTracker</c>, and the shard
    /// then keeps moving rather than parking on the event it could not apply.
    /// </summary>
    [PostgresFact]
    public async Task The_poisoned_stream_produces_exactly_one_dead_letter_and_the_shard_keeps_moving()
    {
        await ProjectionsFixture.WaitForAsync(
            async () =>
            {
                await using IQuerySession session = Fixture.Store.QuerySession();
                return await session.Query<DeadLetterEvent>().AnyAsync(Token);
            },
            "the daemon to record the poisoned event as a dead letter",
            TimeSpan.FromMinutes(2),
            Token);

        await using (IQuerySession letters = Fixture.Store.QuerySession())
        {
            IReadOnlyList<DeadLetterEvent> dead = await letters.Query<DeadLetterEvent>().ToListAsync(Token);

            DeadLetterEvent[] shipments = [.. dead.Where(x => x.ProjectionName == ShipmentTrackerName)];

            shipments.Should().ContainSingle(
                "the seeder writes exactly one POISON item, and one skipped event is one dead letter");

            DeadLetterEvent letter = shipments[0];
            letter.EventSequence.Should().BeGreaterThan(0);
            letter.ShardName.Should().NotBeNullOrWhiteSpace();

            // The projection threw an InvalidOperationException on the POISON item; Marten wraps it with
            // the event it could not apply, so the message names the event rather than the SKU.
            letter.ExceptionType.Should().Contain(nameof(InvalidOperationException));
            letter.ExceptionMessage.Should().Contain(
                $"#{letter.EventSequence}", "the dead letter names the event it could not apply");
        }

        // The point of skipping rather than pausing: the shard is past the event it could not apply and
        // has caught up with everything after it. A dead letter with a parked shard would be the failure
        // this configuration exists to avoid.
        await ProjectionsFixture.WaitForAsync(
            async () =>
            {
                ProjectionsView view = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));
                ShardProgress? shard = view.Progress.FirstOrDefault(x => x.ProjectionName == ShipmentTrackerName);
                return shard is { HasProgressRow: true, Lag: 0, Sequence: > 0 };
            },
            $"{ShipmentTrackerName} to catch up past the event it skipped",
            TimeSpan.FromMinutes(2),
            Token);

        ProjectionsView caughtUp = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));
        ShardProgress tracker = caughtUp.Progress.Single(x => x.ProjectionName == ShipmentTrackerName);

        tracker.Sequence.Should().Be(caughtUp.HighWaterMark);
        tracker.Failure.Should().BeNull("a skipped event is recorded, not a shard failure");

        // And exactly one dead letter still, now that it has been all the way to the end.
        await using IQuerySession after = Fixture.Store.QuerySession();
        IReadOnlyList<DeadLetterEvent> all = await after.Query<DeadLetterEvent>().ToListAsync(Token);

        all.Count(x => x.ProjectionName == ShipmentTrackerName).Should().Be(1);
    }
}
