using System.Text.Json;

using Marten;

using MartenStudio.Tests.Health;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Options;

using Npgsql;

using PostgresStudio;
using PostgresStudio.Services;
using PostgresStudio.Services.ConnectionHistory;
using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Database › Connection's service (NP3, acceptance 3's gates): the page for anybody the store policy passes for the database as a
/// whole - no tenant asked, nothing read before the policy said yes - the application's sessions only through
/// <c>ViewDatabaseActivity</c>, every refusal a value and an audit entry, the first reveal audited by signature, and a contributor
/// handed redacted settings only. A real studio container over a store that never connects, with the facts and the report faked.
/// </summary>
public class ConnectionInsightServiceTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private const string PasswordSentinel = "S3NT1NEL-Password";

    private static readonly JsonSerializerOptions DumpOptions = new() { IncludeFields = true, MaxDepth = 64 };

    // ----------------------------------------------------------------------------------------------
    // The page: the database gate
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_needs_the_store_policy_for_the_database_as_a_whole_and_nothing_is_read_before_it_says_yes()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All(), policies: true);

        // A visitor confined to a tenant: allowed with one, refused without.
        harness.Policies.Allow(static resource => resource.TenantId is not null);

        ConnectionPageView view = await harness.Service.GetAsync(Scope with { TenantId = "acme" }, Token);

        view.Refusal.Should().Be(ConnectionRefusal.StorePolicy, "a connection belongs to no tenant");
        view.Reason.Should().Contain("MartenStudioOptions.StoreAuthorizationPolicy");
        view.Settings.Should().BeEmpty();
        harness.Facts.Reads.Should().Be(0);
        harness.Health.Requests.Should().BeEmpty();
        harness.Ring.Should().BeEmpty("the page is a read of configuration and is not audited");
    }

    [Fact]
    public async Task A_visitor_through_the_gate_is_shown_the_page_and_the_report_is_asked_with_no_tenant()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = new MartenStudioCapabilities(), policies: true);

        ConnectionPageView view = await harness.Service.GetAsync(Scope with { TenantId = "acme" }, Token);

        view.Refusal.Should().Be(ConnectionRefusal.None);
        view.Header.Select(static x => x.Label).Should().Contain(["Server", "TLS", "Pool"]);
        view.Settings.Should().NotBeEmpty();
        harness.Facts.Reads.Should().Be(1);
        harness.Health.Requests.Should().ContainSingle();
        harness.Policies.Calls.Should().NotBeEmpty().And.OnlyContain(static x => x.Resource.TenantId == null, "the database as a whole is what is asked about");
        harness.Ring.Should().BeEmpty();
    }

    [Fact]
    public async Task A_read_that_fails_is_a_value_in_the_studios_words_and_never_the_exceptions()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());
        harness.Facts.Throws = new NpgsqlException("password authentication failed for user svc with " + PasswordSentinel);

        ConnectionPageView view = await harness.Service.GetAsync(Scope, Token);

        view.Refusal.Should().Be(ConnectionRefusal.Unavailable);
        view.Reason.Should().NotContain(PasswordSentinel).And.NotContain("svc");
    }

    // ----------------------------------------------------------------------------------------------
    // The page: the activity gate it reports, asked of nobody
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_reports_whether_the_applications_sessions_would_be_read_and_asks_nobody_and_audits_nothing()
    {
        await using (Harness off = Harness.Create(static options => options.Capabilities = new MartenStudioCapabilities(), policies: true))
        {
            ConnectionPageView view = await off.Service.GetAsync(Scope, Token);

            view.Activity.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
            view.Activity.Denial.Should().Contain("MartenStudioOptions.Capabilities.ViewDatabaseActivity");
            off.Policies.Calls.Should().NotContain(static x => x.Resource.Capability == nameof(StudioCapability.ViewDatabaseActivity), "a capability that is off asks nobody");
            off.Ring.Should().BeEmpty();
        }

        await using (Harness on = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All(), policies: true))
        {
            on.Policies.Allow(static resource => resource.Capability is null);

            ConnectionPageView view = await on.Service.GetAsync(Scope, Token);

            view.Activity.Refusal.Should().Be(DatabaseRefusal.WritePolicy);
            view.Activity.IsOpen.Should().BeFalse();
            on.Ring.Should().BeEmpty("the page's question about the gate is never audited");
        }

        await using Harness open = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());
        (await open.Service.GetAsync(Scope, Token)).Activity.IsOpen.Should().BeTrue();
    }

    // ----------------------------------------------------------------------------------------------
    // The contributors
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_contributor_is_handed_the_redacted_settings_and_its_cards_join_the_page_and_a_failing_one_costs_nothing()
    {
        RecordingContributor recording = new();
        await using Harness harness = Harness.Create(
            static options => options.Capabilities = MartenStudioCapabilities.All(),
            contributors: [new FailingContributor(), recording]);

        ConnectionPageView view = await harness.Service.GetAsync(Scope, Token);

        view.Cards.Should().Contain(static x => x.Id == "conn-card-test");
        view.Contributed.Should().ContainSingle().Which.Title.Should().Be("Test");

        recording.Seen.Should().NotBeNull();
        JsonSerializer.Serialize(recording.Seen, DumpOptions)
            .Should().NotContain(PasswordSentinel, "a contributor is handed the redacted settings, never the connection string");
        recording.Seen!.Settings.Rows.Single(static x => x.Keyword == "Password").Value.Should().Be("set");
    }

    // ----------------------------------------------------------------------------------------------
    // The application's sessions: three refusals, each a value and an audit entry, before anything is read
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_the_capability_off_the_sessions_are_refused_and_audited_and_nothing_is_read()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = new MartenStudioCapabilities(), policies: true);

        ConnectionApplicationsView view = await harness.Service.GetApplicationsAsync(Scope, Token);

        view.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        view.Reason.Should().Contain("MartenStudioOptions.Capabilities.ViewDatabaseActivity");

        StudioActionLogEntry entry = harness.Ring.Should().ContainSingle().Subject;
        entry.Succeeded.Should().BeFalse();
        entry.Action.Should().Be(ConnectionInsightService.ApplicationsAction);
        entry.Capability.Should().Be(nameof(StudioCapability.ViewDatabaseActivity));
        harness.Policies.Calls.Should().BeEmpty("a capability that is off is answered before anybody is asked who is asking");
        harness.Facts.ApplicationReads.Should().Be(0);
    }

    [Fact]
    public async Task A_write_policy_that_refuses_is_asked_with_no_tenant_and_audited()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All(), policies: true);
        harness.Policies.Allow(static resource => resource.Capability is null);

        ConnectionApplicationsView view = await harness.Service.GetApplicationsAsync(Scope with { TenantId = "acme" }, Token);

        view.Refusal.Should().Be(DatabaseRefusal.WritePolicy);
        harness.Policies.Calls.Should().NotBeEmpty().And.OnlyContain(static x => x.Resource.TenantId == null);
        harness.Policies.Calls.Should().Contain(static x => x.Resource.Capability == nameof(StudioCapability.ViewDatabaseActivity));
        harness.Ring.Should().ContainSingle().Which.Succeeded.Should().BeFalse();
        harness.Facts.ApplicationReads.Should().Be(0);
    }

    [Fact]
    public async Task A_store_policy_that_refuses_the_database_as_a_whole_refuses_the_sessions_and_audits_it()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All(), policies: true);
        harness.Policies.Allow(static resource => resource.Capability is not null);

        ConnectionApplicationsView view = await harness.Service.GetApplicationsAsync(Scope, Token);

        view.Refusal.Should().Be(DatabaseRefusal.StorePolicy);
        harness.Ring.Should().ContainSingle().Which.Succeeded.Should().BeFalse();
        harness.Facts.ApplicationReads.Should().Be(0);
    }

    // ----------------------------------------------------------------------------------------------
    // The application's sessions: through the gate
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Through_the_gate_the_sessions_are_named_the_role_is_the_logins_and_the_first_reveal_is_audited_once()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        ConnectionApplicationsView view = await harness.Service.GetApplicationsAsync(Scope, Token);
        await harness.Service.GetApplicationsAsync(Scope, Token);
        await harness.Service.GetApplicationsAsync(Scope, Token);

        view.Refusal.Should().Be(DatabaseRefusal.None);
        view.Role.Should().Be("svc");
        view.Rows.Select(static x => x.Application).Should().Equal("billing-api", string.Empty);
        view.ApplicationCount.Should().Be(2);

        StudioActionLogEntry entry = harness.Ring.Should().ContainSingle("a refresh repeats the signature").Subject;
        entry.Succeeded.Should().BeTrue();
        entry.Action.Should().Be(ConnectionInsightService.ApplicationsAction);
        entry.Message.Should().Contain("2 of 2 applications").And.NotContain("billing-api", "the ring is readable by more people than the page");
        entry.Capability.Should().Be(nameof(StudioCapability.ViewDatabaseActivity));

        await using AsyncServiceScope other = harness.Provider.CreateAsyncScope();
        await other.ServiceProvider.GetRequiredService<IConnectionInsightService>().GetApplicationsAsync(Scope, Token);

        harness.Ring.Where(static x => x.Succeeded).Should().HaveCount(2, "a circuit of its own is a page view of its own");
    }

    [Fact]
    public async Task A_role_the_server_will_not_let_read_the_sessions_is_a_sentence_with_the_sqlstate()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());
        harness.Facts.ApplicationsThrow = new PostgresException("permission denied for view pg_stat_activity (S3NT1NEL)", "ERROR", "ERROR", PostgresErrorCodes.InsufficientPrivilege);

        ConnectionApplicationsView view = await harness.Service.GetApplicationsAsync(Scope, Token);

        view.Refusal.Should().Be(DatabaseRefusal.Unavailable);
        view.Reason.Should().Contain("42501").And.NotContain("S3NT1NEL");
        harness.Ring.Should().BeEmpty("nothing was revealed");
    }

    // ----------------------------------------------------------------------------------------------
    // The pool's peak and the sessions: two reveals, each recorded once
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_overviews_read_asks_for_no_peak_and_so_reads_and_records_nothing()
    {
        FakeHistoryStore history = new();
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All(), history: history);

        ConnectionPageView glance = await harness.Service.GetGlanceAsync(Scope, Token);

        glance.Refusal.Should().Be(ConnectionRefusal.None);
        glance.Header.Single(static x => x.Label == "Pool").Value.Should().NotContain("peak");
        history.Reads.Should().Be(0, "the Overview shows no peak, so the history is not read for it");
        harness.Ring.Should().BeEmpty("nothing application-grade was shown");
    }

    [Fact]
    public async Task The_page_records_the_peak_it_shows_once_and_a_later_sessions_reveal_is_recorded_on_its_own_signature()
    {
        FakeHistoryStore history = new();
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All(), history: history);

        ConnectionPageView page = await harness.Service.GetAsync(Scope, Token);
        await harness.Service.GetAsync(Scope, Token);

        page.Header.Single(static x => x.Label == "Pool").Value.Should().EndWith("peak 12 named 'billing-api', 7 days");
        harness.Ring.Should().ContainSingle("a second read repeats the signature").Which.Target.Should().Be(ConnectionInsightService.PoolPeakTarget);

        await harness.Service.GetApplicationsAsync(Scope, Token);
        await harness.Service.GetApplicationsAsync(Scope, Token);
        await harness.Service.GetAsync(Scope, Token);

        harness.Ring.Select(static x => x.Target).Should().BeEquivalentTo([ConnectionInsightService.PoolPeakTarget, ConnectionInsightService.ApplicationsTarget]);
        harness.Ring.Should().OnlyContain(static x => x.Succeeded && x.Capability == nameof(StudioCapability.ViewDatabaseActivity));
        harness.Ring.Select(static x => x.Message).Should().NotContain(static x => x != null && x.Contains("billing-api"));
    }

    [Fact]
    public async Task Opening_the_overview_does_not_use_up_the_sessions_reveal()
    {
        FakeHistoryStore history = new();
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All(), history: history);

        await harness.Service.GetGlanceAsync(Scope, Token);
        await harness.Service.GetApplicationsAsync(Scope, Token);

        harness.Ring.Should().ContainSingle().Which.Target.Should().Be(ConnectionInsightService.ApplicationsTarget);
    }

    // ----------------------------------------------------------------------------------------------
    // The read time
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_says_when_the_facts_were_read_and_not_when_somebody_looked()
    {
        DateTimeOffset readAt = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
        FakeTimeProvider clock = new(readAt);
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All(), clock: clock);
        harness.Facts.ReadAt = readAt;

        // The facts are shared for up to a health interval; the page is opened well after they were read.
        clock.Advance(TimeSpan.FromMinutes(4));
        ConnectionPageView view = await harness.Service.GetAsync(Scope, Token);

        view.MeasuredAt.Should().Be(readAt);
        clock.GetUtcNow().Should().Be(readAt.AddMinutes(4));
    }

    [Theory]
    [InlineData(7, 0, "7 days")]
    [InlineData(1, 0, "1 day")]
    [InlineData(0, 36, "36 hours")]
    [InlineData(0, 1, "1 hour")]
    public void The_history_window_reads_in_days_or_hours(int days, int hours, string expected) =>
        ConnectionInsightService.WindowText(new TimeSpan(days, hours, 0, 0)).Should().Be(expected);

    // ----------------------------------------------------------------------------------------------
    // The harness
    // ----------------------------------------------------------------------------------------------

    private sealed class RecordingContributor : IConnectionInsightContributor
    {
        public ConnectionContributorContext? Seen { get; private set; }

        public Task<ConnectionContribution?> ContributeAsync(ConnectionContributorContext context, CancellationToken cancellationToken)
        {
            Seen = context;

            return Task.FromResult<ConnectionContribution?>(new ConnectionContribution
            {
                Cards = [new("conn-card-test", ConnectionCardKind.Contributed, "A studio's own advice", "text", ["TEST"], [], [new("StoreOptions", "options.X = 1;")])],
                Sections = [new("conn-test", "Test", null, [], [])],
            });
        }
    }

    private sealed class FailingContributor : IConnectionInsightContributor
    {
        public Task<ConnectionContribution?> ContributeAsync(ConnectionContributorContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a contributor that fails");
    }

    private sealed class FakeFactsSource : IConnectionFactsSource
    {
        private static readonly DateTimeOffset ReadTime = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

        private int reads;
        private int applicationReads;

        public int Reads => Volatile.Read(ref reads);

        public int ApplicationReads => Volatile.Read(ref applicationReads);

        public Exception? Throws { get; set; }

        public DateTimeOffset ReadAt { get; set; } = ReadTime;

        public Exception? ApplicationsThrow { get; set; }

        public ConnectionFacts Facts { get; } = new()
        {
            Settings = NpgsqlSettingsReader.Read(
                "Host=db.example;Database=app;Username=svc;Application Name=billing-api;Password=" + PasswordSentinel + ";Persist Security Info=true",
                new ConnectionReadContext()),
            Observed = new ConnectionObserved(
                "svc", "svc", "app", "10.0.0.5", 5432, false, "UTC", "UTF8", "public", "PostgresStudio",
                0, 0, 0, null, "scram-sha-256", true, "TLSv1.3", "TLS_AES_256_GCM_SHA384", 256, null, null,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        };

        public Task<ConnectionFacts> GetAsync(ResolvedSource resolved, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref reads);
            return Throws is not null ? Task.FromException<ConnectionFacts>(Throws) : Task.FromResult(Facts with { ReadAt = ReadAt });
        }

        public Task<ConnectionApplicationsRead> ReadApplicationsAsync(ResolvedSource resolved, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref applicationReads);

            return ApplicationsThrow is not null
                ? Task.FromException<ConnectionApplicationsRead>(ApplicationsThrow)
                : Task.FromResult(new ConnectionApplicationsRead(
                    ReadTime,
                    2,
                    [new ConnectionApplicationRow("billing-api", 12, ReadTime, 12, 12), new ConnectionApplicationRow(string.Empty, 2, null, 0, 0)]));
        }
    }

    /// <summary>A connection history holding one sample of the harness's server: 12 sessions named billing-api.</summary>
    private sealed class FakeHistoryStore : IPostgresConnectionHistoryStore
    {
        private int reads;

        public int Reads => Volatile.Read(ref reads);

        public ValueTask AppendAsync(PostgresConnectionSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server, DateTimeOffset since, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref reads);

            return ValueTask.FromResult<IReadOnlyList<PostgresConnectionSample>>(
            [
                new PostgresConnectionSample
                {
                    Server = server,
                    At = DateTimeOffset.UtcNow.AddHours(-1),
                    Connections = 30,
                    ClientLimit = 100,
                    ByApplication = [new PostgresConnectionCounts { ApplicationName = "billing-api", Total = 12 }],
                },
            ]);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly AsyncServiceScope circuit;

        private Harness(ServiceProvider provider, TestStoreAuthorizationService policies, FakeFactsSource facts, FakeHealthService health)
        {
            Provider = provider;
            Policies = policies;
            Facts = facts;
            Health = health;
            circuit = provider.CreateAsyncScope();
        }

        public ServiceProvider Provider { get; }

        public TestStoreAuthorizationService Policies { get; }

        public FakeFactsSource Facts { get; }

        public FakeHealthService Health { get; }

        public IConnectionInsightService Service => circuit.ServiceProvider.GetRequiredService<IConnectionInsightService>();

        public IReadOnlyList<StudioActionLogEntry> Ring => Provider.GetRequiredService<StudioActionLogService>().GetLatest();

        public static Harness Create(
            Action<MartenStudioOptions> configure,
            bool policies = false,
            IReadOnlyList<IConnectionInsightContributor>? contributors = null,
            FakeHistoryStore? history = null,
            TimeProvider? clock = null)
        {
            TestAuthenticationStateProvider users = new();
            users.SignIn("operator");

            TestStoreAuthorizationService authorization = new();
            FakeFactsSource facts = new();
            FakeHealthService health = new();

            ServiceCollection services = new();
            services.AddLogging();
            services.AddMarten(static options =>
            {
                options.Connection(HealthHarness.RefusedConnectionString);
                options.DatabaseSchemaName = "connection_unit";
                options.Schema.For<ConnectionNote>();
            });

            services.AddMartenStudio(options =>
            {
                if (policies)
                {
                    options.StoreAuthorizationPolicy = "store-policy";
                    options.WriteAuthorizationPolicy = "write-policy";
                }

                configure(options);
            });

            services.AddSingleton<IAuthorizationService>(authorization);
            services.AddScoped<AuthenticationStateProvider>(_ => users);
            services.AddSingleton<IConnectionFactsSource>(facts);
            services.AddSingleton<IHealthService>(health);

            if (history is not null)
            {
                services.AddSingleton<IPostgresConnectionHistoryStore>(history);
                services.AddSingleton(static provider => new ConnectionHistoryReader(
                    provider.GetRequiredService<IPostgresConnectionHistoryStore>(),
                    Options.Create(new PostgresConnectionHistoryOptions()),
                    provider.GetRequiredService<IDiagnosticsSettings>()));
            }

            if (clock is not null)
            {
                services.AddSingleton(clock);
            }

            foreach (IConnectionInsightContributor contributor in contributors ?? [])
            {
                services.AddSingleton<IConnectionInsightContributor>(contributor);
            }

            return new Harness(services.BuildServiceProvider(), authorization, facts, health);
        }

        public async ValueTask DisposeAsync()
        {
            await circuit.DisposeAsync();
            await Provider.DisposeAsync();
        }
    }
}

/// <summary>A document type of the harness's own store.</summary>
public sealed class ConnectionNote
{
    /// <summary>Its id.</summary>
    public Guid Id { get; set; }
}
