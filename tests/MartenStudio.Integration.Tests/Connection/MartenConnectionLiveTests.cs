using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;

using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;

using MartenStudio.Integration.Tests.Projections;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Connection;

namespace MartenStudio.Integration.Tests.Connection;

/// <summary>An aggregate the live host's async projection builds, so its daemon has a shard to run.</summary>
public sealed class ConnectionNoteTally
{
    public Guid Id { get; set; }

    public int Count { get; set; }

    public void Apply(ConnectionNoted noted) => Count += noted.Text.Length > 0 ? 1 : 0;
}

/// <summary>The event it counts.</summary>
/// <param name="Text">What was noted.</param>
public sealed record ConnectionNoted(string Text);

/// <summary>
/// "How Marten uses this connection" (1.1-NP4) on a real store and a real Postgres: whether Marten's pool is the application's own
/// <c>NpgsqlDataSource</c> (<c>UseNpgsqlDataSource()</c>) or Marten's (<c>Connection(string)</c>), found by reference over the
/// container's registrations, and what the hosted daemon needs from it - through the real page service, so the gates, the sweep and
/// the contributor run as they do in a host.
/// </summary>
public class MartenConnectionLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_store_that_uses_UseNpgsqlDataSource_shares_the_applications_pool_and_a_hosted_daemon_is_counted()
    {
        await using LiveHost host = await LiveHost.StartAsync(postgres, "np4_shared_pool", useRegisteredDataSource: true, daemon: true);

        ConnectionContributedSection section = await host.SectionAsync();

        // The registration is a factory (as AddNpgsqlDataSource() makes it), so no instance is compared and nothing is built to compare:
        // Marten did not build the data source, and that is the whole answer.
        Row(section, "Data source").Value.Should().Be("the application's own data source, handed to Marten directly");
        Row(section, "Data source").Note.Should().Contain("Marten did not build this NpgsqlDataSource").And.Contain("none of its own");
        Row(section, "Npgsql logging").Value.Should().Be("no effect here");
        section.Cards.Should().BeEmpty("there is no second pool to point out");

        // A Solo daemon runs its shards in this process: one connection for the daemon, one per running shard.
        ConnectionObservedLine daemon = Row(section, "Async daemon");
        Match shards = Regex.Match(daemon.Value, @"^(\d+) shards? running: about (\d+) connections?$");
        shards.Success.Should().BeTrue("the daemon row says what it needs: {0}", daemon.Value);
        int running = int.Parse(shards.Groups[1].Value, CultureInfo.InvariantCulture);
        int needs = int.Parse(shards.Groups[2].Value, CultureInfo.InvariantCulture);
        running.Should().BePositive();
        needs.Should().Be(running + 1);
        daemon.Note.Should().Contain("C09");

        // Connection(dataSource) stored the data source's own timeout (Npgsql's 30 s, since the string names none) into the option.
        Row(section, "Command timeout").Value.Should().Be("30 s");
        Row(section, "Command timeout").Note.Should().Contain("so it was set, by the application or by Connection(string) / UseNpgsqlDataSource()")
            .And.Contain("(Npgsql's 30 s when the string names none), and it wins");
    }

    [PostgresFact]
    public async Task A_store_that_connects_with_a_string_has_a_pool_of_its_own_and_says_the_application_registers_another()
    {
        await using LiveHost host = await LiveHost.StartAsync(postgres, "np4_own_pool", useRegisteredDataSource: false, daemon: false);

        ConnectionContributedSection section = await host.SectionAsync();

        Row(section, "Data source").Value.Should().Be("Marten's own");
        Row(section, "Data source").Note.Should().Contain("registers 1 NpgsqlDataSource").And.Contain("C18");
        section.Cards.Should().ContainSingle().Which.Fixes.Should().ContainSingle().Which.Code.Should().Contain("UseNpgsqlDataSource()");

        Row(section, "Async daemon").Value.Should().Be("not hosted in this process");
    }

    [PostgresFact]
    public async Task Events_larger_than_the_read_buffer_that_the_deeper_checks_measured_reach_the_buffer_hint()
    {
        await using LiveHost host = await LiveHost.StartAsync(postgres, "np4_buffer", useRegisteredDataSource: true, daemon: false);

        // Before any deeper run there is nothing to judge a buffer by.
        ConnectionPageView before = await host.ViewAsync();
        before.Cards.Concat(before.Notes).Should().NotContain(static x => x.Code == ConnectionHintIds.Buffer);

        IDocumentStore store = host.Store;
        await using (IDocumentSession session = store.LightweightSession())
        {
            // Random, so TOAST cannot compress it away: the size E04 measures is the stored one.
            string payload = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(30_000));

            for (int index = 0; index < 150; index++)
            {
                session.Events.StartStream<ConnectionNoteTally>(Guid.NewGuid(), new ConnectionNoted(payload));
            }

            await session.SaveChangesAsync(Token);
        }

        // "Run deeper checks": E04 measures the recent events and records their average stored size for the Connection page.
        await host.RunDeeperChecksAsync();

        ConnectionPageView view = await host.ViewAsync();
        ConnectionCard buffer = view.Cards.Concat(view.Notes).Should().ContainSingle(static x => x.Code == ConnectionHintIds.Buffer).Which;
        buffer.Text.Should().Contain("larger than Read Buffer Size (8192)");
        buffer.Fixes.Should().Contain(static x => x.Label == "Connection string" && x.Code.StartsWith("Read Buffer Size=", StringComparison.Ordinal));
    }

    private static ConnectionObservedLine Row(ConnectionContributedSection section, string label) => section.Rows.Single(x => x.Label == label);

    /// <summary>A real host over a real Postgres: Marten registered either way, the studio beside it, a daemon or none.</summary>
    private sealed class LiveHost : IAsyncDisposable
    {
        private readonly IHost host;

        private LiveHost(IHost host)
        {
            this.host = host;
        }

        public static async Task<LiveHost> StartAsync(PostgresFixture postgres, string schema, bool useRegisteredDataSource, bool daemon)
        {
            await postgres.CreateSchemaAsync(schema, Token);
            await postgres.CreateSchemaAsync(schema + "_events", Token);

            HostApplicationBuilder builder = Host.CreateApplicationBuilder();

            // The application's own data source, in its container. A store that connects with a string has a registered one beside it.
            builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(
                useRegisteredDataSource ? postgres.ConnectionString : postgres.ConnectionString + ";Application Name=np4-other"));

            MartenServiceCollectionExtensions.MartenConfigurationExpression marten = builder.Services.AddMarten(options =>
            {
                if (!useRegisteredDataSource)
                {
                    options.Connection(postgres.ConnectionString);
                }

                options.DatabaseSchemaName = schema;
                options.Events.DatabaseSchemaName = schema + "_events";
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.Events.AddEventType<ConnectionNoted>();
                options.Projections.Snapshot<ConnectionNoteTally>(SnapshotLifecycle.Async);
            }).UseLightweightSessions();

            if (useRegisteredDataSource)
            {
                marten.UseNpgsqlDataSource();
            }

            if (daemon)
            {
                marten.AddAsyncDaemon(DaemonMode.Solo);
            }

            builder.Services.AddAuthorization();
            builder.Services.AddSingleton<AuthenticationStateProvider, Anonymous>();
            builder.Services.AddMartenStudio(static options => options.Capabilities = MartenStudioCapabilities.All());

            IHost built = builder.Build();
            IDocumentStore store = built.Services.GetRequiredService<IDocumentStore>();
            await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            await built.StartAsync(Token);

            if (daemon)
            {
                await ProjectionsFixture.WaitForAsync(
                    () => Task.FromResult(built.Services.GetRequiredService<Marten.Events.Daemon.Coordination.IProjectionCoordinator>().DaemonForMainDatabase().IsRunning),
                    "the async daemon to start",
                    TimeSpan.FromSeconds(60),
                    Token);
            }

            return new LiveHost(built);
        }

        public IDocumentStore Store => host.Services.GetRequiredService<IDocumentStore>();

        /// <summary>The page, through the real page service in a circuit-shaped scope.</summary>
        public async Task<ConnectionPageView> ViewAsync()
        {
            await using AsyncServiceScope circuit = host.Services.CreateAsyncScope();
            ConnectionPageView view = await circuit.ServiceProvider.GetRequiredService<IConnectionInsightService>().GetAsync(await ScopeAsync(), Token);

            view.Refusal.Should().Be(ConnectionRefusal.None, view.Reason);
            return view;
        }

        /// <summary>The page's section.</summary>
        public async Task<ConnectionContributedSection> SectionAsync() =>
            (await ViewAsync()).Contributed.Should().ContainSingle(static x => x.Id == "conn-marten").Which;

        /// <summary>What "Run deeper checks" does, for a visitor who may.</summary>
        public async Task RunDeeperChecksAsync()
        {
            await using AsyncServiceScope circuit = host.Services.CreateAsyncScope();
            PostgresStudio.Services.Health.HealthReport report = await circuit.ServiceProvider.GetRequiredService<PostgresStudio.Services.Health.IHealthService>()
                .GetReportAsync(
                    await ScopeAsync(),
                    new PostgresStudio.Services.Health.HealthReportRequest(PostgresStudio.Services.Health.HealthDeeperChecks.Run),
                    Token);

            report.Refusal.Should().Be(PostgresStudio.Services.Health.HealthRefusal.None, report.Reason);
        }

        private async Task<StudioScope> ScopeAsync()
        {
            IReadOnlyList<Marten.Storage.IMartenDatabase> databases = await Store.Storage.AllDatabases();
            return new StudioScope("default", databases[0].Id.Identity, null);
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync(TimeSpan.FromSeconds(30));
            host.Dispose();
        }

        private sealed class Anonymous : AuthenticationStateProvider
        {
            public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
                Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
        }
    }
}
