using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Integration.Tests.Replication;

/// <summary>
/// R02 and R04 on servers of their own: an archive command that fails, one that hangs and one that succeeds; a
/// <c>max_wal_size</c> small enough that bulk writes request checkpoint after checkpoint, on 17 and on 18; and a server that
/// archives nothing and checkpoints on time.
/// </summary>
/// <remarks>
/// Two sweeps an interval apart are two reports on a clock the test moves (<see cref="ManualClock" />): the windows R02 and
/// R04 judge are the advisor's own samples. Nothing here runs <c>CHECKPOINT</c>: a requested checkpoint must be one WAL asked
/// for. The two backlogs keep a gibibyte of WAL each that nothing archives, so the class runs in the heavy collection, before
/// or after the standby pairs and never beside them (W5-R2-fix 8).
/// </remarks>
[Collection(ReplicationHeavySuite.Name)]
public class ArchiverAndCheckpointLiveTests
{
    private static readonly StudioScope Scope = new("default", "default", null);

    public static TheoryData<string> Images => [ReplicationServer.DefaultImage, ReplicationServer.Image18];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<HealthReport> ReportAsync(StudioHost host) =>
        host.CircuitAsync("admin", services => services.GetRequiredService<IHealthService>().GetReportAsync(Scope, HealthReportRequest.Regular, Token));

    /// <summary>Sets <c>archive_command</c> with <c>ALTER SYSTEM</c> and waits until a new session sees it.</summary>
    private static async Task SetArchiveCommandAsync(ReplicationServer server, string command)
    {
        await server.ExecuteAsync("alter system set archive_command = '" + command + "'");
        await server.ExecuteAsync("select pg_catalog.pg_reload_conf()");
        await ReplicationServer.UntilAsync(
            async () => await server.ScalarAsync<string>("select setting from pg_catalog.pg_settings where name = 'archive_command'") == command,
            TimeSpan.FromSeconds(30),
            "archive_command to be '" + command + "'");
    }

    // ---- R02 ------------------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("R02")]
    public async Task An_archive_command_that_fails_is_failing_and_critical()
    {
        await using ReplicationServer server = await ReplicationServer.StartAsync(ReplicationServer.DefaultImage, null, null, "archive_mode=on", "archive_command=/bin/false");

        await server.SwitchSegmentsAsync(2);
        await ReplicationServer.UntilAsync(
            async () => await server.ScalarAsync<long>("select failed_count from pg_catalog.pg_stat_archiver") > 0,
            TimeSpan.FromSeconds(60),
            "the archiver to count a failure");

        await using StudioHost host = server.Host(MatrixRole.App, TimeProvider.System);
        HealthReportFinding finding = (await ReportAsync(host)).Findings.Should().ContainSingle(static x => x.Code == "R02").Subject;

        finding.Finding.Variant.Should().Be("failing");
        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Finding.Title.Should().Be("WAL archiving is failing");
        finding.Evidence.Should().Contain(new HealthEvidence("Last successful archive", "none since the archiver's statistics began"));
        finding.Evidence.Should().Contain(static x => x.Label == "Last failed WAL file" && x.Value.StartsWith("00000001", StringComparison.Ordinal));
        finding.Evidence.Should().Contain(new HealthEvidence("archive_command's text", "/bin/false", HealthGate.Activity),
            "every capability is on and no write policy is set, so the activity gate passes");
        finding.Finding.Remedy.Should().BeNull();
    }

    [PostgresFact]
    [HealthFires("R02")]
    public async Task A_command_that_archived_and_then_hung_counts_no_failure_and_is_a_growing_distance()
    {
        // Archiving with /bin/true first, so there is a last archived segment to measure from, and hung only once it is up:
        // the image's entrypoint stops a first server of its own after initdb, and a hung archiver never lets it stop
        // (W5-R2-fix 9 - the distance path; the next test is the path with nothing archived). Both commands are set with
        // ALTER SYSTEM, never on the command line, which outranks postgresql.auto.conf and would keep the first one for good.
        await using ReplicationServer server = await ReplicationServer.StartAsync(ReplicationServer.DefaultImage, null, null, "archive_mode=on");
        await SetArchiveCommandAsync(server, "/bin/true");

        await server.SwitchSegmentsAsync(2);
        await ReplicationServer.UntilAsync(
            async () => await server.ScalarAsync<long>("select archived_count from pg_catalog.pg_stat_archiver") >= 2,
            TimeSpan.FromSeconds(60),
            "the archiver to archive two segments");

        await SetArchiveCommandAsync(server, "sleep 3600");

        ManualClock clock = new(DateTimeOffset.UtcNow);

        // As a member of pg_monitor, so the evidence counts the files waiting in archive_status.
        await using StudioHost host = server.Host(MatrixRole.Monitor, clock);

        HealthReport first = await ReportAsync(host);
        first.Findings.Should().NotContain(static x => x.Code == "R02", "one sample is watching");

        await server.SwitchSegmentsAsync(70);
        clock.Advance(TimeSpan.FromMinutes(11));

        HealthReportFinding finding = (await ReportAsync(host)).Findings.Should().ContainSingle(static x => x.Code == "R02").Subject;

        finding.Finding.Variant.Should().Be("backlog");
        finding.Severity.Should().Be(HealthSeverity.Warning, "the last success is minutes old, not an hour");
        finding.Finding.Title.Should().MatchRegex(@"^WAL archiving is 1\.\d GiB behind, and falling further behind$");
        finding.Evidence.Single(static x => x.Label == "Last archived WAL file").Value.Should().StartWith("00000001");
        finding.Evidence.Single(static x => x.Label == "Failed attempts").Value.Should().StartWith("0 ");
        finding.Evidence.Single(static x => x.Label == "Waiting in pg_wal").Value.Should().MatchRegex(@"^\d+ files ready for the archiver; pg_wal holds");
        finding.Finding.Caveat.Should().Contain("from the last segment archived to the one being written");

        (await server.ScalarAsync<long>("select failed_count from pg_catalog.pg_stat_archiver")).Should().Be(0, "a hung command never fails");
        TestContext.Current.TestOutputHelper?.WriteLine("disk at the peak (pg_wal, KiB): {0}", await server.WalKibAsync());
    }

    [PostgresFact]
    [HealthFires("R02")]
    public async Task An_empty_archive_command_archives_nothing_and_is_a_backlog_once_the_written_wal_has_grown_for_ten_minutes()
    {
        // archive_mode = on with an empty archive_command: every segment is kept, nothing is archived and nothing fails.
        await using ReplicationServer server = await ReplicationServer.StartAsync(ReplicationServer.DefaultImage, null, null, "archive_mode=on", "archive_command=");
        ManualClock clock = new(DateTimeOffset.UtcNow);
        await using StudioHost host = server.Host(MatrixRole.Monitor, clock);

        HealthReport first = await ReportAsync(host);
        first.Findings.Should().NotContain(static x => x.Code == "R02", "one sample is watching");

        await server.SwitchSegmentsAsync(70);
        clock.Advance(TimeSpan.FromMinutes(11));

        HealthReportFinding finding = (await ReportAsync(host)).Findings.Should().ContainSingle(static x => x.Code == "R02").Subject;

        finding.Finding.Variant.Should().Be("backlog");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Title.Should().StartWith("Nothing has been archived while");
        finding.Evidence.Should().Contain(new HealthEvidence("archive_command", "empty: Postgres keeps every WAL segment and archives none"));
        finding.Evidence.Single(static x => x.Label == "Failed attempts").Value.Should().StartWith("0 ");

        (await server.ScalarAsync<long>("select failed_count from pg_catalog.pg_stat_archiver")).Should().Be(0, "an empty command never fails");
        TestContext.Current.TestOutputHelper?.WriteLine("disk at the peak (pg_wal, KiB): {0}", await server.WalKibAsync());
    }

    [PostgresFact]
    [HealthQuiet("R02")]
    public async Task An_archive_command_that_succeeds_is_quiet()
    {
        await using ReplicationServer server = await ReplicationServer.StartAsync(ReplicationServer.DefaultImage, null, null, "archive_mode=on", "archive_command=/bin/true");

        await server.SwitchSegmentsAsync(3);
        await ReplicationServer.UntilAsync(
            async () => await server.ScalarAsync<long>("select archived_count from pg_catalog.pg_stat_archiver") >= 3,
            TimeSpan.FromSeconds(60),
            "the archiver to archive the switched segments");

        await using StudioHost host = server.Host(MatrixRole.App, TimeProvider.System);
        HealthReport report = await ReportAsync(host);

        report.Findings.Should().NotContain(static x => x.Code == "R02");
        report.Checks.Single(static x => x.Code == "R02").Note.Should().StartWith("archive_mode is on; the archiver last archived");
    }

    // ---- R04 ------------------------------------------------------------------------------------

    [PostgresTheory]
    [MemberData(nameof(Images))]
    [HealthFires("R04")]
    public async Task Bulk_writes_past_a_tiny_max_wal_size_request_checkpoints_and_fire_with_max_wal_size_doubled(string image)
    {
        await using ReplicationServer server = await ReplicationServer.StartAsync(image, null, null, "max_wal_size=32MB", "min_wal_size=32MB", "checkpoint_timeout=300");
        ManualClock clock = new(DateTimeOffset.UtcNow);
        await using StudioHost host = server.Host(MatrixRole.App, clock);

        // Both images are 17 or later, where the checkpointer keeps its own view.
        const string requested = "select num_requested from pg_catalog.pg_stat_checkpointer";

        HealthReport first = await ReportAsync(host);
        first.Findings.Should().NotContain(static x => x.Code == "R04", "one sample is watching");

        long before = await server.ScalarAsync<long>(requested);
        await server.ExecuteAsync("create table public.bulk (id int, pad text)");

        // A batch at a time, truncated after each, so the table never holds more than one batch: the WAL is what requests
        // the checkpoints, and a table that kept every batch would be a few hundred megabytes of disk for nothing (W5-R2-fix 8).
        await ReplicationServer.UntilAsync(
            async () =>
            {
                await server.ExecuteAsync("insert into public.bulk select g, repeat('x', 200) from pg_catalog.generate_series(1, 200000) g; truncate public.bulk");
                return await server.ScalarAsync<long>(requested) - before >= 12;
            },
            TimeSpan.FromMinutes(3),
            "twelve requested checkpoints");

        TestContext.Current.TestOutputHelper?.WriteLine("disk after the batches (pg_wal, KiB): {0}", await server.WalKibAsync());

        clock.Advance(TimeSpan.FromMinutes(2));

        HealthReportFinding finding = (await ReportAsync(host)).Findings.Should().ContainSingle(static x => x.Code == "R04").Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Title.Should().Contain("were requested rather than timed");
        finding.Finding.Remedy!.Code.Should().EndWith("max_wal_size = 64MB");
        finding.Finding.Remedy.Explanation.Should().Contain("Double it (from 32MB)").And.EndWith("Set it in postgresql.conf, followed by a reload.");
        finding.Evidence.Should().Contain(new HealthEvidence("Counted in", "pg_stat_checkpointer"));
    }

    [PostgresFact]
    [HealthQuiet("R04")]
    [HealthQuiet("R02")]
    public async Task A_server_that_archives_nothing_and_checkpoints_on_time_is_quiet()
    {
        await using ReplicationServer server = await ReplicationServer.StartAsync(ReplicationServer.DefaultImage, null, null);
        ManualClock clock = new(DateTimeOffset.UtcNow);
        await using StudioHost host = server.Host(MatrixRole.App, clock);

        await ReportAsync(host);
        await server.ExecuteAsync("create table public.light (id int); insert into public.light select pg_catalog.generate_series(1, 1000)");
        clock.Advance(TimeSpan.FromMinutes(2));

        HealthReport report = await ReportAsync(host);

        report.Findings.Should().NotContain(static x => x.Code == "R02" || x.Code == "R04");
        report.Checks.Single(static x => x.Code == "R04").Note.Should().StartWith("In the last 2 min, 0 requested checkpoints of");
        report.Checks.Single(static x => x.Code == "R02").Note.Should().Be("archive_mode is off: this server archives no WAL, so there is nothing to fail.");
    }
}
