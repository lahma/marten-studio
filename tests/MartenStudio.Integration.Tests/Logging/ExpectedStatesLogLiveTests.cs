using JasperFx.Events.Daemon;

using Marten;

using MartenStudio.Integration.Tests.Projections;
using MartenStudio.Services;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Events;
using MartenStudio.Services.Live;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using MartenCoordinator = Marten.Events.Daemon.Coordination.IProjectionCoordinator;

namespace MartenStudio.Integration.Tests.Logging;

/// <summary>
/// What a circuit on the Overview and the projections screen asks, tick after tick, in one DI scope the
/// way a Blazor circuit holds one.
/// </summary>
/// <remarks>
/// <para>
/// The ticks are called directly rather than waited for: the page's loop is a <c>PeriodicTimer</c> over
/// exactly these calls, and a test that slept through several <c>RefreshInterval</c>s would be a slower
/// way of making the same calls (AGENTS.md: never <c>Task.Delay</c>).
/// </para>
/// <para>
/// Every read is the one the page makes - the Overview's six regions and its store cards, the projections
/// screen's view, running operations and tracker lease, and the navigation badges - so the log that
/// results is the log a production host gets from a tab left open on either page.
/// </para>
/// </remarks>
internal static class StudioPolling
{
    /// <summary>How many ticks each test runs: enough that "once" and "every time" cannot be confused.</summary>
    public const int Ticks = 6;

    public static async Task PollOverviewAndProjectionsAsync(ProjectionsFixture fixture, int ticks, CancellationToken token)
    {
        using IServiceScope circuit = fixture.CreateScope();
        IServiceProvider services = circuit.ServiceProvider;

        var state = services.GetRequiredService<StudioState>();
        await state.EnsureInitializedAsync(token);

        StudioScope scope = state.ActiveScope ?? throw new InvalidOperationException("The studio settled on no scope.");

        var storeInfo = services.GetRequiredService<IStoreInfoService>();
        var events = services.GetRequiredService<IEventDataService>();
        var projections = services.GetRequiredService<IProjectionDataService>();
        var navigation = services.GetRequiredService<INavIndicatorService>();

        // The Overview's store cards, read once per page load.
        _ = await storeInfo.GetOverviewAsync(token);

        using IDisposable lease = await projections.SubscribeToLiveStateAsync(scope, token);

        for (int tick = 0; tick < ticks; tick++)
        {
            // The Overview's tick, region by region.
            EventStoreCounts counts = await events.GetEventStoreCountsAsync(scope, token);
            counts.TablesExist.Should().BeTrue("the fixture seeded events, so every region below is really read");

            _ = await projections.GetSummaryAsync(scope, token);
            _ = await events.CountDeadLettersAsync(scope, token);
            _ = await events.GetFeedAsync(
                scope, new EventFeedRequest { PageSize = 15, IncludeArchived = false, IncludeSkipped = true }, token);
            _ = await events.GetRecentStreamsAsync(scope, 8, token);

            // The projections screen's tick.
            _ = await projections.GetProjectionsAsync(scope, token);
            _ = await projections.RunningOperationsAsync(scope, token);

            // The navigation badges.
            _ = await navigation.ReadAsync(token);
        }
    }
}

/// <summary>
/// Acceptance 7, first host: no async daemon here on purpose - the production report this packet was
/// written for.
/// </summary>
public class NoDaemonHostLogLiveTests(PostgresFixture postgres) : ProjectionsTestBase(postgres)
{
    private readonly LogCapture logs = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    protected override bool WithDaemon => false;

    private protected override ILoggerProvider? Logs => logs;

    private protected override void ConfigureStore(StoreOptions options) =>
        options.Schema.For<LoggingRailThing>();

    [PostgresFact]
    public async Task Polling_the_Overview_and_the_projections_screen_logs_nothing_at_Warning()
    {
        await StudioPolling.PollOverviewAndProjectionsAsync(Fixture, StudioPolling.Ticks, Token);

        ProjectionsView view = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));
        view.Daemon.Hosting.Should().Be(DaemonHostingState.NotHostedInThisProcess);
        view.Daemon.Explanation.Should().Be(
            DaemonAccessor.NotRegisteredExplanation, "the sample domain has async projections, so this is the informative sentence");

        logs.Lines.Should().NotBeEmpty("the capture is attached to the host and the studio does log, at Debug");
        logs.WarningsOrWorse.Should().BeEmpty(
            "a host with no daemon here is a supported deployment: {0}", string.Join(" | ", logs.WarningsOrWorse));
    }

    /// <summary>
    /// Acceptance 5, live: the rail's speculative count running out of its two-second budget is the
    /// budget working, and is Debug.
    /// </summary>
    /// <remarks>
    /// Forced the way <c>DocumentCountThresholdLiveTests</c> forces it: a never-analysed collection with an
    /// <c>ACCESS EXCLUSIVE</c> lock held on it by another transaction, so the rail's probe cannot finish.
    /// </remarks>
    [PostgresFact]
    public async Task The_rails_speculative_count_timing_out_is_Debug()
    {
        string table = $"\"{Fixture.Schema}\".\"mt_doc_loggingrailthing\"";

        await using (NpgsqlConnection setup = await Postgres.OpenAsync(Token))
        {
            await using var off = new NpgsqlCommand($"alter table {table} set (autovacuum_enabled = off)", setup);
            await off.ExecuteNonQueryAsync(Token);
        }

        await using (IDocumentSession session = Fixture.Store.LightweightSession())
        {
            for (int i = 0; i < 10; i++)
            {
                session.Store(new LoggingRailThing { Id = Guid.NewGuid() });
            }

            await session.SaveChangesAsync(Token);
        }

        await using NpgsqlConnection blocker = await Postgres.OpenAsync(Token);

        await using (var reltuples = new NpgsqlCommand($"select reltuples from pg_catalog.pg_class where oid = '{table}'::regclass", blocker))
        {
            ((float) (await reltuples.ExecuteScalarAsync(Token))!).Should().BeLessThan(
                0, "only a never-analysed collection gets a speculative count, which is the path under test");
        }

        await using NpgsqlTransaction holding = await blocker.BeginTransactionAsync(Token);

        await using (var take = new NpgsqlCommand($"lock table {table} in access exclusive mode", blocker, holding))
        {
            await take.ExecuteNonQueryAsync(Token);
        }

        CollectionRail rail;
        using (IServiceScope circuit = Fixture.CreateScope())
        {
            rail = await circuit.ServiceProvider.GetRequiredService<IDocumentDataService>().GetCollectionsAsync(Fixture.Scope, Token);
        }

        await holding.RollbackAsync(Token);

        rail.Error.Should().BeNull();
        rail.Find("loggingrailthing")!.Count.IsUnknown.Should().BeTrue("the count ran out of its budget");

        logs.Lines.Should().Contain(
            x => x.Level == LogLevel.Debug && x.Message.Contains("rail count of 'loggingrailthing' timed out", StringComparison.Ordinal),
            "the timeout is recorded - at Debug");
        logs.WarningsOrWorse.Should().BeEmpty(string.Join(" | ", logs.WarningsOrWorse));
    }
}

/// <summary>
/// Acceptance 7, second host: <c>AsyncMode = ExternallyManaged</c> and a Wolverine-shaped coordinator, the
/// shape Wolverine's managed distribution leaves in a host's container.
/// </summary>
public class ExternallyManagedHostLogLiveTests(PostgresFixture postgres) : ProjectionsTestBase(postgres)
{
    private readonly LogCapture logs = new();
    private readonly WolverineShapedCoordinator coordinator = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    protected override bool WithDaemon => false;

    private protected override ILoggerProvider? Logs => logs;

    private protected override void ConfigureStore(StoreOptions options) =>
        options.Projections.AsyncMode = DaemonMode.ExternallyManaged;

    private protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<MartenCoordinator>(coordinator);

    [PostgresFact]
    public async Task Polling_the_Overview_and_the_projections_screen_logs_nothing_at_Warning_and_asks_the_coordinator_nothing()
    {
        await StudioPolling.PollOverviewAndProjectionsAsync(Fixture, StudioPolling.Ticks, Token);

        ProjectionsView view = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));
        view.Daemon.Hosting.Should().Be(DaemonHostingState.ExternallyManaged);
        view.Daemon.IsExternallyManaged.Should().BeTrue();
        view.Daemon.Explanation.Should().Be(DaemonAccessor.ExternallyManagedExplanation);
        view.Daemon.Mode.Should().Be("ExternallyManaged");
        view.Progress.Should().NotBeEmpty("progress is read from the database whoever runs the projections");

        ProjectionSummary summary = await Fixture.UseAsync(service => service.GetSummaryAsync(Fixture.Scope, Token));
        summary.Error.Should().BeNull();
        summary.Daemon.IsExternallyManaged.Should().BeTrue();

        coordinator.Calls.Should().Be(0, "Wolverine's coordinator throws from the calls a daemon card would make");

        logs.Lines.Should().NotBeEmpty("the capture is attached to the host and the studio does log, at Debug");
        logs.WarningsOrWorse.Should().BeEmpty(
            "an externally managed store is a supported deployment: {0}", string.Join(" | ", logs.WarningsOrWorse));
    }

    /// <summary>
    /// A client driving a control the page does not offer is refused with the explanation, and the
    /// coordinator is still never asked; the only line above Debug is the audit's own (9201).
    /// </summary>
    [PostgresFact]
    public async Task Every_daemon_control_is_refused_without_a_Warning_and_without_asking_the_coordinator()
    {
        Func<Task>[] throwing =
        [
            () => Fixture.UseAsync(service => service.PauseDaemonAsync(Fixture.Scope, Token)),
            () => Fixture.UseAsync(service => service.ResumeDaemonAsync(Fixture.Scope, Token)),
            () => Fixture.UseAsync(service => service.RestartHighWaterAgentAsync(Fixture.Scope, Token)),
            () => Fixture.UseAsync(service => service.RebuildAsync(Fixture.Scope, "DailySales", Token)),
        ];

        foreach (Func<Task> control in throwing)
        {
            (await control.Should().ThrowAsync<StudioDaemonNotHostedException>())
                .WithMessage(DaemonAccessor.ExternallyManagedExplanation);
        }

        DaemonControlResult start = await Fixture.UseAsync(service => service.StartAgentAsync(Fixture.Scope, "DailySales:All", Token));
        start.Applied.Should().BeFalse();
        start.Reason.Should().Be(DaemonAccessor.ExternallyManagedExplanation);

        coordinator.Calls.Should().Be(0);
        Fixture.Operations.All().Should().BeEmpty("nothing was started");

        logs.WarningsOrWorse.Should().BeEmpty(string.Join(" | ", logs.WarningsOrWorse));
        logs.Lines.Where(static x => x.Level == LogLevel.Information)
            .Should().OnlyContain(static x => x.EventId.Id == 9201, "the audit records each refusal, and nothing else is said");
    }
}

/// <summary>A document whose only job is to have its table locked while the rail counts it.</summary>
public sealed class LoggingRailThing
{
    public Guid Id { get; set; }
}
