using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
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
            (await HistorySessionsAsync(owned[0])).Should().Be(1, "the kept session is named as the history's between samples too");

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

        (await HistorySessionsAsync(owned[0])).Should().Be(1);

        leader = false;
        (await sampler.SampleOnceAsync(Token)).Should().Be(0, "another process samples");
        (await HistorySessionsAsync(owned[0])).Should().Be(0, "a follower keeps no connection, and the one it kept went back to the pool with its own name");

        string server = ConnectionHistorySampler.ServerOf(dataSource.ConnectionString)!;
        (await host.Services.GetRequiredService<IPostgresConnectionHistoryStore>().ReadAsync(server, DateTimeOffset.UtcNow.AddHours(-1), Token))
            .Should().ContainSingle();
    }

    /// <summary>
    /// Issue #50: a host that stops its sampler after disposing it (<c>WebApplicationFactory.DisposeAsync</c> does) must not fail in
    /// teardown, and the kept connection must be closed even though nothing stopped the sampler first.
    /// </summary>
    [PostgresFact]
    public async Task Stop_after_Dispose_does_not_throw_and_the_kept_connection_is_gone()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_history_stop"], Token);
        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(owned[0]);

        StudioHost host = StudioHost.Create(services =>
        {
            services.AddSingleton(dataSource);
            services.AddPostgresConnectionHistory();
        });

        ConnectionHistorySampler sampler = host.Services.GetRequiredService<ConnectionHistorySampler>();
        IPostgresConnectionHistoryStore store = host.Services.GetRequiredService<IPostgresConnectionHistoryStore>();
        string server = ConnectionHistorySampler.ServerOf(dataSource.ConnectionString)!;

        await sampler.StartAsync(Token);

        // A round has run once the store holds a sample, and the sampler keeps its connection between rounds.
        await WaitForAsync(
            async () => (await store.ReadAsync(server, DateTimeOffset.UtcNow.AddHours(-1), Token)).Count > 0 && await HistorySessionsAsync(owned[0]) == 1,
            "the sampler's first round to store a sample over its kept connection");

        await host.DisposeAsync();

        Func<Task> stop = () => sampler.StopAsync(Token);
        await stop.Should().NotThrowAsync("the host stops a hosted service it has already disposed");

        await WaitForAsync(async () => await HistorySessionsAsync(owned[0]) == 0, "the kept session to leave pg_stat_activity");
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(Token);
        limit.CancelAfter(TimeSpan.FromSeconds(30));

        while (!await condition())
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), limit.Token);
            }
            catch (OperationCanceledException) when (!Token.IsCancellationRequested)
            {
                throw new TimeoutException("Gave up waiting for " + what + ".");
            }
        }
    }

    /// <summary>How many sessions on the database carry the connection history's name, read over a connection of the test's own.</summary>
    private static async Task<long> HistorySessionsAsync(string connectionString)
    {
        await using NpgsqlConnection probe = new(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, ApplicationName = "probe" }.ConnectionString);
        await probe.OpenAsync(Token);
        await using NpgsqlCommand command = new(
            "select count(*) from pg_stat_activity where datname = current_database() and application_name = @name", probe);
        command.Parameters.AddWithValue("name", ConnectionHistorySampler.HistoryName(null, connectionString, "Postgres Studio")!);
        return (long) (await command.ExecuteScalarAsync(Token))!;
    }

    /// <summary>
    /// 1.0.1 (issue #47): samples a store holds - no connection storm - are what a real sweep's C15 judges and the diagnostics
    /// API's section summarizes, and Holders reads the same peak: one arithmetic.
    /// </summary>
    [PostgresFact]
    [HealthFires("C15")]
    public async Task A_real_sweep_raises_C15_from_the_samples_a_store_holds_and_the_API_and_Holders_agree_on_the_peak()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_history_c15"], Token);
        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(owned[0]);

        await using StudioHost host = StudioHost.Create(
            services =>
            {
                services.AddSingleton(dataSource);
                services.AddPostgresConnectionHistory();
            },
            static o => o.Capabilities.ViewDatabaseActivity = true);

        string server = ConnectionHistorySampler.ServerOf(dataSource.ConnectionString)!;
        IPostgresConnectionHistoryStore store = host.Services.GetRequiredService<IPostgresConnectionHistoryStore>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        PostgresConnectionSample Sample(TimeSpan ago, long connections, params (string? Application, int Total)[] applications) => new()
        {
            At = now - ago,
            Server = server,
            Instance = "a",
            Connections = connections,
            ClientLimit = 100,
            ByApplication = [.. applications.Select(static x => new PostgresConnectionCounts { ApplicationName = x.Application, Total = x.Total })],
        };

        // 99 of 100 was 30 hours ago (the window keeps it; C15's day does not), 96 of 100 three hours ago, and the latest is quiet.
        await store.AppendAsync(Sample(TimeSpan.FromHours(30), 99), Token);
        await store.AppendAsync(Sample(TimeSpan.FromHours(3), 96, ("peak-app", 70), (null, 26)), Token);
        await store.AppendAsync(Sample(TimeSpan.FromMinutes(1), 10), Token);

        PostgresDiagnosticsSnapshot snapshot = await host.Services.GetRequiredService<IPostgresDiagnostics>().GetSnapshotAsync(
            new PostgresDatabaseRef("default", ""),
            PostgresDiagnosticsRequest.FindingsOnly with { ConnectionHistory = true, IncludeActivity = true },
            Token);

        snapshot.Outcome.Should().Be(PostgresReadOutcome.Read, snapshot.Reason);

        PostgresFinding finding = snapshot.Findings.Should().ContainSingle(static x => x.Code == "C15").Subject;
        finding.Severity.Should().Be(PostgresSeverity.Critical, "96 of 100 within the last day; the 30-hour-old 99 is not judged");
        finding.Title.Should().Contain("96 of 100");
        finding.Evidence.Should().Contain(static x => x.Key == "Application 1 (highest peak)" && x.Value.Contains("peak-app", StringComparison.Ordinal));
        finding.StudioLinks.Should().ContainSingle().Which.Path.Should().Contain("view=holders");
        snapshot.Checks.Single(static x => x.Code == "C15").Outcome.Should().Be(PostgresReadOutcome.Read);

        PostgresConnectionHistorySection section = snapshot.ConnectionHistory!;
        section.Outcome.Should().Be(PostgresReadOutcome.Read, section.Reason);
        section.Samples.Should().Be(3);
        section.Peak.Should().Be(99, "the whole window");
        section.ByApplication.Select(static x => x.ApplicationName).Should().Contain("peak-app");

        await using StudioCircuit circuit = host.Circuit();
        SessionActivitySummary summary = await circuit.Get<ISessionActivityService>().GetSummaryAsync(Scope, Token);

        summary.ConnectionHistory!.Peak.Should().Be(section.Peak, "Holders and the API read one arithmetic");
        summary.ConnectionHistory.PeakAt.Should().Be(section.PeakAt!.Value);
        summary.ConnectionHistory.ClientLimitNow.Should().Be(section.ClientLimitNow);
        summary.ConnectionHistory.Samples.Should().Be(section.Samples);
    }

    /// <summary>C15 stays quiet: a peak under the line, one a resize has made room for, and one older than a day.</summary>
    [PostgresFact]
    [HealthQuiet("C15")]
    public async Task C15_is_quiet_for_a_peak_under_the_line_a_peak_a_resize_has_made_room_for_and_a_peak_older_than_a_day()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_history_c15_quiet"], Token);
        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(owned[0]);

        await using StudioHost host = StudioHost.Create(services =>
        {
            services.AddSingleton(dataSource);
            services.AddPostgresConnectionHistory();
        });

        string server = ConnectionHistorySampler.ServerOf(dataSource.ConnectionString)!;
        IPostgresConnectionHistoryStore store = host.Services.GetRequiredService<IPostgresConnectionHistoryStore>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        PostgresConnectionSample Sample(TimeSpan ago, long connections, int limit) => new()
        {
            At = now - ago,
            Server = server,
            Instance = "a",
            Connections = connections,
            ClientLimit = limit,
        };

        IPostgresDiagnostics diagnostics = host.Services.GetRequiredService<IPostgresDiagnostics>();
        PostgresDatabaseRef database = new("default", "");

        async Task<PostgresDiagnosticsSnapshot> SnapshotAsync() =>
            await diagnostics.GetSnapshotAsync(database, PostgresDiagnosticsRequest.FindingsOnly, Token);

        // Under the line: 79 of 100.
        await store.AppendAsync(Sample(TimeSpan.FromHours(2), 79, 100), Token);
        await store.AppendAsync(Sample(TimeSpan.FromMinutes(1), 10, 100), Token);
        PostgresDiagnosticsSnapshot under = await SnapshotAsync();
        under.Outcome.Should().Be(PostgresReadOutcome.Read, under.Reason);
        under.Findings.Should().NotContain(static x => x.Code == "C15");
        under.Checks.Single(static x => x.Code == "C15").Outcome.Should().Be(PostgresReadOutcome.Read);

        // A 96 of 100 peak that a resize since has made room for: the latest sample says 300.
        await using StudioHost resized = StudioHost.Create(services =>
        {
            services.AddSingleton(dataSource);
            services.AddPostgresConnectionHistory();
        });
        IPostgresConnectionHistoryStore resizedStore = resized.Services.GetRequiredService<IPostgresConnectionHistoryStore>();
        await resizedStore.AppendAsync(Sample(TimeSpan.FromHours(3), 96, 100), Token);
        await resizedStore.AppendAsync(Sample(TimeSpan.FromMinutes(1), 10, 300), Token);
        (await resized.Services.GetRequiredService<IPostgresDiagnostics>().GetSnapshotAsync(database, PostgresDiagnosticsRequest.FindingsOnly, Token))
            .Findings.Should().NotContain(static x => x.Code == "C15", "the peak is judged against the client limit now");

        // A 99 of 100 peak 30 hours ago: out of C15's day.
        await using StudioHost old = StudioHost.Create(services =>
        {
            services.AddSingleton(dataSource);
            services.AddPostgresConnectionHistory();
        });
        IPostgresConnectionHistoryStore oldStore = old.Services.GetRequiredService<IPostgresConnectionHistoryStore>();
        await oldStore.AppendAsync(Sample(TimeSpan.FromHours(30), 99, 100), Token);
        await oldStore.AppendAsync(Sample(TimeSpan.FromMinutes(1), 10, 100), Token);
        (await old.Services.GetRequiredService<IPostgresDiagnostics>().GetSnapshotAsync(database, PostgresDiagnosticsRequest.FindingsOnly, Token))
            .Findings.Should().NotContain(static x => x.Code == "C15", "a peak older than 24 hours is no longer a warning");
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
