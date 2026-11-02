using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.ConnectionHistory;
using PostgresStudio.Services.Database;

namespace PostgresStudio.Integration.Tests.Sources;

/// <summary>
/// Issue #43 on a server: a round of the connection history samples the server's client sessions by application into the store,
/// over a connection it keeps; Sessions › Holders shows the peak under its own gate and names the applications past the activity
/// gate; and a process the host says is not the leader samples nothing and holds no connection.
/// </summary>
public class ConnectionHistoryLiveTests(PostgresFixture postgres)
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_round_samples_the_servers_applications_and_Holders_shows_the_peak()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_history"], Token);
        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(owned[0]);
        NpgsqlDataSource workers = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(owned[0]) { ApplicationName = "history-worker" }.ConnectionString);

        await using StudioHost host = StudioHost.Create(
            services =>
            {
                services.AddSingleton(dataSource);
                services.AddPostgresConnectionHistory();
            },
            static o => o.Capabilities.ViewDatabaseActivity = true);

        List<NpgsqlConnection> held = [];

        try
        {
            for (int i = 0; i < 3; i++)
            {
                held.Add(await workers.OpenConnectionAsync(Token));
            }

            ConnectionHistorySampler sampler = host.Services.GetRequiredService<ConnectionHistorySampler>();
            (await sampler.SampleOnceAsync(Token)).Should().Be(1, "one server, sampled once");
            (await sampler.SampleOnceAsync(Token)).Should().Be(1, "the kept connection serves the next round");

            string server = ConnectionHistorySampler.ServerOf(dataSource.ConnectionString)!;
            IReadOnlyList<PostgresConnectionSample> samples = await host.Services.GetRequiredService<IPostgresConnectionHistoryStore>()
                .ReadAsync(server, DateTimeOffset.UtcNow.AddHours(-1), Token);

            samples.Should().HaveCount(2);
            PostgresConnectionSample sample = samples[^1];
            sample.Connections.Should().BeGreaterThanOrEqualTo(3);
            sample.ClientLimit.Should().BeGreaterThan(0);
            sample.Instance.Should().NotBeNullOrEmpty();
            sample.ByApplication.Should().Contain(static x => x.ApplicationName == "history-worker" && x.Total == 3);

            await using StudioCircuit circuit = host.Circuit();
            ISessionActivityService sessions = circuit.Get<ISessionActivityService>();

            SessionActivitySummary summary = await sessions.GetSummaryAsync(Scope, Token);
            summary.ConnectionHistoryKept.Should().BeTrue();
            summary.ConnectionHistory!.Samples.Should().Be(2);
            summary.ConnectionHistory.Peak.Should().BeGreaterThanOrEqualTo(3);
            summary.ConnectionHistory.Applications.Should().BeEmpty("the summary is the page's counts, which name no application");

            SessionActivityDetails details = await sessions.GetDetailsAsync(Scope, Token);
            details.ConnectionHistory!.Applications.Should().Contain(static x => x.Application == "history-worker" && x.Peak == 3);
        }
        finally
        {
            foreach (NpgsqlConnection connection in held)
            {
                await connection.DisposeAsync();
            }
        }
    }

    [PostgresFact]
    public async Task A_process_that_is_not_the_leader_samples_nothing()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_history_follower"], Token);
        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(owned[0]);
        bool leader = true;

        await using StudioHost host = StudioHost.Create(services =>
        {
            services.AddSingleton(dataSource);
            services.AddPostgresConnectionHistory(o => o.ShouldSample = _ => ValueTask.FromResult(leader));
        });

        ConnectionHistorySampler sampler = host.Services.GetRequiredService<ConnectionHistorySampler>();
        (await sampler.SampleOnceAsync(Token)).Should().Be(1);

        leader = false;
        (await sampler.SampleOnceAsync(Token)).Should().Be(0, "another process samples");

        string server = ConnectionHistorySampler.ServerOf(dataSource.ConnectionString)!;
        (await host.Services.GetRequiredService<IPostgresConnectionHistoryStore>().ReadAsync(server, DateTimeOffset.UtcNow.AddHours(-1), Token))
            .Should().ContainSingle();
    }

    [PostgresFact]
    public async Task Without_the_registration_Holders_says_the_history_is_not_kept()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_history_off"], Token);
        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(owned[0]);

        await using StudioHost host = StudioHost.Create(services => services.AddSingleton(dataSource));
        await using StudioCircuit circuit = host.Circuit();

        SessionActivitySummary summary = await circuit.Get<ISessionActivityService>().GetSummaryAsync(Scope, Token);

        summary.ConnectionHistoryKept.Should().BeFalse();
        summary.ConnectionHistory.Should().BeNull();
    }
}
