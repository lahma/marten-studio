using Bunit;

using MartenStudio.Components.Pages.Database;
using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Services.Health;
using MartenStudio.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Database › Sessions in bUnit, over a fake service (acceptance 3, the page's half): counts for everybody the
/// store policy passes, details only behind the open gate and never asked for otherwise, the disabled state
/// naming the option, no cancel or terminate button, nothing read in a prerender, and the pool lease given back
/// with the page.
/// </summary>
public class SessionsPageTests
{
    /// <summary>A context with the fake service and an observer of a meter of this test's own.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        public required StudioComponentContext Context { get; init; }

        public required FakeSessionActivityService Sessions { get; init; }

        public required SessionPoolObserver Pools { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            Pools.Dispose();
        }
    }

    private static async Task<Fixture> ReadyAsync(SessionDetailsAccess access)
    {
        FakeSessionActivityService sessions = new() { Summary = FakeSessionActivityService.SummaryWith(access) };
        SessionPoolObserver pools = new("MartenStudio.Tests.SessionsPage." + Guid.NewGuid().ToString("N"));

        StudioComponentContext context = new(services =>
        {
            services.AddSingleton<ISessionActivityService>(sessions);
            services.AddSingleton(pools);
        });

        await context.ReadyAsync();
        return new Fixture { Context = context, Sessions = sessions, Pools = pools };
    }

    private static readonly SessionDetailsAccess Closed =
        new(false, false, null, DatabaseRefusal.CapabilityOff, SessionActivityService.CapabilityDenial);

    private static readonly SessionDetailsAccess Open = new(true, false, true, DatabaseRefusal.None, null);

    [Fact]
    public async Task Nothing_is_read_while_prerendered_and_the_circuit_reads_once()
    {
        await using Fixture fixture = await ReadyAsync(Open);
        StudioComponentContext context = fixture.Context;
        FakeSessionActivityService sessions = fixture.Sessions;
        SessionPoolObserver pools = fixture.Pools;

        string html = await DatabasePagePrerenderTests.StaticAsync<Sessions>(context);

        html.Should().Contain("ms-loading-spinner");
        sessions.SummaryRequests.Should().BeEmpty("a static render reads - and audits - nothing");
        sessions.DetailsRequests.Should().BeEmpty();
        pools.IsListening.Should().BeFalse("a prerender takes no lease on the pool observer");

        var page = context.Render<Sessions>();
        page.WaitForAssertion(() => page.FindAll(".ms-sessions-stats").Should().ContainSingle());

        sessions.SummaryRequests.Should().ContainSingle();
        sessions.DetailsRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Without_the_capability_the_page_shows_counts_names_the_option_and_never_asks_for_details()
    {
        await using Fixture fixture = await ReadyAsync(Closed);
        StudioComponentContext context = fixture.Context;
        FakeSessionActivityService sessions = fixture.Sessions;

        var page = context.Render<Sessions>();
        page.WaitForAssertion(() => page.FindAll(".ms-sessions-stats").Should().ContainSingle());

        page.Find(".ms-capability-disabled").TextContent.Should().Contain("MartenStudioOptions.Capabilities.ViewDatabaseActivity");
        sessions.DetailsRequests.Should().BeEmpty("a page whose gate is closed refuses - and audits - nothing");

        page.TextOfAll(".ms-sessions-table thead th").Should().NotContain("pid").And.NotContain("Statement");
        page.Markup.Should().NotContain("4242").And.NotContain("app_user").And.NotContain("billing-api").And.NotContain("10.1.2.3")
            .And.NotContain("gid-orphan").And.NotContain("legacy.employees");
        page.Markup.Should().Contain("idle in transaction").And.Contain("12 min");
    }

    [Fact]
    public async Task Through_the_gate_the_page_names_the_sessions_and_shows_their_statements()
    {
        await using Fixture fixture = await ReadyAsync(Open);
        StudioComponentContext context = fixture.Context;
        FakeSessionActivityService sessions = fixture.Sessions;
        sessions.Details = FakeSessionActivityService.DetailsFor();

        var page = context.Render<Sessions>();
        page.WaitForAssertion(() => page.FindAll(".ms-sessions-statement code").Should().NotBeEmpty());

        page.TextOfAll(".ms-sessions-table thead th").Should().Contain("pid").And.Contain("Statement");
        page.Markup.Should().Contain("4242").And.Contain("billing-api").And.Contain("gid-orphan");
        page.Find(".ms-sessions-statement code").TextContent.Should().Contain("‹withheld›.employees");
        page.FindAll(".ms-capability-disabled").Should().BeEmpty();
    }

    [Fact]
    public async Task A_write_policy_refusal_says_which_policy_and_asks_for_nothing_more()
    {
        await using Fixture fixture = await ReadyAsync(
            new SessionDetailsAccess(true, false, false, DatabaseRefusal.WritePolicy, SessionActivityService.WritePolicyDenial));
        StudioComponentContext context = fixture.Context;
        FakeSessionActivityService sessions = fixture.Sessions;

        var page = context.Render<Sessions>();
        page.WaitForAssertion(() => page.FindAll(".ms-sessions-gate").Should().NotBeEmpty());

        page.Markup.Should().Contain("WriteAuthorizationPolicy");
        sessions.DetailsRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task There_is_no_button_that_ends_a_session()
    {
        await using Fixture fixture = await ReadyAsync(Open);
        StudioComponentContext context = fixture.Context;
        FakeSessionActivityService sessions = fixture.Sessions;
        sessions.Details = FakeSessionActivityService.DetailsFor();

        var page = context.Render<Sessions>();
        page.WaitForAssertion(() => page.FindAll(".ms-sessions-statement code").Should().NotBeEmpty());

        page.TextOfAll("button").Should().Equal(["Refresh"], "the studio never cancels or terminates another session (plan §9)");
        page.Markup.Should().NotContainEquivalentOf("pg_cancel_backend").And.NotContainEquivalentOf("pg_terminate_backend");
    }

    [Fact]
    public async Task The_page_holds_a_lease_on_the_pool_observer_while_it_is_open_and_gives_it_back()
    {
        await using Fixture fixture = await ReadyAsync(Closed);
        StudioComponentContext context = fixture.Context;
        SessionPoolObserver pools = fixture.Pools;

        var page = context.Render<Sessions>();
        page.WaitForAssertion(() => page.FindAll(".ms-sessions-stats").Should().ContainSingle());

        pools.IsListening.Should().BeTrue("the page shows this process's own pool while it is open");

        await context.DisposeComponentsAsync();

        pools.IsListening.Should().BeFalse("the last lease takes the listener with it");
    }

    [Fact]
    public async Task A_refusal_of_the_summary_is_said_in_its_own_words()
    {
        await using Fixture fixture = await ReadyAsync(Closed);
        StudioComponentContext context = fixture.Context;
        FakeSessionActivityService sessions = fixture.Sessions;
        sessions.Summary = SessionActivitySummary.Refused(DatabaseRefusal.StorePolicy, SessionActivityService.StorePolicyDenial);

        var page = context.Render<Sessions>();
        page.WaitForAssertion(() => page.FindAll(".ms-sessions-refusal").Should().ContainSingle());

        page.Find(".ms-sessions-refusal").TextContent.Should().Contain("no tenant selected");
        page.FindAll(".ms-sessions-stats").Should().BeEmpty();
    }
}

/// <summary>Database › Sessions' service, as a test says it answers.</summary>
internal sealed class FakeSessionActivityService : ISessionActivityService
{
    private static readonly DateTimeOffset ReadAt = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Every summary asked for.</summary>
    public List<StudioScope> SummaryRequests { get; } = [];

    /// <summary>Every details read asked for.</summary>
    public List<StudioScope> DetailsRequests { get; } = [];

    /// <summary>The summary it answers.</summary>
    public SessionActivitySummary Summary { get; set; } = SummaryWith(new(false, false, null, DatabaseRefusal.CapabilityOff, null));

    /// <summary>The details it answers.</summary>
    public SessionActivityDetails Details { get; set; } = DetailsFor();

    /// <inheritdoc />
    public Task<SessionActivitySummary> GetSummaryAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        SummaryRequests.Add(scope);
        return Task.FromResult(Summary);
    }

    /// <inheritdoc />
    public Task<SessionActivityDetails> GetDetailsAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        DetailsRequests.Add(scope);
        return Task.FromResult(Details);
    }

    /// <summary>Two sessions, two locks and a prepared transaction, counted, with <paramref name="access" /> as the gate.</summary>
    public static SessionActivitySummary SummaryWith(SessionDetailsAccess access) => new()
    {
        MeasuredAt = ReadAt,
        SessionCount = 2,
        ByState = [new("idle in transaction", 1), new("active", 1)],
        Sessions =
        [
            new SessionSummaryRow("idle in transaction", "Client", TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(11), 0, true),
            new SessionSummaryRow("active", "Lock", TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(40), 1, false),
        ],
        Waiting = 1,
        LongestTransaction = TimeSpan.FromMinutes(12),
        Locks =
        [
            new LockSummaryRow("another transaction to finish", "ShareLock", false, 1, TimeSpan.FromSeconds(40)),
            new LockSummaryRow("a relation Marten does not own", "RowExclusiveLock", true, 1, null),
        ],
        LockCount = 2,
        PreparedCount = 1,
        OldestPrepared = TimeSpan.FromMinutes(30),
        MaxPreparedTransactions = 10,
        Pool = new SessionPoolSummary(true, 3, 7, 100, true),
        Details = access,
        Statements = [new HealthStatement("select 1", [])],
    };

    /// <summary>The details behind the gate, a withheld schema already masked in a statement.</summary>
    public static SessionActivityDetails DetailsFor() => new()
    {
        MeasuredAt = ReadAt,
        Sessions =
        [
            new SessionDetailRow(4242, "app_user", "billing-api", "10.1.2.3", ReadAt.AddHours(-1), "idle in transaction", "Client", "ClientRead",
                TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(11), [],
                new SessionStatementText("update ‹withheld›.employees set salary = 1 where id = 7", null)),
        ],
        Locks = [new LockDetailRow(4242, "a relation Marten does not own", null, "RowExclusiveLock", true, null)],
        Prepared = [new PreparedDetailRow("gid-orphan", "tx_owner", ReadAt.AddMinutes(-30), TimeSpan.FromMinutes(30))],
    };
}
