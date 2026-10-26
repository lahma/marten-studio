using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services.Health;

using Testcontainers.PostgreSql;

namespace PostgresStudio.Integration.Tests.Maintenance;

/// <summary>
/// T14 and a standby whose <c>hot_standby_feedback</c> reaches the primary through a physical slot (B0 §4): its oldest
/// snapshot lands on the slot's <c>xmin</c>, not on its <c>pg_stat_replication</c> row, so T14 names the slot - as a
/// standby, through it - and dates the hold from when the studio first saw that slot at that xmin.
/// </summary>
/// <remarks>
/// B0's pair (<c>repl-up.sh</c>): a primary on a private network that trusts replication connections, a slot, and a
/// standby made by <c>pg_basebackup -R -S</c> streaming from it with feedback on. Both are this test's alone and go with it;
/// nothing is written beyond a thousand empty transactions, so the WAL stays a few megabytes.
/// </remarks>
public class StandbySlotLiveTests
{
    private const string Slot = "standby1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_standbys_snapshot_held_through_its_slot_is_a_T14_holder_named_as_a_standby_through_that_slot()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        string image = PostgresVersionMatrix.Versions.Single(static x => x.Major == 17).Image;
        string run = PostgresVersionMatrix.RunId;

        await using INetwork network = new NetworkBuilder().WithName("ms-standby-" + run).Build();
        await network.CreateAsync(Token);

        await using PostgreSqlContainer primary = new PostgreSqlBuilder(image)
            .WithNetwork(network)
            .WithNetworkAliases("primary")
            .WithName("ms-standby-primary-" + run)
            .WithReuse(false)
            .Build();
        await StartAsync(primary, image);

        ExecResult trusted = await primary.ExecAsync(["sh", "-c", "echo 'host replication all all trust' >> \"$PGDATA/pg_hba.conf\""], Token);
        trusted.ExitCode.Should().Be(0, trusted.Stderr);

        string primaryConnection = new NpgsqlConnectionStringBuilder(primary.GetConnectionString()) { Pooling = false }.ConnectionString;
        await RunAsync(primaryConnection,
            "select pg_catalog.pg_reload_conf()",
            "select pg_catalog.pg_create_physical_replication_slot('" + Slot + "')",
            "create schema ps_t14");

        await using IContainer standby = new ContainerBuilder(image)
            .WithNetwork(network)
            .WithName("ms-standby-replica-" + run)
            .WithCreateParameterModifier(static parameters => parameters.User = "postgres")
            .WithEntrypoint("sh")
            .WithCommand("-c", "pg_basebackup -h primary -U postgres -D /tmp/standby -R -X stream -S " + Slot
                + " && chmod 0700 /tmp/standby && exec postgres -D /tmp/standby -c hot_standby_feedback=on")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-h", "127.0.0.1", "-p", "5432"))
            .Build();
        await StartAsync(standby, image);

        string standbyConnection = new NpgsqlConnectionStringBuilder
        {
            Host = standby.Hostname,
            Port = standby.GetMappedPublicPort(5432),
            Username = "postgres",
            Password = "postgres",
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;

        await AutovacuumServer.UntilAsync(
            async () => await ScalarAsync<bool>(primaryConnection, "select coalesce(bool_or(active), false) from pg_catalog.pg_replication_slots where slot_name = '" + Slot + "'"),
            TimeSpan.FromSeconds(60),
            "the standby to stream from its slot");

        // A repeatable-read snapshot on the standby, held: with feedback on, the primary's slot keeps its xmin.
        await using NpgsqlConnection reader = new(standbyConnection);
        await reader.OpenAsync(Token);
        await using NpgsqlTransaction snapshot = await reader.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, Token);
        await using (NpgsqlCommand read = new("select pg_catalog.count(*) from pg_catalog.pg_class", reader, snapshot))
        {
            await read.ExecuteScalarAsync(Token);
        }

        // A thousand-odd transactions on the primary, each with an id of its own, so the slot is that far behind.
        await RunAsync(primaryConnection, "do $$ begin for i in 1..1100 loop perform pg_catalog.pg_current_xact_id(); commit; end loop; end $$");

        await AutovacuumServer.UntilAsync(
            async () => await ScalarAsync<int>(primaryConnection,
                "select coalesce(max(pg_catalog.age(s.xmin)), 0) from pg_catalog.pg_replication_slots s where s.slot_name = '" + Slot + "'") >= 1_000,
            TimeSpan.FromSeconds(60),
            "the standby's feedback to hold the slot's xmin a thousand transactions back");

        (await ScalarAsync<bool>(primaryConnection, "select bool_and(r.backend_xmin is null) from pg_catalog.pg_stat_replication r"))
            .Should().BeTrue("feedback through a slot lands on the slot, and the standby's own row shows no backend_xmin (B0 §4)");

        ManualClock clock = new(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
        await using StudioHost host = StudioHost.Create(
            services =>
            {
                services.AddSingleton(_ => NpgsqlDataSource.Create(primaryConnection));
                services.AddSingleton<TimeProvider>(clock);
            },
            options =>
            {
                options.BrowsableSchemas.Add("ps_t14");
                options.Capabilities = PostgresStudioCapabilities.All();
            });

        HealthReport first = await AutovacuumServer.ReportAsync(host);
        first.Findings.Should().NotContain(static x => x.Code == "T14", "the slot was first seen in this sweep");

        clock.Advance(TimeSpan.FromHours(2));
        HealthReport later = await AutovacuumServer.ReportAsync(host);

        HealthReportFinding finding = later.Findings.Should().ContainSingle(static x => x.Code == "T14",
            AutovacuumServer.Status(later, "T14").Note ?? AutovacuumServer.Status(later, "T14").Problem).Subject;
        finding.Evidence.Should().Contain(static x => x.Label == "Held back by" && x.Value == "a standby, through a physical replication slot");
        finding.Evidence.Single(static x => x.Label == "Who it is").Value.Should().StartWith("a standby, through slot '" + Slot + "' (physical, active)");
        finding.Finding.Title.Should().StartWith("A standby, through a physical replication slot, has held back vacuum for ");

        await snapshot.RollbackAsync(Token);
    }

    /// <summary>
    /// Starts a container of the pair - D19's rule, as <see cref="MatrixServer" /> applies it: an image that cannot be pulled or
    /// started skips the test locally, with the <c>docker pull</c> line in the reason, and fails it on CI.
    /// </summary>
    private static async Task StartAsync(IContainer container, string image)
    {
        try
        {
            await container.StartAsync(Token);
        }
        catch (Exception exception) when (!DockerAvailability.OnContinuousIntegration && exception is not OperationCanceledException)
        {
            Assert.Skip("The standby pair could not start " + image + " here (" + exception.GetType().Name + ": " + exception.Message
                + "); `docker pull " + image + "` makes it run. On CI this is a failure, not a skip.");
        }
    }

    private static async Task RunAsync(string connectionString, params string[] statements)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(Token);

        foreach (string sql in statements)
        {
            await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(Token);
        }
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(Token);
        await using NpgsqlCommand command = new(sql, connection);
        return (T) (await command.ExecuteScalarAsync(Token))!;
    }
}
