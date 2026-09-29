using System.Reflection;

using JasperFx.Descriptors;
using JasperFx.Events.Daemon;

using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MartenCoordinator = Marten.Events.Daemon.Coordination.IProjectionCoordinator;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// DB-0-fix-2, F2, F3, F4 and F6: which question the daemon lookup asks of which coordinator, and what it
/// does with an answer it cannot prove.
/// </summary>
/// <remarks>
/// <para>
/// The coordinators here are Marten's own - <c>AddAsyncDaemon(DaemonMode.Solo)</c> registers the real
/// <c>ProjectionCoordinator</c>, and nothing is started - over stores that are never connected to. The
/// tenancy is Marten's own static multi-tenancy seen through <see cref="ObservedTenancy" />, which counts
/// <c>FindOrCreateDatabase</c> and can answer <c>Default</c> and <c>Cardinality</c> the way a master-table or
/// sharded tenancy does, without a master table to read.
/// </para>
/// </remarks>
public class DaemonLookupOrderTests
{
    private const string PrimaryConnectionString =
        "Host=marten-studio-lookup-order.invalid;Database=primary;Username=none;Password=none;Timeout=2";

    private const string SecondaryConnectionString =
        "Host=marten-studio-lookup-order.invalid;Database=secondary;Username=none;Password=none;Timeout=2";

    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------------------------------
    // F3: no FindOrCreateDatabase on a poll
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// On Marten's coordinator, <c>DaemonForDatabase(id)</c> is <c>Storage.FindOrCreateDatabase(id)</c>, and
    /// a sharded tenancy provisions a tenant for a pool id it misses under <c>ForceLowerCase</c> - an
    /// assignment row and its partition and sequence DDL, because somebody opened the Projections page.
    /// DB-0-fix asked that call first on every multi-database poll. The set is asked instead, and matched
    /// by tracker: twenty polls, both databases, no <c>FindOrCreateDatabase</c> at all.
    /// </summary>
    [Fact]
    public async Task Polling_a_multi_database_store_never_asks_Martens_coordinator_to_find_or_create_a_database()
    {
        await using Harness harness = await Harness.CreateAsync(twoDatabases: true);

        foreach (IMartenDatabase database in harness.Databases)
        {
            for (int poll = 0; poll < 10; poll++)
            {
                DaemonHosting hosting = await harness.Accessor.ForScopeAsync(harness.ScopeFor(database), Token);

                hosting.State.Should().Be(DaemonHostingState.Hosted, hosting.Explanation);
                hosting.TryGetDaemon(out IProjectionDaemon daemon).Should().BeTrue();
                daemon.Tracker.Should().BeSameAs(database.Tracker, "each database gets the daemon built against it");
            }
        }

        harness.Tenancy.FindOrCreateDatabaseCalls.Should().Be(0);
        harness.Logs.AboveDebug().Should().BeEmpty();

        // The anti-vacuity half: the call the lookup no longer makes is the one that reaches the tenancy.
#pragma warning disable CS0618 // the tenancy's own name, which is what the coordinator is keyed on
        await harness.Coordinator.DaemonForDatabase(harness.Databases[0].Identifier);
#pragma warning restore CS0618
        harness.Tenancy.FindOrCreateDatabaseCalls.Should().Be(1, "DaemonForDatabase on Marten's coordinator is FindOrCreateDatabase");
    }

    // ------------------------------------------------------------------------------------------------
    // F2: Marten's own coordinator is never "external"
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A master-table or sharded tenancy with one database so far: <c>AllDatabases()</c> answers one,
    /// <c>Tenancy.Default</c> throws <c>NotSupportedException</c>, and Marten's
    /// <c>DaemonForMainDatabase()</c> reads it. DB-0-fix asked that call for any store with one database
    /// and read the exception as "an external system runs these projections" - of a daemon hosted in this
    /// very process. The set is matched instead, and whatever Marten's coordinator throws is never
    /// "external".
    /// </summary>
    [Fact]
    public async Task A_hosted_daemon_on_a_one_database_tenancy_with_no_default_is_Hosted_and_not_External()
    {
        await using Harness harness = await Harness.CreateAsync(twoDatabases: false, masterTableShape: true);

        harness.Databases.Should().ContainSingle();
        harness.Store.Options.Tenancy.Cardinality.Should().Be(DatabaseCardinality.DynamicMultiple);

        // The shape, proven: this is the throw DB-0-fix read as "external".
        FluentActions.Invoking(() => harness.Coordinator.DaemonForMainDatabase()).Should().Throw<NotSupportedException>();

        for (int poll = 0; poll < 10; poll++)
        {
            DaemonHosting hosting = await harness.Accessor.ForScopeAsync(harness.ScopeFor(harness.Databases[0]), Token);

            hosting.State.Should().Be(DaemonHostingState.Hosted, hosting.Explanation);
            hosting.TryGetDaemon(out IProjectionDaemon daemon).Should().BeTrue();
            daemon.Tracker.Should().BeSameAs(harness.Databases[0].Tracker);
        }

        harness.Tenancy.FindOrCreateDatabaseCalls.Should().Be(0);
        harness.Logs.AboveDebug().Should().BeEmpty();
    }

    [Fact]
    public void Only_the_coordinators_Marten_ships_are_Martens_own()
    {
        DaemonAccessor.IsMartensOwn(new WolverineShapedCoordinator()).Should().BeFalse();
        DaemonAccessor.IsMartensOwn(new FailingCoordinator()).Should().BeFalse();
        DaemonAccessor.IsMartensOwn(new CountingForeignCoordinator()).Should().BeFalse();
    }

    // ------------------------------------------------------------------------------------------------
    // F6: an unverified daemon is never handed out
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A foreign coordinator whose <c>DaemonForDatabase</c> answers with a daemon that is not provably this
    /// database's, and whose set holds none that is. DB-0-fix returned the unverified one - which may be
    /// another database's, and every control on the page would then have acted on it. It is "not hosted
    /// here" and a throttled 9212 instead.
    /// </summary>
    [Fact]
    public async Task A_daemon_that_is_not_provably_this_databases_is_never_handed_out()
    {
        IProjectionDaemon stray = TrackerOnlyDaemon.For(null);
        var coordinator = new CountingForeignCoordinator { Lookup = () => stray, Set = () => [stray] };

        await using Harness harness = await Harness.CreateAsync(twoDatabases: true, coordinator: coordinator);

        for (int poll = 0; poll < 5; poll++)
        {
            DaemonHosting hosting = await harness.Accessor.ForScopeAsync(harness.ScopeFor(harness.Databases[0]), Token);

            hosting.State.Should().Be(DaemonHostingState.NotHostedInThisProcess);
            hosting.TryGetDaemon(out _).Should().BeFalse();
            hosting.Explanation.Should().Contain(DaemonAccessor.NoDaemonForDatabaseMessage);
        }

        harness.Logs.Of(9212).Should().HaveCount(5, "every occurrence is written");
        harness.Logs.Of(9212).Count(static x => x.Level == LogLevel.Warning).Should().Be(1, "and throttled to one Warning per window");
    }

    /// <summary>The anti-vacuity half: the same coordinator, once its set holds this database's daemon, is found.</summary>
    [Fact]
    public async Task The_same_coordinator_is_Hosted_once_its_set_holds_this_databases_daemon()
    {
        var coordinator = new CountingForeignCoordinator { Lookup = () => TrackerOnlyDaemon.For(null) };

        await using Harness harness = await Harness.CreateAsync(twoDatabases: true, coordinator: coordinator);
        coordinator.Set = () => [.. harness.Databases.Select(static x => TrackerOnlyDaemon.For(x.Tracker))];

        DaemonHosting hosting = await harness.Accessor.ForScopeAsync(harness.ScopeFor(harness.Databases[1]), Token);

        hosting.State.Should().Be(DaemonHostingState.Hosted);
        hosting.TryGetDaemon(out IProjectionDaemon daemon).Should().BeTrue();
        daemon.Tracker.Should().BeSameAs(harness.Databases[1].Tracker);
    }

    // ------------------------------------------------------------------------------------------------
    // F4: pause and resume only ever reach a Hosted coordinator
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// "Not hosted here" is also what a lookup that faulted answers. DB-0-fix refused pause and resume
    /// only on "external", so a coordinator the accessor had just failed to get an answer from was paused
    /// anyway. Both are refused now, and the coordinator's own pause and resume are never called.
    /// </summary>
    [Theory]
    [InlineData("throws")]
    [InlineData("stray")]
    public async Task Pause_and_resume_are_refused_when_the_lookup_did_not_answer_Hosted(string fault)
    {
        var coordinator = new CountingForeignCoordinator
        {
            Lookup = fault == "throws"
                ? static () => throw new InvalidOperationException("The coordinator lost its advisory lock connection.")
                : static () => TrackerOnlyDaemon.For(null),
        };

        await using Harness harness = await Harness.CreateAsync(twoDatabases: true, coordinator: coordinator);

        StudioScope scope = harness.ScopeFor(harness.Databases[0]).Scope;

        DaemonHosting hosting = await harness.Accessor.ForScopeAsync(harness.ScopeFor(harness.Databases[0]), Token);
        hosting.State.Should().Be(DaemonHostingState.NotHostedInThisProcess, "the lookup faulted");

        await FluentActions.Invoking(() => harness.ProjectionData.PauseDaemonAsync(scope, Token))
            .Should().ThrowAsync<StudioDaemonNotHostedException>();
        await FluentActions.Invoking(() => harness.ProjectionData.ResumeDaemonAsync(scope, Token))
            .Should().ThrowAsync<StudioDaemonNotHostedException>();

        coordinator.PauseCalls.Should().Be(0, "a coordinator the lookup could not get a straight answer from is not paused blind");
        coordinator.ResumeCalls.Should().Be(0);

        harness.Accessor.CoordinatorForScope(harness.ScopeFor(harness.Databases[0]), hosting, out _)
            .Should().BeNull("only a Hosted answer hands a coordinator out");

        harness.Ring.GetLatest().Where(static x => x.Action is "PauseDaemon" or "ResumeDaemon")
            .Should().HaveCount(2).And.OnlyContain(static x => !x.Succeeded);
    }

    /// <summary>The anti-vacuity half: a Hosted answer does reach the coordinator's pause and resume.</summary>
    [Fact]
    public async Task Pause_and_resume_reach_a_coordinator_whose_lookup_answered_Hosted()
    {
        var coordinator = new CountingForeignCoordinator();

        await using Harness harness = await Harness.CreateAsync(twoDatabases: true, coordinator: coordinator);
        coordinator.Lookup = () => TrackerOnlyDaemon.For(harness.Databases[0].Tracker);

        StudioScope scope = harness.ScopeFor(harness.Databases[0]).Scope;

        await harness.ProjectionData.PauseDaemonAsync(scope, Token);
        await harness.ProjectionData.ResumeDaemonAsync(scope, Token);

        coordinator.PauseCalls.Should().Be(1);
        coordinator.ResumeCalls.Should().Be(1);
    }

    // ------------------------------------------------------------------------------------------------
    // The harness
    // ------------------------------------------------------------------------------------------------

    /// <summary>A studio container over a statically multi-tenanted store that is never connected to.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private Harness(ServiceProvider provider, CapturingLoggerProvider logs, ObservedTenancy tenancy, IReadOnlyList<IMartenDatabase> databases)
        {
            this.provider = provider;
            scope = provider.CreateScope();
            Logs = new LogBook(logs);
            Tenancy = tenancy;
            Databases = databases;
        }

        public LogBook Logs { get; }

        public ObservedTenancy Tenancy { get; }

        /// <summary>The store's databases, primary first.</summary>
        public IReadOnlyList<IMartenDatabase> Databases { get; }

        public IDocumentStore Store => provider.GetRequiredService<IDocumentStore>();

        public MartenCoordinator Coordinator => provider.GetRequiredService<MartenCoordinator>();

        public DaemonAccessor Accessor => scope.ServiceProvider.GetRequiredService<DaemonAccessor>();

        public IProjectionDataService ProjectionData => scope.ServiceProvider.GetRequiredService<IProjectionDataService>();

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        public ResolvedScope ScopeFor(IMartenDatabase database) => new(
            new StudioScope(MartenStoreRegistry.DefaultStoreKey, database.Id.Identity, null),
            new MartenStoreRegistration(MartenStoreRegistry.DefaultStoreKey, "Default", typeof(IDocumentStore)),
            Store,
            database);

        /// <param name="twoDatabases">Two databases rather than one.</param>
        /// <param name="masterTableShape">
        /// Answer <c>Default</c> with <c>NotSupportedException</c> and <c>Cardinality</c> with
        /// <c>DynamicMultiple</c>, as <c>MasterTableTenancy</c> and <c>ShardedTenancy</c> do.
        /// </param>
        /// <param name="coordinator">A coordinator of the test's own, instead of Marten's.</param>
        public static async Task<Harness> CreateAsync(
            bool twoDatabases,
            bool masterTableShape = false,
            MartenCoordinator? coordinator = null)
        {
            var logs = new CapturingLoggerProvider();
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            ObservedTenancy? observed = null;

            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddFilter((category, _) => category?.StartsWith("MartenStudio", StringComparison.Ordinal) == true);
                builder.AddProvider(logs);
            });

            services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));

            MartenServiceCollectionExtensions.MartenConfigurationExpression marten = services.AddMarten(options =>
            {
                options.MultiTenantedDatabases(tenancy =>
                {
                    tenancy.AddMultipleTenantDatabase(PrimaryConnectionString, "primary").ForTenants("tenant-primary");
                    if (twoDatabases)
                    {
                        tenancy.AddMultipleTenantDatabase(SecondaryConnectionString, "secondary").ForTenants("tenant-secondary");
                    }
                });

                observed = ObservedTenancy.Over(options.Tenancy, masterTableShape);
                options.Tenancy = (ITenancy) (object) observed;
            });

            if (coordinator is null)
            {
                marten.AddAsyncDaemon(DaemonMode.Solo);
            }

            services.AddMartenStudio(static options => options.Capabilities = MartenStudioCapabilities.All());
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            if (coordinator is not null)
            {
                services.AddSingleton(coordinator);
            }

            ServiceProvider provider = services.BuildServiceProvider();

            var store = provider.GetRequiredService<IDocumentStore>();
            IReadOnlyList<IMartenDatabase> databases = [.. (await store.Storage.AllDatabases()).OrderBy(static x => x.Id.Name, StringComparer.Ordinal)];

            return new Harness(provider, logs, observed!, databases);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }

    /// <summary>What the studio logged, with the questions these tests ask of it.</summary>
    private sealed class LogBook(CapturingLoggerProvider provider)
    {
        public IEnumerable<CapturedLogEntry> AboveDebug() => provider.Entries.Where(static x => x.Level > LogLevel.Debug);

        public IReadOnlyList<CapturedLogEntry> Of(int eventId) => [.. provider.Entries.Where(x => x.EventId.Id == eventId)];
    }
}

/// <summary>
/// A real tenancy, seen through a proxy that counts <c>FindOrCreateDatabase</c> and can answer
/// <c>Default</c> and <c>Cardinality</c> the way a master-table or sharded tenancy does.
/// </summary>
/// <remarks>
/// A <see cref="DispatchProxy" /> because <c>ITenancy</c> and its base interfaces move between Marten
/// releases; the proxy implements whatever the resolved version declares and forwards it to the tenancy it
/// wraps, so the databases, their trackers and everything the coordinator does with them are Marten's.
/// </remarks>
public class ObservedTenancy : DispatchProxy
{
    private ITenancy? inner;
    private bool masterTableShape;
    private int findOrCreateDatabaseCalls;

    /// <summary>How many times anything asked the tenancy to find or create a database by name.</summary>
    public int FindOrCreateDatabaseCalls => Volatile.Read(ref findOrCreateDatabaseCalls);

    internal static ObservedTenancy Over(ITenancy tenancy, bool masterTableShape)
    {
        ITenancy proxy = Create<ITenancy, ObservedTenancy>();
        var observed = (ObservedTenancy) (object) proxy;
        observed.inner = tenancy;
        observed.masterTableShape = masterTableShape;
        return observed;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        switch (targetMethod.Name)
        {
            case "FindOrCreateDatabase":
                Interlocked.Increment(ref findOrCreateDatabaseCalls);
                break;

            case "get_Default" when masterTableShape:
                throw new NotSupportedException("Default tenant does not supported");

            case "get_Cardinality" when masterTableShape:
                return DatabaseCardinality.DynamicMultiple;
        }

        return UnlistableStore.Forward(targetMethod, inner!, args);
    }
}

/// <summary>
/// A coordinator that is not Marten's, whose lookup and set a test supplies, and which counts the two
/// calls a studio must never make blind.
/// </summary>
internal sealed class CountingForeignCoordinator : MartenCoordinator
{
    /// <summary>What both per-database lookups answer. Throws unless a test says otherwise.</summary>
    public Func<IProjectionDaemon> Lookup { get; set; } =
        static () => throw new InvalidOperationException("No lookup was configured.");

    /// <summary>What <see cref="AllDaemonsAsync" /> answers.</summary>
    public Func<IReadOnlyList<IProjectionDaemon>> Set { get; set; } = static () => [];

    public int PauseCalls { get; private set; }

    public int ResumeCalls { get; private set; }

    public IProjectionDaemon DaemonForMainDatabase() => Lookup();

    public ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier) => ValueTask.FromResult(Lookup());

    public ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync() => ValueTask.FromResult(Set());

    public Task PauseAsync()
    {
        PauseCalls++;
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        ResumeCalls++;
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
