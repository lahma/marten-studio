using System.Text.RegularExpressions;

using MartenStudio.Services.Health;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Acceptance 4: <c>pg_stat_activity</c>, <c>pg_locks</c> and <c>pg_prepared_xacts</c> - the process array, the
/// lock table and the two-phase state - are read on no five-second poll: only by a health sweep, at
/// <c>HealthInterval</c>, and by Database › Sessions, at the same interval or on Refresh behind a shared ten-second
/// floor.
/// </summary>
/// <remarks>
/// <para>
/// <c>pg_stat_database</c> is not among them. It is one row of cumulative statistics per database, read from the
/// statistics snapshot rather than from shared process state, and other health builders read it too (H4's table
/// checks take <c>stats_reset</c> from it); C08 reads it in the same sweep.
/// </para>
/// <para>
/// Four nets, because the rule can be broken in four places. A statement that reads one of the views can be
/// written only in <c>Internal/Sql/HealthConnectionQueries.cs</c>; that builder is called only by the connection
/// checks and the Sessions reader; the Sessions service is reached only from the Sessions page; and that page's
/// loop runs at <c>HealthInterval</c>. The checks themselves run in the regular sweep, which nothing but a page's
/// own <c>HealthInterval</c> loop starts (<c>HealthSweepCacheTests</c>), and the navigation badge reads only the
/// last sweep.
/// </para>
/// <para>
/// The first net reads source text, so it carries an anti-vacuity theory of its own: it must catch a read and
/// leave prose that merely names a view alone.
/// </para>
/// </remarks>
public class ActivityViewsOffThePollPathTests
{
    private const string Builder = "src/MartenStudio/Internal/Sql/HealthConnectionQueries.cs";

    /// <summary>
    /// The daemon checks' builder: D02 reads the advisory locks in <c>pg_locks</c> and D01's Explain reads
    /// <c>pg_stat_activity</c>, <c>pg_locks</c> and <c>pg_prepared_xacts</c>. Neither is on a poll path either:
    /// D02 runs in the regular sweep, Explain behind its button through a 60-second single-flight cache, and the
    /// Projections screen's own tests (<c>ProjectionsHealthPageTests</c>) hold its 5-second poll to the cache.
    /// </summary>
    private const string DaemonBuilder = "src/MartenStudio/Internal/Sql/HealthDaemonQueries.cs";

    /// <summary>SQL that reads one of the views: from or join it, or call <c>pg_blocking_pids</c>.</summary>
    internal static readonly Regex ReadsAnActivityView = new(
        @"\b(from|join)\s+(pg_catalog\.)?pg_(stat_activity|locks|prepared_xacts)\b(?!\.)|\bpg_blocking_pids\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void Only_the_connection_builder_writes_a_statement_that_reads_the_activity_views()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty();

        List<string> offenders =
        [
            .. files
                .Where(static file => SourceScanner.Relative(file) is not (Builder or DaemonBuilder))
                .Where(static file => ReadsAnActivityView.IsMatch(File.ReadAllText(file)))
                .Select(SourceScanner.Relative),
        ];

        offenders.Should().BeEmpty("a read of the process array or the lock table is written in one place, which is reviewed for its callers");

        ReadsAnActivityView.IsMatch(File.ReadAllText(RepositoryRoot.Combine(Builder.Split('/'))))
            .Should().BeTrue("the builder itself reads them, so a scan that found nothing there would be a scan that finds nothing");
        ReadsAnActivityView.IsMatch(File.ReadAllText(RepositoryRoot.Combine(DaemonBuilder.Split('/'))))
            .Should().BeTrue("the daemon builder reads pg_locks for D02, so its exemption is not an exemption for nothing");
    }

    [Fact]
    public void The_builder_is_called_only_by_the_connection_checks_and_the_sessions_reader()
    {
        List<string> callers =
        [
            .. SourceScanner.ShippedSourceFiles()
                .Where(static file => SourceScanner.Relative(file) != Builder)
                .Where(static file => SourceScanner.StripCommentsAndStrings(File.ReadAllText(file)).Contains("HealthConnectionQueries.", StringComparison.Ordinal))
                .Select(SourceScanner.Relative),
        ];

        callers.Should().NotBeEmpty();
        callers.Should().OnlyContain(
            static x => x.StartsWith("src/MartenStudio/Services/Health/Checks/", StringComparison.Ordinal)
                || x == "src/MartenStudio/Services/Database/SessionActivityReader.cs",
            "every other caller would be a new path to the process array and the lock table");
    }

    [Fact]
    public void The_sessions_service_is_reached_only_from_the_sessions_page()
    {
        List<string> users =
        [
            .. SourceScanner.ShippedSourceFiles()
                .Where(static file => SourceScanner.StripCommentsAndStrings(File.ReadAllText(file)).Contains("ISessionActivityService", StringComparison.Ordinal))
                .Select(SourceScanner.Relative),
        ];

        users.Should().Contain("src/MartenStudio/Components/Pages/Database/Sessions.razor");
        users.Should().OnlyContain(
            static x => x == "src/MartenStudio/Components/Pages/Database/Sessions.razor"
                || x == "src/MartenStudio/MartenStudioServiceCollectionExtensions.cs"
                || x.StartsWith("src/MartenStudio/Services/Database/", StringComparison.Ordinal),
            "the sidebar, the Overview and every other page poll every RefreshInterval");
    }

    [Fact]
    public void The_sessions_page_polls_at_the_health_interval_and_never_the_refresh_interval()
    {
        string page = SourceScanner.StripCommentsAndStrings(
            File.ReadAllText(RepositoryRoot.Combine("src", "MartenStudio", "Components", "Pages", "Database", "Sessions.razor")));

        page.Should().Contain("EffectiveHealthInterval");
        page.Should().NotContain("RefreshInterval");
    }

    [Fact]
    public void Every_connection_check_runs_in_the_regular_sweep_at_the_health_interval()
    {
        IReadOnlySet<HealthCostTier> regular = HealthSweeper.TiersOf(HealthSweepKind.Regular);

        HealthCheckDescriptor[] checks = [.. new HealthCheckRegistry().All.Where(static x => x.Info.Area == HealthArea.Connections)];

        checks.Select(static x => x.Info.Code).Should().Equal("C01", "C02", "C03", "C04", "C05", "C06", "C07", "C08", "C09");
        checks.Should().OnlyContain(x => regular.Contains(x.Info.Tier), "a regular sweep is cached for HealthInterval, never RefreshInterval");
    }

    /// <summary>The anti-vacuity half: the scan catches a read and ignores prose that names a view.</summary>
    [Theory]
    [InlineData("\"select count(*) from pg_stat_activity a\"", true)]
    [InlineData("\"  join pg_locks l on l.pid = a.pid\"", true)]
    [InlineData("\"select * from pg_catalog.pg_prepared_xacts\"", true)]
    [InlineData("\"select pg_blocking_pids (a.pid)\"", true)]
    [InlineData("\"SELECT * FROM PG_STAT_ACTIVITY a\"", true)]
    [InlineData("\"from pg_catalog.pg_stat_database db\"", false)]
    [InlineData("\"Read in pg_stat_activity, for this database only\"", false)]
    [InlineData("\"Waits are measured by pg_locks.waitstart\"", false)]
    [InlineData("/// look at <c>pg_locks</c> from a second connection", false)]
    [InlineData("\"from pg_locks.waitstart\"", false)]
    public void The_scan_finds_a_read_and_leaves_prose_alone(string text, bool found)
    {
        ReadsAnActivityView.IsMatch(text).Should().Be(found);
    }
}
