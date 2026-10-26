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
/// for.
/// </remarks>
public class ArchiverAndCheckpointLiveTests
{
    private static readonly StudioScope Scope = new("default", "default", null);

    public static TheoryData<string> Images => [ReplicationServer.DefaultImage, ReplicationServer.Image18];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<HealthReport> ReportAsync(StudioHost host) =>
        host.CircuitAsync("admin", services => services.GetRequiredService<IHealthService>().GetReportAsync(Scope, HealthReportRequest.Regular, Token));

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
    public async Task A_hung_archive_command_counts_no_failure_and_is_a_backlog_once_it_has_grown_for_ten_minutes()
    {
        // Started with an empty command - which archives nothing and keeps every segment - and hung only once it is up: the
        // image's entrypoint stops a first server of its own after initdb, and a hung archiver never lets it stop.
        await using ReplicationServer server = await ReplicationServer.StartAsync(ReplicationServer.DefaultImage, null, null, "archive_mode=on", "archive_command=");
        await server.ExecuteAsync("alter system set archive_command = 'sleep 3600'");
        await server.ExecuteAsync("select pg_catalog.pg_reload_conf()");

        ManualClock clock = new(DateTimeOffset.UtcNow);

        // As a member of pg_monitor, so the evidence counts the files waiting in archive_status.
        await using StudioHost host = server.Host(MatrixRole.Monitor, clock);

        HealthReport first = await ReportAsync(host);
        first.Findings.Should().NotContain(static x => x.Code == "R02", "one sample is watching");

        await server.SwitchSegmentsAsync(75);
        clock.Advance(TimeSpan.FromMinutes(11));

        HealthReportFinding finding = (await ReportAsync(host)).Findings.Should().ContainSingle(static x => x.Code == "R02").Subject;

        finding.Finding.Variant.Should().Be("backlog");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Title.Should().StartWith("Nothing has been archived while");
        finding.Evidence.Single(static x => x.Label == "Failed attempts").Value.Should().StartWith("0 ");
        finding.Evidence.Single(static x => x.Label == "Waiting in pg_wal").Value.Should().MatchRegex(@"^\d+ files ready for the archiver; pg_wal holds");

        (await server.ScalarAsync<long>("select failed_count from pg_catalog.pg_stat_archiver")).Should().Be(0, "a hung command never fails");
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

        await ReplicationServer.UntilAsync(
            async () =>
            {
                await server.ExecuteAsync("insert into public.bulk select g, repeat('x', 200) from pg_catalog.generate_series(1, 200000) g");
                return await server.ScalarAsync<long>(requested) - before >= 12;
            },
            TimeSpan.FromMinutes(3),
            "twelve requested checkpoints");

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
