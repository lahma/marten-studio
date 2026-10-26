using System.Globalization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

using Npgsql;

using Testcontainers.PostgreSql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// R01, S02 and S03 on a Postgres server of this class's own (<see cref="ServerSafetyServerFixture" />): one that can make
/// logical slots, checks page checksums and is durable - three things the shared server is not - and whose WAL and slots
/// a test can fill without touching anybody else's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Server-wide state, so one class and one server.</b> Replication slots and checksum counters are the server's, not
/// a database's, so every test here runs on this class's own server, one after another (xunit runs a class's tests in
/// sequence), and leaves it as it found it: every slot it makes is dropped and <c>max_slot_wal_keep_size</c> reset in a
/// <c>finally</c>, and the database a corruption was made in is dropped with it.
/// </para>
/// <para>
/// <b>Creating a slot and corrupting a page are the tests' own acts.</b> The checks only read; the no-DDL / no-lock
/// sweep is <c>ServerSweepNoDdlLiveTests</c>.
/// </para>
/// </remarks>
public class ServerSafetyLiveTests(ServerSafetyServerFixture server) : IClassFixture<ServerSafetyServerFixture>
{
    private const long MiB = 1L << 20;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ----------------------------------------------------------------------------------------------
    // R01
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// A physical slot that nobody reads, holding over a gibibyte: watched on the first sweep, a Warning on a sweep ten
    /// minutes later by the advisor's own samples - on a clock the test moves, while Postgres's own <c>inactive_since</c>
    /// says only seconds.
    /// </summary>
    [PostgresFact]
    [HealthFires("R01")]
    public async Task A_physical_slot_nobody_reads_holding_a_gibibyte_for_ten_minutes_of_samples_is_a_warning()
    {
        const string Slot = "h10_forgotten_replica";
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "health_r01_inactive");

        await SetSlotCapAsync("-1");

        try
        {
            await ExecuteAsync(server.ConnectionString, "select pg_create_physical_replication_slot('" + Slot + "', true)");
            await EmitWalAsync(1_100);

            ManualClock clock = new(DateTimeOffset.UtcNow);
            await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, clock: clock);

            HealthReport first = await host.ReportAsync();
            first.Findings.Should().NotContain(static x => x.Code == "R01", "one sample is watching, not judging");
            ConnectionsLiveHost.Status(first, "R01").Note.Should().Contain("1 inactive replication slot holds at least 1.0 GiB");

            clock.Advance(TimeSpan.FromMinutes(11));

            HealthReport second = await host.ReportAsync();
            HealthReportFinding finding = second.Findings.Should().ContainSingle(static x => x.Code == "R01").Subject;

            finding.Severity.Should().Be(HealthSeverity.Warning);
            finding.Finding.Variant.Should().Be("inactive");
            finding.Evidence.Should().Contain(static x => x.Label == "Held for at least" && x.Value == "11 min",
                "the run is the advisor's two samples, eleven minutes apart; Postgres's own inactive_since is seconds old");
            finding.Evidence.Should().Contain(new HealthEvidence("max_slot_wal_keep_size", "unlimited (-1, the default)"));
        }
        finally
        {
            await DropSlotAsync(Slot);
        }
    }

    /// <summary>
    /// A logical slot - a change data capture consumer's - at more than 80% of <c>max_slot_wal_keep_size</c>: Critical, and
    /// its name, plugin and database are shown to a visitor through the activity gate and to nobody else.
    /// </summary>
    [PostgresFact]
    [HealthFires("R01")]
    public async Task A_logical_slot_near_max_slot_wal_keep_size_is_critical_and_named_only_through_the_activity_gate()
    {
        const string Slot = "h10_cdc_orders";
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "health_r01_logical");

        await SetSlotCapAsync("64MB");

        try
        {
            await ExecuteAsync(database, "select pg_create_logical_replication_slot('" + Slot + "', 'pgoutput')");
            await EmitWalAsync(56);

            ManualClock clock = new(DateTimeOffset.UtcNow);
            await using ConnectionsLiveHost open = await ConnectionsLiveHost.CreateAsync(
                database,
                studio: static options => options.Capabilities = MartenStudioCapabilities.All(),
                clock: clock);
            await using ConnectionsLiveHost closed = await ConnectionsLiveHost.CreateAsync(database, clock: clock);

            // The first sample at the line is a note, not a Critical - unless PostgreSQL 17's own inactive_since already says
            // the slot has been without a consumer for a minute, which a slow runner can reach before the first sweep. So the
            // slot's inactive age is read on both sides of the sweep, by Postgres's clock: under the window after it, the
            // sweep saw under it and must have said a note; at the window before it, the sweep saw at least that and must
            // have said Critical; in between, either is right.
            double before = await InactiveSecondsAsync(Slot);
            HealthReport first = await open.ReportAsync();
            double after = await InactiveSecondsAsync(Slot);

            if (after < ReplicationSlotCheckWindow.TotalSeconds)
            {
                first.Findings.Should().NotContain(static x => x.Code == "R01", "a minute has not passed by Postgres's clock, and there is one sample");
                ConnectionsLiveHost.Status(first, "R01").Note.Should().Contain("for the first time");
            }
            else if (before >= ReplicationSlotCheckWindow.TotalSeconds)
            {
                first.Findings.Should().ContainSingle(static x => x.Code == "R01").Which.Severity.Should().Be(HealthSeverity.Critical,
                    "inactive_since was a minute old before the sweep began");
            }

            await closed.ReportAsync();

            clock.Advance(TimeSpan.FromMinutes(2));

            HealthReportFinding shown = (await open.ReportAsync()).Findings.Should().ContainSingle(static x => x.Code == "R01").Subject;
            shown.Severity.Should().Be(HealthSeverity.Critical);
            shown.Finding.Variant.Should().Be("near-limit");
            shown.Evidence.Should().Contain(new HealthEvidence("max_slot_wal_keep_size", "64.0 MiB"));
            shown.Evidence.Should().ContainSingle(static x => x.Gate == HealthGate.Activity).Which.Value
                .Should().Contain("'" + Slot + "'").And.Contain("logical (pgoutput)").And.Contain("for database 'health_r01_logical'");

            HealthReportFinding withheld = (await closed.ReportAsync()).Findings.Should().ContainSingle(static x => x.Code == "R01").Subject;
            withheld.Severity.Should().Be(HealthSeverity.Critical, "the finding and its numbers are the database's");

            string visible = string.Join("\n", withheld.Evidence.Select(static x => x.Value)) + "\n" + withheld.Finding.Title + "\n" + withheld.MuteKey;
            visible.Should().NotContain(Slot).And.NotContain("pgoutput").And.NotContain("health_r01_logical'");
            withheld.Evidence.Should().Contain(static x => x.Value.StartsWith("withheld - ", StringComparison.Ordinal)
                && x.Value.Contains("ViewDatabaseActivity", StringComparison.Ordinal));
        }
        finally
        {
            await DropSlotAsync(Slot);
            await SetSlotCapAsync("-1");
        }
    }

    /// <summary>
    /// B1, live: a logical slot of database B, swept from database A by a visitor with every capability. A's finding counts
    /// it and says what happened to it, and nothing in the report - evidence, titles, caveats, mute keys, anchors, status
    /// rows - names the slot, its plugin or database B: a visitor admitted to A has not been admitted to B. Invalidated
    /// rather than merely full, so that the finding is there on the first sweep, with no wait.
    /// </summary>
    [PostgresFact]
    public async Task A_logical_slot_of_another_database_is_counted_from_this_one_and_never_named()
    {
        const string Slot = "h10_cdc_tenant_b";
        const string Other = "health_r01_tenant_b";
        string swept = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "health_r01_tenant_a");
        string other = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, Other);

        await SetSlotCapAsync("16MB");

        try
        {
            await ExecuteAsync(other, "select pg_create_logical_replication_slot('" + Slot + "', 'pgoutput')");
            await EmitWalAsync(64);

            await LiveWait.UntilAsync(
                async () =>
                {
                    await ExecuteAsync(server.ConnectionString, "select pg_switch_wal()");
                    await ExecuteAsync(server.ConnectionString, "checkpoint");
                    return await LiveWait.ScalarAsync<string>(server.ConnectionString, "select wal_status from pg_replication_slots where slot_name = @slot", ("slot", Slot)) == "lost";
                },
                TimeSpan.FromSeconds(60),
                "a checkpoint to remove the WAL database B's slot holds past max_slot_wal_keep_size");

            await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(swept, studio: static options => options.Capabilities = MartenStudioCapabilities.All());
            HealthReport report = await host.ReportAsync();

            HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "R01").Subject;
            finding.Finding.Variant.Should().Be("lost");
            finding.Evidence.Should().Contain(new HealthEvidence("Invalidated slots", "1 of 1 slot on this server"), "database B's slot is counted from A");
            finding.Evidence.Should().ContainSingle(static x => x.Gate == HealthGate.Activity).Which.Value
                .Should().StartWith("a logical slot of another database on this server").And.Contain("wal_removed");

            string everything = string.Join("\n", report.Findings.SelectMany(static x =>
                    new[] { x.Finding.Title, x.Finding.WhyItMatters, x.Finding.Caveat, x.MuteKey, x.Anchor }
                        .Concat(x.Evidence.Select(static e => e.Label + " " + e.Value)))
                .Concat(report.Checks.Select(static x => x.Note + " " + x.Problem)));

            everything.Should().NotContain(Slot).And.NotContain(Other).And.NotContain("pgoutput",
                "neither the slot, its plugin nor its database is A's to name, through any gate");
        }
        finally
        {
            await DropSlotAsync(Slot);
            await SetSlotCapAsync("-1");
        }
    }

    /// <summary>A slot whose WAL Postgres removed at a checkpoint is lost: Info, with Postgres 17's reason.</summary>
    [PostgresFact]
    [HealthFires("R01")]
    public async Task A_slot_Postgres_invalidated_is_info_with_its_reason()
    {
        const string Slot = "h10_lost_replica";
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "health_r01_lost");

        await SetSlotCapAsync("16MB");

        try
        {
            await ExecuteAsync(server.ConnectionString, "select pg_create_physical_replication_slot('" + Slot + "', true)");
            await EmitWalAsync(64);

            await LiveWait.UntilAsync(
                async () =>
                {
                    await ExecuteAsync(server.ConnectionString, "select pg_switch_wal()");
                    await ExecuteAsync(server.ConnectionString, "checkpoint");
                    return await LiveWait.ScalarAsync<string>(server.ConnectionString, "select wal_status from pg_replication_slots where slot_name = @slot", ("slot", Slot)) == "lost";
                },
                TimeSpan.FromSeconds(60),
                "a checkpoint to remove the WAL the slot holds past max_slot_wal_keep_size");

            await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, studio: static options => options.Capabilities = MartenStudioCapabilities.All());

            HealthReportFinding finding = (await host.ReportAsync()).Findings.Should().ContainSingle(static x => x.Code == "R01").Subject;
            finding.Severity.Should().Be(HealthSeverity.Info);
            finding.Finding.Variant.Should().Be("lost");
            finding.Evidence.Should().ContainSingle(static x => x.Gate == HealthGate.Activity).Which.Value.Should().Contain("wal_removed");
        }
        finally
        {
            await DropSlotAsync(Slot);
            await SetSlotCapAsync("-1");
        }
    }

    [PostgresFact]
    [HealthQuiet("R01")]
    public async Task No_slot_and_then_a_slot_holding_little_are_quiet()
    {
        const string Slot = "h10_small_replica";
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "health_r01_quiet");
        ManualClock clock = new(DateTimeOffset.UtcNow);
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, clock: clock);

        HealthReport none = await host.ReportAsync();
        none.Findings.Should().NotContain(static x => x.Code == "R01");
        ConnectionsLiveHost.Status(none, "R01").Note.Should().Be("This server has no replication slots.");

        try
        {
            await ExecuteAsync(server.ConnectionString, "select pg_create_physical_replication_slot('" + Slot + "', true)");
            clock.Advance(TimeSpan.FromMinutes(2));

            HealthReport small = await host.ReportAsync();
            small.Findings.Should().NotContain(static x => x.Code == "R01");
            ConnectionsLiveHost.Status(small, "R01").Note.Should().StartWith("1 replication slot on this server, none of them forgotten or near a limit");
        }
        finally
        {
            await DropSlotAsync(Slot);
        }
    }

    // ----------------------------------------------------------------------------------------------
    // S02 - quiet on a durable server, in Production
    // ----------------------------------------------------------------------------------------------

    [PostgresFact]
    [HealthQuiet("S02")]
    public async Task A_durable_server_as_Postgres_ships_it_is_quiet_in_Production()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "health_s02_quiet");

        // The host's own environment, registered after ConnectionsLiveHost's Development one: the last registration is the one resolved.
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(
            database,
            services: static services => services.AddSingleton<IHostEnvironment>(new ProductionHost()));

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "S02");
        ConnectionsLiveHost.Status(report, "S02").Note.Should().Be(
            "track_counts, autovacuum, track_activities, fsync, full_page_writes, zero_damaged_pages and ignore_checksum_failure are at their safe values, "
            + "synchronous_commit is on, and nothing waits for a restart.",
            "every watched setting was read, and each is at its safe value");
    }

    private sealed class ProductionHost : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "MartenStudio.Integration.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    // ----------------------------------------------------------------------------------------------
    // S03
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// A page changed on disk behind Postgres's back: written out, evicted from shared buffers, overwritten in the data
    /// file with random bytes, and read again - which fails, and is counted.
    /// </summary>
    [PostgresFact]
    [HealthFires("S03")]
    public async Task A_page_corrupted_on_disk_and_read_again_is_a_critical_checksum_failure()
    {
        const string Name = "health_s03_fires";
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, Name);

        try
        {
            string path;

            await using (NpgsqlConnection connection = new(Unpooled(database)))
            {
                await connection.OpenAsync(Token);
                await RunAsync(connection, "create extension if not exists pg_buffercache");
                await RunAsync(connection, "create table h10_pages (id int primary key, pad text not null)");
                await RunAsync(connection, "insert into h10_pages select g, repeat('x', 100) from generate_series(1, 50) g");
                await RunAsync(connection, "checkpoint");

                await using NpgsqlCommand where = new("select pg_relation_filepath('h10_pages')", connection);
                path = (string) (await where.ExecuteScalarAsync(Token))!;

                await RunAsync(connection,
                    "select pg_buffercache_evict(bufferid) from pg_buffercache "
                    + "where relfilenode = pg_relation_filenode('h10_pages') "
                    + "and reldatabase = (select oid from pg_database where datname = current_database())");
            }

            // 64 random bytes in the middle of block 0 of the table's file: its header still parses, its checksum cannot match.
            DotNet.Testcontainers.Containers.ExecResult corrupt = await server.ExecAsync(
                "sh", "-c", "dd if=/dev/urandom of=\"$PGDATA/" + path + "\" bs=1 count=64 seek=4096 conv=notrunc");
            corrupt.ExitCode.Should().Be(0, corrupt.Stderr);

            await using (NpgsqlConnection reader = new(Unpooled(database)))
            {
                await reader.OpenAsync(Token);
                Func<Task> read = () => RunAsync(reader, "select count(*) from h10_pages");
                (await read.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.DataCorrupted);
            }

            await LiveWait.UntilAsync(
                async () => await LiveWait.ScalarAsync<long>(database, "select coalesce(checksum_failures, 0) from pg_stat_database where datname = current_database()") > 0,
                TimeSpan.FromSeconds(20),
                "pg_stat_database to count the checksum failure");

            await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database);
            HealthReportFinding finding = (await host.ReportAsync()).Findings.Should().ContainSingle(static x => x.Code == "S03").Subject;

            finding.Severity.Should().Be(HealthSeverity.Critical);
            finding.Evidence.Should().Contain(static x => x.Label == "In this database" && x.Value.StartsWith("1, the last at ", StringComparison.Ordinal));
        }
        finally
        {
            // The database goes, and its pg_stat_database row - and so its counted failure - with it.
            await ExecuteAsync(server.ConnectionString, "drop database if exists " + Name + " with (force)");
        }
    }

    [PostgresFact]
    [HealthQuiet("S03")]
    public async Task A_server_that_checks_checksums_and_has_counted_no_failure_is_quiet()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "health_s03_checked");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database);

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "S03");
        ConnectionsLiveHost.Status(report, "S03").Note.Should().StartWith("Data checksums are on, and no page has failed one");
    }

    // ----------------------------------------------------------------------------------------------
    // Helpers: every one the test's own act, on a connection of its own
    // ----------------------------------------------------------------------------------------------

    private static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    private static async Task RunAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = new(Unpooled(connectionString));
        await connection.OpenAsync(Token);
        await RunAsync(connection, sql);
    }

    /// <summary>Writes about <paramref name="mebibytes" /> MiB of WAL with no table at all: non-transactional logical messages.</summary>
    private async Task EmitWalAsync(int mebibytes)
    {
        for (int done = 0; done < mebibytes; done += 100)
        {
            int batch = Math.Min(100, mebibytes - done);
            await ExecuteAsync(
                server.ConnectionString,
                "select count(pg_logical_emit_message(false, 'h10', repeat('x', " + MiB.ToString(CultureInfo.InvariantCulture) + ")))"
                + " from generate_series(1, " + batch.ToString(CultureInfo.InvariantCulture) + ")");
        }
    }

    /// <summary>Sets <c>max_slot_wal_keep_size</c> on the server, reloads, and waits until a new session sees it.</summary>
    private async Task SetSlotCapAsync(string value)
    {
        await ExecuteAsync(server.ConnectionString, value == "-1"
            ? "alter system reset max_slot_wal_keep_size"
            : "alter system set max_slot_wal_keep_size = '" + value + "'");
        await ExecuteAsync(server.ConnectionString, "select pg_reload_conf()");

        await LiveWait.UntilAsync(
            async () => await LiveWait.ScalarAsync<string>(server.ConnectionString, "select current_setting('max_slot_wal_keep_size')") == value,
            TimeSpan.FromSeconds(20),
            "max_slot_wal_keep_size to read " + value);
    }

    /// <summary>R01's window for a slot at 80% of its cap, by PostgreSQL 17's inactive_since.</summary>
    private static TimeSpan ReplicationSlotCheckWindow => ReplicationSlotCheck.CapWindow;

    /// <summary>How long, by Postgres's clock, <paramref name="slot" /> has been without a consumer (PostgreSQL 17+).</summary>
    private async Task<double> InactiveSecondsAsync(string slot) =>
        await LiveWait.ScalarAsync<double>(
            server.ConnectionString,
            "select extract(epoch from (now() - inactive_since))::float8 from pg_replication_slots where slot_name = @slot",
            ("slot", slot));

    /// <summary>Drops a slot if it is there, and checkpoints so that the WAL it held goes.</summary>
    private async Task DropSlotAsync(string slot)
    {
        await ExecuteAsync(server.ConnectionString,
            "select pg_drop_replication_slot(slot_name) from pg_replication_slots where slot_name = '" + slot + "'");
        await ExecuteAsync(server.ConnectionString, "checkpoint");
    }
}

/// <summary>
/// A Postgres server of the class's own for R01, S02 and S03: <c>wal_level = logical</c>, so a logical slot can be made;
/// data checksums on (<c>initdb --data-checksums</c>), so a corrupted page is noticed; and durable - <c>fsync</c>,
/// <c>full_page_writes</c> and <c>synchronous_commit</c> back on after the Testcontainers module turns them off, a later
/// <c>-c</c> on the command line winning over an earlier one. Never reused: its settings and its slots are its own.
/// </summary>
public sealed class ServerSafetyServerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithEnvironment("POSTGRES_INITDB_ARGS", "--data-checksums")
        .WithCommand("-c", "wal_level=logical")
        .WithCommand("-c", "fsync=on")
        .WithCommand("-c", "full_page_writes=on")
        .WithCommand("-c", "synchronous_commit=on")
        .WithReuse(false)
        .Build();

    private bool started;

    /// <summary>The server's superuser connection string.</summary>
    public string ConnectionString => started
        ? container.GetConnectionString()
        : throw new InvalidOperationException("The server-safety server is not running; a test that needs it must be a [PostgresFact].");

    /// <summary>Runs a command inside the container.</summary>
    public Task<DotNet.Testcontainers.Containers.ExecResult> ExecAsync(params string[] command) =>
        container.ExecAsync(command, TestContext.Current.CancellationToken);

    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await container.StartAsync();
        started = true;
    }

    public async ValueTask DisposeAsync() => await container.DisposeAsync();
}
