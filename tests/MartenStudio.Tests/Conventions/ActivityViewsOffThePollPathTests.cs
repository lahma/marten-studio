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
    private const string Builder = "src/PostgresStudio.Diagnostics/Internal/Sql/HealthConnectionQueries.cs";

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
    private const string HorizonBuilder = "src/PostgresStudio.Diagnostics/Internal/Sql/HealthHorizonQueries.cs";

    /// <summary>
    /// The table checks' builder (W5-VERS). Its index read never deparses an index of a table a migration is altering
    /// (<c>pg_get_indexdef</c> opens the table and would queue behind it), and until X3 took its own snapshot of
    /// <c>pg_locks</c> to know which: from 0.6 the sweep's one read of the lock table (<c>HealthConnectionQueries.LockTable</c>)
    /// hands it the relations under an <c>AccessExclusiveLock</c> as a parameter, so it reads no activity view and needs no
    /// exemption. It still runs only in the regular sweep, at <c>HealthInterval</c>, through the sweep's shared index read -
    /// which only the health checks call (the net below).
    /// </summary>
    private const string TableBuilder = "src/PostgresStudio.Diagnostics/Internal/Sql/HealthTableQueries.cs";

    /// <summary>
    /// The maintenance builder (W5-V1): T16 counts the autovacuum workers in <c>pg_stat_activity</c> and reads what an
    /// autovacuum in the progress views is from it. It runs only in the regular sweep, at <c>HealthInterval</c>, through the
    /// sweep's two shared maintenance reads - which only T16 calls (the net below) - and the Maintenance page reads the sweep's
    /// memo of them, never the builder.
    /// </summary>
    private const string MaintenanceBuilder = "src/PostgresStudio.Diagnostics/Internal/Sql/MaintenanceQueries.cs";

    /// <summary>
    /// The replication builder (W5-R2-fix B1): its standby read joins <c>pg_stat_activity</c> on the WAL senders' pids for
    /// one thing only - whether a sender is physical, or a logical one of this database or another - so that another
    /// database's logical sender is never named. It runs in R03's regular sweep and in the Replication page's facts read,
    /// which the page polls at <c>HealthInterval</c> through a cache of that time to live (the nets below).
    /// </summary>
    private const string ReplicationBuilder = "src/PostgresStudio.Diagnostics/Internal/Sql/ReplicationQueries.cs";

    /// <summary>
    /// The Aurora builder (W5-AUR, issue #8 A4): its per-session read lists this database's client sessions from
    /// <c>pg_stat_activity</c> to ask <c>aurora_stat_backend_waits(pid)</c> of each. It is called only by the Aurora facts
    /// reader's session read (the net below), which <c>AuroraService</c> calls only for a visitor through the activity gate and
    /// only through <c>AuroraFactsCache</c>'s single flight of <c>HealthInterval</c> time to live, and the Server page that
    /// shows it polls at <c>HealthInterval</c>.
    /// </summary>
    private const string AuroraBuilder = "src/PostgresStudio.Diagnostics/Internal/Sql/AuroraQueries.cs";

    /// <summary>
    /// The connections-by-application builder (W5-API, issue #8 G9): one aggregate of this database's sessions by
    /// <c>application_name</c>, with the server's budget. It is called only by the Sessions reader (the net below), as the
    /// snapshot's fourth statement, so it is read exactly when Database › Sessions' snapshot is - at <c>HealthInterval</c>
    /// or on Refresh, behind the snapshot's ten-second floor - and by the diagnostics API through that same snapshot.
    /// </summary>
    private const string ApplicationsBuilder = "src/PostgresStudio.Diagnostics/Internal/Sql/ConnectionsByApplicationQueries.cs";

    /// <summary>
    /// The connection history's builder (issue #43): it runs the server holders read for the opt-in sampler, once per
    /// <c>PostgresConnectionHistoryOptions.Interval</c> (fifteen seconds at the least, a minute by default) over the one connection the sampler keeps per
    /// server - its own cadence, in the background, never a page's poll.
    /// </summary>
    private const string HistoryBuilder = "src/PostgresStudio.Diagnostics/Internal/Sql/ConnectionHistoryQueries.cs";

    /// <summary>
    /// The Connection page's builder (1.1-NP3): its <c>Applications</c> read lists the studio's own login's sessions in this database
    /// from <c>pg_stat_activity</c>, by application name, and takes H3's session predicate. It is called only by the connection facts
    /// source's applications read (the net below), which the Connection service reaches only past the activity gate, through a
    /// single flight of <c>HealthInterval</c> time to live, and the page that shows it polls at <c>HealthInterval</c>.
    /// </summary>
    private const string ConnectionBuilder = "src/PostgresStudio.Diagnostics/Internal/Sql/ConnectionObservedQueries.cs";

    /// <summary>
    /// The migration notes' builder (1.2-APPLY): its sessions read counts the sessions holding a lock on the tables a previewed
    /// migration names, by state - counts only, never text. It is called only by the Schema service's impact read, after the
    /// Activity gate has passed, and that read runs only when the Schema tab's Preview button is pressed (the net below).
    /// </summary>
    private const string ImpactBuilder = "src/MartenStudio/Internal/Sql/MigrationImpactQueries.cs";

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
                .Where(static file => SourceScanner.Relative(file) is not (Builder or DaemonBuilder or HorizonBuilder or MaintenanceBuilder or ReplicationBuilder or AuroraBuilder or ApplicationsBuilder or ConnectionBuilder or ImpactBuilder))
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
            .Should().BeFalse("the table builder takes the sweep's one read of the lock table as parameters (X3), and has no exemption");
        ReadsAnActivityView.IsMatch(File.ReadAllText(RepositoryRoot.Combine(ReplicationBuilder.Split('/'))))
            .Should().BeTrue("the replication builder joins pg_stat_activity for the senders' databases, so its exemption is not an exemption for nothing");
        ReadsAnActivityView.IsMatch(File.ReadAllText(RepositoryRoot.Combine(AuroraBuilder.Split('/'))))
            .Should().BeTrue("the Aurora builder lists this database's sessions for their waits, so its exemption is not an exemption for nothing");
        ReadsAnActivityView.IsMatch(File.ReadAllText(RepositoryRoot.Combine(ApplicationsBuilder.Split('/'))))
            .Should().BeTrue("the applications builder counts this database's sessions, so its exemption is not an exemption for nothing");
        ReadsAnActivityView.IsMatch(File.ReadAllText(RepositoryRoot.Combine(ConnectionBuilder.Split('/'))))
            .Should().BeTrue("the connection builder lists the login's sessions by application, so its exemption is not an exemption for nothing");
        ReadsAnActivityView.IsMatch(File.ReadAllText(RepositoryRoot.Combine(ImpactBuilder.Split('/'))))
            .Should().BeTrue("the migration notes' builder counts the sessions holding a lock, so its exemption is not an exemption for nothing");
    }

    /// <summary>
    /// 1.2-APPLY: the migration notes' sessions read is called by one place, the Schema service's impact read, and that read asks
    /// the Activity gate - the capability, then the write policy with the capability named - before it opens a connection for it.
    /// The Preview button is the only thing that reaches it.
    /// </summary>
    [Fact]
    public void The_migration_notes_sessions_read_is_called_only_behind_the_activity_gate()
    {
        List<string> callers =
        [
            .. SourceScanner.ShippedSourceFiles()
                .Where(static file => SourceScanner.Relative(file) != ImpactBuilder)
                .Where(static file => SourceScanner.StripCommentsAndStrings(File.ReadAllText(file)).Contains("MigrationImpactQueries.ReadSessionsAsync(", StringComparison.Ordinal))
                .Select(SourceScanner.Relative),
        ];

        callers.Should().Equal(["src/MartenStudio/Services/Schema/SchemaDataService.Impact.cs"],
            "the sessions on a migration's tables are the Schema service's impact read, and nothing else's");

        string service = SourceScanner.StripCommentsAndStrings(
            File.ReadAllText(RepositoryRoot.Combine("src", "MartenStudio", "Services", "Schema", "SchemaDataService.Impact.cs")));
        int method = service.IndexOf("Task<MigrationImpact> ReadImpactAsync(", StringComparison.Ordinal);
        int gate = service.IndexOf("ActivityGateAsync(whole", method, StringComparison.Ordinal);
        int read = service.IndexOf("MigrationImpactQueries.ReadSessionsAsync(", method, StringComparison.Ordinal);

        method.Should().BePositive();
        gate.Should().BePositive();
        read.Should().BeGreaterThan(gate, "the sessions are read only after the activity gate has said yes");

        string gateBody = service[service.IndexOf("Task<(bool Passes, string? Reason)> ActivityGateAsync(", StringComparison.Ordinal)..];
        int enabled = gateBody.IndexOf("capabilities.IsEnabled(StudioCapability.ViewDatabaseActivity)", StringComparison.Ordinal);
        int policy = gateBody.IndexOf("IsAuthorizedAsync(whole, StudioCapability.ViewDatabaseActivity", StringComparison.Ordinal);

        enabled.Should().BePositive();
        policy.Should().BeGreaterThan(enabled, "the capability is asked before anybody's policy");

        string tab = SourceScanner.StripCommentsAndStrings(
            File.ReadAllText(RepositoryRoot.Combine("src", "MartenStudio", "Components", "Pages", "Schema", "DriftTab.razor")));
        tab.Should().NotContain("PeriodicTimer").And.NotContain("RefreshInterval", "the Schema tab's drift section polls nothing: Preview is a button");
    }

    /// <summary>
    /// 1.1-NP3: the Connection page's application-sessions read is called by one place, the connection facts source, which only the
    /// Connection service reaches - and the service reaches it only after the activity gate (capability, store policy, write policy)
    /// has passed, and the page asks the service only when the view says the gate is open.
    /// </summary>
    [Fact]
    public void The_connection_builders_applications_read_is_called_only_behind_the_activity_gate()
    {
        List<string> callers =
        [
            .. SourceScanner.ShippedSourceFiles()
                .Where(static file => SourceScanner.Relative(file) != ConnectionBuilder)
                .Where(static file => SourceScanner.StripCommentsAndStrings(File.ReadAllText(file)).Contains("ConnectionObservedQueries.Applications(", StringComparison.Ordinal))
                .Select(SourceScanner.Relative),
        ];

        callers.Should().Equal(["src/PostgresStudio/Services/Connection/ConnectionFactsSource.cs"],
            "the application sessions are the connection facts source's read, and nothing else's");

        string service = SourceScanner.StripCommentsAndStrings(
            File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "Services", "Connection", "ConnectionInsightService.cs")));
        int method = service.IndexOf("Task<ConnectionApplicationsView> GetApplicationsAsync(", StringComparison.Ordinal);
        method.Should().BePositive();
        int require = service.IndexOf("capabilities.Require(StudioCapability.ViewDatabaseActivity)", method, StringComparison.Ordinal);
        int writePolicy = service.IndexOf("IsAuthorizedAsync(whole, StudioCapability.ViewDatabaseActivity", method, StringComparison.Ordinal);
        int read = service.IndexOf("facts.ReadApplicationsAsync(", method, StringComparison.Ordinal);

        require.Should().BePositive();
        writePolicy.Should().BeGreaterThan(require);
        read.Should().BeGreaterThan(writePolicy, "the sessions are read only after the capability and both policies have said yes");

        string page = SourceScanner.StripCommentsAndStrings(
            File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "Components", "Pages", "Database", "ConnectionPage.razor")));
        page.Should().Contain("Activity.IsOpen").And.Contain("EffectiveHealthInterval").And.NotContain("RefreshInterval");
        page.IndexOf("Activity.IsOpen", StringComparison.Ordinal).Should().BeLessThan(page.IndexOf("GetApplicationsAsync(", StringComparison.Ordinal),
            "the page asks the service only through an open gate");
    }

    [Fact]
    public void The_applications_builder_is_called_only_by_the_sessions_reader()
    {
        List<string> callers =
        [
            .. SourceScanner.ShippedSourceFiles()
                .Where(static file => SourceScanner.Relative(file) != ApplicationsBuilder)
                .Where(static file => SourceScanner.StripCommentsAndStrings(File.ReadAllText(file)).Contains("ConnectionsByApplicationQueries.", StringComparison.Ordinal))
                .Select(SourceScanner.Relative),
        ];

        callers.Should().Equal(["src/PostgresStudio.Diagnostics/Services/Database/SessionActivityReader.cs"],
            "connections by application are the sessions snapshot's fourth statement, behind its ten-second floor, and nothing else's");
    }

    /// <summary>
    /// Issue #20: the one statement that groups the whole server's sessions - other databases' applications, roles and addresses -
    /// is called by the Sessions reader, as the snapshot's sixth statement behind its ten-second floor, and by the sweep's one
    /// shared read, which only the two activity checks C13 and C14 ask for, and since 1.0 by the opt-in connection history's
    /// builder on its own cadence (<see cref="HistoryBuilder" />): nothing else, so nothing reads it on a poll.
    /// </summary>
    [Fact]
    public void The_holders_read_is_called_only_by_the_sessions_reader_and_the_sweeps_one_shared_read()
    {
        List<string> callers =
        [
            .. SourceScanner.ShippedSourceFiles()
                .Where(static file => SourceScanner.Relative(file) != Builder)
                .Where(static file => SourceScanner.StripCommentsAndStrings(File.ReadAllText(file)).Contains("HealthConnectionQueries.ServerHolders(", StringComparison.Ordinal))
                .Select(SourceScanner.Relative),
        ];

        callers.Should().BeEquivalentTo(
            ["src/PostgresStudio.Diagnostics/Services/Database/SessionActivityReader.cs", "src/PostgresStudio.Diagnostics/Services/Health/Checks/ServerConnectionHolders.cs", HistoryBuilder],
            "the holders are the Sessions snapshot's and the sweep's one shared read - which C13 and C14 alone ask for - and the opt-in connection history's, on its own cadence");

        List<string> askers =
        [
            .. SourceScanner.ShippedSourceFiles()
                .Where(static file => SourceScanner.Relative(file) != "src/PostgresStudio.Diagnostics/Services/Health/Checks/ServerConnectionHolders.cs")
                .Where(static file => SourceScanner.StripCommentsAndStrings(File.ReadAllText(file)).Contains("ServerConnectionHolders.", StringComparison.Ordinal))
                .Select(SourceScanner.Relative),
        ];

        askers.Should().BeEquivalentTo(
            ["src/PostgresStudio.Diagnostics/Services/Health/Checks/IdleConnectionsHoardedCheck.cs", "src/PostgresStudio.Diagnostics/Services/Health/Checks/UnattributedConnectionsCheck.cs"],
            "only the two activity checks take the shared holders read");
    }

    [Fact]
    public void The_aurora_builders_session_read_is_called_only_by_the_aurora_facts_session_read()
    {
        List<string> callers =
        [
            .. SourceScanner.ShippedSourceFiles()
                .Where(static file => SourceScanner.Relative(file) != AuroraBuilder)
                .Where(static file => SourceScanner.StripCommentsAndStrings(File.ReadAllText(file)).Contains("AuroraQueries.BackendWaits(", StringComparison.Ordinal))
                .Select(SourceScanner.Relative),
        ];

        callers.Should().Equal(["src/PostgresStudio.Diagnostics/Services/Aurora/AuroraFacts.cs"],
            "the per-session waits are read by AuroraFactsReader.ReadSessionsAsync alone, which only the activity gate reaches");

        string service = SourceScanner.StripCommentsAndStrings(
            File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "Services", "Aurora", "AuroraService.cs")));
        int gate = service.IndexOf("SessionsGateAsync(whole", StringComparison.Ordinal);
        int read = service.IndexOf("facts.GetSessionsAsync(", StringComparison.Ordinal);

        gate.Should().BePositive();
        read.Should().BeGreaterThan(gate, "the sessions are read only after the activity gate has passed");
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

        callers.Should().Equal(["src/PostgresStudio.Diagnostics/Services/Replication/ReplicationFacts.cs"],
            "the standby read is StandbySnapshot's, which R03's regular sweep and the page's cached facts read share");
    }

    [Fact]
    public void The_replication_page_polls_at_the_health_interval_through_a_cache_of_that_time_to_live()
    {
        string page = SourceScanner.StripCommentsAndStrings(
            File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "Components", "Pages", "Replication", "Replication.razor")));
        string cache = SourceScanner.StripCommentsAndStrings(
            File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio.Diagnostics", "Services", "Replication", "ReplicationFactsReader.cs")));

        page.Should().Contain("EffectiveHealthInterval").And.NotContain("RefreshInterval");
        cache.Should().Contain("new StudioSnapshotCache(timeProvider ?? TimeProvider.System, settings.EffectiveHealthInterval)");

        HealthCheckDescriptor r03 = Health.StudioHealthChecks.Registry.Find("R03")!;
        HealthSweeper.TiersOf(HealthSweepKind.Regular).Should().Contain(r03.Info.Tier, "R03 runs in the regular sweep, cached for HealthInterval");
    }

    /// <summary>
    /// The maintenance builder's two shared reads - the workers and what is running - are called by the health checks alone
    /// (T16), so the process array is read once per regular sweep; the bloat measure, the one thing the Maintenance service
    /// runs itself, reads no activity view.
    /// </summary>
    [Fact]
    public void The_maintenance_builders_activity_reads_are_called_only_by_the_health_checks()
    {
        ReadsAnActivityView.IsMatch(File.ReadAllText(RepositoryRoot.Combine(MaintenanceBuilder.Split('/'))))
            .Should().BeTrue("the maintenance builder reads pg_stat_activity for T16, so its exemption is not an exemption for nothing");
        ReadsAnActivityView.IsMatch(PostgresStudio.Internal.Sql.MaintenanceQueries.BloatHeapSql("ext") + PostgresStudio.Internal.Sql.MaintenanceQueries.BloatPreconditionsSql)
            .Should().BeFalse("the on-demand measure reads no activity view");

        List<string> callers =
        [
            .. SourceScanner.ShippedSourceFiles()
                .Where(static file => SourceScanner.Relative(file) != MaintenanceBuilder)
                .Where(static file =>
                {
                    string text = SourceScanner.StripCommentsAndStrings(File.ReadAllText(file));
                    return text.Contains("MaintenanceQueries.Workers(", StringComparison.Ordinal) || text.Contains("MaintenanceQueries.Progress(", StringComparison.Ordinal);
                })
                .Select(SourceScanner.Relative),
        ];

        callers.Should().NotBeEmpty("a scan that found no caller would pass for the wrong reason");
        callers.Should().OnlyContain(
            static x => StudioProjects.IsUnder(x, "Services", "Health", "Checks"),
            "every other caller would be a new path to the process array");
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
        callers.Should().Contain("src/PostgresStudio.Diagnostics/Services/Database/SessionActivityReader.cs", "the Sessions reader is a caller, so the scan reads the shared studio");
        callers.Should().OnlyContain(
            static x => StudioProjects.IsUnder(x, "Services", "Health", "Checks")
                || x == "src/PostgresStudio.Diagnostics/Services/Database/SessionActivityReader.cs"
                || x == HorizonBuilder
                || x == ApplicationsBuilder
                || x == ConnectionBuilder
                || x == HistoryBuilder,
            "every other caller would be a new path to the process array and the lock table; the horizon, applications and connection builders take only H3's session predicate, and the opt-in connection history (#43) reads the holders on its own cadence over its own kept connection");
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
                || x == SnapshotBuilder
                || StudioProjects.IsUnder(x, "Services", "Database"),
            "the sidebar, MartenStudio's Overview and every other page poll every RefreshInterval; PostgresStudio's Overview "
            + "reads the summary at HealthInterval only (the fact below), and the Health page's Snapshot menu once per click");
    }

    /// <summary>
    /// The Health page's Snapshot download (W5-API): it reads the sessions summary and details as the visitor, once per click of
    /// the menu - never on a timer, and the builder is resolved only when the menu is used.
    /// </summary>
    private const string SnapshotBuilder = "src/PostgresStudio/Services/Diagnostics/VisitorSnapshotBuilder.cs";

    [Fact]
    public void The_snapshot_download_reads_the_sessions_only_when_its_menu_is_used()
    {
        string menu = SourceScanner.StripCommentsAndStrings(
            File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "Components", "Pages", "Health", "SnapshotMenu.razor")));

        menu.Should().NotContain("@inject VisitorSnapshotBuilder", "the builder is resolved on click, so a Health page builds none of its services");
        menu.Should().NotContain("PeriodicTimer").And.NotContain("StudioLiveUpdates.Create", "a snapshot is taken when asked, never polled");
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

        // C10 (H10) reads no activity view - only pg_backend_pid(), twice - and runs in the same regular sweep; nor does C11
        // (W5-CAP), which reads pg_stat_database and the shared statistics views through the statistics window.
        checks.Select(static x => x.Info.Code).Should().Equal("C01", "C02", "C03", "C04", "C05", "C06", "C07", "C08", "C09", "C10", "C11", "C12", "C13", "C14", "C15", "C16", "C17", "C18");
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
