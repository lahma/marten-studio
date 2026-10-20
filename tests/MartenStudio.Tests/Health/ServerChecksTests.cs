using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>S01, S02 and S03's judgement over stated facts, and no Postgres.</summary>
public class ServerChecksTests
{
    private static readonly DateTimeOffset Today = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static readonly PostgresPlatform Rds = new(PostgresPlatformKind.AmazonRds, "the server has Amazon RDS's rds.extensions settings");

    private static readonly PostgresPlatform Neon = new(PostgresPlatformKind.Neon, "the server has Neon's neon_superuser role");

    // ---------------------------------------------------------------------------------------------------
    // S01
    // ---------------------------------------------------------------------------------------------------

    private static HealthVerdict EndOfLife(int versionNumber, DateTimeOffset at, PostgresPlatform? platform = null) =>
        new PostgresEndOfLifeCheck().Evaluate(
            new PostgresServerFacts(at, versionNumber, versionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), platform ?? PostgresPlatform.SelfHosted),
            HealthEvaluation.At(at));

    [Theory]
    [InlineData(170004, 17, "17")]
    [InlineData(140019, 14, "14")]
    [InlineData(180000, 18, "18")]
    [InlineData(90624, 906, "9.6")]
    [InlineData(90500, 905, "9.5")]
    public void A_server_version_number_is_read_as_its_major(int number, int major, string name)
    {
        PostgresServerFacts facts = new(Today, number, "x", PostgresPlatform.SelfHosted);

        facts.Major.Should().Be(major);
        facts.MajorName.Should().Be(name);
    }

    [Fact]
    public void PostgreSQL_14_today_is_a_warning_naming_2026_11_12()
    {
        HealthFinding finding = EndOfLife(140019, Today).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Be("PostgreSQL 14's community support ends on 2026-11-12, in 26 days");
        finding.Evidence.Should().Contain(new HealthEvidence("Community final release", "2026-11-12"));
        finding.Evidence.Should().Contain(new HealthEvidence("Source", "https://www.postgresql.org/support/versioning/"));
        finding.Evidence.Should().NotContain(static x => x.Label == "Platform", "a self-hosted server has no provider to name");
    }

    [Fact]
    public void PostgreSQL_13_is_past_its_end_of_life_and_critical()
    {
        HealthFinding finding = EndOfLife(130021, Today).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Title.Should().Be("PostgreSQL 13 reached the community's end of life on 2025-11-13");
    }

    [Fact]
    public void PostgreSQL_17_today_is_quiet_and_says_until_when()
    {
        HealthVerdict verdict = EndOfLife(170004, Today);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Be("PostgreSQL 17 has community support until 2029-11-08.");
    }

    [Theory]
    [InlineData("2028-11-07", null)]
    [InlineData("2028-11-08", "Info")]
    [InlineData("2029-08-09", "Info")]
    [InlineData("2029-08-10", "Warning")]
    [InlineData("2029-11-07", "Warning")]
    [InlineData("2029-11-08", "Critical")]
    [InlineData("2031-01-01", "Critical")]
    public void The_lines_are_a_year_ninety_days_and_the_final_release_itself(string day, string? severity)
    {
        DateTimeOffset at = new(DateTime.Parse(day, System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero);

        HealthVerdict verdict = EndOfLife(170004, at);

        if (severity is null)
        {
            verdict.Findings.Should().BeEmpty();
        }
        else
        {
            verdict.Findings.Should().ContainSingle().Which.Severity.Should().Be(Enum.Parse<HealthSeverity>(severity));
        }
    }

    [Fact]
    public void A_major_older_than_the_table_is_past_its_end_of_life()
    {
        HealthFinding finding = EndOfLife(90500, Today).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Title.Should().Be("PostgreSQL 9.5 is past the community's end of life, which came by 2021-11-11 at the latest");
    }

    [Fact]
    public void A_major_newer_than_the_table_is_quiet_and_says_so_rather_than_guessing()
    {
        HealthVerdict verdict = EndOfLife(190001, Today);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("PostgreSQL 19 is newer than").And.Contain("ends at 18");
    }

    [Fact]
    public void On_a_managed_platform_the_date_is_the_communitys_and_the_finding_says_so()
    {
        HealthFinding finding = EndOfLife(140019, Today, Rds).Findings.Should().ContainSingle().Subject;

        finding.Title.Should().EndWith("; your provider's schedule may differ");
        finding.WhyItMatters.Should().Contain("Community support ends on this date; your provider runs this server and its schedule may differ");
        finding.Evidence.Should().ContainSingle(static x => x.Label == "Platform").Which
            .Should().Be(new HealthEvidence("Platform", "Amazon RDS for PostgreSQL", HealthGate.Database), "the version is everybody's, the platform the database's");
    }

    // ---------------------------------------------------------------------------------------------------
    // S02
    // ---------------------------------------------------------------------------------------------------

    private static ServerSetting Off(string name, string source = "configuration file") => new(name, "off", source, false);

    private static ServerSetting On(string name, string source = "configuration file") => new(name, "on", source, false);

    private static HealthVerdict Settings(string? environment, PostgresPlatform? platform, params ServerSetting[] settings) =>
        new DangerousSettingsCheck().Evaluate(
            new DangerousSettingsFacts(Today, settings, settings.Length, environment, platform ?? PostgresPlatform.SelfHosted),
            HealthEvaluation.At(Today));

    private static ServerSetting[] Defaults =>
    [
        On("track_counts", "default"), On("fsync", "default"), On("full_page_writes", "default"),
        Off("zero_damaged_pages", "default"), Off("ignore_checksum_failure", "default"), On("synchronous_commit", "default"),
    ];

    private static ServerSetting[] With(params ServerSetting[] changed) =>
        [.. Defaults.Where(x => changed.All(c => c.Name != x.Name)), .. changed];

    [Fact]
    public void Postgres_s_defaults_are_quiet()
    {
        HealthVerdict verdict = Settings("Production", null, Defaults);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Be("track_counts, fsync, full_page_writes, zero_damaged_pages and ignore_checksum_failure are at their safe values, "
            + "synchronous_commit is on, and nothing waits for a restart.");
    }

    /// <summary>A watched setting with no row is not judged, and the status row says so, rather than calling it safe.</summary>
    [Fact]
    public void A_setting_that_could_not_be_read_is_named_and_not_called_safe()
    {
        HealthVerdict verdict = Settings("Production", null, [.. Defaults.Where(static x => x.Name is not ("fsync" or "zero_damaged_pages"))]);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().StartWith("Could not read fsync, zero_damaged_pages: this role is not shown them, or this server does not have them, so they are not judged.")
            .And.EndWith("Everything else S02 judges is at its safe value, and nothing waits for a restart.");
    }

    [Theory]
    [InlineData("local", "waits for this server's own WAL flush - it is durable here - and never for a synchronous standby")]
    [InlineData("remote_write", "for the standby to have handed the WAL to its operating system rather than flushed it")]
    [InlineData("remote_apply", "for the standby to have applied it")]
    public void A_synchronous_commit_that_loses_nothing_here_is_a_note_that_says_what_it_waits_for(string value, string words)
    {
        HealthVerdict verdict = Settings("Production", null, With(new ServerSetting("synchronous_commit", value, "configuration file", false)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("synchronous_commit = " + value + ": a commit").And.Contain(words).And.NotContain("synchronous_commit is on");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData(null)]
    public void Track_counts_off_is_critical_in_every_environment(string? environment)
    {
        HealthFinding finding = Settings(environment, null, With(Off("track_counts"))).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Subject.Should().Be("track_counts");
        finding.Title.Should().Contain("track_counts = off");
        finding.Evidence.Should().Contain(new HealthEvidence("Setting", "track_counts = off"));
        finding.Remedy!.Code.Should().Contain("track_counts = on");
    }

    [Theory]
    [InlineData("fsync", "off")]
    [InlineData("full_page_writes", "off")]
    [InlineData("zero_damaged_pages", "on")]
    [InlineData("ignore_checksum_failure", "on")]
    public void A_setting_that_trades_safety_is_critical_in_Production(string name, string value)
    {
        HealthFinding finding = Settings("Production", null, With(new ServerSetting(name, value, "configuration file", false)))
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Subject.Should().Be(name);
        finding.Title.Should().Contain(name + " = " + value).And.EndWith("in Production");
        finding.Evidence.Should().Contain(new HealthEvidence("Environment", "Production"));
        finding.Evidence.Should().Contain(new HealthEvidence("Set by", "the server's configuration file"));
    }

    [Theory]
    [InlineData("fsync", "off")]
    [InlineData("zero_damaged_pages", "on")]
    public void Outside_Production_the_same_setting_is_a_note(string name, string value)
    {
        HealthVerdict verdict = Settings("Development", null, With(new ServerSetting(name, value, "configuration file", false)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain(name + " = " + value).And.Contain("judged only in Production");
    }

    [Fact]
    public void A_host_that_names_no_environment_gets_a_note_and_no_guess()
    {
        HealthVerdict verdict = Settings(null, null, With(Off("fsync")));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("registers no IHostEnvironment");
    }

    [Fact]
    public void Neon_s_documented_fsync_is_muted_in_Production()
    {
        HealthVerdict verdict = Settings("Production", Neon, With(Off("fsync")));

        verdict.Findings.Should().BeEmpty("Neon documents fsync = off as its own design");
        verdict.Note.Should().Contain("fsync = off is Neon's documented design");
    }

    private static readonly PostgresPlatform Aurora = new(PostgresPlatformKind.AmazonAurora, "the server has Aurora's aurora_version() function");

    /// <summary>
    /// Neon's full_page_writes and Aurora's two: the provider's storage is its own and documents no value for them, so an
    /// off is reported as Info - never Critical, never muted - with no remedy line, and says it cannot be verified.
    /// </summary>
    [Theory]
    [InlineData("neon", "full_page_writes")]
    [InlineData("aurora", "fsync")]
    [InlineData("aurora", "full_page_writes")]
    public void A_durability_setting_a_managed_storage_platform_does_not_document_is_info_and_unverified(string platform, string name)
    {
        PostgresPlatform on = platform == "neon" ? Neon : Aurora;

        HealthFinding finding = Settings("Production", on, With(Off(name))).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Subject.Should().Be(name);
        finding.Title.Should().Be(name + " = off on " + on.Name + ", whose storage manages durability itself");
        finding.WhyItMatters.Should().Contain("Marten Studio cannot verify this setting's meaning on " + on.Name);
        finding.Remedy.Should().BeNull("there is nothing the host can be told to change with confidence");
    }

    [Fact]
    public void Aurora_and_Neon_judge_nothing_else_differently()
    {
        Settings("Production", Neon, With(On("zero_damaged_pages"), Off("track_counts")))
            .Findings.Select(static x => (x.Subject, x.Severity)).Should().BeEquivalentTo(
                [("track_counts", HealthSeverity.Critical), ("zero_damaged_pages", HealthSeverity.Critical)]);

        Settings("Production", Aurora, With(On("ignore_checksum_failure")))
            .Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Critical);

        Settings("Production", Rds, With(Off("fsync")))
            .Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Critical,
                "RDS is ordinary Postgres on block storage, where fsync means what it means anywhere");
    }

    [Fact]
    public void Synchronous_commit_off_is_info_anywhere()
    {
        HealthFinding finding = Settings("Development", null, With(Off("synchronous_commit"))).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Subject.Should().Be("synchronous_commit");
    }

    [Fact]
    public void Settings_waiting_for_a_restart_are_info_and_named()
    {
        HealthFinding finding = Settings("Development", null,
                [.. Defaults, new ServerSetting("max_connections", "100", "configuration file", true), new ServerSetting("shared_buffers", "16384", "configuration file", true)])
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Subject.Should().Be("pending-restart");
        finding.Title.Should().Be("2 changed settings wait for the server to restart");
        finding.Evidence.Should().Contain(new HealthEvidence("Waiting for a restart", "max_connections, shared_buffers"));
        finding.Remedy.Should().BeNull();
    }

    [Fact]
    public void A_setting_the_connection_sends_points_the_remedy_at_the_connection_string()
    {
        HealthFinding finding = Settings("Development", null, With(Off("synchronous_commit", "client"))).Findings.Single();

        finding.Evidence.Should().Contain(static x => x.Label == "Set by" && x.Value.Contains("connection string", StringComparison.Ordinal));
        finding.Remedy!.Explanation.Should().Contain("remove it from the Options of the connection string");
    }

    [Fact]
    public void A_setting_made_for_the_role_points_the_remedy_at_the_role()
    {
        HealthFinding finding = Settings("Development", null, With(Off("synchronous_commit", "user"))).Findings.Single();

        finding.Remedy!.Explanation.Should().Contain("set for this role or this database");
    }

    [Fact]
    public void A_server_setting_on_a_managed_platform_is_changed_where_the_platform_says()
    {
        HealthFinding finding = Settings("Development", Rds, With(Off("track_counts"))).Findings.Single();

        finding.Remedy!.Explanation.Should().Contain("DB parameter group");
        finding.Evidence.Should().Contain(new HealthEvidence("Platform", "Amazon RDS for PostgreSQL"));
    }

    // ---------------------------------------------------------------------------------------------------
    // S03
    // ---------------------------------------------------------------------------------------------------

    private static readonly DateTimeOffset Reset = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static HealthVerdict Checksums(ChecksumFacts facts) => new ChecksumFailuresCheck().Evaluate(facts, HealthEvaluation.At(Today));

    private static ChecksumFacts Clean => new(Today, true, 0, null, 0, 0, null, 0, null, Reset);

    [Fact]
    public void Checksums_off_is_a_status_line_and_never_a_finding()
    {
        HealthVerdict verdict = Checksums(new ChecksumFacts(Today, false, null, null, null, 0, null, null, null, Reset));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("Data checksums are off").And.Contain("pg_checksums --enable");
    }

    [Fact]
    public void Checksums_on_and_nothing_failed_is_quiet()
    {
        HealthVerdict verdict = Checksums(Clean);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Be("Data checksums are on, and no page has failed one since the statistics were reset at 2026-10-01 00:00:00Z.");
    }

    [Fact]
    public void One_failure_in_this_database_is_critical()
    {
        DateTimeOffset when = new(2026, 10, 16, 22, 5, 0, TimeSpan.Zero);

        HealthFinding finding = Checksums(Clean with { ThisDatabase = 1, ThisLast = when }).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Title.Should().Be("Postgres has read a page whose checksum did not match, the last at 2026-10-16 22:05:00Z");
        finding.Evidence.Should().Contain(new HealthEvidence("In this database", "1, the last at 2026-10-16 22:05:00Z"));
        finding.Evidence.Should().Contain(new HealthEvidence("In other databases on this server", "none"));
        finding.Evidence.Should().Contain(new HealthEvidence("Counted since", "2026-10-01 00:00:00Z"));
    }

    [Fact]
    public void Failures_elsewhere_on_the_server_are_counted_into_the_finding_and_the_databases_only_counted()
    {
        HealthFinding finding = Checksums(Clean with { OtherDatabases = 5, OtherDatabasesFailing = 2, SharedObjects = 1 })
            .Findings.Should().ContainSingle().Subject;

        finding.Title.Should().StartWith("Postgres has read 6 pages whose checksums did not match");
        finding.Evidence.Should().Contain(new HealthEvidence("In other databases on this server", "5 across 2 databases"));
        finding.Evidence.Should().Contain(new HealthEvidence("In the shared catalogs", "1"));
        finding.Remedy.Should().BeNull("the repair is a restore, not a line of configuration");
    }
}
