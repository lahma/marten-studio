using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Integration.Tests.Storage;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Integration.Tests.Health;

/// <summary>
/// C11 on a real PostgreSQL 17 (W5-CAP, issue #8 G5), in a database of each test's own: a million and more blocks read from
/// outside shared buffers fire it once the statistics window has counted a day, and a warm database's index lookups do not.
/// </summary>
/// <remarks>
/// "A day" is the studio's clock set a day and a minute past the window's start, read from the server first - so the blocks
/// a day are the blocks read, nearly to the block. Seeding the history with an older sample cannot lengthen a window the
/// server's own reset times measure; moving the studio's "now" is what does, as C08's live tests move it.
/// </remarks>
public class CacheHitLiveTests(PostgresVersionMatrix matrix)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<HealthCheckResult> SweepADayLaterAsync(MatrixServer server, string database)
    {
        SettableClock clock = new(DateTimeOffset.UtcNow);
        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database, services: s => s.AddSingleton<TimeProvider>(clock));

        StatisticsWindow window = await StorageLive.WindowAsync(host);
        clock.Set(window.Since + StatisticsWindow.Day + TimeSpan.FromMinutes(1));

        HealthSweep sweep = await host.SweepAsync(HealthSweepKind.Regular);
        return sweep.Results.Single(static x => x.Check.Code == "C11");
    }

    [PostgresFact]
    [HealthFires("C11")]
    public async Task A_million_blocks_a_day_read_from_outside_shared_buffers_is_a_warning()
    {
        MatrixServer server = await matrix.ServerAsync(17);
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync("cap_c11_fires", Token);
        await StorageLive.ForceReadsAsync(server.ConnectionString(MatrixRole.App, database), 1_200_000);

        HealthCheckResult c11 = await SweepADayLaterAsync(server, database);

        c11.Outcome.Should().Be(HealthCheckOutcome.Checked, c11.Problem?.Reason);
        HealthFinding finding = c11.Findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Select(static x => x.Label).Should().Contain(["Cache hit ratio", "Blocks read a day", "Window", "Counting since"]);
        finding.Title.Should().Contain("found in shared buffers");
    }

    [PostgresFact]
    [HealthQuiet("C11")]
    public async Task A_warm_database_read_through_its_index_is_quiet()
    {
        MatrixServer server = await matrix.ServerAsync(17);
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync("cap_c11_quiet", Token);
        await StorageLive.WarmAsync(server.ConnectionString(MatrixRole.App, database));

        HealthCheckResult c11 = await SweepADayLaterAsync(server, database);

        c11.Outcome.Should().Be(HealthCheckOutcome.Checked, c11.Problem?.Reason);
        c11.Findings.Should().BeEmpty();
        c11.Note.Should().Contain("judged only below 95.0% with at least 1,000,000 blocks read a day");
    }

    [PostgresFact]
    public async Task Before_a_day_of_statistics_it_watches_and_says_how_long_so_far()
    {
        MatrixServer server = await matrix.ServerAsync(17);
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync("cap_c11_watching", Token);
        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);

        HealthSweep sweep = await host.SweepAsync(HealthSweepKind.Regular);
        HealthCheckResult c11 = sweep.Results.Single(static x => x.Check.Code == "C11");

        c11.Findings.Should().BeEmpty();
        c11.Note.Should().StartWith("Watching: ").And.Contain("of statistics so far");
    }
}
