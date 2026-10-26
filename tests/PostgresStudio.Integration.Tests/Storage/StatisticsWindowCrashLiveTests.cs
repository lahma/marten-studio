using System.Net;
using System.Net.Sockets;
using System.Security.Claims;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Sources;

using Testcontainers.PostgreSql;

namespace PostgresStudio.Integration.Tests.Storage;

/// <summary>
/// The statistics window across a real crash, on PostgreSQL 14 and 17 (W5-CAP, B0 G4): a sweep reads the window, the
/// postmaster is sent <c>SIGQUIT</c> - an immediate shutdown, which keeps no statistics - and started again, and the next read
/// sees the reset, says it looks like a crash, and starts the window again at the restart. 14 moves the database's own
/// <c>stats_reset</c> as well; 17 leaves it null and moves only the shared views'.
/// </summary>
/// <remarks>
/// <b>A server of its own for each leg</b>, never the matrix's shared one, which other classes read while this one kills it:
/// the matrix's exact images (<see cref="Versions.PostgresVersionMatrix.Versions" />), never reused, on a fixed host port so the
/// studio's data source finds the same server after the restart. An image that cannot be started skips locally and fails on CI
/// (D19), as the matrix's legs do.
/// </remarks>
public class StatisticsWindowCrashLiveTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> Images => ["postgres:14.24-alpine", "postgres:17.11-alpine"];

    [PostgresTheory]
    [MemberData(nameof(Images))]
    public async Task A_crash_and_a_restart_read_as_a_reset_that_starts_the_window_again(string image)
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        int port = FreePort();
        PostgreSqlContainer container = new PostgreSqlBuilder(image)
            .WithReuse(false)
            .WithName("ms-cap-crash-" + Guid.NewGuid().ToString("N")[..12])
            .WithPortBinding(port, 5432)
            .Build();

        await using (container)
        {
            try
            {
                await container.StartAsync(Token);
            }
            catch (Exception exception) when (!DockerAvailability.OnContinuousIntegration)
            {
                Assert.Skip("Could not start " + image + " here (" + exception.GetType().Name + ": " + exception.Message + "); `docker pull " + image + "` makes it run.");
            }

            string connectionString = container.GetConnectionString();
            await StorageLive.ExecuteAsync(connectionString, "create table t (id int primary key); insert into t select g from generate_series(1, 1000) g;");

            await using ServiceProvider services = Host(connectionString);
            ResolvedSource resolved = await ResolveAsync(services);
            HealthSweeper sweeper = services.GetRequiredService<HealthSweeper>();

            await sweeper.SweepAsync(resolved, HealthSweepKind.Regular, Token);
            StatisticsWindow before = await WindowAsync(services, resolved);
            before.LooksReset.Should().BeFalse("the first sweep's sample is what the next read compares with");

            // An immediate shutdown of the postmaster - PID 1 in the official image - which is what a crash leaves behind.
            try
            {
                await container.ExecAsync(["kill", "-QUIT", "1"], Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The container went down under the exec; its exit is what is waited for next.
            }

            await container.GetExitCodeAsync(Token);
            DateTimeOffset restarting = DateTimeOffset.UtcNow;
            await container.StartAsync(Token);

            await StorageLive.UntilAsync(
                async () =>
                {
                    try
                    {
                        return await StorageLive.ScalarAsync<int>(connectionString, "select 1") == 1;
                    }
                    catch (NpgsqlException)
                    {
                        return false;
                    }
                },
                TimeSpan.FromSeconds(60),
                image + " to accept connections again after the restart");

            // The studio's pool still holds the connections the dead server closed, and a host's would find each out on first
            // use. Each failed use discards one; once none is left, the pool opens a live one.
            await StorageLive.UntilAsync(
                async () =>
                {
                    await using NpgsqlConnection pooled = resolved.Target.CreateConnection(StudioConnectionUsage.ReadWrite);

                    try
                    {
                        await pooled.OpenAsync(Token);
                        await using NpgsqlCommand probe = new("select 1", pooled);
                        await probe.ExecuteScalarAsync(Token);
                        return true;
                    }
                    catch (NpgsqlException)
                    {
                        return false;
                    }
                },
                TimeSpan.FromSeconds(30),
                "the studio's pool to give up the dead server's connections");

            StatisticsWindow after = await WindowAsync(services, resolved);

            after.LooksReset.Should().BeTrue();
            after.Reason.Should().Be(StatisticsResetReason.Crash, after.Signal);
            after.Signal.Should().Contain("without its statistics");
            after.Since.Should().BeAfter(restarting.AddMinutes(-1), "the window starts again at the restart");
            after.Basis.Should().BeOneOf(StatisticsBasis.ServerReset, StatisticsBasis.ArchiverReset, StatisticsBasis.DatabaseReset);
            after.ServerStartedAt.Should().BeAfter(before.ServerStartedAt!.Value);

            // And the sweep itself: C11 watches, and says why.
            HealthSweep sweep = await sweeper.SweepAsync(resolved, HealthSweepKind.Regular, Token);
            HealthCheckResult c11 = sweep.Results.Single(static x => x.Check.Code == "C11");
            c11.Outcome.Should().Be(HealthCheckOutcome.Checked, c11.Problem?.Reason);
            c11.Note.Should().Contain("look reset");

            TestContext.Current.TestOutputHelper?.WriteLine("{0}: {1}, since {2} ({3})", image, after.Reason, after.Since, after.BasisText);
        }
    }

    /// <summary>The window as the next sweep would compute it: a read now, against the history the sweeps wrote.</summary>
    private static async Task<StatisticsWindow> WindowAsync(ServiceProvider services, ResolvedSource resolved)
    {
        await using HealthConnection connection = new(resolved.Target);
        HealthCheckContext context = new(
            resolved,
            services.GetRequiredService<PostgresStudio.Services.Settings.IStudioSettings>(),
            NoStudioRuntime.Instance,
            services,
            new HealthSqlRunner(connection, new PostgresStudio.Internal.Sql.ReadOnlySqlOptions(), observer: null),
            DateTimeOffset.UtcNow,
            new HealthSweepMemo(),
            Token);

        StatisticsReading reading = await StatisticsWindow.ReadAsync(context, Token);
        return StatisticsWindow.Compute(reading, services.GetRequiredService<HealthHistory>().Read(resolved.Source.Key, resolved.Target.Identity));
    }

    /// <summary>A Postgres Studio host over the one data source, as <c>MatrixHost</c> builds one.</summary>
    private static ServiceProvider Host(string connectionString)
    {
        NpgsqlConnectionStringBuilder own = new(connectionString) { ApplicationName = "ms-cap-crash-" + Guid.NewGuid().ToString("N")[..8] };

        ServiceCollection collection = new();
        collection.AddLogging();
        collection.AddAuthorization();
        collection.AddSingleton<AuthenticationStateProvider, Anonymous>();
        collection.AddSingleton(NpgsqlDataSource.Create(own.ConnectionString));
        collection.AddPostgresStudio(studio => studio.Capabilities = PostgresStudioCapabilities.All());

        return collection.BuildServiceProvider();
    }

    private static async Task<ResolvedSource> ResolveAsync(ServiceProvider services)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<StudioSourceResolver>().ResolveAsync(new StudioScope("default", "default", null), null, Token);
    }

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint) listener.LocalEndpoint).Port;
    }

    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
