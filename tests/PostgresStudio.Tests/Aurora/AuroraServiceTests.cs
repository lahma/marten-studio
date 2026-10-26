using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Aurora;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Sources;
using PostgresStudio.Tests.Support;

namespace PostgresStudio.Tests.Aurora;

/// <summary>
/// <see cref="AuroraService" />'s gates and audit (W5-AUR-fix F3), over a real Postgres Studio container whose data source never
/// connects, with the facts reader faked: the store policy for the database as a whole, which reads nothing when it refuses;
/// the activity gate in its order (capability, <c>ReadOnly</c>, write policy), which reads no session unless it passes; the
/// audit once per circuit, and never for a view with no session; and the tenant dropped, because a server belongs to none.
/// </summary>
public class AuroraServiceTests
{
    private const string Refused = "Host=127.0.0.1;Port=1;Database=app;Username=app;Password=secret;Timeout=1";

    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Through_every_gate_the_sessions_are_read_and_the_first_view_is_audited_once_per_circuit()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = PostgresStudioCapabilities.All(), policies: true);

        AuroraView first = await harness.Service.GetAsync(Scope, Token);
        AuroraView second = await harness.Service.GetAsync(Scope, Token);

        first.Refused.Should().BeNull();
        first.SessionsShown.Should().BeTrue();
        first.Sessions!.Value!.Sessions.Single().ProcessId.Should().Be(3027);
        second.SessionsShown.Should().BeTrue();

        StudioActionLogEntry entry = harness.Ring.Should().ContainSingle("a second view in the same circuit records nothing").Subject;
        entry.Action.Should().Be(AuroraService.SessionsAction);
        entry.Succeeded.Should().BeTrue();
        entry.Capability.Should().Be(nameof(StudioCapability.ViewDatabaseActivity));

        await using (AsyncServiceScope other = harness.Provider.CreateAsyncScope())
        {
            await other.ServiceProvider.GetRequiredService<IAuroraService>().GetAsync(Scope, Token);
        }

        harness.Ring.Should().HaveCount(2, "another circuit is another first view");
        harness.Cache.ReadCount.Should().Be(1, "the facts are the database's, read once for every circuit within an interval");
        harness.Reader.Reads.Should().Be(1);
        harness.Reader.SessionReads.Should().Be(1, "the sessions are cached for the interval too");
        harness.Policies.Calls.Should().Contain(static x => x.Resource.Capability == nameof(StudioCapability.ViewDatabaseActivity));
    }

    [Fact]
    public async Task A_view_with_no_session_is_never_audited()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = PostgresStudioCapabilities.All());
        harness.Reader.Sessions = new AuroraSessionWaits(Now, []);

        AuroraView view = await harness.Service.GetAsync(Scope, Token);

        view.SessionsShown.Should().BeTrue("the gate is open");
        harness.Ring.Should().BeEmpty("nothing the gate guards was shown");
    }

    [Fact]
    public async Task With_the_capability_off_no_session_is_read_and_the_option_is_named()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = new PostgresStudioCapabilities(), policies: true);

        AuroraView view = await harness.Service.GetAsync(Scope, Token);

        view.Refused.Should().BeNull("the instance, its waits and the commit latency are the database-wide gate's");
        view.Waits!.Value.Should().NotBeNull();
        view.SessionsShown.Should().BeFalse();
        view.Sessions.Should().BeNull();
        view.SessionsWithheld.Should().Contain("PostgresStudioOptions.Capabilities.ViewDatabaseActivity");
        harness.Cache.SessionReadCount.Should().Be(0, "a visitor who may not see the sessions costs no read");
        harness.Reader.SessionReads.Should().Be(0);
        harness.Policies.Calls.Should().NotContain(static x => x.Resource.Capability != null, "a capability that is off is answered before the write policy is asked");
        harness.Ring.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadOnly_reads_no_session_and_says_why()
    {
        await using Harness harness = Harness.Create(static options =>
        {
            options.Capabilities = PostgresStudioCapabilities.All();
            options.ReadOnly = true;
        });

        AuroraView view = await harness.Service.GetAsync(Scope, Token);

        view.SessionsShown.Should().BeFalse();
        view.SessionsWithheld.Should().Contain("PostgresStudioOptions.ReadOnly is true");
        harness.Cache.SessionReadCount.Should().Be(0);
        harness.Ring.Should().BeEmpty();
    }

    [Fact]
    public async Task A_write_policy_that_refuses_ViewDatabaseActivity_reads_no_session_and_shows_the_rest()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = PostgresStudioCapabilities.All(), policies: true);
        harness.Policies.Allow(static resource => resource.Capability is null);

        AuroraView view = await harness.Service.GetAsync(Scope, Token);

        view.Refused.Should().BeNull();
        view.IsAurora.Should().BeTrue();
        view.Waits!.Value!.Rows.Single().Event.Name.Should().Be("IO:XactSync");
        view.SessionsShown.Should().BeFalse();
        view.SessionsWithheld.Should().Contain("write policy").And.Contain("ViewDatabaseActivity");
        harness.Cache.SessionReadCount.Should().Be(0);
        harness.Reader.SessionReads.Should().Be(0);
        harness.Ring.Should().BeEmpty();
    }

    [Fact]
    public async Task A_store_policy_that_refuses_the_database_refuses_everything_and_reads_nothing()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = PostgresStudioCapabilities.All(), policies: true);
        harness.Policies.Allow(static _ => false);

        AuroraView view = await harness.Service.GetAsync(Scope, Token);

        view.Refused.Should().Contain("PostgresStudioOptions.SourceAuthorizationPolicy").And.Contain("the server belongs to no tenant");
        view.IsAurora.Should().BeFalse("nothing was read to say so");
        harness.Cache.ReadCount.Should().Be(0);
        harness.Cache.SessionReadCount.Should().Be(0);
        harness.Reader.Reads.Should().Be(0);
        harness.Ring.Should().BeEmpty();
    }

    [Fact]
    public async Task The_tenant_is_dropped_because_a_server_belongs_to_none()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = PostgresStudioCapabilities.All(), policies: true);

        // Postgres Studio's source has no tenant called acme, so a resolution that kept it would refuse: the view answers
        // because the service asks for the database as a whole.
        AuroraView view = await harness.Service.GetAsync(Scope with { TenantId = "acme" }, Token);

        view.Refused.Should().BeNull();
        view.SessionsShown.Should().BeTrue();
        harness.Reader.Tenants.Should().OnlyContain(static x => x == null);
        harness.Ring.Single().TenantId.Should().BeNull("the audit names the database as a whole");
    }

    private static AuroraFacts Facts()
    {
        PostgresServerFacts server = new(Now, 160004, "16.4", new PostgresPlatform(PostgresPlatformKind.AmazonAurora, "marker"))
        {
            Instance = "studio-test-instance-1",
        };

        return new AuroraFacts(
            Now,
            server,
            new CapacityReading(null, Now, null, false),
            ServerRead<AuroraReplicaStatus>.Of(AuroraReplicaStatus.FromJson(Now, [])),
            ServerRead<AuroraWaitProfile>.Of(new AuroraWaitProfile(Now, [new AuroraWaitEvent(10, 167772231, "IO", "XactSync", 187073, 597041642)], 0)),
            null,
            []);
    }

    private sealed class FakeAuroraFactsReader : IAuroraFactsReader
    {
        private int reads;
        private int sessionReads;

        public int Reads => Volatile.Read(ref reads);

        public int SessionReads => Volatile.Read(ref sessionReads);

        public List<string?> Tenants { get; } = [];

        public AuroraSessionWaits Sessions { get; set; } = new(Now,
            [new AuroraSession(3027, "app", "svcA", "active", Now, 4870, [new AuroraSessionWait("IO:XactSync", 4867, 0.98)])]);

        public Task<AuroraFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref reads);
            Record(resolved);
            return Task.FromResult(Facts());
        }

        public Task<AuroraSessionWaits> ReadSessionsAsync(ResolvedSource resolved, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref sessionReads);
            Record(resolved);
            return Task.FromResult(Sessions);
        }

        private void Record(ResolvedSource resolved)
        {
            lock (Tenants)
            {
                Tenants.Add(resolved.TenantId);
            }
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly AsyncServiceScope circuit;

        private Harness(ServiceProvider provider, TestSourceAuthorizationService policies, FakeAuroraFactsReader reader)
        {
            Provider = provider;
            Policies = policies;
            Reader = reader;
            circuit = provider.CreateAsyncScope();
        }

        public ServiceProvider Provider { get; }

        public TestSourceAuthorizationService Policies { get; }

        public FakeAuroraFactsReader Reader { get; }

        public AuroraFactsCache Cache => Provider.GetRequiredService<AuroraFactsCache>();

        public IAuroraService Service => circuit.ServiceProvider.GetRequiredService<IAuroraService>();

        public IReadOnlyList<StudioActionLogEntry> Ring => Provider.GetRequiredService<StudioActionLogService>().GetLatest();

        public static Harness Create(Action<PostgresStudioOptions> configure, bool policies = false)
        {
            TestAuthenticationStateProvider users = new();
            users.SignIn("operator");

            TestSourceAuthorizationService authorization = new();
            FakeAuroraFactsReader reader = new();

            ServiceCollection services = new();
            services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Trace));
            services.AddSingleton(NpgsqlDataSource.Create(Refused));
            services.AddPostgresStudio(options =>
            {
                if (policies)
                {
                    options.SourceAuthorizationPolicy = "source-policy";
                    options.WriteAuthorizationPolicy = "write-policy";
                }

                configure(options);
            });

            services.AddSingleton<IAuthorizationService>(authorization);
            services.AddScoped<AuthenticationStateProvider>(_ => users);
            services.AddSingleton<IAuroraFactsReader>(reader);

            return new Harness(services.BuildServiceProvider(), authorization, reader);
        }

        public async ValueTask DisposeAsync()
        {
            await circuit.DisposeAsync();

            foreach (NpgsqlDataSource dataSource in Provider.GetServices<NpgsqlDataSource>())
            {
                await dataSource.DisposeAsync();
            }

            await Provider.DisposeAsync();
        }
    }
}
