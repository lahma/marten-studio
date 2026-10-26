using JasperFx.Events.Daemon;

using MartenStudio.Services.Health.Checks;

using Npgsql;
using PostgresStudio;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// S01, S02, S03, K04 and C10 on the shared Postgres: what each reads of a real server, and what a real store's
/// configuration makes of it.
/// </summary>
/// <remarks>
/// <b>The shared server runs without durability.</b> The Testcontainers PostgreSQL module starts every server with
/// <c>fsync</c>, <c>full_page_writes</c> and <c>synchronous_commit</c> off (<c>PostgreSqlBuilder.Init</c>, 4.15.0), so S02
/// fires here by design - which makes it this check's live "fires" case - and its quiet case runs on
/// <see cref="ServerSafetyServerFixture" />, which turns them back on.
/// </remarks>
public class ServerSettingsLiveTests(PostgresFixture postgres)
{
    // ----------------------------------------------------------------------------------------------
    // S01
    // ----------------------------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("S01")]
    public async Task The_servers_major_is_read_live_and_judged_as_its_end_of_life_comes()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_s01_fires");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database);

        PostgresEndOfLifeCheck check = new();
        PostgresServerFacts facts = await host.GatherAsync(check);

        int expected = await LiveWait.ScalarAsync<int>(database, "select current_setting('server_version_num')::int4") / 10_000;
        facts.Major.Should().Be(expected);
        facts.Platform.Kind.Should().Be(PostgresPlatformKind.SelfHosted, "a container of the suite's own carries no provider's marker");

        PostgresMajorSupport row = PostgresCommunitySupport.Find(facts.Major)
            ?? throw new InvalidOperationException("The suite's Postgres " + facts.MajorName + " is not in S01's table.");
        DateTimeOffset end = new(row.FinalRelease.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        check.Evaluate(facts, HealthEvaluation.At(end.AddDays(-200))).Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Info);
        check.Evaluate(facts, HealthEvaluation.At(end.AddDays(-30))).Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Warning);

        HealthFinding ended = check.Evaluate(facts, HealthEvaluation.At(end)).Findings.Should().ContainSingle().Subject;
        ended.Severity.Should().Be(HealthSeverity.Critical);
        ended.Evidence.Should().Contain(x => x.Label == "Server version" && x.Value.StartsWith(row.Name, StringComparison.Ordinal),
            "the version Postgres reported is the version the finding names");
    }

    [PostgresFact]
    [HealthQuiet("S01")]
    public async Task A_supported_major_is_quiet_and_says_until_when()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_s01_quiet");

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "S01");
        HealthCheckStatus status = HealthLiveHost.Status(report, "S01");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked);
        status.Note.Should().StartWith("PostgreSQL ").And.Contain("has community support until");
    }

    // ----------------------------------------------------------------------------------------------
    // S02
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Two settings from the server's command line (the Testcontainers module's), two from the store's connection
    /// string, under a Production environment: four Criticals and an Info, each saying where it was set.
    /// </summary>
    [PostgresFact]
    [HealthFires("S02")]
    public async Task Settings_from_the_command_line_and_the_connection_string_fire_in_Production()
    {
        string withOptions = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            // Superuser-only settings, which the container's superuser may send at connection start - as a host could.
            Options = "-c track_counts=off -c zero_damaged_pages=on",
        }.ConnectionString;

        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            "health_s02_fires",
            environment: "Production",
            store: options => options.Connection(withOptions));

        HealthReport report = await host.ReportAsync();

        HealthReportFinding[] findings = [.. report.Findings.Where(static x => x.Code == "S02")];
        findings.Select(static x => (x.Finding.Subject, x.Severity)).Should().BeEquivalentTo(
        [
            ("track_counts", HealthSeverity.Critical),
            ("zero_damaged_pages", HealthSeverity.Critical),
            ("fsync", HealthSeverity.Critical),
            ("full_page_writes", HealthSeverity.Critical),
            ("synchronous_commit", HealthSeverity.Info),
        ]);

        findings.Single(static x => x.Finding.Subject == "track_counts").Evidence.Should().Contain(static x =>
            x.Label == "Set by" && x.Value.Contains("connection string", StringComparison.Ordinal));
        findings.Single(static x => x.Finding.Subject == "fsync").Evidence.Should().Contain(new HealthEvidence("Set by", "the server's command line"));
        findings.Single(static x => x.Finding.Subject == "track_counts").Finding.Remedy!.Explanation.Should().Contain("Options of the connection string");
    }

    // ----------------------------------------------------------------------------------------------
    // S03 - checksums off is a status line
    // ----------------------------------------------------------------------------------------------

    [PostgresFact]
    [HealthQuiet("S03")]
    public async Task A_server_with_no_checksum_failure_is_quiet_and_says_whether_it_checks()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_s03_quiet");

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "S03");
        HealthCheckStatus status = HealthLiveHost.Status(report, "S03");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked);
        status.Note.Should().StartWith("Data checksums are", "checksums off is a status line, not a finding, and checksums on with no failure is quiet");
    }

    // ----------------------------------------------------------------------------------------------
    // K04 and C10 on a direct connection
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// K04 reads only configuration, so a store whose data source names Neon's pooled endpoint - under <c>.invalid</c>,
    /// which nothing resolves - is gathered from a real container's store and never connects.
    /// </summary>
    /// <remarks>
    /// <b>This duplicates the fast suite's</b>
    /// <c>PooledDaemonLockCheckTests.K04_reads_the_mode_the_lock_and_the_hosts_from_a_real_store_and_opens_nothing</c>,
    /// which proves the same reading through the real sweeper with no Docker at all. It is here because the
    /// fires/quiet convention wants a live test for every check, and a tier-0 check has nothing more for Postgres to
    /// show: a pooler-shaped host or port cannot be had on a container port Testcontainers maps at random. What a live
    /// pooler adds is C10's proof, in <c>PooledDaemonLockLiveTests</c>.
    /// </remarks>
    [PostgresFact]
    [HealthFires("K04")]
    public async Task A_HotCold_session_lock_whose_data_source_names_a_pooled_endpoint_is_a_K04_warning()
    {
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(
            "Host=ep-studio-h10-pooler.example.invalid;Port=5432;Database=none;Username=none;Password=none",
            sample: false,
            store: static options =>
            {
                options.Projections.AsyncMode = DaemonMode.HotCold;
                options.Events.UseAdvisoryLockTransaction = false;
            });

        PooledDaemonLockCheck check = new();
        DaemonLockPooling facts = await host.GatherAsync(check);

        facts.AsyncMode.Should().Be(DaemonMode.HotCold);
        facts.TransactionScopedLock.Should().BeFalse();

        HealthFinding finding = check.Evaluate(facts, HealthEvaluation.At(DateTimeOffset.UtcNow)).Findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(static x => x.Label == "Looks pooled" && x.Value.StartsWith("ep-studio-h10-pooler.example.invalid:5432", StringComparison.Ordinal));
    }

    /// <summary>
    /// The configuration C10 is about, on a direct connection: the probe runs, two transactions reach one server
    /// process, and both K04 and C10 are quiet and say why.
    /// </summary>
    [PostgresFact]
    [HealthQuiet("K04")]
    [HealthQuiet("C10")]
    public async Task A_HotCold_session_lock_on_a_direct_connection_is_quiet_in_both_checks()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c10_quiet");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(
            database,
            store: static options =>
            {
                options.Projections.AsyncMode = DaemonMode.HotCold;
                options.Events.UseAdvisoryLockTransaction = false;
            });

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "K04" || x.Code == "C10");

        HealthCheckStatus k04 = ConnectionsLiveHost.Status(report, "K04");
        k04.Note.Should().Contain("looks like a transaction-mode pooler").And.Contain("C10 tests the connection itself");

        HealthCheckStatus c10 = ConnectionsLiveHost.Status(report, "C10");
        c10.Outcome.Should().Be(HealthCheckOutcome.Checked);
        c10.Note.Should().Contain("reached the same Postgres server process");
        c10.Statements.Should().HaveCount(2).And.OnlyContain(static x => x.Sql == "select pg_catalog.pg_backend_pid()");
    }

    [PostgresFact]
    public async Task C10_probes_nothing_for_a_store_whose_daemon_takes_no_HotCold_lock()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c10_solo");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, store: static options => options.Projections.AsyncMode = DaemonMode.Solo);

        HealthCheckStatus c10 = ConnectionsLiveHost.Status(await host.ReportAsync(), "C10");

        c10.Note.Should().Contain("Solo");
        c10.Statements.Should().BeEmpty("nothing is asked of the server for a daemon that takes no lock");
    }
}
