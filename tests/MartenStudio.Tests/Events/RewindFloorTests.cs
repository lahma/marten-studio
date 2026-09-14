using MartenStudio.Services.Events;
using MartenStudio.Tests.Projections;

namespace MartenStudio.Tests.Events;

/// <summary>
/// The one number a dead-letter rewind turns on, and the argument order it is handed over in.
/// </summary>
/// <remarks>
/// <para>
/// A rewind is asked for by the sequence of the event that failed, and Marten is given the sequence
/// <em>below</em> it. <c>RewindSubscriptionProgressAsync</c> writes the floor into
/// <c>mt_event_progression.last_seq_id</c>, which records the last sequence the shard has already
/// processed, and the restarted agent asks for events strictly after it (verified against Marten 9.35).
/// Handing over the dead letter's own sequence would rewind the shard to just past the event somebody is
/// trying to replay, which is the one thing the button must not do - and it would look right in every
/// screenshot.
/// </para>
/// <para>
/// The order matters just as much. <c>RewindSubscriptionAsync(string, CancellationToken, long?,
/// DateTimeOffset?)</c> takes the token second and both interesting arguments last and optional
/// (JasperFx.Events 2.69.3), so a call written in the usual order compiles and silently means
/// <c>sequenceFloor: 0</c> - a replay of the whole projection.
/// </para>
/// </remarks>
public class RewindFloorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(42, 41)]
    [InlineData(2, 1)]
    [InlineData(1_000_000, 999_999)]
    public void The_floor_is_one_below_the_event_that_failed(long sequence, long expected)
    {
        EventDataService.RewindFloor(sequence).Should().Be(expected);
    }

    /// <summary>
    /// Marten treats a floor of zero as "delete the progression row", which replays the projection from
    /// its first event - and that is the right answer when the poison event <em>is</em> the first one.
    /// </summary>
    [Fact]
    public void The_first_event_rewinds_to_a_floor_of_zero()
    {
        EventDataService.RewindFloor(1).Should().Be(0);
    }

    [Fact]
    public async Task The_daemon_is_given_the_floor_as_the_third_argument_and_no_timestamp()
    {
        using var daemon = new FakeProjectionDaemon();

        await EventDataService.RewindToFloorAsync(daemon, "OrderSummary", 41, Token);

        daemon.Rewinds.Should().ContainSingle().Which.Should().Be("OrderSummary/41");
        daemon.LastSequenceFloor.Should().Be(41);

        daemon.LastTimestamp.Should().BeNull(
            "the studio rewinds to a sequence it was handed, never to a wall-clock time Marten would have "
            + "to resolve to a sequence of its own");
    }

    [Fact]
    public async Task A_null_daemon_is_refused_rather_than_dereferenced()
    {
        Func<Task> rewinding = () => EventDataService.RewindToFloorAsync(null!, "OrderSummary", 41, Token);

        await rewinding.Should().ThrowAsync<ArgumentNullException>();
    }
}
