using PostgresStudio.Services.Health;
using PostgresStudio.Services.Server;

using static PostgresStudio.Tests.Server.ServerTestFacts;

namespace PostgresStudio.Tests.Server;

/// <summary>
/// The curated settings table, pinned with its frozen groups; the facts' rows and chips; and the page's view of them (W5-S2).
/// </summary>
public class ServerSettingsFactsTests
{
    /// <summary>The curated table, as it ships: every name with its group. A change here is a change of what the page shows.</summary>
    private static readonly (string Name, string Group)[] Pinned =
    [
        ("shared_buffers", "memory"), ("work_mem", "memory"), ("maintenance_work_mem", "memory"), ("effective_cache_size", "memory"),
        ("max_locks_per_transaction", "memory"), ("max_prepared_transactions", "memory"),
        ("max_connections", "connections"), ("superuser_reserved_connections", "connections"), ("reserved_connections", "connections"),
        ("idle_in_transaction_session_timeout", "connections"), ("idle_session_timeout", "connections"), ("statement_timeout", "connections"),
        ("lock_timeout", "connections"), ("transaction_timeout", "connections"), ("default_transaction_read_only", "connections"),
        ("default_transaction_isolation", "connections"), ("password_encryption", "connections"), ("ssl", "connections"),
        ("wal_level", "wal"), ("max_wal_size", "wal"), ("checkpoint_timeout", "wal"), ("synchronous_commit", "wal"), ("fsync", "wal"),
        ("full_page_writes", "wal"), ("archive_mode", "wal"), ("max_slot_wal_keep_size", "wal"), ("wal_keep_size", "wal"),
        ("max_replication_slots", "wal"), ("max_wal_senders", "wal"), ("hot_standby_feedback", "wal"),
        ("vacuum_failsafe_age", "vacuum"), ("vacuum_multixact_failsafe_age", "vacuum"),
        ("random_page_cost", "planner"), ("seq_page_cost", "planner"), ("default_statistics_target", "planner"), ("jit", "planner"),
        ("effective_io_concurrency", "io"), ("maintenance_io_concurrency", "io"),
        ("track_counts", "statistics"), ("track_activities", "statistics"), ("track_io_timing", "statistics"),
        ("track_wal_io_timing", "statistics"), ("track_activity_query_size", "statistics"), ("compute_query_id", "statistics"),
        ("shared_preload_libraries", "statistics"),
    ];

    [Fact]
    public void The_curated_table_is_pinned_name_by_name_with_its_group()
    {
        ServerSettingsCatalog.Settings.Select(static x => (x.Name, x.Group)).Should().Equal(Pinned);
        ServerSettingsCatalog.Families.Select(static x => (x.Prefix, x.Group)).Should().Equal(("autovacuum", "vacuum"), ("enable_", "planner"));
    }

    [Fact]
    public void The_groups_are_exactly_the_frozen_seven_in_order_and_every_one_is_used()
    {
        ServerSettingsCatalog.Groups.Should().Equal("memory", "connections", "wal", "vacuum", "planner", "io", "statistics");

        ServerSettingsCatalog.Settings.Select(static x => x.Group).Concat(ServerSettingsCatalog.Families.Select(static x => x.Group))
            .Should().OnlyContain(static x => ServerSettingsCatalog.Groups.Contains(x))
            .And.Contain(ServerSettingsCatalog.Groups);
    }

    [Fact]
    public void No_named_setting_is_a_member_of_a_family_and_no_name_repeats()
    {
        ServerSettingsCatalog.Names.Should().OnlyHaveUniqueItems();
        ServerSettingsCatalog.Names.Should().NotContain(x => ServerSettingsCatalog.Families.Any(f => f.Contains(x)), "a row is never read twice");
    }

    [Fact]
    public void A_family_is_a_LIKE_pattern_with_its_underscore_escaped()
    {
        ServerSettingsCatalog.Patterns.Should().Equal("autovacuum%", "enable\\_%");
        ServerSettingsCatalog.GroupOf("enable_seqscan").Should().Be("planner");
        ServerSettingsCatalog.GroupOf("autovacuum_naptime").Should().Be("vacuum");
        ServerSettingsCatalog.GroupOf("work_mem").Should().Be("memory");
        ServerSettingsCatalog.GroupOf("enablex").Should().BeNull();
    }

    [Fact]
    public void The_settings_gated_on_a_release_name_it()
    {
        ServerSettingsCatalog.Settings.Where(static x => x.Since is not null).Select(static x => (x.Name, x.Since!.Value)).Should().Equal(
            ("reserved_connections", PostgresFeature.ReservedConnections),
            ("transaction_timeout", PostgresFeature.TransactionTimeout),
            ("vacuum_failsafe_age", PostgresFeature.VacuumFailsafeAge),
            ("vacuum_multixact_failsafe_age", PostgresFeature.VacuumFailsafeAge));
    }

    // ---- The facts' rows --------------------------------------------------------------------------------------------

    private static ServerSettingsFacts Facts(
        int version = 170011,
        PostgresGrants? grants = null,
        ServerSettingsRead? settings = null,
        ServerExtensionsRead? extensions = null,
        ServerConnectionPosture? posture = null,
        PostgresPlatform? platform = null) =>
        new(
            Now,
            new PostgresServerFacts(Now, version, "17.11", platform ?? SelfHosted),
            grants is null ? new ServerRead<PostgresGrants>(null, "The role the studio reads as may not read this (SQLSTATE 42501).") : ServerRead<PostgresGrants>.Of(grants),
            ServerRead<ServerSettingsRead>.Of(settings ?? Settings()),
            ServerRead<ServerExtensionsRead>.Of(extensions ?? new ServerExtensionsRead([], 0)),
            ServerRead<ServerConnectionPosture>.Of(posture ?? Posture()),
            []);

    [Fact]
    public void A_setting_the_role_is_not_shown_is_hidden_and_one_the_release_lacks_needs_its_version()
    {
        ServerSettingsFacts facts = Facts(160015, Grants("probe"), Settings(Setting("work_mem", "4096", "kB")));

        IReadOnlyList<ServerSettingRow> rows = facts.CuratedSettings();

        rows.Single(x => x.Name == "work_mem").State.Should().Be(ServerSettingState.Read);
        rows.Single(x => x.Name == "shared_preload_libraries").State.Should().Be(ServerSettingState.Hidden);
        rows.Single(x => x.Name == "transaction_timeout").State.Should().Be(ServerSettingState.NeedsVersion);
        rows.Single(x => x.Name == "reserved_connections").State.Should().Be(ServerSettingState.Hidden, "16 has it");
    }

    [Fact]
    public void A_setting_missing_for_a_role_that_reads_every_setting_is_absent_rather_than_hidden()
    {
        ServerSettingsFacts facts = Facts(grants: Grants("mon", roles: "pg_monitor"));

        facts.CuratedSettings().Single(x => x.Name == "shared_preload_libraries").State.Should().Be(ServerSettingState.Absent);
    }

    [Fact]
    public void A_familys_members_are_listed_by_name_under_its_group()
    {
        ServerSettingsFacts facts = Facts(grants: Grants(), settings: Settings(
            Setting("enable_seqscan", "on"), Setting("autovacuum_naptime", "60", "s"), Setting("autovacuum", "on"), Setting("enable_bitmapscan", "on")));

        facts.CuratedSettings().Where(static x => x.Group is "vacuum" or "planner" && x.State == ServerSettingState.Read).Select(static x => x.Name)
            .Should().Equal("autovacuum", "autovacuum_naptime", "enable_bitmapscan", "enable_seqscan");
    }

    [Fact]
    public void The_page_says_what_a_hidden_setting_needs_naming_the_role()
    {
        ServerSettingLine line = ServerPageService.Line(
            new ServerSettingRow("shared_preload_libraries", "statistics", ServerSettingState.Hidden, null), SelfHosted, "probe");

        line.Value.Should().Be("Hidden from role probe: it needs pg_read_all_settings (pg_monitor includes it).");
        line.WhereItChanges.Should().Be("Changes in postgresql.conf, followed by a reload.");
    }

    [Theory]
    [InlineData("16384", "8kB", "128 MB")]
    [InlineData("4096", "kB", "4 MB")]
    [InlineData("1000", "kB", "1000 kB")]
    [InlineData("1024", "MB", "1 GB")]
    [InlineData("0", "ms", "0")]
    [InlineData("300000", "ms", "5 min")]
    [InlineData("200", "ms", "200 ms")]
    [InlineData("60", "s", "1 min")]
    [InlineData("-1", "kB", "-1 (kB)")]
    [InlineData("on", null, "on")]
    [InlineData("0.1", null, "0.1")]
    public void A_value_is_printed_in_the_largest_unit_it_divides_into(string value, string? unit, string expected)
    {
        ServerSettingText.Format(value, unit).Should().Be(expected);
    }

    [Fact]
    public void A_setting_the_studio_sets_for_itself_says_its_value_is_the_one_a_new_session_starts_with()
    {
        ServerSettingLine line = ServerPageService.Line(
            new ServerSettingRow("statement_timeout", "connections", ServerSettingState.Read,
                new ServerSettingValue("statement_timeout", "0", "ms", "default", false, "user", "0") { StudioSet = true }),
            SelfHosted,
            "app");

        line.SetBy.Should().Contain("Postgres's own default").And.Contain("the studio's own read sets its own");
    }

    // ---- Who set a setting the studio's own transaction sets for itself (W5-S2-fix) ---------------------------------

    private static ServerSettingValue StudioSet(string value, string? entrySource, string boot = "0") =>
        new("idle_in_transaction_session_timeout", value, "ms", entrySource, false, "user", boot) { StudioSet = true };

    [Theory]
    [InlineData("30000", "database", "30s", "database")]
    [InlineData("30000", "user", "'30s'", "user")]
    [InlineData("60000", "database user", "1min", "database user")]
    [InlineData("30000", "database", "30000", "database")]
    [InlineData("5000", "database", "30s", "client")]
    [InlineData("30000", "database", "thirty", null)]
    public void A_role_or_database_entry_is_the_source_only_while_its_value_is_the_value(string resetVal, string entrySource, string entryValue, string? expected)
    {
        ServerSettingSource.Resolve(StudioSet(resetVal, entrySource), entryValue, new HashSet<string>()).Should().Be(expected);
    }

    [Fact]
    public void The_connections_startup_options_outrank_every_entry_and_the_default()
    {
        IReadOnlySet<string> options = ServerSettingSource.StartupOptions(
            "Host=db;Username=app;Options=-c idle_in_transaction_session_timeout=5000 --lock-timeout=1s -cstatement_timeout=2s");

        options.Should().BeEquivalentTo(["idle_in_transaction_session_timeout", "lock_timeout", "statement_timeout"]);
        ServerSettingSource.Resolve(StudioSet("5000", "database"), "30s", options).Should().Be("client");
        ServerSettingSource.Resolve(StudioSet("0", null), null, options).Should().Be("client");
    }

    [Fact]
    public void With_no_entry_the_built_in_value_is_the_default_and_anything_else_is_not_known()
    {
        ServerSettingSource.Resolve(StudioSet("0", null), null, new HashSet<string>()).Should().Be("default");
        ServerSettingSource.Resolve(StudioSet("10000", null), null, new HashSet<string>()).Should().BeNull(
            "the configuration file, the command line or an environment variable, which the read cannot tell apart");
        ServerSettingSource.StartupOptions("Host=db").Should().BeEmpty();
        ServerSettingSource.StartupOptions(null).Should().BeEmpty();
    }

    // ---- Chips ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Pg_stat_statements_is_usable_installed_and_loaded_and_says_shared_preload_libraries_when_not_loaded()
    {
        ServerExtensionRow row = new("pg_stat_statements", true, "1.11", "1.11", false);

        ServerSettingsFacts.ChipOf(row, Posture(statementsLoaded: true), Grants(), SelfHosted).Usable.Should().BeTrue();

        ServerExtensionStatus notLoaded = ServerSettingsFacts.ChipOf(row, Posture(statementsLoaded: false), Grants(), Platform(PostgresPlatformKind.AmazonRds));
        notLoaded.Usable.Should().BeFalse();
        notLoaded.Note.Should().Contain("shared_preload_libraries").And.Contain("the instance's DB parameter group (Amazon RDS)");
    }

    [Fact]
    public void Pgstattuple_needs_pg_stat_scan_tables_and_says_who_grants_it()
    {
        ServerExtensionRow row = new("pgstattuple", true, "1.5", "1.5", false);

        ServerSettingsFacts.ChipOf(row, Posture(), Grants("app", roles: "pg_stat_scan_tables"), SelfHosted).Usable.Should().BeTrue();
        ServerSettingsFacts.ChipOf(row, Posture(), Grants("app", roles: "pg_monitor"), SelfHosted).Usable.Should().BeTrue("pg_monitor includes it");

        ServerExtensionStatus refused = ServerSettingsFacts.ChipOf(row, Posture(), Grants("probe"), SelfHosted);
        refused.Usable.Should().BeFalse();
        refused.Note.Should().Be("Installed in this database. Not checked: this needs pg_stat_scan_tables (pg_monitor includes it) for role probe. "
            + "On a self-hosted server a superuser can grant it.");
    }

    [Fact]
    public void An_extension_not_installed_says_who_can_create_it_and_one_the_server_lacks_says_so()
    {
        ServerSettingsFacts.ChipOf(new("pgstattuple", true, null, "1.5", false), Posture(), Grants(), SelfHosted).Note.Should().Be(
            "Not installed in this database. On a self-hosted server a superuser can run CREATE EXTENSION pgstattuple; it is not a "
            + "trusted extension, so a database owner cannot.");

        ServerSettingsFacts.ChipOf(new("pg_squeeze", true, null, null, null), Posture(), Grants(), SelfHosted).Note
            .Should().Be("Not installed, and this server has no files for it: its package is not installed on the server.");
    }

    [Fact]
    public void Track_io_timing_is_a_setting_chip()
    {
        ServerSettingsFacts.TimingChip(Setting("track_io_timing", "on"), Grants(), SelfHosted).Should().Match<ServerExtensionStatus>(x => x.Usable && x.IsSetting);
        ServerSettingsFacts.TimingChip(Setting("track_io_timing", "off"), Grants(), SelfHosted).Note.Should().Contain("postgresql.conf");
    }

    // ---- The posture, and the page's view ------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "UnixSocket")]
    [InlineData("", "UnixSocket")]
    [InlineData("127.0.0.1", "Loopback")]
    [InlineData("::1", "Loopback")]
    [InlineData("::ffff:127.0.0.1", "Loopback")]
    [InlineData("172.17.0.1", "Network")]
    [InlineData("2001:db8::1", "Network")]
    public void A_connection_is_told_by_its_client_address(string? address, string kind)
    {
        ServerConnectionPosture.KindOf(address).Should().Be(Enum.Parse<ServerConnectionKind>(kind));
    }

    [Fact]
    public void A_superuser_login_has_no_provider_or_file_roles_of_its_own_to_report()
    {
        ServerConnectionPosture posture = Posture("postgres", superuser: true, memberOf: ["pg_read_server_files", "rds_superuser"]);

        posture.LoginProviderAdminRoles.Should().BeEmpty();
        posture.LoginFileAccessRoles.Should().BeEmpty();
    }

    [Fact]
    public void The_view_shows_the_reading_role_its_grants_and_which_role_reads_what()
    {
        ServerSettingsFacts facts = Facts(grants: Grants("console_role", roles: "pg_read_all_stats"), posture: Posture("console_login", reading: "console_role"));

        ServerPageView view = ServerPageService.Build(facts, Services.Settings.StudioText.Marten, consoleRoleSet: true);

        view.ReadsAs.Should().Be("console_role");
        view.LoginRole.Should().Be("console_login");
        view.ReadingRoleNote.Should().Contain("console_role (MartenStudioOptions.SqlConsoleRole)").And.Contain("logs in as console_login");
        view.Grants.Single(x => x.Name == "pg_read_all_stats").Held.Should().BeTrue();
        view.Grants.Single(x => x.Name == "pg_stat_scan_tables").Consequence.Should().EndWith("On a self-hosted server a superuser can grant it.");
        view.Grants.Should().NotContain(x => x.Name == "provider admin role", "a self-hosted server has none, and the role is in none");
        view.Settings.Select(static x => x.Group).Should().Equal(ServerSettingsCatalog.Groups);
    }

    [Fact]
    public void Grants_that_could_not_be_read_are_a_sentence_and_hidden_settings_still_list()
    {
        ServerPageView view = ServerPageService.Build(Facts(grants: null), Services.Settings.StudioText.Marten, consoleRoleSet: false);

        view.Grants.Should().BeEmpty();
        view.GrantsNote.Should().Contain("42501");
        view.Settings.SelectMany(static x => x.Settings).Should().Contain(x => x.Row.State == ServerSettingState.Hidden);
    }

    [Fact]
    public void The_posture_section_is_the_reports_findings_and_rows_for_its_codes_worst_first()
    {
        HealthReport report = new()
        {
            Findings =
            [
                Shown("T01", HealthSeverity.Critical),
                Shown("S07", HealthSeverity.Info),
                Shown("K07", HealthSeverity.Critical),
                Shown("S04", HealthSeverity.Warning),
            ],
            Checks = [Row("S05"), Row("C01"), Row("K05"), Row("S04")],
        };

        (IReadOnlyList<HealthReportFinding> findings, IReadOnlyList<HealthCheckStatus> checks) = ServerPosture.Of(report);

        findings.Select(static x => x.Code).Should().Equal("K07", "S04", "S07");
        checks.Select(static x => x.Code).Should().Equal("S04", "S05", "K05");
        ServerPosture.Withheld(checks).Should().BeNull("none of the section's own rows withholds anything");

        // The section counts only its own rows' withheld findings, never the report's whole count.
        HealthCheckStatus withheld = Row("S04") with { FindingsWithheld = true };
        ServerPosture.Withheld([withheld, Row("S05")]).Should().Be("One of these checks has findings not shown to you; the Health page says why.");
        ServerPosture.Withheld([withheld, withheld with { Code = "K05" }]).Should().StartWith("2 of these checks");

        static HealthReportFinding Shown(string code, HealthSeverity severity) =>
            new(MartenStudio.Tests.Health.FakeHealthService.Finding(code, severity, code + " title"), [], code, "docs", true, null);

        static HealthCheckStatus Row(string code) =>
            new(code, code, HealthArea.Server, HealthCostTier.Catalog, HealthCheckOutcome.Checked, 0, null, null, null, "docs", []);
    }
}
