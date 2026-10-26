using System.Text.RegularExpressions;

using PostgresStudio.Services.Health;

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
    private const string Builder = "src/PostgresStudio/Internal/Sql/HealthConnectionQueries.cs";

    /// <summary>
    /// The daemon checks' builder: D02 reads the advisory locks in <c>pg_locks</c> and D01's Explain reads
    /// <c>pg_stat_activity</c>, <c>pg_locks</c> and <c>pg_prepared_xacts</c>. Neither is on a poll path either:
    /// D02 runs in the regular sweep, Explain behind its button through a 60-second single-flight cache, and the
    /// Projections screen's own tests (<c>ProjectionsHealthPageTests</c>) hold its 5-second poll to the cache.
    /// </summary>
    private const string DaemonBuilder = "src/MartenStudio/Internal/Sql/HealthDaemonQueries.cs";

    /// <summary>
    /// The horizon checks' builder (packet H9): T14 reads <c>pg_stat_activity</c>, <c>pg_locks</c> and
    /// <c>pg_prepared_xacts</c> for the age of what holds the vacuum horizon back. It needed a read of its own rather than
    /// H3's because the horizon is a transaction id: H3's statements say whether a session has one, and this one how old it
    /// is, across this database's sessions, the oldest of the other databases' and the prepared transactions. It runs only
    /// in the regular sweep, at <c>HealthInterval</c>, and only T14 calls it (the third net below), with H3's own session
    /// predicate (<c>HealthConnectionQueries.ClientSession</c>), which is why it is the one builder that may name H3's.
    /// </summary>
    private const string HorizonBuilder = "src/PostgresStudio/Internal/Sql/HealthHorizonQueries.cs";

    /// <summary>
    /// The table checks' builder (W5-VERS): its index read takes one snapshot of <c>pg_locks</c> for an
    /// <c>AccessExclusiveLock</c> held or awaited on a listed table, so that it never deparses an index of a table a
    /// migration is altering (<c>pg_get_indexdef</c> opens the table and would queue behind it). It runs only in the
    /// regular sweep, at <c>HealthInterval</c>, through the sweep's shared index read - which only the health checks call
    /// (the net below).
    /// </summary>
    private const string TableBuilder = "src/PostgresStudio/Internal/Sql/HealthTableQueries.cs";

    /// <summary>
    /// The replication builder (W5-R2-fix B1): its standby read joins <c>pg_stat_activity</c> on the WAL senders' pids for
    /// one thing only - whether a sender is physical, or a logical one of this database or another - so that another
    /// database's logical sender is never named. It runs in R03's regular sweep and in the Replication page's facts read,
    /// which the page polls at <c>HealthInterval</c> through a cache of that time to live (the nets below).
    /// </summary>
    private const string ReplicationBuilder = "src/PostgresStudio/Internal/Sql/ReplicationQueries.cs";

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
                .Where(static file => SourceScanner.Relative(file) is not (Builder or DaemonBuilder or HorizonBuilder or TableBuilder or ReplicationBuilder))
                .Where(static file => ReadsAnActivityView.IsMatch(File.ReadAllText(file)))
                .Select(SourceScanner.Relative),
        ];

        offenders.Should().BeEmpty("a read of the process array or the lock table is written in one place, which is reviewed for its callers");

        ReadsAnActivityView.IsMatch(File.ReadAllText(RepositoryRoot.Combine(Builder.Split('/'))))
            .Should().BeTrue("the builder itself reads them, so a scan that found nothing there would be a scan that finds nothing");
        ReadsAnActivityView.IsMatch(File.ReadAllText(RepositoryRoot.Combine(DaemonBuilder.Split('/'))))
            .Should().BeTrue("the daemon builder reads pg_locks for D02, so its exemption is not an exemption for nothing");
        ReadsAnActivityView.IsMatch(File.ReadAllText(RepositoryRoot.Combine(HorizonBuilder.Split('/'))))
            .Should().BeTrue("the horizon builder reads pg_stat_activity and pg_prepared_xacts for T14, so its exemption is not an exemption for nothing");
        ReadsAnActivityView.IsMatch(File.ReadAllText(RepositoryRoot.Combine(TableBuilder.Split('/'))))
            .Should().BeTrue("the table builder reads pg_locks for its index read, so its exemption is not an exemption for nothing");
        ReadsAnActivityView.IsMatch(File.ReadAllText(RepositoryRoot.Combine(ReplicationBuilder.Split('/'))))
            .Should().BeTrue("the replication builder joins pg_stat_activity for the senders' databases, so its exemption is not an exemption for nothing");
    }

    [Fact]
    public void The_replication_builders_standby_read_is_called_only_by_the_standby_facts()
    {
        List<string> callers =
        [
            .. SourceScanner.ShippedSourceFiles()
                .Where(static file => SourceScanner.Relative(file) != ReplicationBuilder)
                .Where(static file => SourceScanner.StripCommentsAndStrings(File.ReadAllText(file)).Contains("ReplicationQueries.Standbys(", StringComparison.Ordinal))
                .Select(SourceScanner.Relative),
        ];

        callers.Should().Equal(["src/PostgresStudio/Services/Replication/ReplicationFacts.cs"],
            "the standby read is StandbySnapshot's, which R03's regular sweep and the page's cached facts read share");
    }

    [Fact]
    public void The_replication_page_polls_at_the_health_interval_through_a_cache_of_that_time_to_live()
    {
        string page = SourceScanner.StripCommentsAndStrings(
            File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "Components", "Pages", "Replication", "Replication.razor")));
        string cache = SourceScanner.StripCommentsAndStrings(
            File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "Services", "Replication", "ReplicationFactsReader.cs")));

        page.Should().Contain("EffectiveHealthInterval").And.NotContain("RefreshInterval");
        cache.Should().Contain("new StudioSnapshotCache(timeProvider ?? TimeProvider.System, settings.EffectiveHealthInterval)");

        HealthCheckDescriptor r03 = Health.StudioHealthChecks.Registry.Find("R03")!;
        HealthSweeper.TiersOf(HealthSweepKind.Regular).Should().Contain(r03.Info.Tier, "R03 runs in the regular sweep, cached for HealthInterval");
    }

    [Fact]
    public void The_table_builders_index_read_is_called_only_by_the_health_checks()
    {
        List<string> callers =
        [
            .. SourceScanner.ShippedSourceFiles()
                .Where(static file => SourceScanner.Relative(file) != TableBuilder)
                .Where(static file => SourceScanner.StripCommentsAndStrings(File.ReadAllText(file)).Contains("HealthTableQueries.Indexes(", StringComparison.Ordinal))
                .Select(SourceScanner.Relative),
        ];

        callers.Should().NotBeEmpty("a scan that found no caller would pass for the wrong reason");
        callers.Should().OnlyContain(
            static x => StudioProjects.IsUnder(x, "Services", "Health", "Checks"),
            "the index read's pg_locks snapshot is on the regular sweep's path alone");
    }

    [Fact]
    public void The_horizon_builder_is_called_only_by_the_horizon_checks()
    {
        List<string> callers =
        [
            .. SourceScanner.ShippedSourceFiles()
                .Where(static file => SourceScanner.Relative(file) != HorizonBuilder)
                .Where(static file => SourceScanner.StripCommentsAndStrings(File.ReadAllText(file)).Contains("HealthHorizonQueries.", StringComparison.Ordinal))
                .Select(SourceScanner.Relative),
        ];

        callers.Should().NotBeEmpty("a scan that found no caller would pass for the wrong reason");
        callers.Should().OnlyContain(
            static x => StudioProjects.IsUnder(x, "Services", "Health", "Checks"),
            "every other caller would be a new path to the process array and the lock table");
    }

    [Fact]
    public void The_horizon_checks_run_in_the_regular_sweep_at_the_health_interval()
    {
        IReadOnlySet<HealthCostTier> regular = HealthSweeper.TiersOf(HealthSweepKind.Regular);

        HealthCheckDescriptor[] checks = [.. Health.StudioHealthChecks.Registry.All.Where(static x => x.Info.Code is "T12" or "T13" or "T14")];

        checks.Should().HaveCount(3);
        checks.Should().OnlyContain(x => regular.Contains(x.Info.Tier), "a regular sweep is cached for HealthInterval, never RefreshInterval");
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
        callers.Should().Contain("src/PostgresStudio/Services/Database/SessionActivityReader.cs", "the Sessions reader is a caller, so the scan reads the shared studio");
        callers.Should().OnlyContain(
            static x => StudioProjects.IsUnder(x, "Services", "Health", "Checks")
                || x == "src/PostgresStudio/Services/Database/SessionActivityReader.cs"
                || x == HorizonBuilder,
            "every other caller would be a new path to the process array and the lock table; the horizon builder takes only H3's session predicate");
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

        users.Should().Contain("src/PostgresStudio/Components/Pages/Database/Sessions.razor");
        users.Should().OnlyContain(
            static x => x == "src/PostgresStudio/Components/Pages/Database/Sessions.razor"
                || x == PostgresOverview
                || x == "src/PostgresStudio/Services/Settings/StudioCoreRegistration.cs"
                || StudioProjects.IsUnder(x, "Services", "Database"),
            "the sidebar, MartenStudio's Overview and every other page poll every RefreshInterval; PostgresStudio's Overview "
            + "reads the summary at HealthInterval only (the fact below)");
    }

    /// <summary>PostgresStudio's Overview, the one page besides Sessions that reads the sessions summary (PS-3a).</summary>
    private const string PostgresOverview = "src/PostgresStudio/Components/Pages/PostgresOverview.razor";

    /// <summary>
    /// PostgresStudio's Overview shows the connection counts, and reads them as Sessions does: the summary is asked in
    /// one method, <c>LoadAtHealthIntervalAsync</c>, which the five-second tick calls only once <c>EffectiveHealthInterval</c>
    /// has passed - so the process array is never read on the poll, and the shared ten-second snapshot floor still holds.
    /// </summary>
    [Fact]
    public void PostgresStudios_overview_reads_the_sessions_summary_at_the_health_interval_only()
    {
        string page = SourceScanner.StripCommentsAndStrings(File.ReadAllText(RepositoryRoot.Combine(PostgresOverview.Split('/'))));

        Regex.Matches(page, @"\.GetSummaryAsync\(").Should().ContainSingle("the summary is asked in one place");

        int method = page.IndexOf("private async Task LoadAtHealthIntervalAsync(", StringComparison.Ordinal);
        method.Should().BePositive();
        int next = page.IndexOf("private ", method + 1, StringComparison.Ordinal);
        page[method..next].Should().Contain(".GetSummaryAsync(", "that place is the health-interval load");

        int tick = page.IndexOf("private async Task TickAsync(", StringComparison.Ordinal);
        tick.Should().BePositive();
        string tickBody = page[tick..page.IndexOf("private ", tick + 1, StringComparison.Ordinal)];
        tickBody.IndexOf("EffectiveHealthInterval", StringComparison.Ordinal).Should().BePositive()
            .And.BeLessThan(tickBody.IndexOf("LoadAtHealthIntervalAsync(", StringComparison.Ordinal), "the tick asks only once the interval has passed");
    }

    [Fact]
    public void The_sessions_page_polls_at_the_health_interval_and_never_the_refresh_interval()
    {
        string page = SourceScanner.StripCommentsAndStrings(
            File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "Components", "Pages", "Database", "Sessions.razor")));

        page.Should().Contain("EffectiveHealthInterval");
        page.Should().NotContain("RefreshInterval");
    }

    [Fact]
    public void Every_connection_check_runs_in_the_regular_sweep_at_the_health_interval()
    {
        IReadOnlySet<HealthCostTier> regular = HealthSweeper.TiersOf(HealthSweepKind.Regular);

        HealthCheckDescriptor[] checks = [.. Health.StudioHealthChecks.Registry.All.Where(static x => x.Info.Area == HealthArea.Connections)];

        // C10 (H10) reads no activity view - only pg_backend_pid(), twice - and runs in the same regular sweep.
        checks.Select(static x => x.Info.Code).Should().Equal("C01", "C02", "C03", "C04", "C05", "C06", "C07", "C08", "C09", "C10");
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
