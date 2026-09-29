using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;
using Marten.Subscriptions;

using MartenStudio.Services;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Events;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MartenCoordinator = Marten.Events.Daemon.Coordination.IProjectionCoordinator;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// The production report this was written for: a host that runs no async daemon here on purpose, or has
/// Wolverine run its projections, logged "could not reach the async daemon" every <c>RefreshInterval</c>
/// in every open tab.
/// </summary>
/// <remarks>
/// <para>
/// No database. <c>DocumentStore.For</c> never opens a connection, and neither do
/// <see cref="DaemonAccessor" /> nor the control paths up to the point where they would ask a daemon -
/// which is exactly the part under test. The summary path, which does read the database, is proven live
/// in <c>MartenStudio.Integration.Tests/Logging</c>.
/// </para>
/// <para>
/// Every assertion is about levels, and "nothing above Debug" means nothing at Information either on the
/// daemon lookup. The control paths are allowed exactly one Information line per refusal, event 9201: that
/// is the audit (AGENTS.md hard rule 5), not noise.
/// </para>
/// </remarks>
public class DaemonLogHygieneTests
{
    private const string DummyConnectionString = "Host=marten-studio-daemon-logs.invalid;Database=none;Username=none;Password=none";

    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------------------------------
    // The accessor
    // ------------------------------------------------------------------------------------------------

    /// <summary>Acceptance 1: the Wolverine shape, with the mode Wolverine sets.</summary>
    [Fact]
    public async Task An_externally_managed_store_is_External_and_its_coordinator_is_never_asked()
    {
        var coordinator = new WolverineShapedCoordinator();
        await using Harness harness = Harness.Create(
            static options => options.Projections.AsyncMode = DaemonMode.ExternallyManaged,
            services => services.AddSingleton<MartenCoordinator>(coordinator));

        for (int poll = 0; poll < 20; poll++)
        {
            DaemonHosting hosting = await harness.Accessor.ForScopeAsync(harness.Scope, Token);

            hosting.State.Should().Be(DaemonHostingState.ExternallyManaged);
            hosting.Explanation.Should().Be(DaemonAccessor.ExternallyManagedExplanation);
            hosting.TryGetDaemon(out _).Should().BeFalse();
        }

        harness.Accessor.CoordinatorForScope(harness.Scope, out string explanation).Should().BeNull();
        explanation.Should().Be(DaemonAccessor.ExternallyManagedExplanation);

        coordinator.Calls.Should().Be(0, "an externally managed store's coordinator is Wolverine's, and its members throw");
        harness.Logs.AboveDebug().Should().BeEmpty();
    }

    /// <summary>
    /// The same coordinator on an integration that left <c>AsyncMode</c> alone: its
    /// <c>NotSupportedException</c> is the deployment answering, and is read as externally managed.
    /// </summary>
    [Fact]
    public async Task A_coordinator_that_throws_NotSupported_is_read_as_externally_managed_and_logs_only_Debug()
    {
        var coordinator = new WolverineShapedCoordinator();
        await using Harness harness = Harness.Create(
            configureStore: null,
            services => services.AddSingleton<MartenCoordinator>(coordinator));

        for (int poll = 0; poll < 20; poll++)
        {
            DaemonHosting hosting = await harness.Accessor.ForScopeAsync(harness.Scope, Token);
            hosting.State.Should().Be(DaemonHostingState.ExternallyManaged);
        }

        coordinator.Calls.Should().Be(20);
        harness.Logs.AboveDebug().Should().BeEmpty();
        harness.Logs.Entries.Should().Contain(x => x.Level == LogLevel.Debug && x.Message.Contains("does not hand out daemons", StringComparison.Ordinal));
    }

    /// <summary>
    /// Acceptance 2: Wolverine's coordinator calls <c>FindStore</c> in its constructor, which throws for a
    /// store its agent family does not know.
    /// </summary>
    [Fact]
    public async Task A_coordinator_whose_construction_throws_is_not_hosted_here_and_is_never_a_Warning()
    {
        await using Harness harness = Harness.Create(
            configureStore: null,
            static services => services.AddSingleton<MartenCoordinator>(static _ =>
                throw new ArgumentOutOfRangeException("identity", "Unknown identity marten://main, known stores are marten://other")));

        for (int poll = 0; poll < 20; poll++)
        {
            DaemonHosting hosting = await harness.Accessor.ForScopeAsync(harness.Scope, Token);

            hosting.State.Should().Be(DaemonHostingState.NotHostedInThisProcess);
            hosting.Explanation.Should().Be(DaemonAccessor.CoordinatorUnavailableExplanation);
            hosting.Explanation.Should().NotContain("marten://other", "another store's identity is not this visitor's business (D5)");
        }

        harness.Logs.AboveDebug().Should().BeEmpty();
    }

    /// <summary>
    /// Acceptance 3: nothing registered, and nothing a daemon would run. The card states it and gives no
    /// advice.
    /// </summary>
    [Fact]
    public async Task No_coordinator_and_no_async_projections_says_there_is_no_daemon_to_host()
    {
        await using Harness harness = Harness.Create(configureStore: null, configureServices: null);

        for (int poll = 0; poll < 20; poll++)
        {
            DaemonHosting hosting = await harness.Accessor.ForScopeAsync(harness.Scope, Token);

            hosting.State.Should().Be(DaemonHostingState.NotHostedInThisProcess);
            hosting.Explanation.Should().Be("This store has no asynchronous projections, so there is no daemon to host.");
        }

        harness.Logs.AboveDebug().Should().BeEmpty();
    }

    /// <summary>
    /// Nothing registered, and an async projection that something elsewhere runs: the facts, stated as
    /// information rather than as an instruction.
    /// </summary>
    [Fact]
    public async Task No_coordinator_with_an_async_projection_states_the_facts_and_gives_no_instruction()
    {
        await using Harness harness = Harness.Create(
            static options => options.Projections.Snapshot<TimeTravelOrder>(SnapshotLifecycle.Async),
            configureServices: null);

        DaemonHosting hosting = await harness.Accessor.ForScopeAsync(harness.Scope, Token);

        hosting.State.Should().Be(DaemonHostingState.NotHostedInThisProcess);
        hosting.Explanation.Should().Be(DaemonAccessor.NotRegisteredExplanation);
        hosting.Explanation.Should().Contain("AddAsyncDaemon", "the fact that decides it is still named")
            .And.NotContain(". Add ", "a supported deployment is not told to change")
            .And.NotContain("to host one", "and not told what to do instead either");

        harness.Logs.AboveDebug().Should().BeEmpty();
    }

    /// <summary>
    /// A subscription is async work too, and <c>IReadOnlyEventStoreOptions.Projections()</c> does not list
    /// one - which is why the accessor asks <c>HasAnyAsyncProjections()</c>.
    /// </summary>
    [Fact]
    public async Task A_subscription_alone_is_async_work_and_is_not_called_nothing_to_host()
    {
        await using Harness harness = Harness.Create(
            static options => options.Events.Subscribe(new NoOpSubscription()),
            configureServices: null);

        DaemonAccessor.HasAsyncWork(harness.Scope.Store).Should().BeTrue();

        DaemonHosting hosting = await harness.Accessor.ForScopeAsync(harness.Scope, Token);
        hosting.Explanation.Should().Be(DaemonAccessor.NotRegisteredExplanation);
    }

    /// <summary>
    /// Acceptance 4: a coordinator that is there, is asked and fails is a real anomaly - one Warning per
    /// window, however many polls ask, and a second once the window has passed.
    /// </summary>
    [Fact]
    public async Task A_genuinely_failing_coordinator_warns_once_per_ten_minutes_and_is_Debug_in_between()
    {
        var coordinator = new FailingCoordinator();
        await using Harness harness = Harness.Create(
            configureStore: null,
            services => services.AddSingleton<MartenCoordinator>(coordinator));

        for (int poll = 0; poll < 20; poll++)
        {
            DaemonHosting hosting = await harness.Accessor.ForScopeAsync(harness.Scope, Token);
            hosting.State.Should().Be(DaemonHostingState.NotHostedInThisProcess);
            hosting.Explanation.Should().Contain("could not answer for this database");

            harness.Clock.Advance(TimeSpan.FromSeconds(5));
        }

        coordinator.Calls.Should().Be(20);
        harness.Logs.Of(9212).Should().HaveCount(20, "every occurrence is still written, at Debug");
        harness.Logs.Of(9212).Count(static x => x.Level == LogLevel.Warning).Should().Be(1);
        harness.Logs.AboveDebug().Should().ContainSingle().Which.EventId.Id.Should().Be(9212);

        // 20 polls at five seconds is 100 s; past the ten-minute window from the first Warning.
        harness.Clock.Advance(StudioLogThrottle.Window);
        await harness.Accessor.ForScopeAsync(harness.Scope, Token);

        harness.Logs.Of(9212).Count(static x => x.Level == LogLevel.Warning).Should().Be(2);
    }

    // ------------------------------------------------------------------------------------------------
    // The control paths
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Acceptance 1, the control half: every daemon control against an externally managed store is
    /// refused with the explanation, reaches no coordinator member, and logs no Warning - only the audit.
    /// </summary>
    [Fact]
    public async Task Every_daemon_control_on_an_externally_managed_store_is_refused_without_asking_the_coordinator()
    {
        var coordinator = new WolverineShapedCoordinator();
        await using Harness harness = Harness.Create(
            static options => options.Projections.AsyncMode = DaemonMode.ExternallyManaged,
            services => services.AddSingleton<MartenCoordinator>(coordinator));

        IProjectionDataService service = harness.ProjectionData;
        StudioScope scope = harness.Scope.Scope;

        Func<Task>[] throwing =
        [
            () => service.PauseDaemonAsync(scope, Token),
            () => service.ResumeDaemonAsync(scope, Token),
            () => service.RestartHighWaterAgentAsync(scope, Token),
            () => service.RebuildAsync(scope, "TimeTravelOrder", Token),
        ];

        foreach (Func<Task> control in throwing)
        {
            (await control.Should().ThrowAsync<StudioDaemonNotHostedException>())
                .WithMessage(DaemonAccessor.ExternallyManagedExplanation);
        }

        // The per-agent pair answers with a value, as it does for "pause the daemon first".
        DaemonControlResult start = await service.StartAgentAsync(scope, "TimeTravelOrder:All", Token);
        DaemonControlResult stop = await service.StopAgentAsync(scope, "TimeTravelOrder:All", Token);

        start.Should().Be(DaemonControlResult.Refused(DaemonAccessor.ExternallyManagedExplanation));
        stop.Should().Be(DaemonControlResult.Refused(DaemonAccessor.ExternallyManagedExplanation));

        coordinator.Calls.Should().Be(0);

        harness.Logs.Entries.Should().NotContain(static x => x.Level >= LogLevel.Warning);
        harness.Logs.Entries.Where(static x => x.Level == LogLevel.Information)
            .Should().OnlyContain(static x => x.EventId.Id == 9201, "the only line above Debug is the audit's own record of the refusal");

        harness.Ring.GetLatest().Should().HaveCount(6).And.OnlyContain(static x => !x.Succeeded);
    }

    // ------------------------------------------------------------------------------------------------
    // The harness
    // ------------------------------------------------------------------------------------------------

    /// <summary>A subscription that does nothing, so a store can have one and nothing else.</summary>
    private sealed class NoOpSubscription : SubscriptionBase
    {
        public override Task<IChangeListener> ProcessEventsAsync(
            EventRange page,
            ISubscriptionController controller,
            IDocumentOperations operations,
            CancellationToken cancellationToken) =>
            Task.FromResult(NullChangeListener.Instance);
    }

    /// <summary>
    /// A real studio container over a store that will never connect, with the log captured and the
    /// throttle's clock in the test's hand.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private Harness(ServiceProvider provider, IServiceScope scope, LogBook logs, FakeTimeProvider clock, ResolvedScope resolved)
        {
            this.provider = provider;
            this.scope = scope;
            Logs = logs;
            Clock = clock;
            Scope = resolved;
        }

        public LogBook Logs { get; }

        public FakeTimeProvider Clock { get; }

        public ResolvedScope Scope { get; }

        public DaemonAccessor Accessor => scope.ServiceProvider.GetRequiredService<DaemonAccessor>();

        public IProjectionDataService ProjectionData => scope.ServiceProvider.GetRequiredService<IProjectionDataService>();

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        public static Harness Create(Action<StoreOptions>? configureStore, Action<IServiceCollection>? configureServices)
        {
            var logs = new LogBook();
            var clock = new FakeTimeProvider(Now);
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.SetMinimumLevel(LogLevel.Trace);

                // The studio's own categories only: what Marten logs about itself is not what is under test.
                builder.AddFilter((category, _) => category?.StartsWith("MartenStudio", StringComparison.Ordinal) == true);
                builder.AddProvider(logs.Provider);
            });

            services.AddSingleton<TimeProvider>(clock);
            services.AddMarten(options =>
            {
                options.Connection(DummyConnectionString);
                configureStore?.Invoke(options);
            });

            services.AddMartenStudio(static options => options.Capabilities = MartenStudioCapabilities.All());
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            configureServices?.Invoke(services);

            ServiceProvider provider = services.BuildServiceProvider();
            IServiceScope scope = provider.CreateScope();

            var store = provider.GetRequiredService<IDocumentStore>();
            var database = store.Storage.Database;
            var resolved = new ResolvedScope(
                new StudioScope(MartenStoreRegistry.DefaultStoreKey, database.Id.Identity, null),
                new MartenStoreRegistration(MartenStoreRegistry.DefaultStoreKey, "Default", typeof(IDocumentStore)),
                store,
                database);

            return new Harness(provider, scope, logs, clock, resolved);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }

    /// <summary>What the studio logged, with the two questions these tests ask of it.</summary>
    private sealed class LogBook
    {
        public CapturingLoggerProvider Provider { get; } = new();

        public IReadOnlyList<CapturedLogEntry> Entries => Provider.Entries;

        public IEnumerable<CapturedLogEntry> AboveDebug() => Entries.Where(static x => x.Level > LogLevel.Debug);

        public IReadOnlyList<CapturedLogEntry> Of(int eventId) => [.. Entries.Where(x => x.EventId.Id == eventId)];
    }
}
