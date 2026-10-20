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

    // ------------------------------------------------------------------------------------------------
    // L2: the per-tenant rewind (Marten 9.51 with JasperFx 2.91)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>RewindSubscriptionAsync(name, tenantId, token, floor, timestamp)</c> takes the tenant second and the token
    /// third, so a call that put the floor where the tenant goes would not compile - but one that swapped the tenant and
    /// the projection would, and rewind a projection named after a tenant.
    /// </summary>
    [Fact]
    public async Task The_tenant_rewind_is_given_the_tenant_second_and_the_floor_fourth_and_no_timestamp()
    {
        using var daemon = new FakeProjectionDaemon();

        await EventDataService.RewindTenantToFloorAsync(daemon, "OrderSummary", "acme", 41, Token);

        daemon.TenantRewinds.Should().ContainSingle().Which.Should().Be("OrderSummary/acme/41");
        daemon.Rewinds.Should().BeEmpty("the store-global overload is never the one asked for a tenant");
        daemon.LastTimestamp.Should().BeNull();
    }

    /// <summary>
    /// The agent the gate finds is the projection's for that tenant, compared as the daemon itself stops agents: the
    /// projection name ignoring case (Marten's comparison), the tenant ordinally - and it is that agent's own names the
    /// call is made with, so JasperFx's ordinal stop finds the agent that is running.
    /// </summary>
    [Fact]
    public void The_tenant_agent_is_the_projections_agent_for_exactly_that_tenant()
    {
        using var daemon = new FakeProjectionDaemon();
        daemon.Agents.Add(NamedAgent.For("OrderSummary:All:globex"));
        daemon.Agents.Add(NamedAgent.For("OrderSummary:All"));
        daemon.Agents.Add(NamedAgent.For("Other:All:acme"));

        EventDataService.TenantAgentOf(daemon, "OrderSummary", "acme").Should().BeNull(
            "the daemon here runs globex's agent, the store-global one and another projection's acme agent - not acme's");

        daemon.Agents.Add(NamedAgent.For("OrderSummary:All:Acme"));
        EventDataService.TenantAgentOf(daemon, "OrderSummary", "acme").Should().BeNull("a tenant id is compared ordinally");

        daemon.Agents.Add(NamedAgent.For("OrderSummary:All:acme"));
        JasperFx.Events.Projections.ShardName found = EventDataService.TenantAgentOf(daemon, "ordersummary", "acme")!;

        found.Name.Should().Be("OrderSummary", "the agent's own spelling is what the daemon is handed");
        found.TenantId.Should().Be("acme");
    }

    /// <summary>
    /// Whether a daemon implements the per-tenant rewind rather than inheriting <c>IProjectionDaemon</c>'s throwing
    /// default: Marten's own <c>ProjectionDaemon</c> does exactly from JasperFx 2.91 - which is what the light-up probe's
    /// first half says - and a daemon that overrides it does at every version.
    /// </summary>
    [Fact]
    public void Martens_daemon_implements_the_tenant_rewind_exactly_where_the_probe_says_JasperFx_does()
    {
        bool jasperFxImplementsIt = typeof(global::Marten.Events.Daemon.ProjectionDaemon).GetMethod(
            "RewindSubscriptionAsync",
            [typeof(string), typeof(string), typeof(CancellationToken), typeof(long?), typeof(DateTimeOffset?)]) is not null;

        EventDataService.ImplementsTenantRewind(typeof(global::Marten.Events.Daemon.ProjectionDaemon)).Should().Be(jasperFxImplementsIt);
        jasperFxImplementsIt.Should().Be(
            MartenStudio.Services.Configuration.MartenRuntime.Current.VersionOf(MartenStudio.Services.Configuration.RuntimePackage.JasperFxEvents)
                >= new Version(2, 91, 0),
            "jasperfx#1025 implemented it in 2.91.0");

        using var daemon = new FakeProjectionDaemon();
        EventDataService.ImplementsTenantRewind(daemon).Should().BeTrue("the fake implements the overload itself");
    }
}

/// <summary>An agent that answers its name and nothing else - which is all the per-tenant rewind's gate reads.</summary>
public class NamedAgent : System.Reflection.DispatchProxy
{
    private JasperFx.Events.Projections.ShardName? name;

    internal static JasperFx.Events.Daemon.ISubscriptionAgent For(string identity)
    {
        JasperFx.Events.Daemon.ISubscriptionAgent agent =
            Create<JasperFx.Events.Daemon.ISubscriptionAgent, NamedAgent>();

        JasperFx.Events.Projections.ShardName.TryParse(identity, out JasperFx.Events.Projections.ShardName? parsed).Should().BeTrue();
        ((NamedAgent) (object) agent).name = parsed;

        return agent;
    }

    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == "get_Name"
            ? name
            : throw new NotSupportedException($"NamedAgent does not implement {targetMethod?.Name}.");
}
