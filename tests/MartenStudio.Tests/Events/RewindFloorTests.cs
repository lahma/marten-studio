using MartenStudio.Services.Events;
using MartenStudio.Tests.Projections;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

        await EventDataService.RewindToFloorAsync(daemon, "OrderSummary", 41, NullLogger.Instance, "default", "db1", Token);

        daemon.Rewinds.Should().ContainSingle().Which.Should().Be("OrderSummary/41");
        daemon.LastSequenceFloor.Should().Be(41);

        daemon.LastTimestamp.Should().BeNull(
            "the studio rewinds to a sequence it was handed, never to a wall-clock time Marten would have "
            + "to resolve to a sequence of its own");
    }

    [Fact]
    public async Task A_null_daemon_is_refused_rather_than_dereferenced()
    {
        Func<Task> rewinding = () => EventDataService.RewindToFloorAsync(null!, "OrderSummary", 41, NullLogger.Instance, "default", "db1", Token);

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

        await EventDataService.RewindTenantToFloorAsync(daemon, "OrderSummary", "acme", 41, NullLogger.Instance, "default", "db1", Token);

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
    /// A tenant id with a <c>:</c> in it makes an agent identity - <c>OrderSummary:All:a:b</c> - that does not read back as
    /// that tenant's. Marten 9.51 parses the identity the daemon hands it only after the daemon has hard-stopped the
    /// tenant's agents, so such an agent is never the one the rewind is made for, and such a tenant is not addressable.
    /// </summary>
    [Fact]
    public void An_agent_whose_identity_does_not_read_back_is_never_rewound()
    {
        var colon = new JasperFx.Events.Projections.ShardName("OrderSummary", JasperFx.Events.Projections.ShardName.All, 1, "a:b");

        colon.Identity.Should().Be("OrderSummary:All:a:b");
        TenantRewind.RoundTrips(colon).Should().BeFalse("ShardName.TryParse cannot read a tenant with a ':' back");

        using var daemon = new FakeProjectionDaemon();
        daemon.Agents.Add(NamedAgent.For(colon));

        EventDataService.TenantAgentOf(daemon, "OrderSummary", "a:b").Should().BeNull(
            "the daemon runs it, but Marten could not find its shard after the daemon had stopped it");

        TenantRewind.IsAddressable("a:b").Should().BeFalse();
        TenantRewind.IsAddressable("acme").Should().BeTrue();
        TenantRewind.IsAddressable("acme_corp-2").Should().BeTrue();
        TenantRewind.IsAddressable(null).Should().BeFalse();
        TenantRewind.RoundTrips(new JasperFx.Events.Projections.ShardName("OrderSummary", "All", 2, "acme")).Should().BeTrue(
            "a versioned shard's identity reads back too");
    }

    /// <summary>
    /// Marten's and JasperFx's "Unknown subscription name 'x'. Available options are ..." lists every subscription the
    /// store has; neither rewind lets it through - not as the message, and not as an inner exception - so it cannot reach
    /// the audit ring or the page (D5).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_unknown_subscription_refusal_never_carries_the_stores_subscription_list(bool perTenant)
    {
        var listing = new ArgumentOutOfRangeException(
            "Unknown subscription name 'OrderSummary:All:acme'. Available options are OrderSummary, SecretLedger, PayrollAudit",
            innerException: null);

        using var daemon = new FakeProjectionDaemon { RewindFailure = listing };
        var log = new RecordingLogger();

        Func<Task> rewinding = perTenant
            ? () => EventDataService.RewindTenantToFloorAsync(daemon, "OrderSummary", "acme", 41, log, "default", "db1", Token)
            : () => EventDataService.RewindToFloorAsync(daemon, "OrderSummary", 41, log, "default", "db1", Token);

        InvalidOperationException refused = (await rewinding.Should().ThrowAsync<InvalidOperationException>()).Which;

        refused.Message.Should().Be(StreamCompactionRules.SubscriptionNotRecognised("OrderSummary", perTenant ? "acme" : null));
        refused.Message.Should().NotContain("SecretLedger").And.NotContain("PayrollAudit").And.NotContain("Available options");
        refused.InnerException.Should().BeNull("an inner exception's message is one ToString() away from a page");

        // What Marten said is kept where only the host's own log sees it: event 9320, with Marten's exception whole.
        (LogLevel Level, EventId EventId, Exception? Exception, string Message) line = log.Lines.Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Warning);
        line.EventId.Id.Should().Be(9320);
        line.Exception.Should().BeSameAs(listing);
        line.Message.Should().Contain("OrderSummary").And.Contain(perTenant ? "tenant acme" : "tenant (all tenants)")
            .And.Contain("store default").And.Contain("database db1");
    }

    /// <summary>
    /// An <see cref="AggregateException" /> carries its inner exceptions' messages in its own, but not theirs in turn - so
    /// a listing two levels down, under the second of its inner exceptions, is found only by walking each of them.
    /// </summary>
    [Fact]
    public async Task A_subscription_listing_inside_an_aggregate_is_found_and_never_passed_on()
    {
        var aggregate = new AggregateException(
            new TimeoutException("the shard did not stop in time"),
            new InvalidOperationException(
                "the rewind failed",
                new ArgumentOutOfRangeException(
                    "Unknown subscription name 'OrderSummary:All:acme'. Available options are OrderSummary, SecretLedger",
                    innerException: null)));

        aggregate.Message.Should().NotContain("Available options", "the premise: only the walk can find it");
        aggregate.InnerException.Should().BeOfType<TimeoutException>("and the first inner exception is not it");

        EventDataService.ListsSubscriptions(aggregate).Should().BeTrue();
        EventDataService.ListsSubscriptions(new AggregateException(new TimeoutException("only this"))).Should().BeFalse();
        EventDataService.ListsSubscriptions(null).Should().BeFalse();

        using var daemon = new FakeProjectionDaemon { RewindFailure = aggregate };
        var log = new RecordingLogger();

        Func<Task> rewinding = () => EventDataService.RewindTenantToFloorAsync(daemon, "OrderSummary", "acme", 41, log, "default", "db1", Token);

        (await rewinding.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().NotContain("SecretLedger");
        log.Lines.Should().ContainSingle().Which.Exception.Should().BeSameAs(aggregate);
    }

    [Fact]
    public async Task Any_other_rewind_failure_keeps_its_own_words()
    {
        using var daemon = new FakeProjectionDaemon { RewindFailure = new TimeoutException("the shard did not stop in time") };
        var log = new RecordingLogger();

        await ((Func<Task>) (() => EventDataService.RewindTenantToFloorAsync(daemon, "OrderSummary", "acme", 41, log, "default", "db1", Token)))
            .Should().ThrowAsync<TimeoutException>().WithMessage("the shard did not stop in time");

        log.Lines.Should().BeEmpty("a failure that lists nothing is the audit's to record, as it is");
    }

    /// <summary>Every line logged, with its exception - which the shared capturing provider does not keep.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, EventId EventId, Exception? Exception, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, eventId, exception, formatter(state, exception)));
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
        JasperFx.Events.Projections.ShardName.TryParse(identity, out JasperFx.Events.Projections.ShardName? parsed).Should().BeTrue();

        return For(parsed!);
    }

    /// <summary>An agent named exactly <paramref name="name" /> - one whose identity need not parse back.</summary>
    internal static JasperFx.Events.Daemon.ISubscriptionAgent For(JasperFx.Events.Projections.ShardName name)
    {
        JasperFx.Events.Daemon.ISubscriptionAgent agent =
            Create<JasperFx.Events.Daemon.ISubscriptionAgent, NamedAgent>();

        ((NamedAgent) (object) agent).name = name;

        return agent;
    }

    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == "get_Name"
            ? name
            : throw new NotSupportedException($"NamedAgent does not implement {targetMethod?.Name}.");
}
